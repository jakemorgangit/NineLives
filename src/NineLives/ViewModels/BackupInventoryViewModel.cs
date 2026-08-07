using System.Collections.ObjectModel;
using Blackcat.NineLives.Models;
using Blackcat.NineLives.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Blackcat.NineLives.ViewModels;

/// <summary>
/// What a backup location holds, and which server and database within it is being looked at.
///
/// Fourth seam out of RestoreViewModel (#115). Its point is not the line count: it is that the
/// loaded backups now carry the location they came FROM, as a field, instead of everything on the
/// screen assuming that whatever is loaded belongs to whatever is currently selected above it.
///
/// That assumption has produced real bugs twice. #112: changing the container left the previous
/// one's chain and script armed and executable, so Execute ran against blob URLs from a container
/// nobody was looking at. And when the preselection was removed, the working set silently fell
/// back to EVERY set in the container, drawing a timeline of every backup of every database on
/// every server. Both are the same mistake - data whose provenance is implied rather than stated.
///
/// It is also the ONE place that knows a backup can live somewhere other than a container (#165).
/// Everything downstream - the working set, the chain, the timeline, the restore points, the
/// options, the script, the execute path - operates on <see cref="BackupSet"/> and never asks where
/// they came from, which is why widening the app to a second medium is a change to this type and
/// not to any of those.
/// </summary>
public partial class BackupInventoryViewModel : ViewModelBase
{
    private readonly IBlobStorageService _blob;
    private readonly ISqlServerService _sql;
    private readonly OperationLog _log;
    private readonly IBackupAuditStore _auditStore;
    private readonly OperationCancellation _loadCancellation = new();

    /// <param name="log">
    /// Optional so a test can point it at a temp directory. Reaching for App.Log directly is what
    /// put "[headeronly] fake: 1 statement(s)" into a real user's log file - a test run writing
    /// into the profile of whoever ran it, which is the same class of side effect #41 was about.
    /// </param>
    public BackupInventoryViewModel(
        IBlobStorageService blob,
        ISqlServerService sql,
        OperationLog? log = null,
        IBackupAuditStore? auditStore = null)
    {
        _blob = blob;
        _sql = sql;
        _log = log ?? App.Log;
        _auditStore = auditStore ?? new BackupAuditStore();
    }

    /// <summary>Raised when the working set changes, so the chain and timeline can be rebuilt.</summary>
    public event Action? WorkingSetChanged;

    /// <summary>Raised when the cancel state changes, so the parent's Stop button can follow.</summary>
    public event Action? CancelStateChanged;

    // ── what was loaded, and where from ─────────────────────────────────────────

    private List<BackupFileInfo> _allBackups = [];
    private List<BackupSet> _allSets = [];

    /// <summary>
    /// The location these backups were read from.
    ///
    /// The whole reason this type exists. Anything derived from the inventory - a chain, a script,
    /// a restore point - belongs to THIS location, and can be checked against the one currently
    /// selected rather than assumed to match it.
    /// </summary>
    [ObservableProperty]
    private BackupLocation? _loadedFrom;

    /// <summary>True when what is loaded came from a shared path rather than a container.</summary>
    public bool LoadedFromSharedPath => LoadedFrom?.IsSharedPath == true;

    partial void OnLoadedFromChanged(BackupLocation? value)
        => OnPropertyChanged(nameof(LoadedFromSharedPath));

    /// <summary>The sets for the chosen server and database: what a chain can actually be built from.</summary>
    public List<BackupSet> WorkingSet { get; private set; } = [];

    /// <summary>Every set in the container, for the inventory findings that look wider than one database.</summary>
    public List<BackupSet> AllSets => _allSets;

    [ObservableProperty]
    private bool _backupsLoaded;

    [ObservableProperty]
    private string _loadProgressText = string.Empty;

    [ObservableProperty]
    private bool _canCancelLoad;

    // ── what is being looked at within it ───────────────────────────────────────

    [ObservableProperty]
    private ObservableCollection<string> _discoveredServers = [];

    [ObservableProperty]
    private ObservableCollection<string> _discoveredDatabases = [];

    [ObservableProperty]
    private string? _selectedServerName;

    [ObservableProperty]
    private string? _selectedDatabaseName;

    [ObservableProperty]
    private int _fullCount;

    [ObservableProperty]
    private int _diffCount;

