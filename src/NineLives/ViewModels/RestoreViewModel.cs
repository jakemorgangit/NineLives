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

    /// <summary>
    /// The server queries a user starts and might want to abandon: verify, validate, the two
    /// metadata reads, creating the credential, and the post-failure recovery actions (#111).
    ///
    /// They share one source because they are mutually exclusive buttons - starting one while
    /// another runs should stop the first, which is exactly what Begin() does. RESTORE VERIFYONLY
    /// reads every byte of every backup in the chain at CommandTimeout = 0, so on a large chain
    /// this was hours with no Stop button and no timeout.
    /// </summary>
    private readonly OperationCancellation _queryCancellation = new();

    /// <summary>
    /// The server-side credential the restore authenticates with: what is on the instance, what to
    /// say about it, and what Execute should do before it runs (#115 seam 5).
    /// </summary>
    public ServerCredentialViewModel Credential { get; }

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

    /// <summary>
    /// The restore-point timeline: the points, the filters over them, the layout and the
    /// selection (#115 seam 2). Its selection is the one everything downstream hangs off, so this
    /// class watches it in the constructor.
    /// </summary>
    public RestoreTimelineViewModel Timeline { get; } = new();

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

    /// <summary>
    /// The RESTORE options, and the one place they become a <see cref="RestoreOptions"/>
    /// (#115 seam 7). One subscription in the constructor keeps the script in step with all of
    /// them, whatever gets added later.
    /// </summary>
    public RestoreOptionsViewModel Options { get; } = new();

    // ── Point-in-time (STOPAT) ───────────────────────────────────────────────────
    // Only meaningful for a transaction-log restore point. Without this the granularity of a
    // "point in time" restore is the end of whichever log backup was selected - so with
    // 15-minute logs, recovering from a bad DELETE at 14:23:41 could only land on 14:15 or
    // 14:30. The target is bounded to within the selected log's window; to stop earlier the
    // user picks an earlier restore point, which keeps the generated chain correct.

    /// <summary>
    /// The STOPAT target and the window it has to fall inside (#115 seam 3). The window itself is
    /// chain reasoning, so this class works it out and hands it down.
    /// </summary>
    public PointInTimeViewModel PointInTime { get; } = new();

    /// <summary>
    /// The numbered steps, and whether each is open (#117 item 3). Every step was expanded at all
    /// times, including the ones already finished and the ones not yet reachable, on a screen of
    /// roughly 1,300 lines of markup in one column.
    /// </summary>
    public RestoreStepsViewModel Steps { get; } = new();

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

    /// <summary>True while a server query is running and has not been asked to stop (#111).</summary>
    [ObservableProperty]
    private bool _canCancelQuery;

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
        CanCancelQuery = _queryCancellation.CanCancel;
        IsCancelling = _loadCancellation.IsCancelling
            || _executeCancellation.IsCancelling
            || _queryCancellation.IsCancelling;
    }

    /// <summary>True once the chain has been checked against RESTORE HEADERONLY metadata.</summary>
    [ObservableProperty]
    private bool _chainLsnVerified;

    [ObservableProperty]
    private bool _isValidatingChain;

    [ObservableProperty]
    private bool _isVerifyingChain;

    partial void OnIsVerifyingChainChanged(bool value) => RefreshCheckState();
    partial void OnIsValidatingChainChanged(bool value) => RefreshCheckState();
    partial void OnChainLsnVerifiedChanged(bool value) => RefreshCheckState();
    partial void OnHasChainIssuesChanged(bool value) => RefreshCheckState();
    partial void OnHasVerifyResultsChanged(bool value) => RefreshCheckState();
    partial void OnHasVerifyFailuresChanged(bool value) => RefreshCheckState();
    partial void OnHasTargetPathProblemChanged(bool value) => RefreshCheckState();

    /// <summary>
    /// Republishes the two "already passed" flags and re-queries the buttons that depend on them.
    /// Everything they are computed from is a separate observable property, so without this the
    /// tick appears and the button stays enabled - or worse, the other way round.
    /// </summary>
    private void RefreshCheckState()
    {
        OnPropertyChanged(nameof(ChainCheckPassed));
        OnPropertyChanged(nameof(VerifyPassed));
        OnPropertyChanged(nameof(BusyDescription));
        OnPropertyChanged(nameof(IsBusyWithAnything));
        ValidateChainCommand.NotifyCanExecuteChanged();
        VerifyChainCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// What this screen is doing, for the strip at the top of the window - empty when idle (#128).
    /// </summary>
    public string BusyDescription
    {
        get
        {
            if (IsExecuting) return $"Restoring {TargetDatabaseName}...";
            if (IsVerifyingChain) return "Verifying backups...";
            if (IsValidatingChain) return "Checking the chain...";
            if (IsBusy) return "Loading backups...";
            return string.Empty;
        }
    }

    public bool IsBusyWithAnything => BusyDescription.Length > 0;

    /// <summary>Per-set RESTORE VERIFYONLY results for the selected chain.</summary>
    public ObservableCollection<ChainVerifyResult> ChainVerifyResults { get; } = [];

    [ObservableProperty]
    private bool _hasVerifyResults;

    [ObservableProperty]
    private bool _hasVerifyFailures;


    [ObservableProperty]
    private bool _useWithMove;

    public bool ShowMoveOptions => UseWithMove;

    private ServerConnection? _connectedServer;

    /// <summary>
    /// The instance every server call on this screen runs against.
    ///
    /// Setting it re-checks the credential, rather than leaving the caller to remember: forgetting
    /// left the panel describing the credential on the server someone had just disconnected from,
    /// and that panel is what they read before deciding whether to create one (#115 seam 5).
    /// </summary>
    public ServerConnection? ConnectedServer
    {
        get => _connectedServer;
        set
        {
            _connectedServer = value;
            Credential.Server = value;
            _ = Credential.RefreshAsync();
        }
    }

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

    partial void OnSelectedContainerChanged(BlobContainerConfig? value)
    {
        // Everything on screen below this point came from the PREVIOUS container. Leaving it there
        // meant the credential panel and Create credential targeted the new container while the
        // script still restored from the old one's URLs - so Execute stayed armed and enabled, and
        // failed with Msg 3201 after WITH REPLACE had already dropped the target.
        ClearLoadedBackups();

        _ = Credential.PointAtAsync(value);
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

        BackupsLoaded = false;
        DiscoveredServers = [];
        DiscoveredDatabases = [];
        SelectedServerName = null;
        SelectedDatabaseName = null;

        // Clearing the timeline drops its selection, which runs the selection handler below and
        // clears the chain, the script, the verification results and the inventory findings.
        Timeline.Clear();

        // Disarm. An armed Execute that survives a container change is an armed Execute aimed at
        // something the user is no longer looking at.
        IsExecuteArmed = false;
        ExecuteButtonText = "Execute on Server";
        _armTimeoutCts?.Cancel();
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

        // Every console message is written to the log file as it arrives, so the file cannot drift
        // from what was on screen.
        Console = new ConsoleBuffer(message => _log.Info($"[execute] {message.Trim()}"));

        // Shares _queryCancellation so the Stop button stops a credential write too (#111), and
        // reports through the same status line as everything else on this screen (#115 seam 5).
        Credential = new ServerCredentialViewModel(sqlService, credentialStore, _log, _queryCancellation);
        Credential.Reported += (message, isError) =>
        {
            if (isError) SetError(message); else SetStatus(message);
        };

        // IsBusy and TargetDatabaseName live on the base class, so they cannot have a generated
        // partial hook - but the busy strip is computed from them.
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(IsBusy) or nameof(TargetDatabaseName))
                RefreshCheckState();

            if (e.PropertyName is nameof(BackupsLoaded) or nameof(SelectedDatabaseName)
                or nameof(SelectedServerName) or nameof(SelectedContainer)
                or nameof(TargetDatabaseName))
                RefreshSteps();
        };

        // The selection now lives on the timeline (#115 seam 2), which is a different object, so
        // this is a subscription rather than a generated partial hook.
        Timeline.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(RestoreTimelineViewModel.SelectedPoint))
                OnSelectedRestorePointChanged(Timeline.SelectedPoint);
        };

        // The STOPAT target feeds the generated script, so any change to it has to reach the
        // script (#115 seam 3). Skipped while SetWindow is rewriting several properties at once -
        // the caller updates once at the end rather than four times through a half-built state.
        PointInTime.PropertyChanged += (_, _) =>
        {
            if (!PointInTime.IsUpdating) UpdateRestoreSummary();
        };

        // One subscription in place of a one-line change handler per option (#115 seam 7). An
        // option added later is kept in step with the script by existing, rather than by whoever
        // adds it remembering to write a handler - which is what #110 was.
        Options.PropertyChanged += (_, _) => UpdateRestoreSummary();

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
    private void ToggleChainDetails()
    {
        ShowChainDetails = !ShowChainDetails;
    }

    /// <summary>
    /// Everything downstream of the timeline: the chain, the script, the point-in-time window and
    /// the verification results all belong to the selected point, and are rebuilt or thrown away
    /// when it moves.
    ///
    /// A plain PropertyChanged subscription rather than a partial hook - the property now lives on
    /// <see cref="Timeline"/>, and a partial hook can only be written for a property the same class
    /// declares.
    /// </summary>
    private void OnSelectedRestorePointChanged(RestorePoint? value)
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
            _allSets = _blobService.GroupIntoBackupSets(
                _allBackups, SelectedContainer?.BackupServerTimeZoneId);

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

    /// <summary>
    /// Stops whichever server query is running - verify, validate, a metadata read, or a recovery
    /// action. They share one source because they are mutually exclusive buttons (#111).
    /// </summary>
    [RelayCommand]
    private void CancelQuery()
    {
        if (!_queryCancellation.CanCancel) return;

        _queryCancellation.Cancel();
        RefreshCancelState();
        SetStatus("Stopping...");
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
    /// Works out which points this database can be restored to, and hands them to the timeline.
    /// </summary>
    private void ComputeAndDisplayRestorePoints()
    {
        var points = _chainBuilder.ComputeRestorePoints(_dbSets);

        // Inventory-level findings: backups that exist but can never be offered. These belong to
        // the discovered set rather than to any one chain, so they are held separately and survive
        // changing the selected restore point.
        //
        // ValidateInventory was written for #62 and then never called, so orphaned differentials,
        // orphaned logs and "no full backup at all" were being computed nowhere and shown nowhere.
        InventoryIssues = new ObservableCollection<ChainIssue>(
            _chainValidator.ValidateInventory(_dbSets)
                .Concat(_chainValidator.ValidateReachability(_dbSets, points)));
        HasInventoryIssues = InventoryIssues.Count > 0;

        Timeline.Load(points);

        if (points.Count == 0)
        {
            SetError("No valid restore points found. Ensure there is at least one full backup.");
            return;
        }

        ClearStatus();
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
            var (dataPath, logPath) = await _sqlService.GetDefaultPathsAsync(
                ConnectedServer, _queryCancellation.Begin());
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

        RefreshSteps();

        if (RestoreChain == null || string.IsNullOrWhiteSpace(TargetDatabaseName))
        {
            RestoreSummaryText = string.Empty;
            return;
        }

        var parts = new List<string>();
        parts.Add($"Restore '{SelectedDatabaseName}' as '{TargetDatabaseName}'");
        parts.Add($"using {RestoreChain.Summary} ({RestoreChain.FileCount} files total).");

        if (PointInTime.Effective is DateTime stopAt)
            parts.Add($"Stop at {stopAt:yyyy-MM-dd HH:mm:ss} (point-in-time recovery).");
        else if (Timeline.SelectedPoint is { Type: BackupType.TransactionLog } logPoint)
            parts.Add($"Restore to end of log backup at {logPoint.Timestamp:yyyy-MM-dd HH:mm:ss}.");

        var optionsList = new List<string>();

        if (Options.WithReplace)
            optionsList.Add("overwrite existing database (WITH REPLACE)");
        if (Options.DisconnectSessions)
            optionsList.Add("disconnect active sessions");
        if (UseWithMove)
            optionsList.Add($"relocate data files (WITH MOVE)");

        var recoveryDesc = Options.RecoveryMode switch
        {
            RecoveryMode.Recovery => "brought online for use (RECOVERY)",
            RecoveryMode.NoRecovery => "left in restoring state (NORECOVERY)",
            RecoveryMode.Standby => "set to read-only standby mode (STANDBY)",
            _ => "recovered"
        };
        optionsList.Add($"database will be {recoveryDesc}");

        if (Options.KeepReplication) optionsList.Add("preserve replication settings");
        if (Options.EnableBroker) optionsList.Add("enable Service Broker");
        if (Options.NewBroker) optionsList.Add("create new Service Broker ID");

        if (optionsList.Count > 0)
            parts.Add("Options: " + string.Join("; ", optionsList) + ".");

        RestoreSummaryText = string.Join(" ", parts);
    }

    /// <summary>
    /// Tells each step where it stands. Driven from the same places as the restore summary, so a
    /// step's heading cannot disagree with the screen underneath it.
    /// </summary>
    private void RefreshSteps()
    {
        Steps.Source.Report(
            BackupsLoaded && !string.IsNullOrWhiteSpace(SelectedDatabaseName),
            DescribeSource());

        Steps.Point.Report(
            Timeline.SelectedPoint != null,
            Timeline.SelectedPoint is { } point
                ? $"{point.TimestampDisplay}, {RestoreChain?.Summary ?? "no chain"}"
                : string.Empty);

        Steps.Options.Report(
            !string.IsNullOrWhiteSpace(TargetDatabaseName),
            DescribeOptions());
    }

    private string DescribeSource()
    {
        if (SelectedContainer == null) return string.Empty;

        var parts = new List<string> { SelectedContainer.Name };
        if (!string.IsNullOrWhiteSpace(SelectedDatabaseName))
        {
            parts.Add(string.IsNullOrWhiteSpace(SelectedServerName)
                ? SelectedDatabaseName!
                : $"{SelectedDatabaseName} on {SelectedServerName}");
        }

        return string.Join(", ", parts);
    }

    /// <summary>
    /// The short form for a collapsed step: the target and the handful of options that change what
    /// the restore DOES. Not the full sentence from the restore summary - that is a paragraph, and
    /// this has to fit on the heading beside the step title.
    /// </summary>
    private string DescribeOptions()
    {
        if (string.IsNullOrWhiteSpace(TargetDatabaseName)) return string.Empty;

        var parts = new List<string> { $"as {TargetDatabaseName}" };

        parts.Add(Options.RecoveryMode switch
        {
            RecoveryMode.NoRecovery => "NORECOVERY",
            RecoveryMode.Standby => "STANDBY",
            _ => "RECOVERY"
        });

        if (Options.WithReplace) parts.Add("REPLACE");
        if (UseWithMove) parts.Add("MOVE");
        if (PointInTime.Effective is DateTime stopAt) parts.Add($"STOPAT {stopAt:yyyy-MM-dd HH:mm:ss}");

        return string.Join(", ", parts);
    }

    /// <summary>
    /// Points the STOPAT box at the selected restore point's window, or turns it off.
    ///
    /// STOPAT only applies to a log restore, and only inside the LAST log's window - which is what
    /// <see cref="BackupChain.StopAtWindow"/> works out. Deciding whether there is a window is
    /// chain reasoning and stays here; validating against it does not.
    /// </summary>
    private void UpdatePointInTimeWindow(RestorePoint? point)
        => PointInTime.SetWindow(
            point?.Type == BackupType.TransactionLog ? RestoreChain?.StopAtWindow : null);

    /// <summary>
    /// Reads RESTORE HEADERONLY for every member of the selected chain and validates the LSN
    /// relationships - the authoritative check that the chain actually restores, as opposed to
    /// merely looking plausible by filename and timestamp.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanValidateChain))]
    private async Task ValidateChainAsync()
    {
        if (RestoreChain == null || ConnectedServer == null) return;

        var ct = _queryCancellation.Begin();
        IsValidatingChain = true;
        RefreshCancelState();
        ClearStatus();
        try
        {
            var headers = new List<ChainHeader>();
            foreach (var set in RestoreChain.AllSets)
            {
                ct.ThrowIfCancellationRequested();

                var urls = set.Files.Select(f => BlobUrlEncoder.Encode(f.BlobUrl)).ToList();
                try
                {
                    var header = await _sqlService.RestoreHeaderOnlyMultiAsync(ConnectedServer, urls, ct);
                    headers.Add(new ChainHeader(set, header));
                }
                catch (OperationCanceledException)
                {
                    // Asked for. Must not be swallowed by the per-member catch below, which exists
                    // so ONE unreadable backup does not abort the rest.
                    throw;
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
                ? $"Chain check found problems - see the panel above."
                : $"Chain checked: {headers.Count} header(s) read, the LSN chain is unbroken.");
        }
        catch (OperationCanceledException)
        {
            SetStatus("Chain check cancelled.");
        }
        catch (Exception ex)
        {
            SetError($"Chain check failed: {ex.Message}");
        }
        finally
        {
            _queryCancellation.End();
            IsValidatingChain = false;
            RefreshCancelState();
        }
    }

    /// <summary>
    /// True once the chain has been checked and came back clean. The result belongs to the chain
    /// that was checked, so it clears with the selection (#128).
    /// </summary>
    public bool ChainCheckPassed => ChainLsnVerified && !HasChainIssues;

    private bool CanValidateChain() =>
        IsConnectedToServer && RestoreChain != null && !IsValidatingChain && !ChainCheckPassed;

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

        var ct = _queryCancellation.Begin();
        IsVerifyingChain = true;
        RefreshCancelState();
        ClearStatus();
        ChainVerifyResults.Clear();
        HasVerifyResults = false;

        // The same MOVE clauses the restore would use. Without them VERIFYONLY checks the paths
        // recorded inside the backup - the SOURCE server's - and reports directory-lookup failures
        // for a restore that was never going to touch them (#129).
        var fileMoves = BuildFileMoves();
        VerifiedWithMove = fileMoves.Count > 0;

        try
        {
            var sets = RestoreChain.AllSets;
            for (int i = 0; i < sets.Count; i++)
            {
                var set = sets[i];
                SetStatus($"Verifying {i + 1} of {sets.Count}: {set.TypeDisplay} {set.SetId}...");

                var urls = set.Files.Select(f => BlobUrlEncoder.Encode(f.BlobUrl)).ToList();
                var result = await _sqlService.RestoreVerifyOnlyAsync(
                    ConnectedServer, urls, Options.WithChecksum, fileMoves, ct);

                ChainVerifyResults.Add(new ChainVerifyResult { Set = set, Result = result });
                HasVerifyResults = true;
            }

            var failed = ChainVerifyResults.Count(r => !r.IsValid);
            HasVerifyFailures = failed > 0;

            UpdateTargetPathWarning();

            SetStatus(failed > 0
                ? $"{failed} of {ChainVerifyResults.Count} backup(s) failed verification - see below. Do not rely on this chain."
                : HasTargetPathProblem
                    ? $"All {ChainVerifyResults.Count} backup(s) read back intact, but the restore will fail - see below."
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
            _queryCancellation.End();
            IsVerifyingChain = false;
            RefreshCancelState();
        }
    }

    /// <summary>
    /// True once every backup in the chain read back intact AND the restore has somewhere to
    /// write. Re-running VERIFYONLY on a large chain by accident is expensive, so once it has
    /// passed for this selection the button is done.
    /// </summary>
    public bool VerifyPassed => HasVerifyResults && !HasVerifyFailures && !HasTargetPathProblem;

    private bool CanVerifyChain() =>
        IsConnectedToServer && RestoreChain != null && !IsVerifyingChain && !IsExecuting
        && !VerifyPassed;

    /// <summary>
    /// Turns SQL Server's directory-lookup complaint into something that says what to do about it.
    ///
    /// Backups can read back perfectly and still be certain to fail on restore, because the
    /// directories they would be written to do not exist on this server. VERIFYONLY reports that
    /// as an informational message next to "the backup set is valid", which is four lines of grey
    /// text under a green tick - so it gets said properly here instead (#129).
    /// </summary>
    private void UpdateTargetPathWarning()
    {
        HasTargetPathProblem = ChainVerifyResults.Any(r => r.Result.TargetPathsMissing);

        TargetPathProblemMessage = !HasTargetPathProblem
            ? string.Empty
            : VerifiedWithMove
                ? "The target directories do not exist on this server. Create them, or change the "
                  + "file paths above - as it stands the restore will fail once it has already "
                  + "dropped the target database."
                : "The file paths recorded inside these backups do not exist on this server, and "
                  + "no WITH MOVE is set - so the restore will fail once it has already dropped "
                  + "the target database. Tick WITH MOVE and give it paths that exist here.";
    }

    /// <summary>True when the last verification found directories a restore could not write to.</summary>
    [ObservableProperty]
    private bool _hasTargetPathProblem;

    [ObservableProperty]
    private string _targetPathProblemMessage = string.Empty;

    /// <summary>Whether the last verification was given MOVE clauses, which changes what to say.</summary>
    [ObservableProperty]
    private bool _verifiedWithMove;

    private void ClearVerifyResults()
    {
        ChainVerifyResults.Clear();
        HasVerifyResults = false;
        HasVerifyFailures = false;
        HasTargetPathProblem = false;
        TargetPathProblemMessage = string.Empty;
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

    // The WITH MOVE paths are typed into text boxes rather than ticked, and both were missing
    // their handler - so the script on screen did not change when they did. That is the exact
    // failure RegenerateScript exists to prevent: typing a new data-file path, watching the box
    // update, and running a script that still had the old one - or no MOVE clause at all, sending
    // the restore to the file paths baked into the backup. The rest of that class of bug is gone
    // structurally with #115 seam 7; these two are still here because the moves are worked out from
    // the chain and the server rather than being options.
    partial void OnMoveDataFilePathChanged(string value) => UpdateRestoreSummary();
    partial void OnMoveLogFilePathChanged(string value) => UpdateRestoreSummary();

    // Verification opens its own connection and reads whole backups. Letting it start while a
    // restore is running would put a second heavy reader on the same server at the worst moment.
    partial void OnIsExecutingChanged(bool value)
    {
        VerifyChainCommand.NotifyCanExecuteChanged();
        RefreshExecuteBlockedReason();
        RefreshCheckState();
    }

    /// <summary>
    /// Why Execute cannot be pressed, or empty when it can.
    ///
    /// The button was disabled identically whether the user was not connected, had no script, or
    /// had a chain the app already knows will not restore. Three different problems, one greyed-out
    /// button, and the information was all sitting in properties nobody showed.
    /// </summary>
    public string ExecuteBlockedReason
    {
        get
        {
            if (IsExecuting) return string.Empty;
            if (!IsConnectedToServer) return "Connect to a SQL Server to execute this restore.";
            if (!HasScript) return "No script yet - pick a restore point and a target database name.";
            if (HasChainErrors) return "This chain cannot restore. See the problems listed above.";
            return string.Empty;
        }
    }

    public bool IsExecuteBlocked => ExecuteBlockedReason.Length > 0;

    /// <summary>The button's own IsEnabled, so the reason and the enabled state cannot disagree.</summary>
    public bool CanPressExecute => !IsExecuteBlocked;

    private void RefreshExecuteBlockedReason()
    {
        OnPropertyChanged(nameof(ExecuteBlockedReason));
        OnPropertyChanged(nameof(IsExecuteBlocked));
        OnPropertyChanged(nameof(CanPressExecute));
    }

    partial void OnHasScriptChanged(bool value) => RefreshExecuteBlockedReason();

    partial void OnHasChainErrorsChanged(bool value) => RefreshExecuteBlockedReason();
    partial void OnTargetDatabaseNameChanged(string value) => UpdateRestoreSummary();

    partial void OnIsConnectedToServerChanged(bool value)
    {
        RefreshExecuteBlockedReason();
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
        if (PointInTime.Use && PointInTime.CanUse && PointInTime.Effective == null)
        {
            SetError($"Point-in-time target is not valid. {PointInTime.Message}");
            return;
        }

        if (!Options.HasStandbyFileIfNeeded)
        {
            SetError("STANDBY needs an undo file path. Without one the script would end in " +
                     "STANDBY = '', which fails after the database has already been overwritten.");
            return;
        }

        RegenerateScript();
        if (HasScript) SetStatus("Script generated successfully.");
    }

    /// <summary>
    /// The WITH MOVE clauses the restore would use, or an empty list when it is not moving files.
    ///
    /// Shared with the verification, which passes the same list to RESTORE VERIFYONLY - otherwise
    /// VERIFYONLY checks the paths recorded inside the backup, which belong to the SOURCE server,
    /// and reports directory-lookup failures for a restore that was never going to use them (#129).
    /// </summary>
    private List<FileMoveOption> BuildFileMoves()
    {
        var fileMoves = new List<FileMoveOption>();
        if (!UseWithMove) return fileMoves;

        if (HasFetchedFileMoves && FetchedFileMoves.Count > 0)
        {
            foreach (var m in FetchedFileMoves)
            {
                if (!string.IsNullOrWhiteSpace(m.NewPhysicalName))
                {
                    fileMoves.Add(new FileMoveOption
                    {
                        LogicalName = m.LogicalName,
                        PhysicalName = m.PhysicalName,
                        NewPhysicalName = m.NewPhysicalName,
                        Type = m.Type
                    });
                }
            }
        }
        else if (!string.IsNullOrWhiteSpace(MoveDataFilePath))
        {
            // Guessed logical names. Wrong for a renamed database or one with secondary files,
            // which is what Get file names exists to fix.
            var sourceDbName = SelectedDatabaseName ?? TargetDatabaseName;
            fileMoves.Add(new FileMoveOption
            {
                LogicalName = sourceDbName,
                PhysicalName = string.Empty,
                NewPhysicalName = MoveDataFilePath,
                Type = "ROWS"
            });
            fileMoves.Add(new FileMoveOption
            {
                LogicalName = sourceDbName + "_log",
                PhysicalName = string.Empty,
                NewPhysicalName = MoveLogFilePath,
                Type = "LOG"
            });
        }

        return fileMoves;
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
            || (PointInTime.Use && PointInTime.CanUse && PointInTime.Effective == null)
            || !Options.HasStandbyFileIfNeeded)
        {
            GeneratedScript = string.Empty;
            HasScript = false;
            return;
        }

        // The three things that are not options: which database, where the point-in-time target
        // ended up, and where the files land - each worked out somewhere that knows about the
        // chain or the server.
        var options = Options.Build(TargetDatabaseName, PointInTime.Effective, BuildFileMoves());

        GeneratedScript = _scriptGenerator.Generate(RestoreChain, options);
        HasScript = true;
    }

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

        var ct = _queryCancellation.Begin();
        IsBusy = true;
        RefreshCancelState();
        BackupMetadataSummary = null;
        // Captured BEFORE the await. Selecting a different restore point sets RestoreChain to null
        // on the UI thread, and it can do that while this call is in flight - in which case the
        // catch below would itself throw, replacing the error explaining why FILELISTONLY failed
        // with a bare "Nine Lives hit an unexpected error".
        // Use URL without SAS and omit WITH CREDENTIAL. Encode path so spaces/special chars (e.g. in folder names) are valid.
        var urls = RestoreChain.FullSet.Files.Select(f => BlobUrlEncoder.Encode(f.BlobUrl)).ToList();

        try
        {
            var list = await _sqlService.RestoreFileListOnlyAsync(ConnectedServer, urls, ct);

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
            SetStatus($"Read {FetchedFileMoves.Count} logical file name(s) from the backup. Edit the target paths above, and tick WITH MOVE to use them.");
        }
        catch (Exception ex)
        {
            var urlPreview = urls.Count > 0 ? urls[0] : "(no URL)";
            if (urls.Count > 1) urlPreview += $" (+{urls.Count - 1} more)";
            SetError($"RESTORE FILELISTONLY failed: {ex.Message}. URL used: {urlPreview}. Run the same RESTORE FILELISTONLY in SSMS to confirm credential/network.");
            FetchedFileMoves = [];
            HasFetchedFileMoves = false;
        }
        finally
        {
            _queryCancellation.End();
            IsBusy = false;
            RefreshCancelState();
        }
    }

    private bool CanFetchLogicalNames() =>
        IsConnectedToServer && RestoreChain != null && SelectedContainer != null && RestoreChain.FullSet.Files.Count > 0;

    [RelayCommand(CanExecute = nameof(CanInspectMetadata))]
    private async Task InspectBackupMetadataAsync()
    {
        if (RestoreChain == null || ConnectedServer == null || SelectedContainer == null) return;

        var ct = _queryCancellation.Begin();
        IsBusy = true;
        RefreshCancelState();
        // Captured before the await - see the note on FetchLogicalNamesAsync.
        // Use URL without SAS and omit WITH CREDENTIAL. Encode path so spaces/special chars (e.g. in folder names) are valid.
        var urls = RestoreChain.FullSet.Files.Select(f => BlobUrlEncoder.Encode(f.BlobUrl)).ToList();

        try
        {
            var header = await _sqlService.RestoreHeaderOnlyMultiAsync(ConnectedServer, urls, ct);

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
            SetStatus("Header read. See the metadata below.");
        }
        catch (Exception ex)
        {
            var urlPreview = urls.Count > 0 ? urls[0] : "(no URL)";
            if (urls.Count > 1) urlPreview += $" (+{urls.Count - 1} more)";
            SetError($"RESTORE HEADERONLY failed: {ex.Message}. URL used: {urlPreview}. Run the same RESTORE HEADERONLY in SSMS to confirm credential/network.");
            BackupMetadataSummary = null;
        }
        finally
        {
            _queryCancellation.End();
            IsBusy = false;
            RefreshCancelState();
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
        Console.IsRunning = true;
        // Reset alongside ExecutionComplete. Left over from a previous run it could combine with an
        // early bail-out to show the success banner - and write "Outcome: Succeeded" into a saved
        // log - for a restore that never ran.
        ExecutionSuccess = false;
        ExecutionComplete = false;
        Console.Clear();
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

            // Everything about the server-side credential lives on the child (#115 seam 5),
            // including the decision not to touch one. A refusal has written nothing, so there is
            // nothing to unwind - and nothing to file in the history either.
            var preflight = await Credential.PrepareForRestoreAsync(server, AppendLog);
            if (!preflight.CanProceed)
            {
                attempted = false;
                SetError(preflight.Refusal!);
                return;
            }

            _log.ServerChange(server.ServerName,
                $"restore starting: target [{TargetDatabaseName}], " +
                $"{RestoreChain?.Summary ?? "no chain"}, WITH REPLACE={Options.WithReplace}, " +
                $"recovery={Options.RecoveryMode}, stopAt={PointInTime.Effective?.ToString("s") ?? "none"}");

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
            Console.IsRunning = false;
            ExecutionComplete = true;
            Console.Flush();   // nothing buffered may outlive the run
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
            RestorePointTimestamp = Timeline.SelectedPoint?.Timestamp,
            ChainSummary = RestoreChain?.Summary ?? "no chain",
            Outcome = outcome,
            ErrorMessage = failure,
            Script = GeneratedScript,
            Log = Console.Text
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

        // Cancellable, and it needs it more than most: this runs when a restore has already
        // failed, RESTORE ... WITH RECOVERY can take a long time on a large database, and it goes
        // out at CommandTimeout = 0. Without a Stop the only way out was killing the process - at
        // the exact moment the user is trying to get their database back.
        var ct = _queryCancellation.Begin();
        RefreshCancelState();

        try
        {
            AppendLog($"\nRunning: {action.Sql}");
            await _sqlService.ExecuteRecoveryActionAsync(ConnectedServer, action.Sql, ct);
            AppendLog("Completed.");
            await ReportRecoveryStateAsync(ConnectedServer);

            if (!HasRecoveryActions)
                SetStatus($"[{TargetDatabaseName}] is back to a usable state.");
        }
        catch (OperationCanceledException)
        {
            // SQL Server rolls back the statement that was in flight, so the database is where it
            // was before this step - which is still whatever the failed restore left behind.
            AppendLog("\nCancelled. The database is in the same state as before this step.");
            SetStatus("Recovery step cancelled.");
        }
        catch (Exception ex)
        {
            AppendLog($"\nERROR: {ex.Message}");
            SetError($"Recovery step failed: {ex.Message}");
        }
        finally
        {
            _queryCancellation.End();
            RefreshCancelState();
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
    /// The execution console. Its batching and line handling live in ConsoleBuffer (#115) - this
    /// screen only feeds it and shows it.
    /// </summary>
    public ConsoleBuffer Console { get; }

    /// <summary>
    /// Appends to the on-screen console and to the log file at the same time, so the file cannot
    /// drift from what the user was shown.
    /// </summary>
    private void AppendLog(string message) => Console.Append(message);



    [RelayCommand]
    private void CopyConsole()
        => TryCopyToClipboard(Console.Text, "Execution log copied to clipboard.");

    /// <summary>
    /// Writes the console to a file, with a header saying what it was (#31). The clipboard is fine
    /// for pasting into a chat window; a change ticket or an incident write-up wants a file, and
    /// wants it to say which server and which database on its own.
    /// </summary>
    [RelayCommand]
    private void SaveConsole()
    {
        if (string.IsNullOrEmpty(Console.Text))
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
        if (Timeline.SelectedPoint != null)
            header.AppendLine($"Point:      {Timeline.SelectedPoint.TimestampDisplay}");
        header.AppendLine($"Chain:      {RestoreChain?.Summary ?? "none"}");
        header.AppendLine($"Outcome:    {(ExecutionComplete ? (ExecutionSuccess ? "Succeeded" : "Did not succeed") : "Still running")}");
        header.AppendLine(new string('-', 60));
        header.AppendLine();

        return LogRedactor.Redact(header + Console.Text);
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
