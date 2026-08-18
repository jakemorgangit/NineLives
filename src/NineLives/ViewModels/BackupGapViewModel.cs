using System.Collections.ObjectModel;
using Blackcat.NineLives.Models;
using Blackcat.NineLives.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Blackcat.NineLives.ViewModels;

/// <summary>One folder's worth of missing backups, as the screen shows it.</summary>
public sealed partial class MissingLocationRow(MissingLocation location) : ObservableObject
{
    public MissingLocation Location { get; } = location;

    public string Folder => Location.Folder;
    public string Summary => Location.Summary;
    public int FileCount => Location.FileCount;
    public string SizeDisplay => Location.SizeDisplay;

    public string Window =>
        $"{Location.Earliest:yyyy-MM-dd HH:mm} to {Location.Latest:yyyy-MM-dd HH:mm}";

    /// <summary>
    /// Whether these can be copied at all. A backup msdb recorded to a URL is not a file on the
    /// source machine, so there is nothing for a copy script to pick up - that finding means the
    /// container listing did not see something that was written to storage, which is a different
    /// problem with a different answer.
    /// </summary>
    public bool IsOnDisk => BackupDevice.IsPath(Location.Backups[0].Files.FirstOrDefault());

    [ObservableProperty]
    private string _script = string.Empty;

    public bool HasScript => Script.Length > 0;

    partial void OnScriptChanged(string value) => OnPropertyChanged(nameof(HasScript));
}

/// <summary>
/// What the source instance recorded, against what the container actually holds (#451).
///
/// Answers the question the restore screen cannot answer on its own: is this chain short because
/// that is all there ever was, or because the logs went somewhere else? The container cannot tell
/// the difference - only the instance that took them knows - so this asks it, and it has to be
/// asked deliberately because it means connecting to a second server.
/// </summary>
public partial class BackupGapViewModel : ViewModelBase
{
    private readonly ISqlServerService _sql;
    private readonly OperationCancellation _cancellation = new();

    public BackupGapViewModel(ISqlServerService sql) => _sql = sql;

    /// <summary>The servers offered as the source. Filled by the screen that owns this.</summary>
    public ObservableCollection<ServerConnection> Servers { get; } = [];

    /// <summary>
    /// The instance to ask. Notifies CanCheck, because the button reads that rather than the
    /// command (#483) - see the property itself.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCheck))]
    private ServerConnection? _sourceServer;

    partial void OnSourceServerChanged(ServerConnection? oldValue, ServerConnection? newValue)
    {
        CheckCommand.NotifyCanExecuteChanged();

        // The SAME server arriving again is navigation re-reading the config, not a new selection
        // (#478) - and going away is this panel's own workflow. It names the logs that never
        // reached the container and hands over a script to copy them; somebody runs that on the
        // source machine and comes back to press "rescan and check again". Coming back was what
        // threw away the answer the second check compares against, so it reported everything as
        // still missing however much had arrived.
        if (oldValue?.Id == newValue?.Id) return;

        // A previous answer described a different server. Leaving it on screen under a new
        // selection is the stale-result problem, and this panel's whole job is to be believed -
        // including the comparison, which would otherwise measure one instance against another.
        _previouslyMissing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Clear();
    }

    public ObservableCollection<MissingLocationRow> Locations { get; } = [];