    [ObservableProperty]
    private int _logCount;

    [ObservableProperty]
    private int _setCount;

    // ── files the filename could not place (#130) ───────────────────────────────

    /// <summary>
    /// How many loaded files the path and filename could not classify.
    ///
    /// Not a tidiness count. A file with an unknown type never enters the fulls, diffs or logs
    /// collections, and one with no database is filtered out of every working set - so these are
    /// not merely unlabelled, they are invisible to the restore screen entirely. That is worth
    /// saying out loud rather than leaving somebody to wonder why a backup they can see in the
    /// container is not on the timeline.
    /// </summary>
    [ObservableProperty]
    private int _unclassifiedCount;

    public bool HasUnclassified => UnclassifiedCount > 0;

    partial void OnUnclassifiedCountChanged(int value) => OnPropertyChanged(nameof(HasUnclassified));

    [ObservableProperty]
    private string _identifyProgressText = string.Empty;

    // ── auditing a database against its own headers (#130) ──────────────────────

    /// <summary>What the audit disagreed with, newest run only.</summary>
    [ObservableProperty]
    private ObservableCollection<BackupAuditFinding> _auditFindings = [];

    [ObservableProperty]
    private bool _isAuditing;

    /// <summary>How far through, as a percentage, for the bar.</summary>
    [ObservableProperty]
    private double _auditProgress;

    [ObservableProperty]
    private string _auditProgressText = string.Empty;

    /// <summary>What the last audit concluded, or empty before one has run.</summary>
    [ObservableProperty]
    private string _auditSummary = string.Empty;

    public bool HasAuditFindings => AuditFindings.Count > 0;
    public bool HasAuditSummary => AuditSummary.Length > 0;

    partial void OnAuditFindingsChanged(ObservableCollection<BackupAuditFinding> value)
        => OnPropertyChanged(nameof(HasAuditFindings));

    partial void OnAuditSummaryChanged(string value) => OnPropertyChanged(nameof(HasAuditSummary));

    /// <summary>
    /// What auditing the current selection would cost, said BEFORE it is started.
    ///
    /// A three-minute operation somebody did not expect reads as a hang. The number is not a guess:
    /// a header read was measured at about 1.7 seconds against a real container, and what is already
    /// cached is subtracted - so re-running after a fix says so rather than quoting the full price
    /// again.
    /// </summary>
    public string AuditEstimate
    {
        get
        {
            if (WorkingSet.Count == 0) return string.Empty;

            var toRead = BackupAuditor.NotYetAudited(WorkingSet, _auditStore.Load()).Count;
            return BackupAuditor.DescribeEstimate(toRead);
        }
    }

    public bool CanAudit => WorkingSet.Count > 0 && !IsAuditing;

    /// <summary>
    /// Reads every set in the current selection and reports where the headers disagree.
    ///
    /// Findings are reported, not applied. A database full of them almost always means the PATH
    /// PATTERN is wrong, and correcting each symptom silently would hide the one thing worth
    /// knowing.
    /// </summary>
    public async Task AuditAsync(ServerConnection server)
    {
        if (WorkingSet.Count == 0) return;

        var sets = WorkingSet.ToList();
        var ct = _loadCancellation.Begin();

        IsAuditing = true;
        AuditProgress = 0;
        AuditFindings = [];
        AuditSummary = string.Empty;
        ClearStatus();
        RaiseCancelStateChanged();

        try
        {
            var auditor = new BackupAuditor(_sql, _auditStore);
            var progress = new Progress<int>(n =>
            {
                AuditProgress = sets.Count == 0 ? 0 : 100.0 * n / sets.Count;
                AuditProgressText = $"Checked {n:N0} of {sets.Count:N0} backup set(s)...";
            });

            var findings = await auditor.AuditAsync(
                server, sets, progress, t => _log.Info($"[audit] {t}"), ct);

            AuditFindings = new ObservableCollection<BackupAuditFinding>(findings);

            AuditSummary = findings.Count == 0
                ? $"All {sets.Count:N0} backup set(s) match their headers."
                : $"{findings.Count:N0} of {sets.Count:N0} backup set(s) do not match their headers.";

            SetStatus(AuditSummary);
            _log.Info($"[audit] {AuditSummary}");
        }
        catch (OperationCanceledException)
        {
            // What it got through is cached, so stopping costs nothing already paid for.
            SetStatus("Audit stopped. What it had already checked has been kept.");
        }
        catch (Exception ex)
        {
            SetError($"The audit could not finish: {ex.Message}");
        }
        finally
        {
            _loadCancellation.End();
            IsAuditing = false;
            AuditProgressText = string.Empty;
            RaiseCancelStateChanged();
            OnPropertyChanged(nameof(AuditEstimate));
            OnPropertyChanged(nameof(CanAudit));
        }
    }

