using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using Blackcat.NineLives.Models;
using Blackcat.NineLives.Services;

namespace Blackcat.NineLives.ViewModels;

public partial class RestoreViewModel : ViewModelBase
{
    private readonly IBlobStorageService _blobService;
    private readonly ISqlServerService _sqlService;
    private readonly BackupChainBuilder _chainBuilder;
    private readonly RestoreScriptGenerator _scriptGenerator;
    private readonly BackupChainValidator _chainValidator = new();
    private readonly ICredentialStore _credentialStore;

    private List<BackupFileInfo> _allBackups = [];
    private List<BackupSet> _allSets = [];
    private List<BackupSet> _dbSets = [];

    // Two separate operations, two separate sources: browsing a container and running a restore
    // can both be in flight, and cancelling one must not touch the other (#25).
    private readonly OperationCancellation _loadCancellation = new();
    private readonly OperationCancellation _executeCancellation = new();

    #region Observable Properties

    [ObservableProperty]
    private ObservableCollection<BlobContainerConfig> _containers = [];

    [ObservableProperty]
    private BlobContainerConfig? _selectedContainer;

    [ObservableProperty]
    private bool _backupsLoaded;

    [ObservableProperty]
    private ObservableCollection<string> _discoveredServers = [];

    [ObservableProperty]
    private string? _selectedServerName;

    [ObservableProperty]
    private ObservableCollection<string> _discoveredDatabases = [];

    [ObservableProperty]
    private string? _selectedDatabaseName;

    [ObservableProperty]
    private string _targetDatabaseName = string.Empty;

    // Restore points (replaces continuous slider)
    [ObservableProperty]
    private ObservableCollection<RestorePoint> _restorePoints = [];

    [ObservableProperty]
    private RestorePoint? _selectedRestorePoint;

    [ObservableProperty]
    private bool _hasRestorePoints;

    [ObservableProperty]
    private string _restoreWindowText = string.Empty;

    /// <summary>
    /// Left-hand label under the timeline. A plain property rather than the old
    /// {Binding RestorePoints[0].Timestamp}, which threw an index error into WPF's binding trace
    /// on every layout while the collection was empty.
    /// </summary>
    [ObservableProperty]
    private string _timelineStartText = string.Empty;

    [ObservableProperty]
    private ObservableCollection<TimelineTick> _timelineTicks = [];

    [ObservableProperty]
    private int _timelineHeight = 60;

    // Restore chain
    [ObservableProperty]
    private BackupChain? _restoreChain;

    [ObservableProperty]
    private ObservableCollection<BackupFileInfo> _chainFiles = [];

    [ObservableProperty]
    private string _chainSummary = string.Empty;

    [ObservableProperty]
    private bool _hasValidChain;

    [ObservableProperty]
    private bool _showChainDetails;

    [ObservableProperty]
    private ObservableCollection<BackupSet> _chainSets = [];

    // Options
    [ObservableProperty]
    private bool _withReplace = true;

    [ObservableProperty]
    private RecoveryMode _recoveryMode = RecoveryMode.Recovery;

    public bool IsStandbyMode => RecoveryMode == RecoveryMode.Standby;

    // OnRecoveryModeChanged is defined later to also update restore summary

    [ObservableProperty]
    private string _standbyFilePath = string.Empty;

    // ── Point-in-time (STOPAT) ───────────────────────────────────────────────────
    // Only meaningful for a transaction-log restore point. Without this the granularity of a
    // "point in time" restore is the end of whichever log backup was selected - so with
    // 15-minute logs, recovering from a bad DELETE at 14:23:41 could only land on 14:15 or
    // 14:30. The target is bounded to within the selected log's window; to stop earlier the
    // user picks an earlier restore point, which keeps the generated chain correct.

    /// <summary>True when the selected restore point is a log, so STOPAT applies.</summary>
    [ObservableProperty]
    private bool _canUsePointInTime;

    [ObservableProperty]
    private bool _usePointInTime;

    /// <summary>User-entered target time, parsed into <see cref="StopAtDateTime"/>.</summary>
    [ObservableProperty]
    private string _stopAtText = string.Empty;

    /// <summary>Parsed and validated target, or null when unusable.</summary>
    [ObservableProperty]
    private DateTime? _stopAtDateTime;

    /// <summary>Exclusive lower bound: the end of the previous set in the chain.</summary>
    [ObservableProperty]
    private DateTime? _stopAtEarliest;

    /// <summary>Inclusive upper bound: the end of the selected log backup.</summary>
    [ObservableProperty]
    private DateTime? _stopAtLatest;

    [ObservableProperty]
    private string _pointInTimeMessage = string.Empty;

    [ObservableProperty]
    private bool _hasPointInTimeError;

    // ── Chain gap detection ──────────────────────────────────────────────────────
    // Structural validation of the selected chain, run at selection time. The app otherwise
    // assumes every discovered backup is present and intact, so a missing stripe or a hole in
    // the log sequence only surfaces mid-restore - after WITH REPLACE has dropped the target.

    [ObservableProperty]
    private ObservableCollection<ChainIssue> _chainIssues = [];

    [ObservableProperty]
    private bool _hasChainIssues;

    /// <summary>True when at least one issue makes the restore impossible as generated.</summary>
    [ObservableProperty]
    private bool _hasChainErrors;

    [ObservableProperty]
    private string _chainIssueSummary = string.Empty;

    /// <summary>
    /// Findings about the discovered backups as a whole rather than the selected chain - backups
    /// that exist but can never be offered as a restore point. Kept apart from ChainIssues because
    /// they do not change when the selection does, and because they explain a gap between what the
    /// browse list shows and what the timeline offers.
    /// </summary>
    [ObservableProperty]
    private ObservableCollection<ChainIssue> _inventoryIssues = [];

    [ObservableProperty]
    private bool _hasInventoryIssues;

    // ── Aftermath of a failed restore (#14) ─────────────────────────────────────
    // A chain that stops part-way leaves the target in RESTORING, and in SINGLE_USER too if
    // Disconnect sessions was on, because the closing SET MULTI_USER never ran. Both block other
    // connections, at the worst possible moment.

    [ObservableProperty]
    private string _recoveryStateMessage = string.Empty;

    [ObservableProperty]
    private ObservableCollection<RecoveryAction> _recoveryActions = [];

    [ObservableProperty]
    private bool _hasRecoveryActions;

    // ── Cancellation (#25) ──────────────────────────────────────────────────────

    /// <summary>True while a backup listing is running and has not been asked to stop.</summary>
    [ObservableProperty]
    private bool _canCancelLoad;

    /// <summary>True while a restore is running and has not been asked to stop.</summary>
    [ObservableProperty]
    private bool _canCancelExecute;

    /// <summary>True between asking to stop and the operation actually unwinding.</summary>
    [ObservableProperty]
    private bool _isCancelling;

    /// <summary>
    /// Running count during a listing. A container of any size takes long enough that "Loading..."
    /// on its own gives no sense of whether it is progressing or hung (#28).
    /// </summary>
    [ObservableProperty]
    private string _loadProgressText = string.Empty;

    // ── Execution console ───────────────────────────────────────────────────────

    /// <summary>
    /// The console, one line per entry. A collection rather than one growing string so appending
    /// costs the same on the thousandth line as on the first.
    /// </summary>
    [ObservableProperty]
    private ObservableCollection<ConsoleLine> _consoleLines = [];

    [ObservableProperty]
    private bool _hasConsoleOutput;

    /// <summary>
    /// True while the console is showing in its own window.
    ///
    /// The inline console hides itself for the duration, so the output is only ever in one place.
    /// It comes back when the window closes, so the record of what happened is still reachable
    /// from the main view afterwards.
    /// </summary>
    [ObservableProperty]
    private bool _isConsoleDetached;

    /// <summary>Pushes the cancellation sources' state onto the bound properties.</summary>
    private void RefreshCancelState()
    {
        CanCancelLoad = _loadCancellation.CanCancel;
        CanCancelExecute = _executeCancellation.CanCancel;
        IsCancelling = _loadCancellation.IsCancelling || _executeCancellation.IsCancelling;
    }

    /// <summary>True once the chain has been checked against RESTORE HEADERONLY metadata.</summary>
    [ObservableProperty]
    private bool _chainLsnVerified;

    [ObservableProperty]
    private bool _isValidatingChain;

    [ObservableProperty]
    private bool _isVerifyingChain;

    // ── restore point filters (#27) ─────────────────────────────────────────────

    /// <summary>True when the container holds any restore points at all.</summary>
    [ObservableProperty]
    private bool _hasVisiblePoints;

    [ObservableProperty]
    private string _pointCountText = string.Empty;

    [ObservableProperty]
    private bool _showFullPoints = true;

    [ObservableProperty]
    private bool _showDiffPoints = true;

    [ObservableProperty]
    private bool _showLogPoints = true;

    [ObservableProperty]
    private string _pointsFromText = string.Empty;

    [ObservableProperty]
    private string _pointsToText = string.Empty;

    /// <summary>Parsed from the text boxes; null means no bound on that end.</summary>
    [ObservableProperty]
    private DateTime? _pointsFrom;

    [ObservableProperty]
    private DateTime? _pointsTo;

    /// <summary>Per-set RESTORE VERIFYONLY results for the selected chain.</summary>
    public ObservableCollection<ChainVerifyResult> ChainVerifyResults { get; } = [];

    [ObservableProperty]
    private bool _hasVerifyResults;

    [ObservableProperty]
    private bool _hasVerifyFailures;

    [ObservableProperty]
    private bool _disconnectSessions = true;

    [ObservableProperty]
    private int _statsPercent = 10;

    [ObservableProperty]
    private bool _keepReplication;

    [ObservableProperty]
    private bool _enableBroker;

    [ObservableProperty]
    private bool _newBroker;

    [ObservableProperty]
    private bool _withChecksum;

    [ObservableProperty]
    private bool _continueAfterError;

    [ObservableProperty]
    private bool _useWithMove;

    public bool ShowMoveOptions => UseWithMove;

    public ServerConnection? ConnectedServer { get; set; }

    /// <summary>
    /// Tags of the connected server, shown on the execute confirmation. Chips rather than plain
    /// strings so the detected version appears alongside the environment labels, and so this
    /// renders through the same single wrapping template as the lists.
    /// </summary>
    [ObservableProperty]
    private ObservableCollection<TagChip> _connectedServerTags = [];

    [ObservableProperty]
    private bool _hasConnectedServerTags;