    [ObservableProperty]
    private bool _hasChecked;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCheck))]
    private bool _isChecking;

    /// <summary>How far behind the container is, in words, or empty when it is not behind.</summary>
    [ObservableProperty]
    private string _behindBy = string.Empty;

    /// <summary>
    /// Whether there is a measurable gap to quote (#451).
    ///
    /// A gap and a MEASURED gap are different things, which rendering the panel made obvious: a
    /// container holding nothing at all for this database has everything missing and no interval
    /// to state, because there is no newest-held backup to measure from. The banner assumed the
    /// two went together and rendered "This container is  behind what the instance recorded".
    /// </summary>
    public bool HasMeasuredGap => BehindBy.Length > 0;

    partial void OnBehindByChanged(string value) => OnPropertyChanged(nameof(HasMeasuredGap));

    public bool HasGap => Locations.Count > 0;

    public bool FoundNothingMissing => HasChecked && !IsChecking && Locations.Count == 0;

    /// <summary>
    /// What was compared, so the answer can be read without trusting it blindly - somebody has to
    /// be able to see that the check was asked of the right database on the right instance.
    /// </summary>
    [ObservableProperty]
    private string _comparedWhat = string.Empty;

    /// <summary>
    /// Whether the check can run - and a property the BUTTON reads, not just the command (#483).
    ///
    /// The button binds IsEnabled to this as well as binding Command, and an explicit IsEnabled
    /// wins over the command's own answer. So NotifyCanExecuteChanged, which this used to be the
    /// only subject of, updated something nothing was looking at: choosing a source instance left
    /// the button dead, and the only way to wake it was to leave the screen and come back, which
    /// rebuilds the visual tree and re-reads every binding from scratch.
    ///
    /// Same lesson as #130. Asking the property in a test always gives the right answer; a control
    /// caches what it was last told, so only a test watching PropertyChanged can see this.
    /// </summary>
    public bool CanCheck => SourceServer != null && !IsChecking;

    /// <summary>
    /// Every file the previous check reported missing, so this one can say what ARRIVED (#451).
    ///
    /// Re-running the check and drawing a new panel answers "what is missing now", which is not
    /// the question somebody has after running a copy script. Theirs is "did it work" - and a
    /// still-red panel listing five files looks identical whether eighteen arrived or none did.
    ///
    /// By file rather than by count, because a retention job trimming an old log while a copy runs
    /// would otherwise look like a file that arrived.
    /// </summary>
    private HashSet<string> _previouslyMissing = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>What changed since the last check, or empty when there is nothing to compare.</summary>
    [ObservableProperty]
    private string _arrivalReport = string.Empty;

    public bool HasArrivalReport => ArrivalReport.Length > 0;

    partial void OnArrivalReportChanged(string value) => OnPropertyChanged(nameof(HasArrivalReport));

    /// <summary>
    /// Reads the source instance's own record and sets it against the container's contents.
    ///
    /// The container and the database name come from the screen at the moment of the press rather
    /// than being held here, because both move while this panel is open and an answer about a
    /// database nobody is looking at any more is worse than no answer.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanCheck))]
    public async Task CheckAsync(GapCheckRequest? request)
    {
        if (SourceServer is not { } server || request is not { } ask) return;

        if (string.IsNullOrWhiteSpace(ask.Database))
        {
            SetError("Pick a database on this screen first - the check is per database.");
            return;
        }

        // Captured BEFORE the await: both can change while this runs, and the answer has to
        // describe what was actually compared.
        var database = ask.Database;
        var container = ask.Container;
        var held = ask.InContainer;

        IsChecking = true;
        ClearStatus();
        Clear();

        var ct = _cancellation.Begin();

        try
        {
            var history = await _sql.ReadBackupHistoryAsync(
                server, database, SqlServerService.GapCheckHistoryLimit, ct);

            var locations = BackupGapAnalyser.Compare(history, held, database);
            foreach (var location in locations) Locations.Add(new MissingLocationRow(location));

            // What arrived since last time, before this check's own answer replaces it.
            var stillMissing = locations
                .SelectMany(l => l.Backups)
                .SelectMany(b => b.Files)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            ArrivalReport = DescribeArrivals(_previouslyMissing, stillMissing);
            _previouslyMissing = stillMissing;

            var behind = BackupGapAnalyser.RecoveryTimeNotInContainer(history, held, database);
            BehindBy = behind is { } gap ? Humanise(gap) : string.Empty;

            ComparedWhat =
                $"{database} on {server.ServerName}: {history.Count} backup(s) in its history, " +
                $"{held.Count} set(s) in {container?.Name ?? "this container"}.";

            HistoryCapNote = CapNoteFor(history, SqlServerService.GapCheckHistoryLimit);

            HasChecked = true;

            SetStatus(Locations.Count == 0
                ? "Everything the instance recorded is in the container."
                : $"{Locations.Count} location(s) hold backups this container does not.");
        }
        catch (OperationCanceledException)
        {
            SetStatus("Check cancelled.");
        }
        catch (Exception ex)
        {
            // Naming the server, because the usual cause is that this one cannot be reached -
            // and the reader is looking at a screen already connected to a different instance.
            SetError($"Could not read {server.ServerName}'s backup history: {ex.Message}");
        }
        finally
        {
            IsChecking = false;
            _cancellation.End();
            RaiseDerived();
        }
    }

    /// <summary>
    /// The source's logs taken after a moment, rather than what a container is missing (#451).
    ///
    /// The Copy screen's question. It reads no container chain - it writes its own full - so the
    /// useful thing is which of the source's logs would carry that full forward to now.
    /// </summary>
    public async Task CheckLogsAfterAsync(string database, DateTime after)
    {
        if (SourceServer is not { } server) return;

        IsChecking = true;
        ClearStatus();
        Clear();

        var ct = _cancellation.Begin();

        try
        {
            var history = await _sql.ReadBackupHistoryAsync(
                server, database, SqlServerService.GapCheckHistoryLimit, ct);
            var locations = BackupGapAnalyser.LogsTakenAfter(history, database, after);

            foreach (var location in locations) Locations.Add(new MissingLocationRow(location));

            ComparedWhat =
                $"{database} on {server.ServerName}: log backups taken after " +
                $"{after:yyyy-MM-dd HH:mm}.";

            HistoryCapNote = CapNoteFor(history, SqlServerService.GapCheckHistoryLimit);

            HasChecked = true;

            var files = Locations.Sum(l => l.FileCount);
            SetStatus(Locations.Count == 0
                ? "The source has taken no log backups since the copy - there is nothing to roll forward."
                : $"{files} log backup file(s) could roll this copy forward.");
        }
        catch (OperationCanceledException)
        {
            SetStatus("Check cancelled.");
        }
        catch (Exception ex)
        {
            SetError($"Could not read {server.ServerName}'s backup history: {ex.Message}");
        }
        finally
        {
            IsChecking = false;
            _cancellation.End();
            RaiseDerived();
        }
    }

    [RelayCommand]
    private void Cancel() => _cancellation.Cancel();

    /// <summary>
    /// What happened between two checks, in the terms somebody who has just run a copy script
    /// cares about (#451).
    ///
    /// Nothing at all on the first check: there is no previous answer, and inventing a comparison
    /// against zero would report every missing file as a fresh loss.
    ///
    /// Files that were missing and are missing still are not mentioned as a number on their own -
    /// the panel below already lists them, and repeating the count in two places invites the two
    /// to disagree.
    /// </summary>
    internal static string DescribeArrivals(
        IReadOnlySet<string> before, IReadOnlySet<string> after)
    {
        if (before.Count == 0) return string.Empty;

        var arrived = before.Count(f => !after.Contains(f));
        var stillGone = before.Count - arrived;

        // Something the copy did not account for: files missing now that were not missing before.
        // A backup taken since the last check is the ordinary cause, and it is worth separating
        // from "the copy failed" because the answer is different.
        var newlyMissing = after.Count(f => !before.Contains(f));

        var report =
            arrived == 0 ? $"None of the {before.Count} arrived."
            : stillGone == 0 ? $"All {arrived} arrived - the container now holds them."
            : $"{arrived} of {before.Count} arrived. {stillGone} still to come.";

        if (newlyMissing > 0)
            report += $" {newlyMissing} more have been taken since, and are not in the container either.";

        return report;
    }

    /// <summary>
    /// Builds the copy script for one location, on demand rather than for every location up front.
    /// Most checks find one location and nobody reads the others.
    /// </summary>
    public void BuildScript(MissingLocationRow row, BlobContainerConfig container)
        => row.Script = MissingBackupCopyScript.Build(row.Location, container);

    private void Clear()
    {
        Locations.Clear();
        ArrivalReport = string.Empty;
        BehindBy = string.Empty;
        ComparedWhat = string.Empty;
        HistoryCapNote = string.Empty;
        HasChecked = false;
        RaiseDerived();
    }

    /// <summary>
    /// Said out loud when the instance had more history than was asked for (#484).
    ///
    /// This panel is read as "here is what the container is missing". If the read was capped, what
    /// it actually shows is what the container is missing out of the newest N - and everything
    /// older is absent from the list for a reason that has nothing to do with the container. An
    /// under-report here is taken as an all-clear, which is the worst direction for it to be wrong
    /// in, so the cap is stated rather than left to be inferred from a round number.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasHistoryCapNote))]
    private string _historyCapNote = string.Empty;

    public bool HasHistoryCapNote => HistoryCapNote.Length > 0;

    /// <summary>
    /// Whether the read came back full, which means the instance has at least this much and
    /// probably more. Exact rather than approximate: a history of exactly the cap is indeed
    /// complete, and saying "there may be more" then is a small lie in the safe direction.
    /// </summary>
    private static string CapNoteFor(IReadOnlyList<BackupHistoryEntry> history, int limit)
    {
        if (history.Count < limit) return string.Empty;

        var oldest = history.Min(h => h.StartedAt);
        return $"Only the newest {limit:N0} backups this instance recorded were read, back to " +
               $"{oldest:yyyy-MM-dd HH:mm}. It has more, and anything older than that was not " +
               "compared - so this list may be short.";
    }

    private void RaiseDerived()
    {
        OnPropertyChanged(nameof(HasGap));
        OnPropertyChanged(nameof(FoundNothingMissing));
        OnPropertyChanged(nameof(CanCheck));
        CheckCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// "12 hours 45 minutes" - the number somebody decides on. Rounded to minutes, because a
    /// recovery window quoted to the second is false precision about a moving target.
    /// </summary>
    internal static string Humanise(TimeSpan gap)
    {
        if (gap.TotalMinutes < 1) return "under a minute";

        var parts = new List<string>();
        if (gap.Days > 0) parts.Add($"{gap.Days} day{(gap.Days == 1 ? "" : "s")}");
        if (gap.Hours > 0) parts.Add($"{gap.Hours} hour{(gap.Hours == 1 ? "" : "s")}");
        if (gap.Minutes > 0 && gap.Days == 0)
            parts.Add($"{gap.Minutes} minute{(gap.Minutes == 1 ? "" : "s")}");

        return string.Join(" ", parts);
    }
}

/// <summary>
/// What the screen hands the check at the moment it is pressed.
///
/// Passed in rather than held, because the database, the container and the listing all move while
/// this panel is open, and an answer about a database nobody is looking at any more is worse than
/// no answer at all.
/// </summary>
public sealed record GapCheckRequest(
    string Database,
    BlobContainerConfig? Container,
    IReadOnlyList<BackupSet> InContainer);