    private void RefreshUnclassifiedCount()
        => UnclassifiedCount = _allBackups.Count(BackupHeaderIdentifier.NeedsIdentifying);

    /// <summary>
    /// Asks SQL Server what those files actually are, and regroups.
    ///
    /// Scoped to the unclassified ones rather than run over everything, which is the whole design:
    /// one HEADERONLY per file is a network read, and across a container that is thousands of round
    /// trips - but across the handful the pattern could not place it is seconds, and those are
    /// exactly the files where the answer is worth paying for.
    ///
    /// The header also carries LSNs, so a file identified this way reaches the chain builder able to
    /// be paired definitively rather than by proximity in time.
    /// </summary>
    public async Task IdentifyUnclassifiedAsync(ServerConnection server)
    {
        var unidentified = _allBackups.Where(BackupHeaderIdentifier.NeedsIdentifying).ToList();
        if (unidentified.Count == 0) return;

        var ct = _loadCancellation.Begin();
        IsBusy = true;
        ClearStatus();
        RaiseCancelStateChanged();

        try
        {
            var identifier = new BackupHeaderIdentifier(_sql);
            var progress = new Progress<int>(
                n => IdentifyProgressText = $"Read {n:N0} of {unidentified.Count:N0} header(s)...");

            // The timing goes to the log rather than the screen: it is the number #130 needs to
            // settle what a container-wide audit would cost, and it is worth having from every run
            // rather than only from a deliberate measurement.
            var settled = await identifier.IdentifyAsync(
                server, unidentified, progress, t => _log.Info($"[headeronly] {t}"), ct);

            // Regroup: what the headers said changes which set a file belongs to, and a set is
            // keyed on the database and type that have just been corrected.
            _allSets = _blob.GroupIntoBackupSets(
                _allBackups, LoadedFrom?.Container?.BackupServerTimeZoneId);

            DiscoveredServers = new ObservableCollection<string>(_blob.GetDiscoveredServers(_allBackups));
            DiscoveredDatabases = new ObservableCollection<string>(_blob.GetDiscoveredDatabases(_allBackups));

            RefreshUnclassifiedCount();
            RebuildWorkingSet();

            SetStatus(settled == 0
                ? $"Read {unidentified.Count} header(s); none of them said anything the filename did not."
                : $"Identified {settled} of {unidentified.Count} file(s) from their backup headers.");
        }
        catch (OperationCanceledException)
        {
            SetStatus("Stopped.");
        }
        catch (Exception ex)
        {
            SetError($"Could not read the backup headers: {ex.Message}");
        }
        finally
        {
            _loadCancellation.End();
            IsBusy = false;
            IdentifyProgressText = string.Empty;
            RaiseCancelStateChanged();
        }
    }