    partial void OnUseWithMoveChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowMoveOptions));
        if (value)
            _ = FetchDefaultPathsAsync();
        UpdateRestoreSummary();
    }

    [ObservableProperty]
    private string _moveDataFilePath = string.Empty;

    [ObservableProperty]
    private string _moveLogFilePath = string.Empty;

    [ObservableProperty]
    private bool _isFetchingPaths;

    [ObservableProperty]
    private bool _pathsFromServer;

    [ObservableProperty]
    private string _pathSourceText = string.Empty;

    [ObservableProperty]
    private string _restoreSummaryText = string.Empty;

    [ObservableProperty]
    private string _sqlCredentialName = string.Empty;

    // Blob credential status on connected server (credential must exist for RESTORE FROM URL)
    [ObservableProperty]
    private bool? _credentialExistsOnServer; // null = not checked

    [ObservableProperty]
    private bool _credentialIsValidSas; // true when exists and identity is SHARED ACCESS SIGNATURE

    [ObservableProperty]
    private string _credentialStatusMessage = string.Empty;

    [ObservableProperty]
    private bool _isCheckingCredential;

    [ObservableProperty]
    private bool _credentialSectionVisible;

    partial void OnSelectedContainerChanged(BlobContainerConfig? value)
    {
        if (value != null)
            SqlCredentialName = value.ContainerUrl;
        CredentialSectionVisible = value != null;

        // Everything on screen below this point came from the PREVIOUS container. Leaving it there
        // meant the credential panel and Create credential targeted the new container while the
        // script still restored from the old one's URLs - so Execute stayed armed and enabled, and
        // failed with Msg 3201 after WITH REPLACE had already dropped the target.
        ClearLoadedBackups();

        _ = RefreshCredentialStatusAsync();
    }

    /// <summary>
    /// Drops everything derived from a container's contents. Called when the container changes and
    /// when a load is abandoned, so what is on screen always belongs to the container named above
    /// it.
    /// </summary>
    private void ClearLoadedBackups()
    {
        _allBackups = [];
        _allSets = [];
        _dbSets = [];
        _allPoints = [];

        BackupsLoaded = false;
        DiscoveredServers = [];
        DiscoveredDatabases = [];
        SelectedServerName = null;
        SelectedDatabaseName = null;

        RestorePoints = [];
        HasRestorePoints = false;
        HasVisiblePoints = false;
        PointCountText = string.Empty;
        RestoreWindowText = string.Empty;
        TimelineTicks.Clear();
        TimelineHeight = 50;

        // Assigning null runs the selection handler, which clears the chain, the script, the
        // verification results and the inventory findings.
        SelectedRestorePoint = null;

        // Disarm. An armed Execute that survives a container change is an armed Execute aimed at
        // something the user is no longer looking at.
        IsExecuteArmed = false;
        ExecuteButtonText = "Execute on Server";
        _armTimeoutCts?.Cancel();
    }

    /// <summary>Call when ConnectedServer or SelectedContainer changes so credential status is updated.</summary>
    public async Task RefreshCredentialStatusAsync()
    {
        CredentialSectionVisible = SelectedContainer != null;
        if (ConnectedServer == null || SelectedContainer == null)
        {
            CredentialExistsOnServer = null;
            CredentialStatusMessage = string.Empty;
            CredentialIsValidSas = false;
            return;
        }

        IsCheckingCredential = true;
        CredentialStatusMessage = "Checking...";
        var server = ConnectedServer!;
        try
        {
            var (exists, isSas) = await _sqlService.CredentialExistsAsync(server, SqlCredentialName);
            CredentialExistsOnServer = exists;
            CredentialIsValidSas = exists && isSas;
            CredentialStatusMessage = exists
                ? (isSas ? "Credential is present and valid (SHARED ACCESS SIGNATURE)." : "Credential exists but is not a SAS credential; restore may fail.")
                : "Credential is not present on this server. Restore will fail unless you create it.";
        }
        catch (Exception ex)
        {
            CredentialExistsOnServer = null;
            CredentialIsValidSas = false;
            CredentialStatusMessage = $"Could not check credential: {ex.Message}";
        }
        finally
        {
            IsCheckingCredential = false;
            CreateCredentialOnServerCommand.NotifyCanExecuteChanged();
        }
    }

    [ObservableProperty]
    private ObservableCollection<FileMoveOption> _fileMoves = [];

    [ObservableProperty]
    private bool _hasFileMoves;

    /// <summary>Logical files from RESTORE FILELISTONLY for accurate WITH MOVE.</summary>
    [ObservableProperty]
    private ObservableCollection<FileMoveOption> _fetchedFileMoves = [];

    /// <summary>
    /// Each row is edited in place in the grid, so the script has to follow the ROW, not just the
    /// collection. Without this, retyping a target path changed what was on screen and nothing
    /// else - and the script is what gets executed.
    /// </summary>
    partial void OnFetchedFileMovesChanged(
        ObservableCollection<FileMoveOption>? oldValue,
        ObservableCollection<FileMoveOption> newValue)
    {
        if (oldValue != null)
            foreach (var move in oldValue) move.PropertyChanged -= OnFileMoveChanged;

        foreach (var move in newValue) move.PropertyChanged += OnFileMoveChanged;
    }

    private void OnFileMoveChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        => UpdateRestoreSummary();

    [ObservableProperty]
    private bool _hasFetchedFileMoves;

    /// <summary>RESTORE HEADERONLY summary for the selected chain.</summary>
    [ObservableProperty]
    private string? _backupMetadataSummary;

    // Script output
    [ObservableProperty]
    private string _generatedScript = string.Empty;

    [ObservableProperty]
    private bool _hasScript;

    // Execution
    [ObservableProperty]
    private bool _isConnectedToServer;

    [ObservableProperty]
    private string _connectedServerName = string.Empty;

    [ObservableProperty]
    private bool _isExecuting;

    [ObservableProperty]
    private bool _executionComplete;

    [ObservableProperty]
    private bool _executionSuccess;

    [ObservableProperty]
    private bool _isExecuteArmed;

    [ObservableProperty]
    private int _executeCountdown;

    [ObservableProperty]
    private string _executeButtonText = "Execute on Server";

    private CancellationTokenSource? _armTimeoutCts;

    // Backup summary
    [ObservableProperty]
    private int _fullCount;

    [ObservableProperty]
    private int _diffCount;

    [ObservableProperty]
    private int _logCount;

    [ObservableProperty]
    private int _setCount;

    #endregion

    public RestoreViewModel(
        IBlobStorageService blobService,
        ISqlServerService sqlService,
        BackupChainBuilder chainBuilder,
        RestoreScriptGenerator scriptGenerator,
        ICredentialStore credentialStore,
        OperationLog? log = null,
        IRestoreHistoryStore? history = null)
    {
        _history = history ?? new RestoreHistoryStore();

        _blobService = blobService;
        _sqlService = sqlService;
        _chainBuilder = chainBuilder;
        _scriptGenerator = scriptGenerator;
        _credentialStore = credentialStore;

        // Defaults to the app's one log. Optional so a test can point it at a temp directory -
        // without it, running the execute path in a test appends real restore lines to the user's
        // actual log file, which is the same class of side effect this whole change is about.
        _log = log ?? App.Log;

        RefreshContainers();
    }

    private readonly OperationLog _log;
    private readonly IRestoreHistoryStore _history;

    public void RefreshContainers()
    {
        var previous = SelectedContainer?.Name;
        var config = _credentialStore.LoadConfig();
        Containers = new ObservableCollection<BlobContainerConfig>(config.BlobContainers);
        foreach (var c in Containers)
        {
            var sas = _credentialStore.GetSasToken(c);
            if (sas != null) c.CacheSasToken(sas);
        }
        if (previous != null)
            SelectedContainer = Containers.FirstOrDefault(c => c.Name == previous);
        if (SelectedContainer == null && Containers.Count > 0)
            SelectedContainer = Containers[0];
    }

    partial void OnSelectedServerNameChanged(string? value)
    {
        if (_allSets.Count == 0) return;

        // Compare the FULL server identity. Matching on the host alone made selecting
        // SQLHOST\PROD also match SQLHOST\TEST, so the database list offered databases that
        // only exist on the other instance.
        var filtered = _allSets.Where(s => s.MatchesServer(value));

        var dbs = filtered
            .Where(s => !string.IsNullOrEmpty(s.DatabaseName))
            .Select(s => s.DatabaseName!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();

        DiscoveredDatabases = new ObservableCollection<string>(dbs);
        if (DiscoveredDatabases.Count > 0)
            SelectedDatabaseName = DiscoveredDatabases[0];
    }

    partial void OnSelectedDatabaseNameChanged(string? value) => RefreshSelectedDatabase(value);

    /// <summary>
    /// Rebuilds the working set and the restore points for the chosen database.
    ///
    /// Called on selection change AND at the end of every load. Relying on the change alone meant
    /// a reload with the same server and database still selected - the natural thing to do after
    /// taking a fresh backup - raised no property change at all, so the sets and the timeline kept
    /// the PREVIOUS scan's contents and the new backup never appeared.
    /// </summary>
    private void RefreshSelectedDatabase(string? value)
    {
        if (value == null || _allSets.Count == 0) return;

        _dbSets = _allSets
            .Where(s => string.Equals(s.DatabaseName, value, StringComparison.OrdinalIgnoreCase))
            .ToList();

        // Full server identity again - this is the filter that decides which sets reach
        // BackupChainBuilder, so a host-only match let one instance's full pair with another
        // instance's differentials and logs.
        if (!string.IsNullOrEmpty(SelectedServerName))
            _dbSets = _dbSets.Where(s => s.MatchesServer(SelectedServerName)).ToList();

        FullCount = _dbSets.Count(s => s.Type == BackupType.Full);
        DiffCount = _dbSets.Count(s => s.Type == BackupType.Differential);
        LogCount = _dbSets.Count(s => s.Type == BackupType.TransactionLog);
        SetCount = _dbSets.Count;

        TargetDatabaseName = value;
        AutoPopulateMoveDefaults();

        ComputeAndDisplayRestorePoints();
    }

    [RelayCommand]
    private void SelectRestorePoint(RestorePoint? point)
    {
        if (point != null)
            SelectedRestorePoint = point;
    }

    [RelayCommand]
    private void ToggleChainDetails()
    {
        ShowChainDetails = !ShowChainDetails;
    }

    partial void OnSelectedRestorePointChanged(RestorePoint? value)
    {
        ShowChainDetails = false;

        // Verification belongs to the chain that was verified. Leaving the results on screen
        // after the selection moves would show a green tick against backups nothing has read.
        ClearVerifyResults();

        if (value == null)
        {
            RestoreChain = null;
            HasValidChain = false;
            ChainFiles.Clear();
            ChainSets.Clear();
            ChainSummary = string.Empty;
            UpdatePointInTimeWindow(null);
            UpdateChainIssues(null);
            FetchedFileMoves = [];
            HasFetchedFileMoves = false;
            BackupMetadataSummary = null;
            FetchLogicalNamesCommand.NotifyCanExecuteChanged();
            InspectBackupMetadataCommand.NotifyCanExecuteChanged();
            CopyFileListOnlyCommandCommand.NotifyCanExecuteChanged();
            CopyHeaderOnlyCommandCommand.NotifyCanExecuteChanged();

            // No selected point means no script. Clearing the chain and leaving the script behind
            // left a complete, authoritative-looking RESTORE on screen with nothing selected above
            // it - which is the stale-script problem in its purest form.
            UpdateRestoreSummary();
            return;
        }

        var chain = _chainBuilder.BuildChainFromRestorePoint(value);
        RestoreChain = chain;
        HasValidChain = true;
        ChainFiles = new ObservableCollection<BackupFileInfo>(chain.AllFiles);
        ChainSets = new ObservableCollection<BackupSet>(chain.AllSets);
        ChainSummary = $"{chain.Summary} | {chain.FileCount} files | Target: {value.Timestamp:yyyy-MM-dd HH:mm:ss}";
        UpdatePointInTimeWindow(value);
        // Structural only at selection time; LSN verification is on demand since it costs a
        // round trip per chain member.
        ChainLsnVerified = false;
        UpdateChainIssues(chain);
        ValidateChainCommand.NotifyCanExecuteChanged();
        VerifyChainCommand.NotifyCanExecuteChanged();
        UpdateRestoreSummary();
        FetchLogicalNamesCommand.NotifyCanExecuteChanged();
        InspectBackupMetadataCommand.NotifyCanExecuteChanged();
        CopyFileListOnlyCommandCommand.NotifyCanExecuteChanged();
        CopyHeaderOnlyCommandCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private async Task LoadBackupsAsync()
    {
        if (SelectedContainer == null)
        {
            SetError("Please select a container first.");
            return;
        }

        var ct = _loadCancellation.Begin();
        IsBusy = true;
        LoadProgressText = "Scanning container...";
        RefreshCancelState();
        ClearStatus();
        try
        {
            // If a database is already chosen - a reload after taking a fresh backup, say - push
            // that down to Azure as a prefix instead of walking the whole container and discarding
            // most of it. Measured on a real 4,440-blob container: about 1,075 ms unscoped versus
            // about 233 ms for one database (#28).
            //
            // The FIRST load has no selection yet and stays a full scan, which it has to be: the
            // server and database lists are built from what it finds.
            var scope = BuildListingScope();
            var progress = new Progress<int>(n => LoadProgressText = $"Scanned {n:N0} blobs...");

            _allBackups = await _blobService.ListBackupFilesAsync(SelectedContainer, scope, progress, ct);
            _allSets = _blobService.GroupIntoBackupSets(_allBackups);

            var servers = _blobService.GetDiscoveredServers(_allBackups);
            DiscoveredServers = new ObservableCollection<string>(servers);

            var dbs = _blobService.GetDiscoveredDatabases(_allBackups);
            DiscoveredDatabases = new ObservableCollection<string>(dbs);
            BackupsLoaded = _allBackups.Count > 0;

            if (DiscoveredServers.Count > 0)
            {
                SelectedServerName = DiscoveredServers[0];
            }
            else if (DiscoveredDatabases.Count > 0)
            {
                SelectedDatabaseName = DiscoveredDatabases[0];
            }
            else
            {
                _dbSets = _allSets;
                FullCount = _dbSets.Count(s => s.Type == BackupType.Full);
                DiffCount = _dbSets.Count(s => s.Type == BackupType.Differential);
                LogCount = _dbSets.Count(s => s.Type == BackupType.TransactionLog);
                SetCount = _dbSets.Count;
                ComputeAndDisplayRestorePoints();
            }


            // Unconditionally, not just when the selection changed - see RefreshSelectedDatabase.
            RefreshSelectedDatabase(SelectedDatabaseName);

            // Only when nothing above went wrong. Selecting the first server or database runs the
            // whole filter-and-compute cascade, which can end in "no valid restore points found" -
            // and painting a success line over that left an empty timeline with the status bar
            // cheerfully reporting how many files had loaded. Found by the first ViewModel test.
            if (!HasError)
                SetStatus($"Loaded {_allBackups.Count} files in {_allSets.Count} backup set(s) across {dbs.Count} database(s).");
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
            RefreshCancelState();
        }
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

    /// <summary>Stops an in-progress backup listing.</summary>
    [RelayCommand]
    private void CancelLoad()
    {
        _loadCancellation.Cancel();
        RefreshCancelState();
        SetStatus("Cancelling...");
    }

    /// <summary>
    /// Every computed restore point, before the view's filters. The timeline and the list both
    /// show a subset of this (#27).
    /// </summary>
    private List<RestorePoint> _allPoints = [];

    private void ComputeAndDisplayRestorePoints()
    {
        _allPoints = _chainBuilder.ComputeRestorePoints(_dbSets);

        // Inventory-level findings: backups that exist but can never be offered. These belong to
        // the discovered set rather than to any one chain, so they are held separately and survive
        // changing the selected restore point.
        //
        // ValidateInventory was written for #62 and then never called, so orphaned differentials,
        // orphaned logs and "no full backup at all" were being computed nowhere and shown nowhere.
        InventoryIssues = new ObservableCollection<ChainIssue>(
            _chainValidator.ValidateInventory(_dbSets)
                .Concat(_chainValidator.ValidateReachability(_dbSets, _allPoints)));
        HasInventoryIssues = InventoryIssues.Count > 0;

        // A fresh load starts wide open. Carrying a previous database's date range over would
        // silently hide points that have nothing to do with it.
        _suppressPointFilterRefresh = true;
        ShowFullPoints = ShowDiffPoints = ShowLogPoints = true;
        PointsFromText = string.Empty;
        PointsToText = string.Empty;
        _suppressPointFilterRefresh = false;

        HasRestorePoints = _allPoints.Count > 0;

        if (_allPoints.Count == 0)
        {
            RestorePoints = [];
            HasVisiblePoints = false;
            RestoreWindowText = string.Empty;
            PointCountText = string.Empty;
            TimelineTicks.Clear();
            TimelineHeight = 50;
            SetError("No valid restore points found. Ensure there is at least one full backup.");
            return;
        }

        ApplyPointFilters(selectLatest: true);
        ClearStatus();
    }

    /// <summary>
    /// Narrows the timeline and the list to the points the filters allow, then lays the timeline
    /// out over just those.
    ///
    /// Laying out over the VISIBLE set rather than all of them is what makes the date range behave
    /// like a zoom: narrow to a two-hour window and those points spread across the whole track
    /// instead of staying bunched in the sliver they occupied before (#27).
    /// </summary>
    private void ApplyPointFilters(bool selectLatest = false)
    {
        var previous = SelectedRestorePoint;
        var visible = _allPoints.Where(PointMatchesFilters).ToList();

        LayOutTimeline(visible);

        RestorePoints = new ObservableCollection<RestorePoint>(visible);
        HasVisiblePoints = visible.Count > 0;

        PointCountText = visible.Count == _allPoints.Count
            ? $"{_allPoints.Count} restore point(s)"
            : $"Showing {visible.Count} of {_allPoints.Count} restore point(s)";

        if (visible.Count == 0)
        {
            RestoreWindowText = string.Empty;
            return;
        }

        // Keep the selection when it survives the filter - changing a type toggle should not throw
        // away the point someone had already chosen.
        SelectedRestorePoint = !selectLatest && previous != null && visible.Contains(previous)
            ? previous
            : visible[^1];
    }

    private bool PointMatchesFilters(RestorePoint p)
    {
        var typeAllowed = p.Type switch
        {
            BackupType.Full => ShowFullPoints,
            BackupType.Differential => ShowDiffPoints,
            BackupType.TransactionLog => ShowLogPoints,
            _ => true
        };
        if (!typeAllowed) return false;

        if (PointsFrom.HasValue && p.Timestamp < PointsFrom.Value) return false;
        if (PointsTo.HasValue && p.Timestamp > PointsTo.Value) return false;

        return true;
    }

    private void LayOutTimeline(List<RestorePoint> points)
    {
        if (points.Count > 1)
        {
            var minTicks = points[0].Timestamp.Ticks;
            var maxTicks = points[^1].Timestamp.Ticks;
            var range = (double)(maxTicks - minTicks);
            foreach (var p in points)
                p.TimelinePosition = range > 0 ? (p.Timestamp.Ticks - minTicks) / range : 0.5;
        }
        else if (points.Count == 1)
        {
            points[0].TimelinePosition = 0.5;
        }

        // Vertical stacking, so points too close together to draw side by side are not drawn on
        // top of each other.
        ComputeRows(points);

        if (points.Count == 0)
        {
            TimelineTicks.Clear();
            TimelineHeight = 50;
            return;
        }

        var first = points[0].Timestamp;
        var last = points[^1].Timestamp;
        RestoreWindowText = $"{first:yyyy-MM-dd HH:mm} to {last:yyyy-MM-dd HH:mm}";
        TimelineStartText = $"{first:yyyy-MM-dd HH:mm}";

        TimelineTicks = new ObservableCollection<TimelineTick>(ComputeTicks(first, last));

        int maxRow = points.Max(p => p.Row);
        TimelineHeight = Math.Max(50, 30 + (maxRow + 1) * 18);
    }

    /// <summary>Set while the filters are being reset in bulk, so each one does not re-filter.</summary>
    private bool _suppressPointFilterRefresh;

    private void OnPointFilterChanged()
    {
        if (_suppressPointFilterRefresh || _allPoints.Count == 0) return;
        ApplyPointFilters();
    }

    partial void OnShowFullPointsChanged(bool value) => OnPointFilterChanged();
    partial void OnShowDiffPointsChanged(bool value) => OnPointFilterChanged();
    partial void OnShowLogPointsChanged(bool value) => OnPointFilterChanged();

    partial void OnPointsFromTextChanged(string value)
    {
        PointsFrom = ParseFilterDate(value);
        OnPointFilterChanged();
    }

    partial void OnPointsToTextChanged(string value)
    {
        PointsTo = ParseFilterDate(value);
        OnPointFilterChanged();
    }

    /// <summary>
    /// Invariant, and forgiving about how much of a timestamp was typed - "2026-01-10" is a
    /// perfectly reasonable thing to enter into a date box. Unparsable means no bound rather than
    /// an error, so half-typed input does not blank the timeline mid-keystroke.
    /// </summary>
    private static DateTime? ParseFilterDate(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        return DateTime.TryParse(
            text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? parsed
            : null;
    }

    /// <summary>Back to every point, without reloading anything from the container.</summary>
    [RelayCommand]
    private void ResetPointFilters()
    {
        _suppressPointFilterRefresh = true;
        ShowFullPoints = ShowDiffPoints = ShowLogPoints = true;
        PointsFromText = string.Empty;
        PointsToText = string.Empty;
        _suppressPointFilterRefresh = false;

        if (_allPoints.Count > 0) ApplyPointFilters();
    }

    /// <summary>
    /// Narrows the range to the day the selected point falls on. A week of 15-minute logs is
    /// roughly 670 points; picking one precisely means getting to a day first, and doing that by
    /// typing two timestamps is a chore when the answer is nearly always "the day it broke".
    /// </summary>
    [RelayCommand]
    private void ZoomToSelectedDay()
    {
        if (SelectedRestorePoint == null) return;

        var day = SelectedRestorePoint.Timestamp.Date;

        _suppressPointFilterRefresh = true;
        PointsFromText = day.ToString("yyyy-MM-dd HH:mm:ss");
        PointsToText = day.AddDays(1).AddSeconds(-1).ToString("yyyy-MM-dd HH:mm:ss");
        _suppressPointFilterRefresh = false;

        ApplyPointFilters();
    }

    private static void ComputeRows(List<RestorePoint> points)
    {
        const double minSeparation = 0.025;
        var rows = new List<List<double>>();

        foreach (var p in points)
        {
            bool placed = false;
            for (int row = 0; row < rows.Count; row++)
            {
                bool overlaps = rows[row].Any(pos => Math.Abs(pos - p.TimelinePosition) < minSeparation);
                if (!overlaps)
                {
                    p.Row = row;
                    rows[row].Add(p.TimelinePosition);
                    placed = true;
                    break;
                }
            }
            if (!placed)
            {
                p.Row = rows.Count;
                rows.Add([p.TimelinePosition]);
            }
        }
    }

    private static List<TimelineTick> ComputeTicks(DateTime first, DateTime last)
    {
        var ticks = new List<TimelineTick>();
        var range = last - first;
        var totalTicks = (double)(last.Ticks - first.Ticks);
        if (totalTicks <= 0) return ticks;

        // Choose interval based on range
        TimeSpan interval;
        string format;
        if (range.TotalDays > 60)
        {
            interval = TimeSpan.FromDays(14);
            format = "MMM dd";
        }
        else if (range.TotalDays > 14)
        {
            interval = TimeSpan.FromDays(7);
            format = "MMM dd";
        }
        else if (range.TotalDays > 3)
        {
            interval = TimeSpan.FromDays(1);
            format = "MMM dd";
        }
        else if (range.TotalHours > 12)
        {
            interval = TimeSpan.FromHours(6);
            format = "HH:mm";
        }
        else
        {
            interval = TimeSpan.FromHours(1);
            format = "HH:mm";
        }

        // Round start to the next clean interval boundary
        var cursor = RoundUp(first, interval);

        while (cursor < last)
        {
            double pos = (cursor.Ticks - first.Ticks) / totalTicks;
            if (pos >= 0.02 && pos <= 0.98)
            {
                ticks.Add(new TimelineTick { Position = pos, Label = cursor.ToString(format) });
            }
            cursor += interval;
        }

        return ticks;
    }

    private static DateTime RoundUp(DateTime dt, TimeSpan interval)
    {
        if (interval.TotalDays >= 1)
        {
            var next = dt.Date.AddDays(1);
            while (next <= dt) next = next.AddDays((int)interval.TotalDays);
            return next;
        }
        var ticks = (dt.Ticks + interval.Ticks - 1) / interval.Ticks * interval.Ticks;
        return new DateTime(ticks);
    }

    private void AutoPopulateMoveDefaults()
    {
        if (string.IsNullOrWhiteSpace(TargetDatabaseName)) return;

        if (!string.IsNullOrEmpty(MoveDataFilePath))
        {
            var dataDir = Path.GetDirectoryName(MoveDataFilePath) ?? string.Empty;
            var logDir = Path.GetDirectoryName(MoveLogFilePath) ?? dataDir;
            MoveDataFilePath = Path.Combine(dataDir, $"{TargetDatabaseName}.mdf");
            MoveLogFilePath = Path.Combine(logDir, $"{TargetDatabaseName}_log.ldf");
        }
    }

    [RelayCommand]
    private async Task FetchDefaultPathsAsync()
    {
        if (ConnectedServer == null)
        {
            var fallbackDir = @"C:\Program Files\Microsoft SQL Server\MSSQL16.MSSQLSERVER\MSSQL\DATA";
            var dbName = string.IsNullOrWhiteSpace(TargetDatabaseName) ? "DatabaseName" : TargetDatabaseName;
            MoveDataFilePath = Path.Combine(fallbackDir, $"{dbName}.mdf");
            MoveLogFilePath = Path.Combine(fallbackDir, $"{dbName}_log.ldf");
            PathsFromServer = false;
            PathSourceText = "Not connected — using generic placeholder paths. Connect to a SQL Server to auto-detect the correct default directories.";
            return;
        }

        IsFetchingPaths = true;
        PathSourceText = $"Querying {ConnectedServer.ServerName} for default paths...";
        try
        {
            var (dataPath, logPath) = await _sqlService.GetDefaultPathsAsync(ConnectedServer);
            var dbName = string.IsNullOrWhiteSpace(TargetDatabaseName) ? "DatabaseName" : TargetDatabaseName;

            if (!string.IsNullOrEmpty(dataPath))
                MoveDataFilePath = Path.Combine(dataPath, $"{dbName}.mdf");

            if (!string.IsNullOrEmpty(logPath))
                MoveLogFilePath = Path.Combine(logPath, $"{dbName}_log.ldf");
            else if (!string.IsNullOrEmpty(dataPath))
                MoveLogFilePath = Path.Combine(dataPath, $"{dbName}_log.ldf");

            PathsFromServer = true;
            PathSourceText = $"Paths detected from {ConnectedServer.ServerName}. You can still override them.";
        }
        catch (Exception ex)
        {
            var fallbackDir = @"C:\Program Files\Microsoft SQL Server\MSSQL16.MSSQLSERVER\MSSQL\DATA";
            var dbName = string.IsNullOrWhiteSpace(TargetDatabaseName) ? "DatabaseName" : TargetDatabaseName;
            MoveDataFilePath = Path.Combine(fallbackDir, $"{dbName}.mdf");
            MoveLogFilePath = Path.Combine(fallbackDir, $"{dbName}_log.ldf");
            PathsFromServer = false;
            PathSourceText = $"Could not fetch paths: {ex.Message}. Using generic placeholders.";
        }
        finally
        {
            IsFetchingPaths = false;
        }
    }

    private void UpdateRestoreSummary()
    {
        // Every option change already funnels through here, so it is also where the script is kept
        // in step. It used to be built only when Generate Script was pressed, which meant the
        // script on screen could quietly disagree with the settings above it - and the script is
        // the thing people read before running a restore against production.
        RegenerateScript();

        if (RestoreChain == null || string.IsNullOrWhiteSpace(TargetDatabaseName))
        {
            RestoreSummaryText = string.Empty;
            return;
        }

        var parts = new List<string>();
        parts.Add($"Restore '{SelectedDatabaseName}' as '{TargetDatabaseName}'");
        parts.Add($"using {RestoreChain.Summary} ({RestoreChain.FileCount} files total).");

        if (EffectiveStopAt is DateTime stopAt)
            parts.Add($"Stop at {stopAt:yyyy-MM-dd HH:mm:ss} (point-in-time recovery).");
        else if (SelectedRestorePoint != null && SelectedRestorePoint.Type == BackupType.TransactionLog)
            parts.Add($"Restore to end of log backup at {SelectedRestorePoint.Timestamp:yyyy-MM-dd HH:mm:ss}.");

        var optionsList = new List<string>();

        if (WithReplace)
            optionsList.Add("overwrite existing database (WITH REPLACE)");
        if (DisconnectSessions)
            optionsList.Add("disconnect active sessions");
        if (UseWithMove)
            optionsList.Add($"relocate data files (WITH MOVE)");

        var recoveryDesc = RecoveryMode switch
        {
            RecoveryMode.Recovery => "brought online for use (RECOVERY)",
            RecoveryMode.NoRecovery => "left in restoring state (NORECOVERY)",
            RecoveryMode.Standby => "set to read-only standby mode (STANDBY)",
            _ => "recovered"
        };
        optionsList.Add($"database will be {recoveryDesc}");

        if (KeepReplication) optionsList.Add("preserve replication settings");
        if (EnableBroker) optionsList.Add("enable Service Broker");
        if (NewBroker) optionsList.Add("create new Service Broker ID");

        if (optionsList.Count > 0)
            parts.Add("Options: " + string.Join("; ", optionsList) + ".");

        RestoreSummaryText = string.Join(" ", parts);
    }

    /// <summary>
    /// Recomputes the STOPAT window for the selected restore point. The window itself is
    /// <see cref="BackupChain.StopAtWindow"/>; this only projects it onto the UI state.
    /// </summary>
    private void UpdatePointInTimeWindow(RestorePoint? point)
    {
        var window = point?.Type == BackupType.TransactionLog
            ? RestoreChain?.StopAtWindow
            : null;

        if (window == null)
        {
            CanUsePointInTime = false;
            UsePointInTime = false;
            StopAtEarliest = null;
            StopAtLatest = null;
            StopAtText = string.Empty;
            StopAtDateTime = null;
            PointInTimeMessage = string.Empty;
            HasPointInTimeError = false;
            return;
        }

        CanUsePointInTime = true;
        StopAtEarliest = window.Value.Earliest;
        StopAtLatest = window.Value.Latest;
        UsePointInTime = false;
        StopAtText = window.Value.Latest.ToString(StopAtFormat);
        ValidatePointInTime();
    }

    /// <summary>
    /// Reads RESTORE HEADERONLY for every member of the selected chain and validates the LSN
    /// relationships - the authoritative check that the chain actually restores, as opposed to
    /// merely looking plausible by filename and timestamp.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanValidateChain))]
    private async Task ValidateChainAsync()
    {
        if (RestoreChain == null || ConnectedServer == null) return;

        IsValidatingChain = true;
        ClearStatus();
        try
        {
            var headers = new List<ChainHeader>();
            foreach (var set in RestoreChain.AllSets)
            {
                var urls = set.Files.Select(f => BlobUrlEncoder.Encode(f.BlobUrl)).ToList();
                try
                {
                    var header = await _sqlService.RestoreHeaderOnlyMultiAsync(ConnectedServer, urls);
                    headers.Add(new ChainHeader(set, header));
                }
                catch (Exception)
                {
                    // One unreadable member must not abort the whole validation - the validator
                    // reports it and carries on checking everything else.
                    headers.Add(new ChainHeader(set, null));
                }
            }

            UpdateChainIssues(RestoreChain, headers);
            ChainLsnVerified = true;

            SetStatus(HasChainIssues
                ? $"Chain validation found problems - see the panel above."
                : $"Chain validated: {headers.Count} backup(s) read, LSN chain is intact.");
        }
        catch (Exception ex)
        {
            SetError($"Chain validation failed: {ex.Message}");
        }
        finally
        {
            IsValidatingChain = false;
        }
    }

    private bool CanValidateChain() =>
        IsConnectedToServer && RestoreChain != null && !IsValidatingChain;

    /// <summary>
    /// Runs RESTORE VERIFYONLY over every set in the chain: does each backup actually read back,
    /// before an hour is spent finding out that it does not.
    ///
    /// This asks a different question from Validate chain. That one reads headers and checks the
    /// LSNs line up - whether these backups belong together. This one reads the whole backup and
    /// checks it is intact - whether they are usable at all. A truncated or half-uploaded blob
    /// passes the first and fails this.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanVerifyChain))]
    private async Task VerifyChainAsync()
    {
        if (RestoreChain == null || ConnectedServer == null) return;

        IsVerifyingChain = true;
        ClearStatus();
        ChainVerifyResults.Clear();
        HasVerifyResults = false;

        try
        {
            var sets = RestoreChain.AllSets;
            for (int i = 0; i < sets.Count; i++)
            {
                var set = sets[i];
                SetStatus($"Verifying {i + 1} of {sets.Count}: {set.TypeDisplay} {set.SetId}...");

                var urls = set.Files.Select(f => BlobUrlEncoder.Encode(f.BlobUrl)).ToList();
                var result = await _sqlService.RestoreVerifyOnlyAsync(
                    ConnectedServer, urls, WithChecksum);

                ChainVerifyResults.Add(new ChainVerifyResult { Set = set, Result = result });
                HasVerifyResults = true;
            }

            var failed = ChainVerifyResults.Count(r => !r.IsValid);
            HasVerifyFailures = failed > 0;

            SetStatus(failed > 0
                ? $"{failed} of {ChainVerifyResults.Count} backup(s) failed verification - see below. Do not rely on this chain."
                : $"All {ChainVerifyResults.Count} backup(s) in the chain verified.");
        }
        catch (OperationCanceledException)
        {
            SetStatus("Verification cancelled.");
        }
        catch (Exception ex)
        {
            SetError($"Verification could not run: {ex.Message}");
        }
        finally
        {
            IsVerifyingChain = false;
        }
    }

    private bool CanVerifyChain() =>
        IsConnectedToServer && RestoreChain != null && !IsVerifyingChain && !IsExecuting;

    private void ClearVerifyResults()
    {
        ChainVerifyResults.Clear();
        HasVerifyResults = false;
        HasVerifyFailures = false;
    }

    private void UpdateChainIssues(BackupChain? chain, IReadOnlyList<ChainHeader>? headers = null)
    {
        var issues = _chainValidator.Validate(chain);
        if (headers != null)
            issues.AddRange(_chainValidator.ValidateLsnChain(chain, headers));

        ChainIssues = new ObservableCollection<ChainIssue>(issues);
        HasChainIssues = issues.Count > 0;
        HasChainErrors = issues.Any(i => i.IsError);

        if (issues.Count == 0)
        {
            ChainIssueSummary = string.Empty;
            return;
        }

        var errors = issues.Count(i => i.IsError);
        var warnings = issues.Count - errors;

        ChainIssueSummary = errors > 0 && warnings > 0
            ? $"{errors} problem(s) will prevent this restore, and {warnings} warning(s)"
            : errors > 0
                ? $"{errors} problem(s) will prevent this restore"
                : $"{warnings} warning(s) about this chain";
    }

    private const string StopAtFormat = "yyyy-MM-dd HH:mm:ss";

    private static readonly string[] StopAtAcceptedFormats =
    [
        "yyyy-MM-dd HH:mm:ss",
        "yyyy-MM-ddTHH:mm:ss",
        "yyyy-MM-dd HH:mm",
        "yyyy-MM-ddTHH:mm"
    ];

    private void ValidatePointInTime()
    {
        if (!CanUsePointInTime)
        {
            StopAtDateTime = null;
            PointInTimeMessage = string.Empty;
            HasPointInTimeError = false;
            return;
        }

        if (!DateTime.TryParseExact(
                StopAtText?.Trim(), StopAtAcceptedFormats,
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            StopAtDateTime = null;
            HasPointInTimeError = UsePointInTime;
            PointInTimeMessage = $"Enter a time as {StopAtFormat}.";
            UpdateRestoreSummary();
            return;
        }

        // Exclusive lower bound: at exactly the previous set's time nothing from this log has
        // been applied yet, which is the earlier restore point, not this one.
        if (parsed <= StopAtEarliest)
        {
            StopAtDateTime = null;
            HasPointInTimeError = UsePointInTime;
            PointInTimeMessage =
                $"Must be after {StopAtEarliest:yyyy-MM-dd HH:mm:ss} — to stop earlier, " +
                "select an earlier restore point on the timeline.";
            UpdateRestoreSummary();
            return;
        }

        if (parsed > StopAtLatest)
        {
            StopAtDateTime = null;
            HasPointInTimeError = UsePointInTime;
            PointInTimeMessage =
                $"Must be at or before {StopAtLatest:yyyy-MM-dd HH:mm:ss} — to stop later, " +
                "select a later restore point on the timeline.";
            UpdateRestoreSummary();
            return;
        }

        StopAtDateTime = parsed;
        HasPointInTimeError = false;
        PointInTimeMessage = UsePointInTime
            ? $"Recovery will stop at {parsed:yyyy-MM-dd HH:mm:ss}; later transactions in this log are discarded."
            : $"Valid range: after {StopAtEarliest:yyyy-MM-dd HH:mm:ss} up to {StopAtLatest:yyyy-MM-dd HH:mm:ss}.";
        UpdateRestoreSummary();
    }

    /// <summary>The STOPAT value to generate, or null to restore the whole log chain.</summary>
    private DateTime? EffectiveStopAt =>
        CanUsePointInTime && UsePointInTime && !HasPointInTimeError ? StopAtDateTime : null;

    partial void OnStopAtTextChanged(string value) => ValidatePointInTime();

    partial void OnUsePointInTimeChanged(bool value)
    {
        ValidatePointInTime();
        UpdateRestoreSummary();
    }

    partial void OnWithReplaceChanged(bool value) => UpdateRestoreSummary();
    partial void OnDisconnectSessionsChanged(bool value) => UpdateRestoreSummary();
    partial void OnRecoveryModeChanged(RecoveryMode oldValue, RecoveryMode newValue)
    {
        OnPropertyChanged(nameof(IsStandbyMode));
        UpdateRestoreSummary();
    }
    partial void OnKeepReplicationChanged(bool value) => UpdateRestoreSummary();
    partial void OnEnableBrokerChanged(bool value) => UpdateRestoreSummary();
    partial void OnNewBrokerChanged(bool value) => UpdateRestoreSummary();
    partial void OnWithChecksumChanged(bool value) => UpdateRestoreSummary();
    partial void OnContinueAfterErrorChanged(bool value) => UpdateRestoreSummary();

    // These four are typed into text boxes rather than ticked, and every one of them was missing
    // its handler - so the script on screen did not change when they did. That is the exact
    // failure RegenerateScript exists to prevent: typing a new data-file path, watching the box
    // update, and running a script that still had the old one - or, for WITH MOVE, no MOVE clause
    // at all, sending the restore to the file paths baked into the backup.
    partial void OnMoveDataFilePathChanged(string value) => UpdateRestoreSummary();
    partial void OnMoveLogFilePathChanged(string value) => UpdateRestoreSummary();
    partial void OnStandbyFilePathChanged(string value) => UpdateRestoreSummary();
    partial void OnStatsPercentChanged(int value) => UpdateRestoreSummary();

    // Verification opens its own connection and reads whole backups. Letting it start while a
    // restore is running would put a second heavy reader on the same server at the worst moment.
    partial void OnIsExecutingChanged(bool value) => VerifyChainCommand.NotifyCanExecuteChanged();
    partial void OnTargetDatabaseNameChanged(string value) => UpdateRestoreSummary();

    partial void OnIsConnectedToServerChanged(bool value)
    {
        var chips = value && ConnectedServer != null
            ? ConnectedServer.TagChips
            : [];
        ConnectedServerTags = new ObservableCollection<TagChip>(chips);
        HasConnectedServerTags = ConnectedServerTags.Count > 0;

        ValidateChainCommand.NotifyCanExecuteChanged();
        VerifyChainCommand.NotifyCanExecuteChanged();
        FetchLogicalNamesCommand.NotifyCanExecuteChanged();
        InspectBackupMetadataCommand.NotifyCanExecuteChanged();
        CopyFileListOnlyCommandCommand.NotifyCanExecuteChanged();
        CopyHeaderOnlyCommandCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// STANDBY needs somewhere to put its undo file. Blank produced <c>STANDBY = ''</c>, which
    /// SQL Server rejects - as the LAST statement of the chain, so it failed after SET SINGLE_USER
    /// and WITH REPLACE had already dropped and overwritten the target, leaving it in RESTORING
    /// and single-user with nothing to show for it.
    /// </summary>
    private bool HasStandbyFileIfNeeded =>
        RecoveryMode != RecoveryMode.Standby || !string.IsNullOrWhiteSpace(StandbyFilePath);

    /// <summary>
    /// Explicit Generate. Same work as the live rebuild, but it says why nothing was produced -
    /// the live path stays silent because reporting an error on every keystroke would be noise.
    /// </summary>
    [RelayCommand]
    private void GenerateScript()
    {
        if (RestoreChain == null)
        {
            SetError("No valid restore chain. Load backups and select a restore point first.");
            return;
        }

        if (string.IsNullOrWhiteSpace(TargetDatabaseName))
        {
            SetError("Please enter a target database name.");
            return;
        }

        // Refuse to silently fall back to a full-chain restore when the user asked to stop at a
        // time we could not use - that would replay exactly the transactions they meant to skip.
        if (UsePointInTime && CanUsePointInTime && EffectiveStopAt == null)
        {
            SetError($"Point-in-time target is not valid. {PointInTimeMessage}");
            return;
        }

        if (!HasStandbyFileIfNeeded)
        {
            SetError("STANDBY needs an undo file path. Without one the script would end in " +
                     "STANDBY = '', which fails after the database has already been overwritten.");
            return;
        }

        RegenerateScript();
        if (HasScript) SetStatus("Script generated successfully.");
    }

    /// <summary>
    /// Rebuilds the script from the current settings, silently.
    ///
    /// Called from UpdateRestoreSummary, so every option change keeps the script in step. It used
    /// to be built only when Generate Script was pressed, which meant the script on screen could
    /// quietly disagree with the settings above it - and that script is what people read before
    /// running a restore against production.
    ///
    /// When the settings cannot produce a valid script the script is cleared rather than left
    /// stale. A stale script is worse than none: it looks authoritative and is not.
    /// </summary>
    private void RegenerateScript()
    {
        // Leave the script alone mid-restore - it is the record of what is actually running.
        if (IsExecuting) return;

        if (RestoreChain == null
            || string.IsNullOrWhiteSpace(TargetDatabaseName)
            || (UsePointInTime && CanUsePointInTime && EffectiveStopAt == null)
            || !HasStandbyFileIfNeeded)
        {
            GeneratedScript = string.Empty;
            HasScript = false;
            return;
        }

        var fileMoves = new List<FileMoveOption>();
        if (UseWithMove)
        {
            if (HasFetchedFileMoves && FetchedFileMoves.Count > 0)
            {
                foreach (var m in FetchedFileMoves)
                {
                    if (!string.IsNullOrWhiteSpace(m.NewPhysicalName))
                        fileMoves.Add(new FileMoveOption { LogicalName = m.LogicalName, PhysicalName = m.PhysicalName, NewPhysicalName = m.NewPhysicalName, Type = m.Type });
                }
            }
            else if (!string.IsNullOrWhiteSpace(MoveDataFilePath))
            {
                var sourceDbName = SelectedDatabaseName ?? TargetDatabaseName;
                fileMoves.Add(new FileMoveOption { LogicalName = sourceDbName, PhysicalName = string.Empty, NewPhysicalName = MoveDataFilePath, Type = "ROWS" });
                fileMoves.Add(new FileMoveOption { LogicalName = sourceDbName + "_log", PhysicalName = string.Empty, NewPhysicalName = MoveLogFilePath, Type = "LOG" });
            }
        }

        var options = new RestoreOptions
        {
            TargetDatabaseName = TargetDatabaseName,
            WithReplace = WithReplace,
            RecoveryMode = RecoveryMode,
            StandbyFilePath = string.IsNullOrWhiteSpace(StandbyFilePath) ? null : StandbyFilePath,
            DisconnectSessions = DisconnectSessions,
            StatsPercent = StatsPercent,
            StopAt = EffectiveStopAt,
            KeepReplication = KeepReplication,
            EnableBroker = EnableBroker,
            NewBroker = NewBroker,
            WithChecksum = WithChecksum,
            ContinueAfterError = ContinueAfterError,
            FileMoves = fileMoves
        };

        GeneratedScript = _scriptGenerator.Generate(RestoreChain, options);
        HasScript = true;
    }

    /// <summary>Create or update the blob credential on the connected server (optional; not included in generated script).</summary>
    [RelayCommand(CanExecute = nameof(CanCreateCredential))]
    private async Task CreateCredentialOnServerAsync()
    {
        if (ConnectedServer == null || SelectedContainer == null) return;

        var sasToken = _credentialStore.GetSasToken(SelectedContainer);
        if (string.IsNullOrEmpty(sasToken))
        {
            SetError("No SAS token stored for this container. Add or refresh the token in Blob Storage config.");
            return;
        }

        try
        {
            var change = await _sqlService.EnsureCredentialExistsAsync(
                ConnectedServer, SqlCredentialName, SelectedContainer.ContainerUrl, sasToken);
            await RefreshCredentialStatusAsync();
            SetStatus(change == CredentialChange.Created
                ? "Credential created on server."
                : "Credential updated on server with the stored SAS token.");
        }
        catch (Exception ex)
        {
            SetError($"Failed to create credential: {ex.Message}");
        }
    }

    // Deliberately available even when the credential is present and valid. SQL Server will not
    // hand back a credential's secret, so "present and SAS" says nothing about whether the token
    // inside it still works - a SAS that was rotated or has expired looks identical from here.
    // Since Execute no longer rewrites the credential on every run, this button is the only way
    // to push a fresh token, and hiding it left users with no route at all.
    private bool CanCreateCredential() => IsConnectedToServer && SelectedContainer != null;

    [RelayCommand]
    private void CopyScript()
        => TryCopyToClipboard(GeneratedScript, "Script copied to clipboard.");

    [RelayCommand]
    private void CopyPathHttps(BackupFileInfo? file)
    {
        if (file == null || SelectedContainer == null) return;
        TryCopyToClipboard(
            BlobStorageService.BuildBlobUrl(SelectedContainer, file.BlobName),
            "HTTPS path copied to clipboard (no SAS token).");
    }

    [RelayCommand]
    private void CopyPathContainer(BackupFileInfo? file)
    {
        if (file == null || SelectedContainer == null) return;
        var containerName = SelectedContainer.ContainerName ?? "container";
        TryCopyToClipboard($"{containerName}/{file.BlobName}", "Container path copied to clipboard.");
    }

    [RelayCommand(CanExecute = nameof(CanFetchLogicalNames))]
    private async Task FetchLogicalNamesAsync()
    {
        if (RestoreChain == null || ConnectedServer == null || SelectedContainer == null) return;

        IsBusy = true;
        BackupMetadataSummary = null;
        try
        {
            // Use URL without SAS and omit WITH CREDENTIAL. Encode path so spaces/special chars (e.g. in folder names) are valid.
            var urls = RestoreChain.FullSet.Files.Select(f => BlobUrlEncoder.Encode(f.BlobUrl)).ToList();
            var list = await _sqlService.RestoreFileListOnlyAsync(ConnectedServer, urls);

            var dataDir = Path.GetDirectoryName(MoveDataFilePath) ?? @"C:\SQL\Data";
            var logDir = Path.GetDirectoryName(MoveLogFilePath) ?? dataDir;
            var dbName = string.IsNullOrWhiteSpace(TargetDatabaseName) ? "Database" : TargetDatabaseName;

            int rowsIndex = 0;
            foreach (var m in list)
            {
                m.NewPhysicalName = (m.Type?.ToUpperInvariant() ?? "") switch
                {
                    "L" or "LOG" => Path.Combine(logDir, $"{dbName}_log.ldf"),
                    _ => rowsIndex++ == 0 ? Path.Combine(dataDir, $"{dbName}.mdf") : Path.Combine(dataDir, $"{dbName}_{rowsIndex}.ndf")
                };
            }

            FetchedFileMoves = new ObservableCollection<FileMoveOption>(list);
            HasFetchedFileMoves = FetchedFileMoves.Count > 0;
            SetStatus($"RESTORE FILELISTONLY: {FetchedFileMoves.Count} logical file(s). Edit paths above and use WITH MOVE for correct restore.");
        }
        catch (Exception ex)
        {
            var urlList = RestoreChain!.FullSet.Files.Select(f => BlobUrlEncoder.Encode(f.BlobUrl)).ToList();
            var urlPreview = urlList.Count > 0 ? urlList[0] : "(no URL)";
            if (urlList.Count > 1) urlPreview += $" (+{urlList.Count - 1} more)";
            SetError($"RESTORE FILELISTONLY failed: {ex.Message}. URL used: {urlPreview}. Run the same RESTORE FILELISTONLY in SSMS to confirm credential/network.");
            FetchedFileMoves = [];
            HasFetchedFileMoves = false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanFetchLogicalNames() =>
        IsConnectedToServer && RestoreChain != null && SelectedContainer != null && RestoreChain.FullSet.Files.Count > 0;

    [RelayCommand(CanExecute = nameof(CanInspectMetadata))]
    private async Task InspectBackupMetadataAsync()
    {
        if (RestoreChain == null || ConnectedServer == null || SelectedContainer == null) return;

        IsBusy = true;
        try
        {
            // Use URL without SAS and omit WITH CREDENTIAL. Encode path so spaces/special chars (e.g. in folder names) are valid.
            var urls = RestoreChain.FullSet.Files.Select(f => BlobUrlEncoder.Encode(f.BlobUrl)).ToList();
            var header = await _sqlService.RestoreHeaderOnlyMultiAsync(ConnectedServer, urls);

            if (header == null)
            {
                BackupMetadataSummary = "No header returned.";
                return;
            }

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"Database: {header.DatabaseName ?? "(null)"}");
            sb.AppendLine($"Backup type: {header.TypeDisplay}");
            sb.AppendLine($"Start: {(header.BackupStartDate.HasValue ? header.BackupStartDate.Value.ToString("yyyy-MM-dd HH:mm:ss") : "(null)")}");
            sb.AppendLine($"Finish: {(header.BackupFinishDate.HasValue ? header.BackupFinishDate.Value.ToString("yyyy-MM-dd HH:mm:ss") : "(null)")}");
            sb.AppendLine($"First LSN: {header.FirstLsn?.ToString() ?? "(null)"}");
            sb.AppendLine($"Last LSN: {header.LastLsn?.ToString() ?? "(null)"}");
            sb.AppendLine($"Database backup LSN: {header.DatabaseBackupLsn?.ToString() ?? "(null)"}");
            BackupMetadataSummary = sb.ToString();
            SetStatus("RESTORE HEADERONLY completed. See metadata summary below.");
        }
        catch (Exception ex)
        {
            var urlList = RestoreChain!.FullSet.Files.Select(f => BlobUrlEncoder.Encode(f.BlobUrl)).ToList();
            var urlPreview = urlList.Count > 0 ? urlList[0] : "(no URL)";
            if (urlList.Count > 1) urlPreview += $" (+{urlList.Count - 1} more)";
            SetError($"RESTORE HEADERONLY failed: {ex.Message}. URL used: {urlPreview}. Run the same RESTORE HEADERONLY in SSMS to confirm credential/network.");
            BackupMetadataSummary = null;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanInspectMetadata() =>
        IsConnectedToServer && RestoreChain != null && SelectedContainer != null && RestoreChain.FullSet.Files.Count > 0;

    /// <summary>Builds the exact T-SQL for RESTORE FILELISTONLY (encoded URLs, no WITH CREDENTIAL) for pasting into SSMS.</summary>
    [RelayCommand(CanExecute = nameof(CanFetchLogicalNames))]
    private void CopyFileListOnlyCommand()
    {
        if (RestoreChain == null || RestoreChain.FullSet.Files.Count == 0) return;
        var urls = RestoreChain.FullSet.Files.Select(f => BlobUrlEncoder.Encode(f.BlobUrl)).ToList();
        var urlClauses = string.Join(", ", urls.Select(u => $"URL = N'{TSql.EscapeLiteral(u)}'"));
        var sql = $"RESTORE FILELISTONLY FROM {urlClauses}";
        TryCopyToClipboard(sql, "RESTORE FILELISTONLY command copied to clipboard. Paste into SSMS to run.");
    }

    /// <summary>Builds the exact T-SQL for RESTORE HEADERONLY (encoded URLs, no WITH CREDENTIAL) for pasting into SSMS.</summary>
    [RelayCommand(CanExecute = nameof(CanInspectMetadata))]
    private void CopyHeaderOnlyCommand()
    {
        if (RestoreChain == null || RestoreChain.FullSet.Files.Count == 0) return;
        var urls = RestoreChain.FullSet.Files.Select(f => BlobUrlEncoder.Encode(f.BlobUrl)).ToList();
        var urlClauses = string.Join(", ", urls.Select(u => $"URL = N'{TSql.EscapeLiteral(u)}'"));
        var sql = $"RESTORE HEADERONLY FROM {urlClauses}";
        TryCopyToClipboard(sql, "RESTORE HEADERONLY command copied to clipboard. Paste into SSMS to run.");
    }

    [RelayCommand]
    private void SaveScript()
    {
        if (string.IsNullOrEmpty(GeneratedScript)) return;

        var dialog = new SaveFileDialog
        {
            Filter = "SQL Files (*.sql)|*.sql|All Files (*.*)|*.*",
            DefaultExt = ".sql",
            FileName = $"restore_{TargetDatabaseName}_{DateTime.Now:yyyyMMdd_HHmmss}.sql"
        };

        if (dialog.ShowDialog() != true) return;

        try
        {
            File.WriteAllText(dialog.FileName, GeneratedScript);
            SetStatus($"Script saved to {dialog.FileName}");
        }
        catch (Exception ex)
        {
            // Read-only location, full disk, or a path the database name made invalid. Any of
            // those used to take the whole app down from inside a synchronous command (#13).
            SetError($"Could not save the script: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task ExecuteScriptAsync()
    {
        if (string.IsNullOrEmpty(GeneratedScript) || !IsConnectedToServer)
            return;

        // Rebuild before arming, so what runs is what the options currently say - not whatever the
        // last change handler happened to leave behind. Every option is supposed to keep the script
        // in step on its own; this is the backstop that makes forgetting one a stale display rather
        // than a restore that does something different from what it shows.
        RegenerateScript();
        if (string.IsNullOrEmpty(GeneratedScript))
        {
            SetError("The current options do not produce a script. Check the settings above.");
            IsExecuteArmed = false;
            ExecuteButtonText = "Execute on Server";
            return;
        }

        // Refuse to run when the chain cannot possibly restore. This is the last safe moment:
        // once execution starts, WITH REPLACE drops the target database, and a chain that fails
        // partway leaves nothing usable behind. Failing here costs the user a few seconds;
        // failing mid-restore costs them the database they were restoring over.
        //
        // Checked on the confirm press as well as when arming: validation can finish during the
        // five-second countdown and find an error that was not known when the button was armed.
        if (HasChainErrors)
        {
            var first = ChainIssues.First(i => i.IsError);
            SetError($"Cannot execute: {first.Title}. {first.Detail}");
            IsExecuteArmed = false;
            ExecuteButtonText = "Execute on Server";
            return;
        }

        if (!IsExecuteArmed)
        {
            IsExecuteArmed = true;
            ExecuteButtonText = "Confirm Execute (5)";
            ExecuteCountdown = 5;

            _armTimeoutCts?.Cancel();
            _armTimeoutCts = new CancellationTokenSource();

            _ = RunArmCountdownAsync(_armTimeoutCts.Token);
            return;
        }

        _armTimeoutCts?.Cancel();
        IsExecuteArmed = false;
        ExecuteButtonText = "Execute on Server";

        var startedAt = DateTime.Now;
        var outcome = RestoreOutcome.Failed;
        string? failure = null;

        // Set false when the run is abandoned before anything was attempted, so history records
        // executions rather than button presses.
        var attempted = true;

        var executeToken = _executeCancellation.Begin();
        IsExecuting = true;
        // Reset alongside ExecutionComplete. Left over from a previous run it could combine with an
        // early bail-out to show the success banner - and write "Outcome: Succeeded" into a saved
        // log - for a restore that never ran.
        ExecutionSuccess = false;
        ExecutionComplete = false;
        ConsoleLines.Clear();
        HasConsoleOutput = false;
        RecoveryActions = [];
        HasRecoveryActions = false;
        RefreshCancelState();

        try
        {
            // Execute against the server we are actually connected to, not one looked up by name.
            // This used to re-read config.json and take the first entry whose ServerName matched,
            // which is a different object whenever two entries share a host and differ in auth,
            // port or encryption - a Windows-auth and a SQL-auth entry for the same box, say. The
            // restore would then run under credentials the user never connected with or tested.
            // Every other server call on this screen already uses ConnectedServer.
            var server = ConnectedServer;
            if (server == null)
            {
                attempted = false;
                SetError("Not connected to a server.");
                return;
            }

            if (SelectedContainer != null)
            {
                var sasToken = _credentialStore.GetSasToken(SelectedContainer);
                if (!string.IsNullOrEmpty(sasToken))
                {
                    // Only write to the server when the credential is genuinely missing or is not
                    // a SAS credential. This used to drop and recreate on every single execute,
                    // regardless of the status the panel above was displaying, which contradicted
                    // the UI's own "creating it is optional" wording and briefly removed a
                    // credential that other sessions may have been using.
                    //
                    // A credential that exists and is SAS is left alone. Its secret could still be
                    // a rotated SAS that no longer works - that is unknowable from here, since the
                    // secret cannot be read back - so the fix for that case is the explicit
                    // refresh button, not silently rewriting server state on every run.
                    var (exists, isSas) = await _sqlService.CredentialExistsAsync(server, SqlCredentialName);
                    if (exists && isSas)
                    {
                        AppendLog($"Using the existing SQL credential [{SqlCredentialName}]. Server state not modified.");
                    }
                    else
                    {
                        AppendLog(exists
                            ? $"Credential [{SqlCredentialName}] exists but is not a SAS credential - updating it..."
                            : $"Credential [{SqlCredentialName}] is missing - creating it...");

                        // This statement carries the SAS token to the server. It is the one moment
                        // in a restore where a secret crosses the wire, so if the connection is
                        // encrypted but unverified, say so here rather than only in settings (#17).
                        if (server.TrustServerCertificate)
                            AppendLog(
                                "  Note: this connection trusts the server certificate without validating it, " +
                                "and the SAS token is sent over it.");

                        var change = await _sqlService.EnsureCredentialExistsAsync(
                            server, SqlCredentialName, SelectedContainer.ContainerUrl, sasToken);

                        AppendLog(change == CredentialChange.Created
                            ? "Credential created on the server."
                            : "Credential updated on the server.");

                        // Changing a credential is a change to shared state on someone's instance.
                        // It belongs in the file, not only in a console that closes with the app.
                        _log.ServerChange(server.ServerName,
                            $"credential [{SqlCredentialName}] {change.ToString().ToLowerInvariant()}");
                    }
                }
            }

            _log.ServerChange(server.ServerName,
                $"restore starting: target [{TargetDatabaseName}], " +
                $"{RestoreChain?.Summary ?? "no chain"}, WITH REPLACE={WithReplace}, " +
                $"recovery={RecoveryMode}, stopAt={EffectiveStopAt?.ToString("s") ?? "none"}");

            AppendLog("Beginning restore execution...\n");

            await _sqlService.ExecuteRestoreWithProgressAsync(
                server,
                GeneratedScript,
                // InvokeAsync, not Invoke. This callback runs on the connection's thread when SQL
                // Server sends an info message, and a synchronous Invoke BLOCKS that thread until
                // the UI has finished handling it. With the UI busy re-rendering, progress backed
                // up and then arrived in bursts - which is precisely what "not live" looked like.
                // Posting instead lets the restore keep streaming while the UI catches up.
                msg => Application.Current.Dispatcher.InvokeAsync(() => AppendLog(msg)),
                executeToken);

            ExecutionSuccess = true;
            outcome = RestoreOutcome.Succeeded;
            AppendLog("\nRestore completed successfully!");
            SetStatus("Restore execution completed successfully.");
        }
        catch (OperationCanceledException)
        {
            outcome = RestoreOutcome.Cancelled;
            failure = "Cancelled by the user part-way through the chain.";
            // Cancelling a restore is not the same as never having run it. SqlCommand.Cancel stops
            // the client waiting and SQL Server rolls back the statement that was in flight, but
            // the target stays mid-restore - so this must be as loud as a failure, and it goes
            // through the same recovery guidance (#14, #25).
            ExecutionSuccess = false;
            AppendLog("\nCANCELLED. The statement in flight was rolled back by SQL Server, but the " +
                      "restore stopped part-way through the chain.");

            _log.ServerChange(ConnectedServer?.ServerName ?? "unknown",
                $"restore CANCELLED by user, target [{TargetDatabaseName}]");

            SetError("Restore cancelled. The target database has been left mid-restore - see below.");

            if (ConnectedServer != null)
                await ReportRecoveryStateAsync(ConnectedServer);
        }
        catch (Exception ex)
        {
            ExecutionSuccess = false;
            outcome = RestoreOutcome.Failed;
            failure = ex.Message;
            AppendLog($"\nERROR: {ex.Message}");
            SetError($"Restore failed: {ex.Message}");

            // The restore has stopped part-way and the target is almost certainly not usable.
            // Find out exactly how, and say so, while the connection is still open (#14).
            if (ConnectedServer != null)
                await ReportRecoveryStateAsync(ConnectedServer);
        }
        finally
        {
            _executeCancellation.End();
            IsExecuting = false;
            ExecutionComplete = true;
            FlushConsole();   // nothing buffered may outlive the run
            RefreshCancelState();

            // After the flush, so the recorded log is the whole console rather than whatever had
            // made it out of the batching buffer. Recorded for every outcome including cancelled -
            // "I stopped it" is exactly the kind of thing a change ticket needs to say.
            if (attempted) RecordHistory(startedAt, outcome, failure);
        }
    }

    /// <summary>
    /// Files this execution in the history (#31). Never throws: the store swallows its own
    /// failures, and a restore must not be reported as failed because a record could not be kept.
    /// </summary>
    private void RecordHistory(DateTime startedAt, RestoreOutcome outcome, string? failure)
    {
        _history.Append(new RestoreHistoryEntry
        {
            StartedAt = startedAt,
            CompletedAt = DateTime.Now,
            ServerName = ConnectedServer?.ServerName ?? "unknown",
            TargetDatabase = TargetDatabaseName,
            ContainerName = SelectedContainer?.Name,
            SourceDatabase = SelectedDatabaseName,
            RestorePointTimestamp = SelectedRestorePoint?.Timestamp,
            ChainSummary = RestoreChain?.Summary ?? "no chain",
            Outcome = outcome,
            ErrorMessage = failure,
            Script = GeneratedScript,
            Log = ConsoleText
        });
    }

    /// <summary>
    /// Stops a running restore.
    ///
    /// Deliberately worded as a warning rather than a neutral action: stopping a restore part-way
    /// leaves the target database in RESTORING, which is not a state anyone wants to discover by
    /// accident. The recovery panel that appears afterwards explains how to get out of it.
    /// </summary>
    [RelayCommand]
    private void CancelExecute()
    {
        if (!_executeCancellation.CanCancel) return;

        _executeCancellation.Cancel();
        RefreshCancelState();
        AppendLog("\nCancellation requested - waiting for SQL Server to roll back the current statement...");
    }

    /// <summary>
    /// After a failed restore, works out what state the target database was left in and offers the
    /// statements that put it right.
    ///
    /// This is the moment of maximum stress - a restore has failed mid-incident and the database is
    /// now in a state that blocks other connections. Leaving someone to work that out from a raw
    /// SQL error, when the app is still holding the connection that could tell them, is the wrong
    /// place to stop.
    /// </summary>
    private async Task ReportRecoveryStateAsync(ServerConnection server)
    {
        RecoveryActions = [];
        HasRecoveryActions = false;
        RecoveryStateMessage = string.Empty;

        try
        {
            var state = await _sqlService.GetDatabaseRecoveryStateAsync(server, TargetDatabaseName);
            if (!state.NeedsAttention)
            {
                if (!state.Exists)
                    AppendLog($"\n[{TargetDatabaseName}] is not on the server - nothing was left behind.");
                return;
            }

            RecoveryStateMessage = state.Explain(TargetDatabaseName);
            RecoveryActions = new ObservableCollection<RecoveryAction>(
                state.SuggestedActions(TargetDatabaseName));
            HasRecoveryActions = RecoveryActions.Count > 0;

            AppendLog($"\n{RecoveryStateMessage}");
            foreach (var action in RecoveryActions)
                AppendLog($"\n  {action.Title}:  {action.Sql}");
        }
        catch (Exception ex)
        {
            // Best effort. The restore failure is the news; failing to describe the aftermath must
            // not replace the error the user actually needs to read.
            AppendLog($"\nCould not check the state of [{TargetDatabaseName}]: {ex.Message}");
        }
    }

    /// <summary>Runs one remediation the user picked, then re-reads the state.</summary>
    [RelayCommand]
    private async Task RunRecoveryActionAsync(RecoveryAction? action)
    {
        if (action == null || ConnectedServer == null) return;

        try
        {
            AppendLog($"\nRunning: {action.Sql}");
            await _sqlService.ExecuteRecoveryActionAsync(ConnectedServer, action.Sql);
            AppendLog("Completed.");
            await ReportRecoveryStateAsync(ConnectedServer);

            if (!HasRecoveryActions)
                SetStatus($"[{TargetDatabaseName}] is back to a usable state.");
        }
        catch (Exception ex)
        {
            AppendLog($"\nERROR: {ex.Message}");
            SetError($"Recovery step failed: {ex.Message}");
        }
    }

    [RelayCommand]
    private void CopyRecoveryAction(RecoveryAction? action)
    {
        if (action != null)
            TryCopyToClipboard(action.Sql, "Recovery statement copied to clipboard.");
    }

    /// <summary>
    /// Appends to the on-screen console and to the log file at the same time.
    ///
    /// One call rather than two so the file cannot drift from what the user was shown - and so a
    /// restore that ends with the window being closed still leaves a record of how far it got.
    /// Redaction happens inside the log, not here (#40).
    /// </summary>
    /// <summary>
    /// Appends to the on-screen console and to the log file at the same time, so the file cannot
    /// drift from what the user was shown.
    ///
    /// Writes to a COLLECTION rather than concatenating a bound string. Appending to a bound string
    /// rebuilds it and re-renders the whole TextBox on every message - O(n^2) - which was a large
    /// part of why a restore reporting progress every few percent looked like it arrived in bursts
    /// rather than live.
    /// </summary>
    private void AppendLog(string message)
    {
        foreach (var raw in message.Split('\n'))
        {
            var line = raw.TrimEnd('\r');

            // Messages routinely arrive with a leading or trailing newline for spacing, and SQL
            // Server's own output has its own blank lines. Left alone that produced a gap between
            // almost every line. One blank line is allowed as a separator; runs of them are not.
            if (line.Trim().Length == 0)
            {
                if (_pending.Count > 0 && _pending[^1].Text.Length == 0) continue;
                if (_pending.Count == 0 && (ConsoleLines.Count == 0 || ConsoleLines[^1].Text.Length == 0)) continue;
                _pending.Add(new ConsoleLine(string.Empty));
                continue;
            }

            _pending.Add(ConsoleLine.From(line));
        }

        _log.Info($"[execute] {message.Trim()}");
        ScheduleConsoleFlush();
    }

    /// <summary>
    /// Moves buffered lines onto the bound collection on a timer rather than as they arrive.
    ///
    /// SQL Server emits progress in clusters - several messages within a millisecond, then nothing
    /// for a second. Adding each one individually meant a layout pass and a scroll per message, in
    /// bursts, which is what made the console judder. Flushing on a fixed tick turns any arrival
    /// pattern into a steady redraw, and a whole cluster costs one layout pass instead of ten.
    /// </summary>
    private void ScheduleConsoleFlush()
    {
        if (_consoleFlushTimer != null) return;

        _consoleFlushTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(60)
        };
        _consoleFlushTimer.Tick += (_, _) => FlushConsole();
        _consoleFlushTimer.Start();
    }

    private void FlushConsole()
    {
        if (_pending.Count == 0)
        {
            // Nothing arriving and nothing running - stop ticking rather than spin forever.
            if (!IsExecuting)
            {
                _consoleFlushTimer?.Stop();
                _consoleFlushTimer = null;
            }
            return;
        }

        foreach (var line in _pending) ConsoleLines.Add(line);
        _pending.Clear();
        HasConsoleOutput = true;
    }

    private readonly List<ConsoleLine> _pending = [];
    private DispatcherTimer? _consoleFlushTimer;

    /// <summary>The console as plain text, for copying into a bug report.</summary>
    public string ConsoleText => string.Join(Environment.NewLine, ConsoleLines.Select(l => l.Text));

    [RelayCommand]
    private void CopyConsole()
        => TryCopyToClipboard(ConsoleText, "Execution log copied to clipboard.");

    /// <summary>
    /// Writes the console to a file, with a header saying what it was (#31). The clipboard is fine
    /// for pasting into a chat window; a change ticket or an incident write-up wants a file, and
    /// wants it to say which server and which database on its own.
    /// </summary>
    [RelayCommand]
    private void SaveConsole()
    {
        if (string.IsNullOrEmpty(ConsoleText))
        {
            SetError("There is no execution log to save yet.");
            return;
        }

        var dialog = new SaveFileDialog
        {
            Filter = "Text Files (*.txt)|*.txt|Log Files (*.log)|*.log|All Files (*.*)|*.*",
            DefaultExt = ".txt",
            FileName = $"ninelives_{TargetDatabaseName}_{DateTime.Now:yyyyMMdd_HHmmss}.txt"
        };

        if (dialog.ShowDialog() != true) return;

        try
        {
            File.WriteAllText(dialog.FileName, BuildSavedLog());
            SetStatus($"Execution log saved to {dialog.FileName}");
        }
        catch (Exception ex)
        {
            // Read-only location, full disk, or a path the database name made invalid. Any of
            // those used to take the whole app down from inside a synchronous command (#13).
            SetError($"Could not save the execution log: {ex.Message}");
        }
    }

    /// <summary>
    /// The console plus the context needed to read it a week later. Redacted on the way out for
    /// the same reason the operation log is - this file gets attached to tickets.
    /// </summary>
    private string BuildSavedLog()
    {
        var header = new StringBuilder();
        header.AppendLine("Nine Lives - restore execution log");
        header.AppendLine($"Saved:      {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        header.AppendLine($"Server:     {ConnectedServer?.ServerName ?? "not connected"}");
        header.AppendLine($"Target:     {TargetDatabaseName}");
        if (SelectedContainer != null)
            header.AppendLine($"Container:  {SelectedContainer.Name}");
        if (SelectedRestorePoint != null)
            header.AppendLine($"Point:      {SelectedRestorePoint.TimestampDisplay}");
        header.AppendLine($"Chain:      {RestoreChain?.Summary ?? "none"}");
        header.AppendLine($"Outcome:    {(ExecutionComplete ? (ExecutionSuccess ? "Succeeded" : "Did not succeed") : "Still running")}");
        header.AppendLine(new string('-', 60));
        header.AppendLine();

        return LogRedactor.Redact(header + ConsoleText);
    }

    private async Task RunArmCountdownAsync(CancellationToken ct)
    {
        try
        {
            for (int i = 5; i >= 1; i--)
            {
                ct.ThrowIfCancellationRequested();
                ExecuteCountdown = i;
                ExecuteButtonText = $"Confirm Execute ({i})";
                await Task.Delay(1000, ct);
            }

            Application.Current.Dispatcher.Invoke(() =>
            {
                IsExecuteArmed = false;
                ExecuteButtonText = "Execute on Server";
            });
        }
        catch (OperationCanceledException)
        {
        }
    }
}
