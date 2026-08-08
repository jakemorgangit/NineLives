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

    // The loaded backups, what was found in them and which container they came from all live on
    // the inventory seam (#115 seam 4).

    // Two separate operations, two separate sources: browsing a container and running a restore
    // can both be in flight, and cancelling one must not touch the other (#25).
    private readonly OperationCancellation _loadCancellation = new();
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

    /// <summary>
    /// Running the restore: arming, the countdown, execution, cancellation, the recovery guidance
    /// afterwards and the history entry (#115 seam 6). The only code on this screen that writes to
    /// somebody's database.
    /// </summary>
    public RestoreExecutionViewModel Execution { get; }

    /// <summary>
    /// What the selected container holds, and which server and database within it is being looked
    /// at - carrying the container it was loaded FROM, so nothing downstream has to assume (#115
    /// seam 4).
    /// </summary>
    public BackupInventoryViewModel Inventory { get; }

    #region Observable Properties

    [ObservableProperty]
    private ObservableCollection<BlobContainerConfig> _containers = [];

    [ObservableProperty]
    private BlobContainerConfig? _selectedContainer;

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

    // The aftermath of a failed restore (#14) lives on the execution seam with the run that
    // produced it (#115 seam 6).

    // ── Cancellation (#25) ──────────────────────────────────────────────────────

    /// <summary>True while a server query is running and has not been asked to stop (#111).</summary>
    [ObservableProperty]
    private bool _canCancelQuery;

    /// <summary>True between asking to stop and the operation actually unwinding.</summary>
    [ObservableProperty]
    private bool _isCancelling;

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
        Inventory.CanCancelLoad = _loadCancellation.CanCancel;

        CanCancelQuery = _queryCancellation.CanCancel;
        IsCancelling = _loadCancellation.IsCancelling
            || Execution.IsCancelling
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

    // ── which medium the backups live on (#149, #165) ───────────────────────────
    //
    // Backup media is a choice per operation, not a property of the app. Everything below the
    // listing already works on BackupSet and never asks where the sets came from, so this is the
    // whole of what a second medium changes on this screen: which inputs step 1 offers, and whether
    // the server-side credential is applicable at all.

    /// <summary>
    /// How much of this screen to show (#176).
    ///
    /// Pro by default, so anything that constructs this without a shell - a test, a future CLI -
    /// gets the whole thing rather than silently losing capability.
    /// </summary>
    [ObservableProperty]
    private AppMode _mode = AppMode.Pro;

    [ObservableProperty]
    private BackupMedium _selectedMedium = BackupMedium.AzureBlob;

    public bool ShowMediumChoice => AppModeCapabilities.CanUseSharedPath(Mode);
    public bool ShowPointInTime => AppModeCapabilities.CanRestoreToAPointInTime(Mode);
    public bool ShowFileRelocation => AppModeCapabilities.CanRelocateFiles(Mode);
    public bool ShowVerifyAndAudit => AppModeCapabilities.CanVerifyAndAudit(Mode);
    public bool ShowCredentialPanel => AppModeCapabilities.CanManageServerCredentials(Mode) && CredentialApplies;
    public bool ShowAgentJob => AppModeCapabilities.CanScriptAsAgentJob(Mode);
    public bool ShowAdvancedOptions => AppModeCapabilities.CanUseAdvancedRestoreOptions(Mode);

    partial void OnModeChanged(AppMode value)
    {
        OnPropertyChanged(nameof(ShowMediumChoice));
        OnPropertyChanged(nameof(ShowPointInTime));
        OnPropertyChanged(nameof(ShowFileRelocation));
        OnPropertyChanged(nameof(ShowVerifyAndAudit));
        OnPropertyChanged(nameof(ShowCredentialPanel));
        OnPropertyChanged(nameof(ShowAgentJob));
        OnPropertyChanged(nameof(ShowAdvancedOptions));

        // A medium that is no longer offered must not stay selected underneath a hidden control -
        // the screen would be restoring FROM DISK with nothing on screen saying so.
        if (!ShowMediumChoice && SelectedMedium != BackupMedium.AzureBlob)
            SelectedMedium = BackupMedium.AzureBlob;
    }

    public bool MediumIsBlob => SelectedMedium == BackupMedium.AzureBlob;
    public bool MediumIsSharedPath => SelectedMedium == BackupMedium.SharedPath;

    /// <summary>
    /// Whether the server-side credential means anything here.
    ///
    /// FROM DISK needs no credential at all - SQL Server reaches the path as its own service
    /// account - so under a shared path the whole credential panel is not merely unsatisfied, it is
    /// inapplicable, and it says so rather than sitting there looking like something still to do.
    /// </summary>
    public bool CredentialApplies => MediumIsBlob;

    partial void OnSelectedMediumChanged(BackupMedium value)
    {
        OnPropertyChanged(nameof(MediumIsBlob));
        OnPropertyChanged(nameof(MediumIsSharedPath));
        OnPropertyChanged(nameof(CredentialApplies));
        OnPropertyChanged(nameof(ShowCredentialPanel));

        // Same rule as changing the container, for the same reason: what is on screen came from the
        // other medium entirely, and an armed Execute that survives the switch is aimed at
        // something nobody is looking at any more.
        ClearLoadedBackups();
    }

    /// <summary>The saved connections, for choosing whose backup history to read.</summary>
    [ObservableProperty]
    private ObservableCollection<ServerConnection> _sourceServers = [];

    /// <summary>
    /// The instance whose msdb is read under a shared path - the one that TOOK the backups.
    ///
    /// Not necessarily the instance the restore runs on. That distinction is the whole reason the
    /// shared-path medium exists, and it is the thing people get wrong.
    /// </summary>
    [ObservableProperty]
    private ServerConnection? _sourceServer;

    partial void OnSourceServerChanged(ServerConnection? value) => ClearLoadedBackups();

    /// <summary>The path as the source wrote it, when the target reaches it by another name.</summary>
    [ObservableProperty]
    private string _sourcePathPrefix = string.Empty;

    /// <summary>How the target reaches the same place.</summary>
    [ObservableProperty]
    private string _targetPathPrefix = string.Empty;

    partial void OnSourcePathPrefixChanged(string value) => ClearLoadedBackups();
    partial void OnTargetPathPrefixChanged(string value) => ClearLoadedBackups();

    /// <summary>
    /// Where the backups are being read from, as one value.
    ///
    /// Null when the inputs for the chosen medium are not answered yet, which is what stops a load
    /// running against half a selection.
    /// </summary>
    public BackupLocation? CurrentLocation => SelectedMedium switch
    {
        BackupMedium.AzureBlob =>
            SelectedContainer == null ? null : BackupLocation.Blob(SelectedContainer),

        BackupMedium.SharedPath =>
            SourceServer == null
                ? null
                : BackupLocation.Shared(SourceServer, new BackupPathMapping(SourcePathPrefix, TargetPathPrefix)),

        _ => null
    };

    /// <summary>
    /// Said before anything is read, because it is the one failure here that can end in a
    /// SUCCESSFUL restore of the wrong backup: a local path on the source may resolve on the target
    /// to the target's OWN drive of that letter.
    /// </summary>
    public string PathAdvice
    {
        get
        {
            if (!MediumIsSharedPath || !Inventory.LoadedFromSharedPath) return string.Empty;

            var mapping = new BackupPathMapping(SourcePathPrefix, TargetPathPrefix);
            if (mapping.IsInUse) return string.Empty;

            var local = Inventory.WorkingSet
                .SelectMany(s => s.Files)
                .Select(f => f.RestoreDevice)
                .Where(BackupPathMapping.LooksLocalToTheSource)
                .ToList();

            if (local.Count == 0) return string.Empty;

            return $"{local.Count} of these backups were written to a local path on the source " +
                   $"(for example {local[0]}). That path means something different on the target - " +
                   "at best it will not be found, at worst it resolves to the target's own drive. " +
                   "Give the path as the target reaches it above.";
        }
    }

    public bool HasPathAdvice => PathAdvice.Length > 0;

    /// <summary>
    /// Drops everything derived from a container's contents. Called when the container changes and
    /// when a load is abandoned, so what is on screen always belongs to the container named above it.
    /// </summary>
    private void ClearLoadedBackups()
    {
        Inventory.Clear();

        Timeline.Clear();

        // Disarm. An armed Execute that survives a container change is an armed Execute aimed at
        // something the user is no longer looking at.
        Execution.Disarm();
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

    /// <summary>
    /// Whether a restore is running. The state belongs to the execution seam; this reads it, so
    /// the things on this screen that must not happen mid-restore still have one thing to ask.
    /// </summary>
    public bool IsExecuting => Execution.IsExecuting;

    // Backup summary
    #endregion

    public RestoreViewModel(
        IBlobStorageService blobService,
        ISqlServerService sqlService,
        BackupChainBuilder chainBuilder,
        RestoreScriptGenerator scriptGenerator,
        ICredentialStore credentialStore,
        OperationLog? log = null,
        IRestoreHistoryStore? history = null,
        IBackupAuditStore? auditStore = null)
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

        // The audit cache is passed down for the same reason the log is: left to find its own, it
        // finds the one in the user's profile - so a TEST run reads and writes the real cache, and
        // a stale entry from one test then decides the answer in another (#130).
        Inventory = new BackupInventoryViewModel(blobService, sqlService, _log, auditStore);

        // The chain and the timeline are built from whatever the inventory currently holds, so a
        // change there is the one signal that rebuilds them.
        Inventory.WorkingSetChanged += ComputeAndDisplayRestorePoints;

        // And so are the two offers that act on it. Both are gated on the working set having
        // something in it, and a CanExecute answered once at load time is answered when the working
        // set is still EMPTY - so the Audit button stayed dead after picking a database, next to an
        // estimate that had cheerfully updated to 98 headers. The estimate is a property and
        // re-read itself; the command is not and has to be told.
        Inventory.WorkingSetChanged += RefreshIdentifyState;

        Inventory.CancelStateChanged += RefreshCancelState;
        Inventory.PropertyChanged += (_, e) =>
        {
            switch (e.PropertyName)
            {
                case nameof(BackupInventoryViewModel.StatusMessage) when !Inventory.HasError:
                    SetStatus(Inventory.StatusMessage);
                    break;
                case nameof(BackupInventoryViewModel.ErrorMessage) when Inventory.HasError:
                    SetError(Inventory.ErrorMessage);
                    break;
                case nameof(BackupInventoryViewModel.IsBusy):
                    IsBusy = Inventory.IsBusy;

                    // Both offers are also gated on the inventory being idle, so a load or an audit
                    // finishing is what makes them live again.
                    RefreshIdentifyState();
                    break;

                case nameof(BackupInventoryViewModel.IsAuditing):
                case nameof(BackupInventoryViewModel.UnclassifiedCount):
                    RefreshIdentifyState();
                    break;
                case nameof(BackupInventoryViewModel.SelectedDatabaseName):
                    // The target defaults to the source name, and the MOVE paths follow from it.
                    // Both belong to the screen rather than to the inventory - they describe where
                    // the restore is GOING, not what the container holds.
                    if (!string.IsNullOrEmpty(Inventory.SelectedDatabaseName))
                    {
                        TargetDatabaseName = Inventory.SelectedDatabaseName!;
                        AutoPopulateMoveDefaults();
                    }
                    RefreshSteps();
                    RefreshCheckState();
                    break;

                case nameof(BackupInventoryViewModel.BackupsLoaded):
                case nameof(BackupInventoryViewModel.SelectedServerName):
                    RefreshSteps();
                    RefreshCheckState();
                    break;
            }
        };

        Execution = new RestoreExecutionViewModel(sqlService, _history, _log, _queryCancellation);

        // The run reports through the same status line as the rest of the screen, and its cancel
        // state feeds the one Stop button that covers every server call here.
        Execution.CancelStateChanged += RefreshCancelState;
        Execution.PropertyChanged += (_, e) =>
        {
            switch (e.PropertyName)
            {
                case nameof(ViewModelBase.StatusMessage):
                    if (!Execution.HasError) SetStatus(Execution.StatusMessage);
                    break;
                case nameof(ViewModelBase.ErrorMessage) when Execution.HasError:
                    SetError(Execution.ErrorMessage);
                    break;
                case nameof(RestoreExecutionViewModel.IsExecuting):
                    OnExecutionRunningChanged();
                    OnPropertyChanged(nameof(IsBusyWithAnything));
                    break;
            }
        };

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

            if (e.PropertyName is nameof(Inventory.BackupsLoaded) or nameof(Inventory.SelectedDatabaseName)
                or nameof(Inventory.SelectedServerName) or nameof(SelectedContainer)
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
            if (PointInTime.IsUpdating) return;
            Steps.Options.Invalidate();
            UpdateRestoreSummary();
        };

        // One subscription in place of a one-line change handler per option (#115 seam 7). An
        // option added later is kept in step with the script by existing, rather than by whoever
        // adds it remembering to write a handler - which is what #110 was.
        Options.PropertyChanged += (_, _) =>
        {
            Steps.Options.Invalidate();
            UpdateRestoreSummary();
        };

        RefreshContainers();
    }

    private readonly OperationLog _log;
    private readonly IRestoreHistoryStore _history;

    public void RefreshContainers()
    {
        var previous = SelectedContainer?.Name;
        var config = _credentialStore.LoadConfig();

        // A server added on the SQL Servers screen since the last visit has to be selectable as a
        // backup source here, so the two lists are refreshed together.
        var previousSource = SourceServer?.Id;
        SourceServers = new ObservableCollection<ServerConnection>(config.Servers);
        if (previousSource != null)
            SourceServer = SourceServers.FirstOrDefault(x => x.Id == previousSource);

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

        // A confirmed point that then moves is no longer confirmed. Leaving it standing would let
        // the script, the summary and the execute button describe a moment nobody chose.
        Steps.Point.Invalidate();

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

    /// <summary>Reads the selected location. Everything it finds lives on the inventory seam.</summary>
    [RelayCommand]
    private async Task LoadBackupsAsync()
    {
        var location = CurrentLocation;
        if (location == null)
        {
            SetError(MediumIsSharedPath
                ? "Choose the server the backups were taken on first."
                : "Please select a container first.");
            return;
        }

        await Inventory.LoadAsync(location);

        // Only worth saying once there is something to say it about - it is derived from what was
        // actually loaded, not from the paths typed above.
        OnPropertyChanged(nameof(PathAdvice));
        OnPropertyChanged(nameof(HasPathAdvice));

        // How many files the filenames could not place is only known once they have been read.
        RefreshIdentifyState();
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
    private void CancelLoad() => Inventory.CancelLoadCommand.Execute(null);

    /// <summary>
    /// Asks the connected instance what the unplaceable files actually are (#130).
    ///
    /// Needs a server because the header can only be read by one - which is also why this is an
    /// action rather than something the load does on its own. Browsing a container works with no
    /// connection at all today, and working out WHICH server is needed is sometimes the reason for
    /// browsing in the first place.
    /// </summary>
    public bool CanIdentifyUnclassified =>
        Inventory.HasUnclassified && IsConnectedToServer && !Inventory.IsBusy;

    /// <summary>Why the button cannot be pressed, or empty when it can.</summary>
    public string IdentifyBlockedReason =>
        !Inventory.HasUnclassified ? string.Empty
        : IsConnectedToServer ? string.Empty
        : "Connect to a SQL Server instance to read the backup headers - it is the server that reads them, not this app.";

    [RelayCommand(CanExecute = nameof(CanIdentifyUnclassified))]
    private async Task IdentifyUnclassifiedAsync()
    {
        var server = ConnectedServer;
        if (server == null) return;

        await Inventory.IdentifyUnclassifiedAsync(server);

        // What the headers settled changes the working set, so whatever was on the timeline was
        // computed from the state before them.
        RefreshIdentifyState();
    }

    private void RefreshIdentifyState()
    {
        OnPropertyChanged(nameof(CanIdentifyUnclassified));
        OnPropertyChanged(nameof(IdentifyBlockedReason));
        IdentifyUnclassifiedCommand.NotifyCanExecuteChanged();

        OnPropertyChanged(nameof(CanAuditDatabase));
        OnPropertyChanged(nameof(AuditBlockedReason));
        AuditDatabaseCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// Checks every backup of the selected database against its own header (#130).
    ///
    /// Belt and braces rather than a fix: inference is right most of the time, and the times it is
    /// not are invisible until a restore is needed. This is the button for when a set looks
    /// fragmented, or a container is new and nobody has confirmed the path pattern yet.
    /// </summary>
    public bool CanAuditDatabase => Inventory.CanAudit && IsConnectedToServer;

    public string AuditBlockedReason =>
        Inventory.AuditScope.Count == 0 ? string.Empty
        : IsConnectedToServer ? string.Empty
        : "Connect to a SQL Server instance to audit these backups - it is the server that reads the headers, not this app.";

    [RelayCommand(CanExecute = nameof(CanAuditDatabase))]
    private async Task AuditDatabaseAsync()
    {
        var server = ConnectedServer;
        if (server == null) return;

        await Inventory.AuditAsync(server);

        // The audit marks the files it checked, and the chain on screen holds those same objects -
        // but a list does not re-render because a property nobody is watching changed underneath it.
        RefreshChainFiles();
        RefreshIdentifyState();
    }

    /// <summary>
    /// Re-publishes the chain's files so their audit pills appear.
    ///
    /// BackupFileInfo is a plain model with no change notification - deliberately, it is a
    /// serialised listing rather than a viewmodel - so the collection is replaced rather than the
    /// items being expected to announce themselves.
    /// </summary>
    private void RefreshChainFiles()
    {
        // The restore points grid is what somebody is actually looking at when an audit finishes -
        // the chain panel below it is collapsed until they ask for it - so that grid is the one
        // that has to show the result. Reported: an audit passed 98 sets and nothing said so.
        Timeline.RefreshRows();

        if (RestoreChain == null) return;

        ChainFiles = new ObservableCollection<BackupFileInfo>(RestoreChain.AllFiles);
        ChainSets = new ObservableCollection<BackupSet>(RestoreChain.AllSets);
    }

    [RelayCommand]
    private void ToggleChainDetails()
    {
        ShowChainDetails = !ShowChainDetails;
    }

    /// <summary>
    /// What one header read cost, in the terms #130 needs to settle its design.
    ///
    /// Kept as a property as well as a log line so it can be read off the screen without going to
    /// the file - the point of measuring is that somebody looks at the number.
    /// </summary>
    [ObservableProperty]
    private string _headerReadTiming = string.Empty;

    /// <summary>
    /// Works out which points this database can be restored to, and hands them to the timeline.
    /// </summary>
    private void ComputeAndDisplayRestorePoints()
    {
        var points = _chainBuilder.ComputeRestorePoints(Inventory.WorkingSet);

        // Inventory-level findings: backups that exist but can never be offered. These belong to
        // the discovered set rather than to any one chain, so they are held separately and survive
        // changing the selected restore point.
        //
        // ValidateInventory was written for #62 and then never called, so orphaned differentials,
        // orphaned logs and "no full backup at all" were being computed nowhere and shown nowhere.
        InventoryIssues = new ObservableCollection<ChainIssue>(
            _chainValidator.ValidateInventory(Inventory.WorkingSet)
                .Concat(_chainValidator.ValidateReachability(Inventory.WorkingSet, points)));
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
        parts.Add($"Restore '{Inventory.SelectedDatabaseName}' as '{TargetDatabaseName}'");
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
        // Step 1 finishes by itself, because there is nothing to confirm: a database either has
        // been chosen or has not. It needs BOTH the container loaded and a database picked - the
        // step holds the server and database dropdowns, so completing it on the load alone threw
        // people out of the step before they could use them.
        Steps.Report(
            Steps.Source,
            Inventory.BackupsLoaded && !string.IsNullOrWhiteSpace(Inventory.SelectedDatabaseName),
            DescribeSource());

        // Described, and confirmed by the user rather than by a click. Choosing a restore point is
        // the decision this application exists to support: collapsing the step the instant a dot is
        // clicked takes the timeline away from somebody who is still comparing points.
        Steps.Point.Describe(
            Timeline.SelectedPoint is { } point
                ? $"{point.TimestampDisplay}, {RestoreChain?.Summary ?? "no chain"}"
                : string.Empty);
        Steps.Point.CanConfirm = Timeline.SelectedPoint != null && RestoreChain != null;

        // Described, not completed. The options have defaults, so there is no point at which they
        // are finished - and the target name is derived from the chosen database, so treating a
        // non-empty one as completion folded this step away the moment a database was picked, and
        // the hand-over from step 2 then skipped straight over it to step 4.
        Steps.Options.Describe(DescribeOptions());
        Steps.Options.CanConfirm = !string.IsNullOrWhiteSpace(TargetDatabaseName);
    }

    private string DescribeSource()
    {
        if (SelectedContainer == null) return string.Empty;

        var parts = new List<string> { SelectedContainer.Name };
        if (!string.IsNullOrWhiteSpace(Inventory.SelectedDatabaseName))
        {
            parts.Add(string.IsNullOrWhiteSpace(Inventory.SelectedServerName)
                ? Inventory.SelectedDatabaseName!
                : $"{Inventory.SelectedDatabaseName} on {Inventory.SelectedServerName}");
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

            // Timed, because #130 turns on a number nobody has: how long ONE header read actually
            // costs against a real container. Building chains from headers rather than filenames is
            // only sensible if that number is small, and the whole design in that issue is written
            // around an assumption that it is not. This is the cheapest place to find out - the
            // work is already being done, one read per chain member, against real backups.
            // One connection for the whole chain, not one per member.
            //
            // The first measurement on a real container - three statements over nine striped files,
            // 17.4 seconds - was taken with a fresh connection per read, so part of what it reported
            // was the connect cost paid three times. Batching removes that, and the timing now says
            // which part was which (#130).
            //
            // A null result is an unreadable member rather than a failure of the whole validation:
            // the validator reports it and carries on with the rest.
            var requests = RestoreChain.AllSets
                .Select(s => (IReadOnlyList<string>)s.Files.Select(f => BlobUrlEncoder.Encode(f.BlobUrl)).ToList())
                .ToList();

            var read = await _sqlService.RestoreHeaderOnlyBatchAsync(
                ConnectedServer, requests,
                progress: null,
                timing: t =>
                {
                    HeaderReadTiming = t;
                    _log.Info($"[headeronly] {t}");
                },
                ct: ct);

            headers.AddRange(RestoreChain.AllSets.Select((set, i) =>
                new ChainHeader(set, i < read.Count ? read[i] : null)));

            UpdateChainIssues(RestoreChain, headers);
            ChainLsnVerified = true;

            SetStatus(HasChainIssues
                ? $"Chain check found problems - see the panel above."
                : $"Chain checked: {headers.Count} header(s) read, the LSN chain is unbroken. " +
                  HeaderReadTiming);
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
    // Called from the execution seam's own IsExecuting, which is where that state now lives.
    private void OnExecutionRunningChanged()
    {
        OnPropertyChanged(nameof(IsExecuting));
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

        // Reading a backup header needs a server, so connecting is what makes that offer live -
        // and disconnecting is what has to withdraw it (#130).
        RefreshIdentifyState();

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
            var sourceDbName = Inventory.SelectedDatabaseName ?? TargetDatabaseName;
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

    // ── will it fit (#32) ───────────────────────────────────────────────────────
    //
    // A restore that runs out of disk fails part-way, and by then WITH REPLACE has already dropped
    // the database it was replacing - so the target is gone and the replacement is incomplete. That
    // is the worst outcome this screen can produce and it is entirely predictable: FILELISTONLY
    // already reports how big each file will be, and the instance already knows what it has free.

    /// <summary>Per-volume space, once the file list and the server's volumes are both known.</summary>
    [ObservableProperty]
    private ObservableCollection<VolumeSpace> _volumeSpace = [];

    /// <summary>What to say about it, or empty when everything fits.</summary>
    [ObservableProperty]
    private string _spaceWarning = string.Empty;

    public bool HasSpaceWarning => SpaceWarning.Length > 0;

    partial void OnSpaceWarningChanged(string value) => OnPropertyChanged(nameof(HasSpaceWarning));

    /// <summary>
    /// Asks the target what it has free and compares it with what the restore will write.
    ///
    /// Failing to answer is not a warning. This is a courtesy check over somebody else's storage,
    /// and an instance that will not report its volumes - permissions, an edition that does not
    /// expose the DMV - must not turn that into a scary message about a restore that is probably
    /// fine.
    /// </summary>
    private async Task CheckSpaceAsync(CancellationToken ct)
    {
        VolumeSpace = [];
        SpaceWarning = string.Empty;

        if (ConnectedServer == null || FetchedFileMoves.Count == 0) return;

        try
        {
            var free = await _sqlService.GetVolumeFreeSpaceAsync(ConnectedServer, ct);
            var volumes = RestoreSpaceCheck.Check(FetchedFileMoves, free);

            VolumeSpace = new ObservableCollection<VolumeSpace>(volumes);
            SpaceWarning = RestoreSpaceCheck.Warn(volumes);

            if (HasSpaceWarning) _log.Warn($"[space] {SpaceWarning}");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Logged rather than shown. Not being able to check is not the same as not fitting.
            _log.Info($"[space] could not check free space: {ex.Message}");
        }
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

    /// <summary>
    /// The same restore, wrapped as an Agent job for somebody else to run (#32).
    ///
    /// For the case where a restore cannot happen interactively: a maintenance window at 3am, a
    /// change process that takes submitted scripts rather than a person at a keyboard, an operator
    /// who runs what a DBA hands them.
    ///
    /// The job is created disabled and unscheduled, so what is handed over is reviewable and inert
    /// until somebody deliberately starts it.
    /// </summary>
    [RelayCommand]
    private void CopyAsAgentJob()
    {
        if (!HasScript) return;

        var job = SqlAgentJobScript.Wrap(
            GeneratedScript,
            SqlAgentJobScript.SuggestName(TargetDatabaseName, DateTime.Now),
            $"Restore {Inventory.SelectedDatabaseName ?? "a database"} as {TargetDatabaseName}, " +
            $"generated by Nine Lives on {DateTime.Now:yyyy-MM-dd HH:mm}.");

        TryCopyToClipboard(job,
            "Agent job script copied to clipboard. The job is created disabled and unscheduled - " +
            "enable or start it when the restore should actually happen.");
    }

    [RelayCommand]
    private void CopyPathHttps(BackupFileInfo? file) => CopyBlobHttpsPath(file, SelectedContainer);

    [RelayCommand]
    private void CopyPathContainer(BackupFileInfo? file) => CopyBlobContainerPath(file, SelectedContainer);

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

            // The sizes came back in the same result set, so the question of whether this can
            // physically fit costs one more query rather than another read of the backup (#32).
            await CheckSpaceAsync(ct);
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

    /// <summary>
    /// Arms the button, then hands the run to the execution seam (#115 seam 6).
    ///
    /// What stays here is what belongs to this screen rather than to the run: whether the script
    /// on display is the one the options currently produce, and whether the chain can restore at
    /// all. Both are checked on the CONFIRM press as well as when arming - validation can finish
    /// during the five-second countdown and find an error that was not known when it was armed.
    /// </summary>
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
            Execution.Disarm();
            return;
        }

        // Once execution starts, WITH REPLACE drops the target database, and a chain that fails
        // partway leaves nothing usable behind. Failing here costs a few seconds; failing
        // mid-restore costs the database being restored over.
        if (HasChainErrors)
        {
            var first = ChainIssues.First(i => i.IsError);
            SetError($"Cannot execute: {first.Title}. {first.Detail}");
            Execution.Disarm();
            return;
        }

        if (!Execution.IsArmed)
        {
            Execution.Arm();
            return;
        }

        // Execute against the server we are actually connected to, not one looked up by name. This
        // used to re-read config.json and take the first entry whose ServerName matched, which is a
        // different object whenever two entries share a host and differ in auth, port or encryption
        // - so the restore ran under credentials the user never connected with or tested.
        var server = ConnectedServer;
        if (server == null)
        {
            Execution.Disarm();
            SetError("Not connected to a server.");
            return;
        }

        await Execution.RunAsync(
            new RestoreRun(
                server,
                GeneratedScript,
                TargetDatabaseName,
                Inventory.SelectedDatabaseName,
                SelectedContainer?.Name,
                RestoreChain?.Summary ?? "no chain",
                Timeline.SelectedPoint?.Timestamp,
                $"WITH REPLACE={Options.WithReplace}, recovery={Options.RecoveryMode}, " +
                $"stopAt={PointInTime.Effective?.ToString("s") ?? "none"}"),
            appendLog => PreflightAsync(server, appendLog));
    }

    /// <summary>
    /// The last thing checked before a restore runs, and it differs by medium.
    ///
    /// Blob needs the server-side credential to be usable. A shared path needs no credential at all
    /// - but it needs something the blob path never has to ask: whether the instance about to run
    /// the RESTORE can actually READ the files. Those are the same question in different clothes,
    /// which is why they share one hook rather than the execute path growing a branch.
    /// </summary>
    internal async Task<CredentialPreflight> PreflightAsync(ServerConnection server, Action<string> appendLog)
    {
        if (MediumIsSharedPath)
            return await CheckTargetCanReadFilesAsync(server, appendLog);

        return await Credential.PrepareForRestoreAsync(server, appendLog);
    }

    /// <summary>
    /// Asks the instance the restore will run on whether it can read every file in the chain.
    ///
    /// On that instance and not from here, and this is the entire reason a shared path needs a
    /// preflight of its own: this app's process can see a share the SQL Server service account
    /// cannot, and the RESTORE runs as that account on that host. A File.Exists from here would say
    /// yes and the restore would then fail with "Operating system error 5(Access is denied)" -
    /// after WITH REPLACE had already dropped the database being restored over.
    ///
    /// The usual cause is an instance running as a local account or NT SERVICE\MSSQLSERVER, which
    /// has no identity on the network and so cannot read ANY share. That is worth diagnosing rather
    /// than reporting as a missing file, because it sends people to check the wrong thing.
    /// </summary>
    private async Task<CredentialPreflight> CheckTargetCanReadFilesAsync(
        ServerConnection server, Action<string> appendLog)
    {
        var paths = RestoreChain?.AllFiles.Select(f => f.RestoreDevice).ToList() ?? [];
        if (paths.Count == 0) return CredentialPreflight.Proceed;

        appendLog($"Checking {server.ServerName} can read {paths.Count} backup file(s)...");

        try
        {
            var checks = await _sqlService.CheckBackupFilesAsync(server, paths);
            var failed = checks.FirstOrDefault(c => !c.CanBeRestored);

            if (failed != null)
            {
                var refusal = failed.Explain(server.ServerName);
                appendLog(refusal);
                return CredentialPreflight.Stop(refusal);
            }

            appendLog($"{server.ServerName} can read all {checks.Count} file(s).");
            return CredentialPreflight.Proceed;
        }
        catch (Exception ex)
        {
            // A check that cannot be completed is not a check that passed. Proceeding here would
            // put the whole point of this preflight - finding out BEFORE the target is dropped -
            // back where it was.
            var refusal = $"Could not confirm {server.ServerName} can read the backup files: {ex.Message}";
            appendLog(refusal);
            return CredentialPreflight.Stop(refusal);
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
        header.AppendLine($"Outcome:    {(Execution.ExecutionComplete ? (Execution.ExecutionSuccess ? "Succeeded" : "Did not succeed") : "Still running")}");
        header.AppendLine(new string('-', 60));
        header.AppendLine();

        return LogRedactor.Redact(header + Execution.Console.Text);
    }

}