    partial void OnSelectedServerNameChanged(string? value)
    {
        if (_allSets.Count == 0) return;

        // Compare the FULL server identity. Matching on the host alone made selecting
        // SQLHOST\PROD also match SQLHOST\TEST, so the database list offered databases that only
        // exist on the other instance.
        var dbs = _allSets
            .Where(s => s.MatchesServer(value))
            .Where(s => !string.IsNullOrEmpty(s.DatabaseName))
            .Select(s => s.DatabaseName!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();

        DiscoveredDatabases = new ObservableCollection<string>(dbs);

        // Nothing is chosen for the user. Picking the first database alphabetically is a decision
        // about WHICH DATABASE TO RESTORE, made silently, and the rest of the screen then fills in
        // around it as though somebody had asked for it. On a screen whose last button overwrites a
        // database, the app does not get to guess.
        RebuildWorkingSet();
    }

    partial void OnSelectedDatabaseNameChanged(string? value) => RebuildWorkingSet();

    /// <summary>
    /// Narrows the loaded sets to the chosen server and database.
    ///
    /// With no database chosen the working set is EMPTY, not everything. Falling back to every set
    /// in the container produced a timeline of every backup of every database on every server -
    /// thousands of points on a real container, none of them meaningful, because a restore chain
    /// only exists within one database.
    /// </summary>
    private void RebuildWorkingSet()
    {
        if (string.IsNullOrEmpty(SelectedDatabaseName) || _allSets.Count == 0)
        {
            WorkingSet = [];
            FullCount = DiffCount = LogCount = SetCount = 0;
            WorkingSetChanged?.Invoke();
            return;
        }

        var sets = _allSets
            .Where(s => string.Equals(s.DatabaseName, SelectedDatabaseName, StringComparison.OrdinalIgnoreCase))
            .ToList();

        // Full server identity again - this is the filter that decides which sets reach
        // BackupChainBuilder, so a host-only match let one instance's full pair with another
        // instance's differentials and logs.
        if (!string.IsNullOrEmpty(SelectedServerName))
            sets = sets.Where(s => s.MatchesServer(SelectedServerName)).ToList();

        WorkingSet = sets;

        // A different selection means a different estimate, and findings that belonged to the last
        // one. Leaving either up attaches an answer to a question nobody asked.
        AuditFindings = [];
        AuditSummary = string.Empty;
        OnPropertyChanged(nameof(AuditEstimate));
        OnPropertyChanged(nameof(CanAudit));

        FullCount = sets.Count(s => s.Type == BackupType.Full);
        DiffCount = sets.Count(s => s.Type == BackupType.Differential);
        LogCount = sets.Count(s => s.Type == BackupType.TransactionLog);
        SetCount = sets.Count;

        WorkingSetChanged?.Invoke();
    }

    // ── loading ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Reads a backup location and works out what is in it.
    ///
    /// The two media differ only here. A container is listed and its filenames interpreted; a
    /// shared path cannot be read that way at all - a directory of .bak files says nothing about
    /// which database each belongs to or which full a differential was taken against - so the
    /// instance that TOOK them is asked what it recorded. Both end in a list of
    /// <see cref="BackupSet"/>, and nothing downstream can tell which it was looking at.
    /// </summary>
    public async Task LoadAsync(BackupLocation location)
    {
        var ct = _loadCancellation.Begin();
        IsBusy = true;
        LoadProgressText = location.IsSharedPath
            ? "Reading backup history..."
            : "Scanning container...";
        RaiseCancelStateChanged();
        ClearStatus();

        try
        {
            if (location.IsSharedPath)
            {
                await LoadFromHistoryAsync(location, ct);
                return;
            }

            var container = location.Container!;
            // If a database is already chosen - a reload after taking a fresh backup, say - push
            // that down to Azure as a prefix instead of walking the whole container and discarding
            // most of it. Measured on a real 4,440-blob container: about 1,075 ms unscoped versus
            // about 233 ms for one database (#28).
            //
            // The FIRST load has no selection yet and stays a full scan, which it has to be: the
            // server and database lists are built from what it finds.
            var scope = BuildListingScope();
            var progress = new Progress<int>(n => LoadProgressText = $"Scanned {n:N0} blobs...");

            var files = await _blob.ListBackupFilesAsync(container, scope, progress, ct);
            var sets = _blob.GroupIntoBackupSets(files, container.BackupServerTimeZoneId);

            _allBackups = files;
            _allSets = sets;

            // Stated, not assumed: everything derived from here belongs to THIS location.
            LoadedFrom = location;

            DiscoveredServers = new ObservableCollection<string>(_blob.GetDiscoveredServers(files));
            DiscoveredDatabases = new ObservableCollection<string>(_blob.GetDiscoveredDatabases(files));
            BackupsLoaded = files.Count > 0;

            RefreshUnclassifiedCount();

            // The lists are offered, not answered - and until one IS answered there is nothing to
            // show.
            RebuildWorkingSet();

            // Only when nothing above went wrong. The filter-and-compute cascade can end in "no
            // valid restore points found", and painting a success line over that left an empty
            // timeline with the status bar cheerfully reporting how many files had loaded.
            if (!HasError)
                SetStatus($"Loaded {files.Count} files in {sets.Count} backup set(s) across " +
                          $"{DiscoveredDatabases.Count} database(s).");
        }
        catch (OperationCanceledException)
        {
            // Asked for, not a failure. Nothing was written anywhere - listing is read-only - so
            // there is nothing to explain beyond saying it stopped.
            SetStatus("Loading cancelled.");
            BackupsLoaded = false;
        }
        catch (Exception ex)
        {
            SetError($"Failed to load backups: {ex.Message}");
            BackupsLoaded = false;
        }
        finally
        {
            _loadCancellation.End();
            IsBusy = false;
            LoadProgressText = string.Empty;
            RaiseCancelStateChanged();
        }
    }

    /// <summary>
    /// What a source instance recorded backing up, as the same inventory a container produces.
    ///
    /// The server and database lists come from the history itself rather than from the app's saved
    /// connections, for the same reason the container path derives them from what it found: what
    /// matters is what can actually be restored, not what somebody once configured.
    /// </summary>
    private async Task LoadFromHistoryAsync(BackupLocation location, CancellationToken ct)
    {
        var source = location.SourceServer!;
        var history = await _sql.ReadBackupHistoryAsync(source, SelectedDatabaseName, ct);

        // The substitution is applied once, here, so the chain, the file list on screen, the script
        // and the readability check all name the same file. Applying it in some of those places and
        // not others is how a screen ends up checking one path and restoring another.
        var sets = BackupHistoryInventory.ToSets(history, location.Mapping);

        _allBackups = sets.SelectMany(s => s.Files).ToList();
        _allSets = sets;
        LoadedFrom = location;

        DiscoveredServers = new ObservableCollection<string>(
            sets.Select(s => s.ServerDisplay)
                .Where(n => !string.IsNullOrEmpty(n))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)!);

        DiscoveredDatabases = new ObservableCollection<string>(
            sets.Select(s => s.DatabaseName)
                .Where(n => !string.IsNullOrEmpty(n))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)!);

