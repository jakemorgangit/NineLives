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

    [ObservableProperty]
    private ServerConnection? _sourceServer;

    partial void OnSourceServerChanged(ServerConnection? value)
    {
        CheckCommand.NotifyCanExecuteChanged();

        // A previous answer described a different server. Leaving it on screen under a new
        // selection is the stale-result problem, and this panel's whole job is to be believed.
        Clear();
    }

    public ObservableCollection<MissingLocationRow> Locations { get; } = [];

    [ObservableProperty]
    private bool _hasChecked;

    [ObservableProperty]
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

    public bool CanCheck => SourceServer != null && !IsChecking;

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
            var history = await _sql.ReadBackupHistoryAsync(server, database, ct);

            var locations = BackupGapAnalyser.Compare(history, held, database);
            foreach (var location in locations) Locations.Add(new MissingLocationRow(location));

            var behind = BackupGapAnalyser.RecoveryTimeNotInContainer(history, held, database);
            BehindBy = behind is { } gap ? Humanise(gap) : string.Empty;

            ComparedWhat =
                $"{database} on {server.ServerName}: {history.Count} backup(s) in its history, " +
                $"{held.Count} set(s) in {container?.Name ?? "this container"}.";

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
            var history = await _sql.ReadBackupHistoryAsync(server, database, ct);
            var locations = BackupGapAnalyser.LogsTakenAfter(history, database, after);

            foreach (var location in locations) Locations.Add(new MissingLocationRow(location));

            ComparedWhat =
                $"{database} on {server.ServerName}: log backups taken after " +
                $"{after:yyyy-MM-dd HH:mm}.";

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
    /// Builds the copy script for one location, on demand rather than for every location up front.
    /// Most checks find one location and nobody reads the others.
    /// </summary>
    public void BuildScript(MissingLocationRow row, BlobContainerConfig container)
        => row.Script = MissingBackupCopyScript.Build(row.Location, container);

    private void Clear()
    {
        Locations.Clear();
        BehindBy = string.Empty;
        ComparedWhat = string.Empty;
        HasChecked = false;
        RaiseDerived();
    }

    private void RaiseDerived()
    {
        OnPropertyChanged(nameof(HasGap));
        OnPropertyChanged(nameof(FoundNothingMissing));
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
