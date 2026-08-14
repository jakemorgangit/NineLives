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
    /// The saved servers the target step offers (#318) - the same list the Servers screen
    /// manages, refreshed with the rest of the config-derived lists.
    /// </summary>
    [ObservableProperty]
    private ObservableCollection<ServerConnection> _targetServers = [];

    /// <summary>
    /// The chosen target. Setting it here or connecting on the Servers screen are the same
    /// decision made in two places, so each reflects into the other - the preflights, the
    /// execution and the credential panel all keep reading ConnectedServer, which both feed.
    /// </summary>
    [ObservableProperty]
    private ServerConnection? _selectedTargetServer;

    /// <summary>The step's empty-list hint - no saved servers means the combo has nothing to offer.</summary>
    public bool HasNoTargetServers => TargetServers.Count == 0;

    partial void OnTargetServersChanged(ObservableCollection<ServerConnection> value) =>
        OnPropertyChanged(nameof(HasNoTargetServers));

    partial void OnSelectedTargetServerChanged(ServerConnection? value)
    {
        Steps.Target.Report(value != null, value?.Name ?? string.Empty);
        if (value != null && !ReferenceEquals(_connectedServer, value))
            ConnectedServer = value;

        // Picking a target here PROVES it, rather than only naming it (#420).
        //
        // This step's own subtitle offers "otherwise pick a saved server here" as the equivalent
        // of connecting on the SQL Servers screen. It set everything except the one flag that
        // gates Execute - IsConnectedToServer, which only MainViewModel sets, and only when the
        // Servers screen connects - so a user who did exactly what this screen invited them to do
        // got every step ticked, a generated script, and "Connect to a SQL Server instance (SQL
        // Servers tab)" telling them to go elsewhere and do it again.
        if (value != null && !IsConnectedToServer)
            _ = ConnectToTargetAsync(value);
    }

    /// <summary>
    /// What the target connection is doing, or why it did not happen (#420).
    ///
    /// Failing is a legitimate outcome, not an error: generating a script for an instance this
    /// machine cannot reach is exactly what Copy as Agent job, Export runbook and Save to File are
    /// for. So a failure never blocks generation - it just stops being silent about why Execute is
    /// unavailable.
    /// </summary>
    [ObservableProperty]
    private string _targetConnectionState = string.Empty;

    [ObservableProperty]
    private bool _isConnectingToTarget;

    partial void OnIsConnectingToTargetChanged(bool value) => RefreshExecuteBlockedReason();

    /// <summary>Its own source: this is automatic, so it must not cancel a query somebody started.</summary>
    private readonly OperationCancellation _targetConnectCancellation = new();

    private async Task ConnectToTargetAsync(ServerConnection server)
    {
        var ct = _targetConnectCancellation.Begin();

        IsConnectingToTarget = true;
        TargetConnectionState = $"Connecting to {server.Name}...";

        try
        {
            await _sqlService.TestConnectionAsync(server, ct);

            // Captured before the await, and checked after it (#409): a connection attempt is
            // slow enough for somebody to pick a different target while it runs, and the answer
            // belongs to the server that was asked.
            if (!ReferenceEquals(SelectedTargetServer, server)) return;

            IsConnectedToServer = true;
            ConnectedServerName = server.ServerName;
            TargetConnectionState = $"Connected to {server.Name}. The restore can run from here.";

            // The step collapses itself once answered, so the header is where this has to be
            // legible - otherwise the outcome sits behind a chevron nobody has reason to open.
            Steps.Target.Describe(server.Name);
        }
        catch (OperationCanceledException)
        {
            // Superseded by a later selection, which will report for itself.
        }
        catch (Exception ex)
        {
            if (!ReferenceEquals(SelectedTargetServer, server)) return;

            IsConnectedToServer = false;
            TargetConnectionState =
                $"Could not connect to {server.Name}: {ex.Message} The script still generates - " +
                "save it, copy it as an Agent job or export a runbook and run it where the " +
                "instance can be reached.";

            // On the COLLAPSED header, because choosing a target completes the step and folds it
            // away. Without this the screen showed a green tick against a server it had just
            // failed to reach, with the explanation hidden inside - worse than the bug being
            // fixed. Found by rendering it, not by reading it.
            Steps.Target.Describe($"{server.Name} - could not be reached");
        }
        finally
        {
            if (ReferenceEquals(SelectedTargetServer, server))
                IsConnectingToTarget = false;

            _targetConnectCancellation.End();
        }
    }

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

            // The Servers screen's connect answers the target step too (#318): already
            // connected means the question is pre-answered, visibly and changeably.
            if (!ReferenceEquals(SelectedTargetServer, value))
                SelectedTargetServer = value == null
                    ? null
                    : TargetServers.FirstOrDefault(s => s.Id == value.Id) ?? value;
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

        // Ticking a checkbox does not cancel somebody's work (#413).
        //
        // Fetching the target's default directories is one of the mutually exclusive server
        // operations on this screen - they share a cancellation source so that one Stop button
        // covers them all, and starting one deliberately stops the last. That doctrine is right
        // for the BUTTONS: pressing "Fetch from Server" while a chain verification runs is a
        // choice to do the other thing instead.
        //
        // It is wrong here, because this is not a button. Ticking WITH MOVE is a request to see
        // the move options, and it fired the fetch as a convenience - which cancelled a chain
        // verification that reads the header of every backup in the chain, several minutes'
        // work, for a value the user can ask for explicitly a moment later.
        //
        // So the automatic trigger defers, and says so. Deferring rather than queueing on
        // purpose: a fetch that fires when the query finishes would answer for whatever is
        // selected THEN, which is the stale-answer class fixed in #409.
        if (value)
        {
            if (_queryCancellation.CanCancel)
                PathSourceText =
                    "A server query is already running, so the default paths were not fetched - " +
                    "stopping it for this would have been a poor trade. Press Fetch from Server " +
                    "once it finishes, or type the paths in.";
            else
                _ = FetchDefaultPathsAsync();
        }

        UpdateRestoreSummary();
    }

    [ObservableProperty]
    private string _moveDataFilePath = string.Empty;

    [ObservableProperty]
    private string _moveLogFilePath = string.Empty;

    [ObservableProperty]
    private bool _pathsFromServer;

    [ObservableProperty]
    private string _pathSourceText = string.Empty;

    [ObservableProperty]
    private string _restoreSummaryText = string.Empty;

    /// <summary>
    /// Other containers to read as well as the selected one (#32).
    ///
    /// The layout this exists for is asymmetric, and the model follows it: full backups archived to
    /// cool storage while the logs that carry them forward stay hot. There is a container somebody
    /// is working in - the credential panel points at it, the script header names it, a copied path
    /// is relative to it - and others that happen to hold parts of the chain.
    ///
    /// Peers would have been the tidier model and the wrong one: it would have made every one of
    /// those anchored things ask "which container?" and have no answer.
    /// </summary>
    public ObservableCollection<ContainerChoice> AdditionalContainers { get; } = [];

    /// <summary>Every container a load will read, primary first.</summary>
    public IReadOnlyList<BlobContainerConfig> ContainersToRead
    {
        get
        {
            if (SelectedContainer == null) return [];

            return
            [
                SelectedContainer,
                .. AdditionalContainers
                    .Where(c => c.IsSelected && c.Container.Id != SelectedContainer.Id)
                    .Select(c => c.Container)
            ];
        }
    }

    /// <summary>Whether more than one container is being read, for the screen to say so.</summary>
    public bool ReadsSeveralContainers => ContainersToRead.Count > 1;

    public string ContainersToReadSummary => ContainersToRead.Count switch
    {
        0 or 1 => string.Empty,
        var n => $"Reading {n} containers. A chain may span them."
    };

    /// <summary>
    /// Called by each tick. The list rebuilds rather than the choice being observable itself,
    /// because what changed is what a LOAD would read - and that is a property of the whole set.
    /// </summary>
    private void OnAdditionalContainerToggled()
    {
        // What is on screen came from the previous set of containers.
        ClearLoadedBackups();

        OnPropertyChanged(nameof(ContainersToRead));
        OnPropertyChanged(nameof(ReadsSeveralContainers));
        OnPropertyChanged(nameof(ContainersToReadSummary));
        OnPropertyChanged(nameof(CurrentLocation));
        RefreshSteps();
    }

    /// <summary>
    /// Rebuilds the additional list to be everything EXCEPT the primary.
    ///
    /// Offering the primary again would be a tick that either does nothing or, read literally, asks
    /// to list the same container twice.
    /// </summary>
    private void RefreshAdditionalContainers()
    {
        var ticked = AdditionalContainers
            .Where(c => c.IsSelected)
            .Select(c => c.Container.Id)
            .ToHashSet();

        AdditionalContainers.Clear();

        foreach (var container in Containers)
        {
            if (SelectedContainer != null && container.Id == SelectedContainer.Id) continue;

            AdditionalContainers.Add(new ContainerChoice(container, OnAdditionalContainerToggled)
            {
                IsSelected = ticked.Contains(container.Id)
            });
        }

        OnPropertyChanged(nameof(ContainersToRead));
        OnPropertyChanged(nameof(ReadsSeveralContainers));
        OnPropertyChanged(nameof(ContainersToReadSummary));
        OnPropertyChanged(nameof(ShowMultipleContainers));
    }

    partial void OnSelectedContainerChanged(BlobContainerConfig? value)
    {
        RefreshAdditionalContainers();

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

    /// <summary>
    /// Whether the audit card appears at all.
    ///
    /// The mode AND something to audit. The card used to be shown on the mode alone while its
    /// contents waited on the backups, so anybody in the widest mode met an empty bordered box
    /// sitting under the source picker before they had loaded anything (#195).
    /// </summary>
    public bool ShowAuditPanel => ShowVerifyAndAudit && Inventory.BackupsLoaded;
    public bool ShowCredentialPanel => AppModeCapabilities.CanManageServerCredentials(Mode) && CredentialApplies;
    public bool ShowAgentJob => AppModeCapabilities.CanScriptAsAgentJob(Mode);
    public bool ShowAdvancedOptions => AppModeCapabilities.CanUseAdvancedRestoreOptions(Mode);

    /// <summary>
    /// Whether to offer reading more than one container (#32).
    ///
    /// Pro, and only when there is more than one container to offer. It answers a question somebody
    /// has to know to ask - that a chain CAN be split across containers - and a tick list with
    /// nothing in it is worse than no tick list.
    /// </summary>
    public bool ShowMultipleContainers =>
        AppModeCapabilities.CanUseAdvancedRestoreOptions(Mode) && AdditionalContainers.Count > 0;

    partial void OnModeChanged(AppMode value)
    {
        OnPropertyChanged(nameof(ShowMediumChoice));
        OnPropertyChanged(nameof(ShowPointInTime));
        OnPropertyChanged(nameof(ShowFileRelocation));
        OnPropertyChanged(nameof(ShowVerifyAndAudit));
        OnPropertyChanged(nameof(ShowAuditPanel));
        OnPropertyChanged(nameof(ShowCredentialPanel));
        OnPropertyChanged(nameof(ShowAgentJob));
        OnPropertyChanged(nameof(ShowAdvancedOptions));
        OnPropertyChanged(nameof(ShowMultipleContainers));

        // A medium that is no longer offered must not stay selected underneath a hidden control -
        // the screen would be restoring FROM DISK with nothing on screen saying so.
        if (!ShowMediumChoice && SelectedMedium != BackupMedium.AzureBlob)
            SelectedMedium = BackupMedium.AzureBlob;
    }

    public bool MediumIsBlob => SelectedMedium == BackupMedium.AzureBlob;
    public bool MediumIsSharedPath => SelectedMedium == BackupMedium.SharedPath;
    public bool MediumIsAdHocFile => SelectedMedium == BackupMedium.AdHocFile;

    /// <summary>
    /// The pasted paths, one complete backup file per line (#203).
    ///
    /// As the READING server sees them, not this machine - the same trust rule as the shared path,
    /// and the readability preflight enforces it on the target before anything runs.
    /// </summary>
    [ObservableProperty]
    private string _adHocPathsText = string.Empty;

    partial void OnAdHocPathsTextChanged(string value)
    {
        // What is loaded came from the PREVIOUS files.
        ClearLoadedBackups();
        OnPropertyChanged(nameof(CurrentLocation));
    }

    /// <summary>One path per non-empty line, in the order given.</summary>
    public IReadOnlyList<string> AdHocPaths =>
        AdHocPathsText
            .Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

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
        OnPropertyChanged(nameof(MediumIsAdHocFile));
        OnPropertyChanged(nameof(CredentialApplies));
        OnPropertyChanged(nameof(ShowCredentialPanel));

        // Same rule as changing the container, for the same reason: what is on screen came from the
        // other medium entirely, and an armed Execute that survives the switch is aimed at
        // something nobody is looking at any more.
        ClearLoadedBackups();
    }

    /// <summary>
    /// Picks up where the browser left off (#202): same source, same server, same database,
    /// backups loaded - so the only thing left to do here is choose the restore point.
    /// </summary>
    public async Task AcceptHandoffAsync(BrowseHandoff handoff)
    {
        // This screen already refuses to regenerate the script mid-restore because it is the
        // record of what is actually running - the handoff cannot be allowed to do what the
        // regenerate button may not (#288). Clicking a red dashboard row must not wipe the
        // chain and timeline out from under a 40-minute restore.
        if (IsExecuting)
        {
            SetError("A restore is running on this screen. Wait for it to finish (or stop it) " +
                     "and hand over again - jumping sources now would wipe the record of the run.");
            return;
        }

        SelectedMedium = handoff.Medium == BackupMedium.SharedPath
            ? BackupMedium.SharedPath
            : BackupMedium.AzureBlob;

        if (handoff.Medium == BackupMedium.SharedPath)
        {
            // By id against OUR list, because the browser's instance and this screen's are
            // separate objects loaded from the same config.
            SourceServer = SourceServers.FirstOrDefault(s => s.Id == handoff.SourceServer?.Id)
                           ?? handoff.SourceServer;
        }
        else if (handoff.Container != null)
        {
            // What arrives is what the browser showed. Extra containers ticked on an earlier
            // visit would make the load see MORE than was looked at - a chain assembling from
            // backups the user never saw (#288).
            foreach (var choice in AdditionalContainers)
                choice.IsSelected = false;

            SelectedContainer = Containers.FirstOrDefault(c => c.Id == handoff.Container.Id)
                                ?? handoff.Container;
        }

        await LoadBackupsCommand.ExecuteAsync(null);

        // The filters land only when the load found them - a database the load did not see would
        // put the working set into a state the screen cannot have reached by hand.
        string? droppedFilter = null;
        if (handoff.ServerFilter != null)
        {
            // The way every other server comparison works (#288): identity, not ordinal. The
            // dashboard hands over the name msdb records while a blob listing infers from
            // paths - a case difference must not silently drop the instance filter.
            var match = Inventory.DiscoveredServers.FirstOrDefault(d =>
            {
                var (server, instance) = ServerIdentity.Split(d);
                return ServerIdentity.Matches(server, instance, handoff.ServerFilter);
            });

            if (match != null)
                Inventory.SelectedServerName = match;
            else
                droppedFilter = handoff.ServerFilter;
        }

        if (handoff.Database != null &&
            Inventory.DiscoveredDatabases.Contains(handoff.Database, StringComparer.OrdinalIgnoreCase))
            Inventory.SelectedDatabaseName =
                Inventory.DiscoveredDatabases.First(d =>
                    string.Equals(d, handoff.Database, StringComparison.OrdinalIgnoreCase));

        // Said last, so nothing the filters trigger can clear it: applying only the database
        // filter is a DIFFERENT question than the one clicked, and silence would present its
        // answer as the clicked one's.
        if (droppedFilter != null)
            SetStatus($"The instance filter '{droppedFilter}' is not among this source's " +
                      "instances, so every instance's backups are shown.");
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
            SelectedContainer == null ? null : BackupLocation.Blob(ContainersToRead),

        BackupMedium.SharedPath =>
            SourceServer == null
                ? null
                : BackupLocation.Shared(SourceServer, new BackupPathMapping(SourcePathPrefix, TargetPathPrefix)),

        // The same server picker as the shared path, deliberately: both media mean "an instance I
        // ask about backups", and for a file the natural reader is the target itself.
        BackupMedium.AdHocFile =>
            SourceServer == null || AdHocPaths.Count == 0
                ? null
                : BackupLocation.AdHoc(SourceServer, AdHocPaths),

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
        IOperationHistoryStore? history = null,
        IBackupAuditStore? auditStore = null,
        IRunNotifier? notifier = null)
    {
        _history = history ?? new OperationHistoryStore();

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
                    OnPropertyChanged(nameof(ShowAuditPanel));
                    RefreshSteps();
                    RefreshCheckState();
                    break;

                case nameof(BackupInventoryViewModel.SelectedServerName):
                    RefreshSteps();
                    RefreshCheckState();
                    break;
            }
        };

        Execution = new RestoreExecutionViewModel(sqlService, _history, _log, _queryCancellation, notifier);

        // The run reports through the same status line as the rest of the screen, and its cancel
        // state feeds the one Stop button that covers every server call here.
        Execution.CancelStateChanged += RefreshCancelState;

        // What Save output actually writes (#370). The console alone has none of the context and
        // none of the redaction.
        Execution.BuildSavedDocument = BuildSavedLog;
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

        // A chosen target survives source backtracking (#318). Changing the source withdraws
        // every later step's completion - the container cannot know the target is an independent
        // decision - so when the step is offered again this puts the answer still sitting in the
        // combo straight back, and the user lands on the restore point, not on a question they
        // already answered.
        Steps.Target.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(RestoreStep.IsVisible) &&
                Steps.Target.IsVisible && !Steps.Target.IsComplete && SelectedTargetServer != null)
                Steps.Target.Report(true, SelectedTargetServer.Name);
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
        PointInTime.PropertyChanged += (_, e) =>
        {
            if (PointInTime.IsUpdating) return;

            // The other half of the mark/time exclusivity (#243): turning the clock-time target
            // on clears the mark, exactly as choosing a mark turned the clock off.
            if (e.PropertyName == nameof(PointInTimeViewModel.Use) && PointInTime.Use)
                SelectedLogMark = null;

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
    private readonly IOperationHistoryStore _history;

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

        // The target step offers the same saved list (#318). A selection survives the rebuild by
        // identity; a target chosen on the Servers screen before this screen ever loaded is
        // re-pointed at the config instance so the combo shows it as the answer.
        var previousTarget = SelectedTargetServer?.Id ?? ConnectedServer?.Id;
        TargetServers = new ObservableCollection<ServerConnection>(config.Servers);
        if (previousTarget != null)
        {
            var match = TargetServers.FirstOrDefault(x => x.Id == previousTarget);
            if (match != null) SelectedTargetServer = match;
        }

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

        // After the primary is settled, so the list it excludes is the right one.
        RefreshAdditionalContainers();
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
            SetError(MediumIsAdHocFile
                ? "Enter at least one backup file path, and choose a server that can read it."
                : MediumIsSharedPath
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

        // Only judge an inventory that exists (#419). This runs off WorkingSetChanged, which is
        // raised on the path taken when NOTHING has been loaded and when no database has been
        // picked - so arriving at the screen used to put a red banner up saying there is no full
        // backup, when the truth was that nobody had pressed Load Backups yet.
        //
        // The same confusion as #368 and #356, pointing the other way: "not asked yet" reported
        // as "asked, and the answer is bad". Here it was the first thing on the screen.
        if (Inventory.AllSets.Count == 0 || string.IsNullOrEmpty(Inventory.SelectedDatabaseName))
        {
            ClearStatus();
            return;
        }

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

        // Captured before the await (#409), like every other server call on this screen. Reading
        // it again afterwards attributed one instance's default directories to another when the
        // connection changed mid-query - and those directories go into the WITH MOVE clauses, so
        // the restore would look correct and fail at run time with a directory-not-found, after
        // WITH REPLACE had already dropped the target. Disconnecting mid-query threw a
        // NullReferenceException that surfaced as "Could not fetch paths: Object reference not
        // set to an instance of an object."
        var server = ConnectedServer;

        PathSourceText = $"Querying {server.ServerName} for default paths...";
        try
        {
            var (dataPath, logPath) = await _sqlService.GetDefaultPathsAsync(
                server, _queryCancellation.Begin());
            var dbName = string.IsNullOrWhiteSpace(TargetDatabaseName) ? "DatabaseName" : TargetDatabaseName;

            if (!string.IsNullOrEmpty(dataPath))
                MoveDataFilePath = Path.Combine(dataPath, $"{dbName}.mdf");

            if (!string.IsNullOrEmpty(logPath))
                MoveLogFilePath = Path.Combine(logPath, $"{dbName}_log.ldf");
            else if (!string.IsNullOrEmpty(dataPath))
                MoveLogFilePath = Path.Combine(dataPath, $"{dbName}_log.ldf");

            PathsFromServer = true;
            PathSourceText = $"Paths detected from {server.ServerName}. You can still override them.";
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
                .Select(s => (IReadOnlyList<string>)s.Files.Select(f => f.IsOnDisk ? f.RestoreDevice : BlobUrlEncoder.Encode(f.BlobUrl)).ToList())
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

                var urls = set.Files.Select(f => f.IsOnDisk ? f.RestoreDevice : BlobUrlEncoder.Encode(f.BlobUrl)).ToList();
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
            // Says which state it is actually in (#420). "Connect to a SQL Server" was written for
            // somebody who had chosen no target at all; to somebody who HAS chosen one and could
            // not be reached, it reads as an instruction they have already followed.
            if (!IsConnectedToServer)
                return IsConnectingToTarget
                    ? $"Connecting to {SelectedTargetServer?.Name}..."
                    : SelectedTargetServer != null
                        ? $"{SelectedTargetServer.Name} could not be reached, so this restore " +
                          "cannot run from here. The script above is still valid - save it, or " +
                          "export it as a runbook or an Agent job."
                        : "Choose the target instance above to execute this restore.";
            if (!HasScript) return "No script yet - pick a restore point and a target database name.";
            if (HasChainErrors) return "This chain cannot restore. See the problems listed above.";
            return string.Empty;
        }
    }

    public bool IsExecuteBlocked => ExecuteBlockedReason.Length > 0;

    /// <summary>
    /// The button's own IsEnabled.
    ///
    /// NOT simply the inverse of the blocked reason (#401). That reason is deliberately empty
    /// while a restore runs - there is nothing to nag about mid-run, the Stop button is what is
    /// wanted then - and reading the enablement off it therefore left Execute LIVE during a
    /// restore. Two presses armed it and called RunAsync again, whose cancellation source cancels
    /// the run in flight: a production restore abandoned mid-chain, leaving the target in
    /// RESTORING, and started over from the top.
    ///
    /// So the two questions are asked separately. Backup and Copy Database both had this right
    /// already - CanExecute => HasScript &amp;&amp; !IsRunning - and this is the screen where
    /// getting it wrong costs the most, because WITH REPLACE has already dropped the target.
    /// </summary>
    public bool CanPressExecute => !IsExecuteBlocked && !IsExecuting;

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
    /// Why the script pane is empty, shown where the script would be (#user). The Generate
    /// button used to carry these as error dialogs; the script builds itself live now, so the
    /// explanation lives passively where the eye already is.
    /// </summary>
    [ObservableProperty]
    private string _scriptBlockedReason = string.Empty;

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

        // When nothing can be produced, say which input is missing - quietly, as a hint where
        // the script would be, because this now happens live rather than on a button press and
        // an error dialog per keystroke would be noise.
        var chain = RestoreChain;
        var blocked =
            chain == null
                ? "Select a restore point above and the script appears here."
            : SelectedLogMark != null && chain.LogSets.Count == 0
                ? "The marked transaction lives in a log backup, and this chain has none - " +
                  "restoring it would silently replay everything the mark was meant to stop. " +
                  "Pick a restore point with log backups, or clear the mark."
            : string.IsNullOrWhiteSpace(TargetDatabaseName)
                ? "Enter a target database name and the script appears here."
            : PointInTime.Use && PointInTime.CanUse && PointInTime.Effective == null
                ? $"The point-in-time target is not valid yet. {PointInTime.Message}"
            : !Options.HasStandbyFileIfNeeded
                ? "STANDBY needs an undo file path - without one the script would end in " +
                  "STANDBY = '', which fails after the database has already been overwritten."
            // The generator refuses this one outright rather than emitting it (#365); asked here
            // so the screen explains it instead of surfacing an exception.
            : RestoreScriptGenerator.DescribeSetWithNoFiles(chain);

        ScriptBlockedReason = blocked ?? string.Empty;

        if (blocked != null)
        {
            GeneratedScript = string.Empty;
            HasScript = false;
            return;
        }

        // The three things that are not options: which database, where the point-in-time target
        // ended up, and where the files land - each worked out somewhere that knows about the
        // chain or the server.
        var options = Options.Build(TargetDatabaseName, PointInTime.Effective, BuildFileMoves());
        options.StopAtMark = SelectedLogMark?.Name;
        options.StopBeforeMark = StopBeforeMark;

        // The source container's region rides into the RESTORE_OPTIONS when the source is S3
        // (#51). The generator ignores it - and clears it - for any non-S3 chain, so a stale
        // value cannot leak between sources.
        options.S3Region = SelectedContainer?.S3Region;

        GeneratedScript = _scriptGenerator.Generate(chain!, options);
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
    /// <summary>
    /// Writes the printed folder for the worst day (#240): the chain, the prerequisites in
    /// order, the exact script, what to do when it fails and what finishes the job - one
    /// self-contained Markdown file, readable on a laptop with no SQL tools installed.
    /// </summary>
    [RelayCommand]
    private void ExportRunbook()
    {
        if (!HasScript || RestoreChain == null) return;

        try
        {
            var runbook = RunbookBuilder.Build(new RunbookInputs
            {
                Chain = RestoreChain,
                Script = GeneratedScript,
                TargetDatabase = TargetDatabaseName,
                ServerName = ConnectedServer?.ServerName,
                ContainerName = SelectedContainer?.Name,
                SourceDatabase = Inventory.SelectedDatabaseName,
                RestorePoint = Timeline.SelectedPoint?.Timestamp,
                FileMoves = FetchedFileMoves.ToList()
            });

            var dialog = new SaveFileDialog
            {
                Title = "Export restore runbook",
                FileName = $"runbook-{TargetDatabaseName}-{DateTime.Now:yyyyMMdd}.md",
                Filter = "Markdown|*.md|All files|*.*",
                DefaultExt = ".md"
            };

            if (dialog.ShowDialog() != true) return;

            System.IO.File.WriteAllText(dialog.FileName, runbook);
            SetStatus($"Runbook written to {dialog.FileName}. Regenerate it after any change to " +
                      "the chain or options - a stale runbook is a wrong one that looks right.");
        }
        catch (Exception ex)
        {
            SetError($"The runbook could not be written: {ex.Message}");
        }
    }

    [RelayCommand]
    private void CopyAsAgentJob()
    {
        if (!HasScript) return;

        var job = SqlAgentJobScript.Wrap(
            GeneratedScript,
            SqlAgentJobScript.SuggestName("restore", TargetDatabaseName, DateTime.Now),
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
        if (RestoreChain == null || ConnectedServer == null) return;
        if (SelectedContainer == null && !RestoreChain.FullSet.Files.All(f => f.IsOnDisk)) return;

        var ct = _queryCancellation.Begin();
        IsBusy = true;
        RefreshCancelState();
        BackupMetadataSummary = null;
        // Captured BEFORE the await. Selecting a different restore point sets RestoreChain to null
        // on the UI thread, and it can do that while this call is in flight - in which case the
        // catch below would itself throw, replacing the error explaining why FILELISTONLY failed
        // with a bare "Nine Lives hit an unexpected error".
        // Use URL without SAS and omit WITH CREDENTIAL. Encode path so spaces/special chars (e.g. in folder names) are valid.
        // By the device a RESTORE would name it: an encoded URL for blob, the raw path for
        // disk - a path is not a URI, and percent-encoding one breaks it (#203).
        var urls = RestoreChain.FullSet.Files
            .Select(f => f.IsOnDisk ? f.RestoreDevice : BlobUrlEncoder.Encode(f.BlobUrl))
            .ToList();

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
        IsConnectedToServer && RestoreChain != null && RestoreChain.FullSet.Files.Count > 0
        // A container for blob URLs; a chain on disk needs none - FILELISTONLY reads the path
        // directly, and demanding a container here kept MOVE defaults blob-only (#203).
        && (SelectedContainer != null || RestoreChain.FullSet.Files.All(f => f.IsOnDisk));

    [RelayCommand(CanExecute = nameof(CanInspectMetadata))]
    private async Task InspectBackupMetadataAsync()
    {
        if (RestoreChain == null || ConnectedServer == null || SelectedContainer == null) return;

        var ct = _queryCancellation.Begin();
        IsBusy = true;
        RefreshCancelState();
        // Captured before the await - see the note on FetchLogicalNamesAsync.
        // Use URL without SAS and omit WITH CREDENTIAL. Encode path so spaces/special chars (e.g. in folder names) are valid.
        // By the device a RESTORE would name it: an encoded URL for blob, the raw path for
        // disk - a path is not a URI, and percent-encoding one breaks it (#203).
        var urls = RestoreChain.FullSet.Files
            .Select(f => f.IsOnDisk ? f.RestoreDevice : BlobUrlEncoder.Encode(f.BlobUrl))
            .ToList();

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

            if (header.SoftwareVersionMajor is int major)
                sb.AppendLine($"Taken on: {VersionCompatibility.Describe(major)}");

            // What protects it (#222 part 4): the certificate question, answered where the header
            // is on screen instead of only failing later. Absence is stated too - "not encrypted"
            // is information, not the lack of it.
            var protection = EncryptionGuidance.DescribeProtection(
                header.TdeThumbprint, header.EncryptorThumbprint);
            sb.AppendLine($"Protection: {(protection.Length == 0 ? "Not encrypted." : protection)}");

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
        // By the device a RESTORE would name it: an encoded URL for blob, the raw path for
        // disk - a path is not a URI, and percent-encoding one breaks it (#203).
        var urls = RestoreChain.FullSet.Files
            .Select(f => f.IsOnDisk ? f.RestoreDevice : BlobUrlEncoder.Encode(f.BlobUrl))
            .ToList();
        var urlClauses = string.Join(", ", urls.Select(u => $"URL = N'{TSql.EscapeLiteral(u)}'"));
        var sql = $"RESTORE FILELISTONLY FROM {urlClauses}";
        TryCopyToClipboard(sql, "RESTORE FILELISTONLY command copied to clipboard. Paste into SSMS to run.");
    }

    /// <summary>Builds the exact T-SQL for RESTORE HEADERONLY (encoded URLs, no WITH CREDENTIAL) for pasting into SSMS.</summary>
    [RelayCommand(CanExecute = nameof(CanInspectMetadata))]
    private void CopyHeaderOnlyCommand()
    {
        if (RestoreChain == null || RestoreChain.FullSet.Files.Count == 0) return;
        // By the device a RESTORE would name it: an encoded URL for blob, the raw path for
        // disk - a path is not a URI, and percent-encoding one breaks it (#203).
        var urls = RestoreChain.FullSet.Files
            .Select(f => f.IsOnDisk ? f.RestoreDevice : BlobUrlEncoder.Encode(f.BlobUrl))
            .ToList();
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

    // ── marked transactions (#243) ──────────────────────────────────────────────

    /// <summary>The marks msdb knows for the selected database, newest first.</summary>
    [ObservableProperty]
    private System.Collections.ObjectModel.ObservableCollection<LogMark> _logMarks = [];

    /// <summary>
    /// The mark to stop at. Choosing one turns the time-based target OFF - a mark and a clock
    /// time are the same mechanism aimed differently, and two targets on one restore is a script
    /// that stops where the READER did not decide.
    /// </summary>
    [ObservableProperty]
    private LogMark? _selectedLogMark;

    partial void OnSelectedLogMarkChanged(LogMark? value)
    {
        if (value != null && PointInTime.Use) PointInTime.Use = false;
        UpdateRestoreSummary();
    }

    /// <summary>STOPBEFOREMARK by default: "undo the deployment" is what the mark was planted for.</summary>
    [ObservableProperty]
    private bool _stopBeforeMark = true;

    partial void OnStopBeforeMarkChanged(bool value) => UpdateRestoreSummary();

    /// <summary>
    /// Reads logmarkhistory for the selected database (#243). On demand, because most databases
    /// have no marks and the combo should not cost a round trip to prove it.
    ///
    /// Asked on the SOURCE instance when the medium has one (#268): logmarkhistory is written
    /// where the marked transactions RAN, so on a shared-path restore to a different target, the
    /// target's msdb has never heard of them - asking it reports "no marks" when the truth is
    /// "wrong catalogue". Blob has no source instance, so the connected server answers there;
    /// it also covers ad-hoc's file-that-outlived-its-server case as best effort. The marks
    /// themselves ride inside the log backups either way - this is discovery, not correctness.
    /// </summary>
    [RelayCommand]
    private async Task LoadLogMarksAsync()
    {
        var server = (MediumIsBlob ? null : SourceServer) ?? ConnectedServer;
        var database = Inventory.SelectedDatabaseName;

        if (server == null || string.IsNullOrWhiteSpace(database))
        {
            SetError(MediumIsBlob
                ? "Connect to a server and choose a database first - the marks live in its msdb."
                : "Choose the source server and a database first - the marks were recorded in its msdb.");
            return;
        }

        try
        {
            var marks = await _sqlService.GetLogMarksAsync(server, database!);
            LogMarks = new System.Collections.ObjectModel.ObservableCollection<LogMark>(marks);

            SetStatus(marks.Count == 0
                ? $"No marked transactions recorded for {database}. Marks are planted with " +
                  "BEGIN TRANSACTION ... WITH MARK before risky work."
                : $"{marks.Count} marked transaction(s) found for {database}.");
        }
        catch (Exception ex)
        {
            SetError($"Could not read the marks: {ex.Message}");
        }
    }

    // ── retention (#241) ────────────────────────────────────────────────────────

    /// <summary>The window a rule would keep, in days. 30 is the commonest policy default.</summary>
    [ObservableProperty]
    private int _retentionKeepDays = 30;

    [ObservableProperty]
    private string _retentionSummary = string.Empty;

    /// <summary>The sets a rule must NOT delete despite their age, each with its reason.</summary>
    [ObservableProperty]
    private System.Collections.ObjectModel.ObservableCollection<string> _retentionWarnings = [];

    public bool CanAdviseRetention => Inventory.AllSets.Count > 0;

    /// <summary>
    /// What a keep-N-days rule would do to the loaded backups (#241): what goes, what must stay
    /// despite its age because kept restores depend on it, and what is already broken.
    ///
    /// Report-only, deliberately: the app that exists to make restores safe should not also be
    /// the app that deletes backups. Deleting stays a human act, done elsewhere, with this
    /// report in hand.
    /// </summary>
    [RelayCommand]
    private void AdviseRetention()
    {
        if (Inventory.AllSets.Count == 0)
        {
            RetentionSummary = "Load backups first - the advisor judges what is on screen.";
            return;
        }

        var findings = RetentionAdvisor.Advise(Inventory.AllSets, RetentionKeepDays, DateTime.Now);

        RetentionSummary = RetentionAdvisor.Summarise(findings, RetentionKeepDays);

        RetentionWarnings = new System.Collections.ObjectModel.ObservableCollection<string>(
            findings
                .Where(f => f.Verdict is RetentionVerdict.KeepDespiteAge or RetentionVerdict.Broken)
                .Select(f => $"{f.Set.DatabaseName} {f.Set.Type} {f.Set.Timestamp:yyyy-MM-dd HH:mm} - " +
                             (f.Verdict == RetentionVerdict.Broken ? "ALREADY BROKEN: " : "MUST KEEP: ") +
                             f.Reason));

        _log.Info($"[retention] {RetentionSummary}");
    }

    // ── rehearsal (#238) ────────────────────────────────────────────────────────

    /// <summary>
    /// Everything a rehearsal needs to exist: a chain, a connection, and the mode that shows the
    /// checking machinery. The rest is safety by construction inside the run itself.
    /// </summary>
    public bool CanRehearse =>
        IsConnectedToServer && RestoreChain != null && !IsExecuting &&
        AppModeCapabilities.CanVerifyAndAudit(Mode);

    /// <summary>
    /// Restores the chosen chain to a scratch name, proves it with CHECKDB, and drops it (#238) -
    /// the only tested backup is a restored backup, and the History entry is the receipt.
    ///
    /// The real database is never touchable from here: the target name is generated and refused
    /// if it exists, WITH REPLACE is never emitted, and every file MOVEs to scratch-named files
    /// in the instance's default directories. A failure at any step stops the batch chain, so
    /// the guarded DROP never runs and the scratch copy stands as the evidence.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRehearse))]
    private async Task RehearseAsync()
    {
        var server = ConnectedServer;
        var chain = RestoreChain;
        if (server == null || chain == null || !CanRehearse) return;

        var sourceName = Inventory.SelectedDatabaseName ?? chain.FullSet.DatabaseName ?? "database";
        var scratch = RehearsalPlanner.ScratchName(sourceName, DateTime.Now);

        IsBusy = true;
        try
        {
            // Astronomically unlikely with a timestamped name - checked anyway, because the whole
            // design promise is "cannot touch anything that exists".
            var state = await _sqlService.GetDatabaseRecoveryStateAsync(server, scratch);
            if (state.Exists)
            {
                SetError($"[{scratch}] already exists on {server.ServerName} - rehearsal refused. " +
                         "Try again in a minute, or drop the leftover scratch copy first.");
                return;
            }

            SetStatus($"Preparing the rehearsal of {sourceName}...");

            // The real file list, so every logical file gets a scratch MOVE - device-aware, so
            // this works for blob, shared-path and ad-hoc chains alike (#203).
            var devices = chain.FullSet.Files
                .Select(f => f.IsOnDisk ? f.RestoreDevice : BlobUrlEncoder.Encode(f.BlobUrl))
                .ToList();
            var files = await _sqlService.RestoreFileListOnlyAsync(server, devices);
            if (files.Count == 0)
            {
                SetError("FILELISTONLY returned nothing - the rehearsal cannot relocate files it " +
                         "cannot list.");
                return;
            }

            var (dataPath, logPath) = await _sqlService.GetDefaultPathsAsync(server);
            var moves = RehearsalPlanner.ScratchMoves(files, scratch, dataPath, logPath);

            var restoreScript = _scriptGenerator.Generate(chain, new RestoreOptions
            {
                TargetDatabaseName = scratch,
                WithReplace = false,
                RecoveryMode = RecoveryMode.Recovery,
                FileMoves = moves,
                StopAt = PointInTime.Effective
            });

            var script = RehearsalPlanner.BuildScript(restoreScript, scratch);

            await Execution.RunAsync(
                new RestoreRun(
                    server,
                    script,
                    scratch,
                    sourceName,
                    SelectedContainer?.Name,
                    $"REHEARSAL - {chain.Summary}",
                    Timeline.SelectedPoint?.Timestamp,
                    "rehearsal: restore + CHECKDB + drop",
                    RunKind.Rehearsal),
                appendLog => PreflightAsync(server, appendLog));
        }
        catch (Exception ex)
        {
            SetError($"The rehearsal could not be prepared: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// The rehearsal as a disabled, unscheduled Agent job (#259): the proof renews itself weekly
    /// once somebody adds a schedule, receipts accumulating in the job history. Same prep as the
    /// clicked rehearsal, but the scratch name is STABLE with a guarded pre-drop of its own
    /// leftover - a timestamped name would collide with itself on the job's second run.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRehearse))]
    private async Task CopyRehearsalAsAgentJobAsync()
    {
        var server = ConnectedServer;
        var chain = RestoreChain;
        if (server == null || chain == null || !CanRehearse) return;

        var sourceName = Inventory.SelectedDatabaseName ?? chain.FullSet.DatabaseName ?? "database";
        var scratch = RehearsalPlanner.ScheduledScratchName(sourceName);

        IsBusy = true;
        try
        {
            var devices = chain.FullSet.Files
                .Select(f => f.IsOnDisk ? f.RestoreDevice : BlobUrlEncoder.Encode(f.BlobUrl))
                .ToList();
            var files = await _sqlService.RestoreFileListOnlyAsync(server, devices);
            if (files.Count == 0)
            {
                SetError("FILELISTONLY returned nothing - the rehearsal cannot relocate files it " +
                         "cannot list.");
                return;
            }

            var (dataPath, logPath) = await _sqlService.GetDefaultPathsAsync(server);
            var moves = RehearsalPlanner.ScratchMoves(files, scratch, dataPath, logPath);

            var restoreScript = _scriptGenerator.Generate(chain, new RestoreOptions
            {
                TargetDatabaseName = scratch,
                WithReplace = false,
                RecoveryMode = RecoveryMode.Recovery,
                FileMoves = moves
            });

            var script = RehearsalPlanner.BuildScheduledScript(restoreScript, scratch);

            var job = SqlAgentJobScript.Wrap(
                script,
                $"NineLives rehearsal - {sourceName}",
                $"Weekly proof that {sourceName} restores: scratch restore, DBCC CHECKDB, drop. " +
                "A failed run retains the scratch copy as evidence until the next run clears it.");

            TryCopyToClipboard(job,
                "Rehearsal job copied to clipboard. Created disabled and unscheduled - add the " +
                "weekly schedule and enable it deliberately. Each run's outcome lands in the " +
                "job history; a failure retains the scratch copy as evidence.");
        }
        catch (Exception ex)
        {
            SetError($"The rehearsal job could not be prepared: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
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
        // Can this engine speak S3 at all (#51)? Asked first, and asked here rather than only in
        // the CLI - which is where it shipped, leaving the app promising a check that only the
        // other front end performed. A capability, so nothing overrides it: the connector is
        // either in the engine or it is not.
        if (S3CapabilityPreflight.UsesS3(RestoreChain))
        {
            appendLog("Checking the target can restore from S3-compatible storage...");
            if (await S3CapabilityPreflight.RefusalAsync(_sqlService, server, RestoreChain)
                is { } s3Refusal)
                return CredentialPreflight.Stop(s3Refusal);
        }

        // Both non-blob media end in FROM DISK on the target, so both need the same answer: can
        // the instance that will run the RESTORE actually read these files (#203).
        if (MediumIsSharedPath || MediumIsAdHocFile)
        {
            var readable = await CheckTargetCanReadFilesAsync(server, appendLog);
            if (!readable.CanProceed) return readable;

            return await CheckVersionCompatibilityAsync(server, appendLog);
        }

        var primary = await Credential.PrepareForRestoreAsync(server, appendLog);
        if (!primary.CanProceed) return primary;

        var containers = await CheckOtherContainersHaveCredentialsAsync(server, appendLog);
        if (!containers.CanProceed) return containers;

        await ReportSpaceAsync(appendLog);

        return await CheckVersionCompatibilityAsync(server, appendLog);
    }

    /// <summary>
    /// Puts the free-space answer into the run's own record, every run (#370).
    ///
    /// The check itself has one caller - Get file names - so whether a restore was checked for
    /// room depended on whether somebody had pressed an optional button, on a screen whose own
    /// comment calls running out of disk "the worst outcome this screen can produce". Its inputs
    /// are the file sizes RESTORE FILELISTONLY returns, so it genuinely cannot run without that
    /// read; what it can do is stop being silent about which of the two happened.
    ///
    /// A warning, not a refusal, deliberately. This reads someone else's DMV about someone else's
    /// storage, and an instance that under-reports free space must not be able to block a restore
    /// that would have worked - the same reasoning that already makes a failure to answer silent
    /// rather than scary. What changes is that the answer, or its absence, is now on the record
    /// beside everything else the run did.
    /// </summary>
    private async Task ReportSpaceAsync(Action<string> appendLog)
    {
        try
        {
            if (FetchedFileMoves.Count == 0)
            {
                appendLog(
                    "Free space on the target was not checked - press Get file names before a " +
                    "restore to have it read the file sizes and compare them with the target's " +
                    "volumes.");
                return;
            }

            await CheckSpaceAsync(CancellationToken.None);

            appendLog(HasSpaceWarning
                ? $"WARNING: {SpaceWarning}"
                : "Checked free space on the target: the restored files fit.");
        }
        catch (Exception ex)
        {
            // Never the reason a restore does not start.
            _log.Info($"[space] could not check before execute: {ex.Message}");
        }
    }

    /// <summary>
    /// Refuses a restore whose certificate is missing from the target (#222).
    ///
    /// Two thumbprints can appear in a header, and both mean the same thing: TDEThumbprint says
    /// the database inside the backup is TDE-encrypted, EncryptorThumbprint says the backup file
    /// itself was taken WITH ENCRYPTION. Either way the target must hold a certificate with that
    /// thumbprint in master, or the restore fails with error 33111.
    ///
    /// When the source instance is known (the shared path knows it), the refusal also NAMES the
    /// certificate there - BACKUP CERTIFICATE takes a name, not a thumbprint, and mid-incident is
    /// the wrong time to go looking for which one. Null return means no objection.
    /// </summary>
    private async Task<CredentialPreflight?> CheckCertificatesAsync(
        ServerConnection server, BackupFileInfo? header, Action<string> appendLog)
    {
        if (header == null) return null;

        foreach (var (thumbprint, isTde) in new[]
                 {
                     (header.TdeThumbprint, true),
                     (header.EncryptorThumbprint, false)
                 })
        {
            if (thumbprint == null) continue;

            appendLog(EncryptionGuidance.DescribeProtection(
                isTde ? thumbprint : null, isTde ? null : thumbprint));

            var onTarget = await _sqlService.FindCertificateByThumbprintAsync(server, thumbprint);
            if (onTarget != null)
            {
                appendLog(EncryptionGuidance.ExplainPresent(isTde, onTarget, server.ServerName));
                continue;
            }

            // Name the certificate on the source when an instance is there to ask - best effort,
            // and only meaningful for media that HAVE a source instance.
            string? sourceCertName = null;
            var sourceServer = MediumIsBlob ? null : SourceServer;
            if (sourceServer != null && sourceServer.Id != server.Id)
            {
                try
                {
                    sourceCertName =
                        await _sqlService.FindCertificateByThumbprintAsync(sourceServer, thumbprint);
                }
                catch
                {
                    // The refusal stands on the thumbprint alone.
                }
            }

            return CredentialPreflight.Stop(EncryptionGuidance.ExplainMissingCertificate(
                isTde, thumbprint, server.ServerName, sourceCertName, sourceServer?.ServerName));
        }

        return null;
    }

    /// <summary>
    /// Refuses a newer version's backup before anything is touched (#210).
    ///
    /// The one-directional law of RESTORE: a backup taken on a newer major version can never be
    /// restored onto an older one - error 3169, no exceptions, not even between adjacent releases.
    /// Without this it arrives from the server mid-restore, after WITH REPLACE has already dropped
    /// the database being restored over. Same failure shape as every other preflight here, and the
    /// cheapest: the header carries the version, and the target's is one SERVERPROPERTY away.
    ///
    /// One HEADERONLY on the full set, on the target, device-aware since #203 - so this one path
    /// serves blob, shared path and ad-hoc alike. Best effort throughout: no verdict from silence,
    /// because refusing on a guess would block legal restores.
    /// </summary>
    private async Task<CredentialPreflight> CheckVersionCompatibilityAsync(
        ServerConnection server, Action<string> appendLog)
    {
        var files = RestoreChain?.FullSet.Files;
        if (files == null || files.Count == 0) return CredentialPreflight.Proceed;

        try
        {
            appendLog("Comparing the backup's SQL Server version with the target's...");

            var devices = files
                .Select(f => f.IsOnDisk ? f.RestoreDevice : BlobUrlEncoder.Encode(f.BlobUrl))
                .ToList();

            var header = await _sqlService.RestoreHeaderOnlyMultiAsync(server, devices);
            var targetMajor = await _sqlService.GetProductMajorVersionAsync(server);

            switch (VersionCompatibility.Check(header?.SoftwareVersionMajor, targetMajor))
            {
                case VersionVerdict.Refuse:
                    return CredentialPreflight.Stop(
                        VersionCompatibility.ExplainRefusal(
                            header!.SoftwareVersionMajor!.Value, targetMajor!.Value, server.ServerName));

                case VersionVerdict.UpgradeInPassing:
                    appendLog(VersionCompatibility.ExplainUpgrade(
                        header!.SoftwareVersionMajor!.Value, targetMajor!.Value));
                    break;

                default:
                    appendLog("Versions are compatible.");
                    break;
            }

            // The same header answers the certificate question (#222) - the failure that
            // otherwise arrives as error 33111, mid-DR, with the source server possibly gone.
            var certVerdict = await CheckCertificatesAsync(server, header, appendLog);
            if (certVerdict is { } certificateStop) return certificateStop;
        }
        catch (Exception ex)
        {
            // The check could not run, which is not the same as the check failing. The restore
            // itself will read the same header in a moment and report its own, better error if
            // something is genuinely wrong with the file.
            appendLog($"Could not compare versions: {ex.Message}");
        }

        return CredentialPreflight.Proceed;
    }

    /// <summary>
    /// The containers this chain's files actually came from (#32).
    ///
    /// From the FILES rather than from what is ticked on screen: a container that was read but
    /// contributed nothing to this chain needs no credential for this restore, and saying it does
    /// would be a refusal nobody can act on sensibly.
    /// </summary>
    private IReadOnlyList<BlobContainerConfig> ChainContainers()
    {
        var ids = RestoreChain?.AllFiles
            .Select(f => f.ContainerId)
            .Where(id => !string.IsNullOrEmpty(id))
            .Distinct()
            .ToHashSet() ?? [];

        return [.. ContainersToRead.Where(c => ids.Contains(c.Id))];
    }

    /// <summary>
    /// Every container beyond the primary needs its own credential on the target (#32).
    ///
    /// <c>RESTORE ... FROM URL</c> matches a credential by URL prefix, so a chain whose full is in
    /// one container and whose logs are in another needs a credential for BOTH - and the panel only
    /// ever points at the primary. Without this the restore starts, reads the full, and fails on
    /// the first log with "Cannot open backup device" - after WITH REPLACE has already dropped the
    /// database being restored over.
    ///
    /// Checked, not created. Creating one needs a stored SAS this app may not have, and writing
    /// server-side credentials nobody asked for is what #145 and #10 are both about. Refusing
    /// before anything is touched leaves nothing to unwind.
    /// </summary>
    private async Task<CredentialPreflight> CheckOtherContainersHaveCredentialsAsync(
        ServerConnection server, Action<string> appendLog)
    {
        var others = ChainContainers()
            .Where(c => c.Id != SelectedContainer?.Id)
            .ToList();

        if (others.Count == 0) return CredentialPreflight.Proceed;

        appendLog($"This chain spans {others.Count + 1} containers. "
                  + $"Checking the others have credentials on {server.ServerName}...");

        var missing = new List<string>();

        foreach (var container in others)
        {
            var status = await _sqlService.CredentialExistsAsync(server, container.ContainerUrl);

            if (status.CanRestoreFromUrl)
                appendLog($"  {container.Name}: credential present.");
            else
                missing.Add(container.Name);
        }

        if (missing.Count == 0) return CredentialPreflight.Proceed;

        return CredentialPreflight.Stop(
            $"This restore also reads from {string.Join(" and ", missing)}, and {server.ServerName} "
            + $"has no credential for {(missing.Count == 1 ? "it" : "them")}. RESTORE FROM URL "
            + "matches a credential by container URL, so every container in the chain needs one. "
            + "Select each as the container above and use Create credential, then come back - "
            + "nothing has been changed on the server or the database.");
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