        BackupsLoaded = sets.Count > 0;

        // Never any: msdb recorded each backup's database, type and LSNs, so nothing read from it
        // arrives unplaced.
        UnclassifiedCount = 0;

        // Offered, not answered - the same rule the container path follows.
        RebuildWorkingSet();

        if (!HasError)
            SetStatus(sets.Count == 0
                ? $"{source.ServerName} has no backup history to restore from."
                : $"Read {sets.Count} backup set(s) from {source.ServerName} across " +
                  $"{DiscoveredDatabases.Count} database(s).");
    }

    /// <summary>
    /// The scope to push down to Azure, or null to scan everything.
    ///
    /// Only offered when a database is chosen. A server on its own narrows far less and the
    /// database list is built from the previous scan anyway, so scoping to a server alone would
    /// mostly re-fetch what is already in memory.
    /// </summary>
    private BlobListingScope? BuildListingScope()
    {
        if (string.IsNullOrWhiteSpace(SelectedDatabaseName)) return null;

        // ServerName here is the identity used in the PATH, which for an instance is the host
        // part - "SRV01\PROD" lives under "SRV01". ServerIdentity knows how to split it.
        var pathServer = string.IsNullOrWhiteSpace(SelectedServerName)
            ? null
            : SelectedServerName.Split('\\')[0];

        return new BlobListingScope(pathServer, SelectedDatabaseName);
    }

    /// <summary>Stops an in-progress listing.</summary>
    [RelayCommand]
    private void CancelLoad()
    {
        if (!_loadCancellation.CanCancel) return;

        _loadCancellation.Cancel();
        RaiseCancelStateChanged();
        SetStatus("Stopping the scan...");
    }

    /// <summary>
    /// Drops everything the previous container produced.
    ///
    /// Called when the container changes, so what is on screen always belongs to the container
    /// named above it - the failure #112 was, where the previous container's chain and script
    /// stayed armed and executable against blob URLs nobody was looking at any more.
    /// </summary>
    public void Clear()
    {
        _allBackups = [];
        _allSets = [];
        WorkingSet = [];
        LoadedFrom = null;

        BackupsLoaded = false;
        DiscoveredServers = [];
        DiscoveredDatabases = [];
        SelectedServerName = null;
        SelectedDatabaseName = null;
        FullCount = DiffCount = LogCount = SetCount = 0;
        UnclassifiedCount = 0;

        WorkingSetChanged?.Invoke();
    }

    private void RaiseCancelStateChanged()
    {
        CanCancelLoad = _loadCancellation.CanCancel;
        CancelStateChanged?.Invoke();
    }
}
