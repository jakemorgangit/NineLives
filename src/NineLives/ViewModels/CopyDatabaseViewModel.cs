using System.Collections.ObjectModel;
using Blackcat.NineLives.Models;
using Blackcat.NineLives.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Blackcat.NineLives.ViewModels;

/// <summary>
/// Put a copy of this database from one server onto another, in one action (#105).
///
/// The job the app could not do until both halves existed. It is deliberately not a third
/// implementation of anything: the backup is the backup screen's generator and destinations, the
/// restore is the restore screen's generator, and both run through the same execution path. What is
/// new is only the orchestration between them, and what has to be true for it to be safe.
///
/// Two things make this more dangerous than either half alone.
///
/// First, TWO servers are involved and only one of them is the one being protected. The source is
/// read - which is why COPY_ONLY matters here even more than it does on the backup screen. The
/// target is overwritten. A confirmation that names only one of them is a confirmation somebody can
/// misread, so this one names both.
///
/// Second, a partial run is not a failure. A backup that succeeded and a restore that did not has
/// left a perfectly good backup behind, and the right next step is to restore from it - not to run
/// the whole thing again and read a production database at full speed for a second time.
/// </summary>
public partial class CopyDatabaseViewModel : ViewModelBase
{
    private readonly IRunNotifier _notifier;
    private readonly ICredentialStore _store;
    private readonly ISqlServerService _sql;
    private readonly BackupScriptGenerator _backupGenerator = new();
    private readonly RestoreScriptGenerator _restoreGenerator = new();
    private readonly OperationCancellation _cancellation = new();
    private readonly OperationLog _log;

    public CopyDatabaseViewModel(
        ICredentialStore store, ISqlServerService sql, OperationLog? log = null,
        IRunNotifier? notifier = null)
    {
        _notifier = notifier ?? NullRunNotifier.Instance;
        _store = store;
        _sql = sql;
        _log = log ?? App.Log;

        Refresh();
    }

    // ── the two ends ────────────────────────────────────────────────────────────

    [ObservableProperty]
    private ObservableCollection<ServerConnection> _servers = [];

    /// <summary>The instance the database is copied FROM. It is read, and it is backed up.</summary>
    [ObservableProperty]
    private ServerConnection? _sourceServer;

    /// <summary>The instance the copy lands on. It is overwritten.</summary>
    [ObservableProperty]
    private ServerConnection? _targetServer;

    [ObservableProperty]
    private ObservableCollection<string> _sourceDatabases = [];

    [ObservableProperty]
    private string? _sourceDatabase;

    [ObservableProperty]
    private string _targetDatabaseName = string.Empty;

    [ObservableProperty]
    private ObservableCollection<BlobContainerConfig> _containers = [];

    [ObservableProperty]
    private BlobContainerConfig? _container;

    [ObservableProperty]
    private BackupMedium _medium = BackupMedium.AzureBlob;

    public bool MediumIsBlob => Medium == BackupMedium.AzureBlob;
    public bool MediumIsSharedPath => Medium == BackupMedium.SharedPath;

    /// <summary>
    /// The folder the backup is written to and read back from.
    ///
    /// Both instances have to reach it, and each reaches it as its OWN service account - the source
    /// has to write there and the target has to read there. Neither is the same as this app being
    /// able to see it.
    /// </summary>
    [ObservableProperty]
    private string _sharedPathRoot = string.Empty;

    /// <summary>
    /// COPY_ONLY, on by default. The source here is somebody else's production server, so moving
    /// its differential base as a side effect of refreshing a test environment is the exact thing
    /// this default exists to stop.
    /// </summary>
    [ObservableProperty]
    private bool _copyOnly = true;

    [ObservableProperty]
    private bool _compression = true;

    [ObservableProperty]
    private bool _withReplace = true;

    public void Refresh()
    {
        try
        {
            var config = _store.LoadConfig();

            var previousSource = SourceServer?.Id;
            var previousTarget = TargetServer?.Id;
            var previousContainer = Container?.Id;

            Servers = new ObservableCollection<ServerConnection>(config.Servers);
            Containers = new ObservableCollection<BlobContainerConfig>(config.BlobContainers);

            if (previousSource != null) SourceServer = Servers.FirstOrDefault(s => s.Id == previousSource);
            if (previousTarget != null) TargetServer = Servers.FirstOrDefault(s => s.Id == previousTarget);
            if (previousContainer != null) Container = Containers.FirstOrDefault(c => c.Id == previousContainer);
        }
        catch
        {
            Servers = [];
            Containers = [];
        }
    }

    [RelayCommand]
    private async Task LoadSourceDatabasesAsync()
    {
        if (SourceServer == null)
        {
            SetError("Choose the server to copy from.");
            return;
        }

        var ct = _cancellation.Begin();
        IsBusy = true;
        ClearStatus();

        try
        {
            var names = await _sql.GetDatabaseListAsync(SourceServer, ct);
            SourceDatabases = new ObservableCollection<string>(names);

            // Nothing is chosen for the user. Here the wrong choice reads a production database at
            // full speed AND overwrites a database on another server.
            SourceDatabase = null;

            SetStatus($"{SourceServer.ServerName} has {names.Count} database(s).");
        }
        catch (OperationCanceledException)
        {
            SetStatus("Stopped.");
        }
        catch (Exception ex)
        {
            SetError($"Could not list the databases: {ex.Message}");
        }
        finally
        {
            _cancellation.End();
            IsBusy = false;
        }
    }

    partial void OnSourceServerChanged(ServerConnection? value)
    {
        SourceDatabases = [];
        SourceDatabase = null;
        Invalidate();
    }

    partial void OnTargetServerChanged(ServerConnection? value) => Invalidate();
    partial void OnSourceDatabaseChanged(string? value) => Invalidate();
    partial void OnTargetDatabaseNameChanged(string value) => Invalidate();
    partial void OnContainerChanged(BlobContainerConfig? value) => Invalidate();
    partial void OnSharedPathRootChanged(string value) => Invalidate();
    partial void OnCopyOnlyChanged(bool value) => Invalidate();
    partial void OnCompressionChanged(bool value) => Invalidate();
    partial void OnWithReplaceChanged(bool value) => Invalidate();

    partial void OnMediumChanged(BackupMedium value)
    {
        OnPropertyChanged(nameof(MediumIsBlob));
        OnPropertyChanged(nameof(MediumIsSharedPath));
        Invalidate();
    }

    // ── the guard rails ─────────────────────────────────────────────────────────

    /// <summary>
    /// Why this copy is refused, or empty when it is not.
    ///
    /// Separate from CanGenerate, which only asks whether the inputs are filled in. This asks
    /// whether the thing being described should happen at all, and it is shown rather than merely
    /// disabling a button - a button that is dead for no stated reason is one somebody works around.
    /// </summary>
    public string Refusal
    {
        get
        {
            if (SourceServer == null || TargetServer == null) return string.Empty;
            if (string.IsNullOrWhiteSpace(SourceDatabase)) return string.Empty;
            if (string.IsNullOrWhiteSpace(TargetDatabaseName)) return string.Empty;

            // Onto itself, under its own name. That is not a copy - it is a backup of a database
            // immediately restored over the top of the database it was taken from, which at best
            // is an expensive no-op and at worst destroys it when the restore fails halfway.
            var sameInstance = string.Equals(
                SourceServer.ServerName, TargetServer.ServerName, StringComparison.OrdinalIgnoreCase);

            var sameName = string.Equals(
                SourceDatabase, TargetDatabaseName, StringComparison.OrdinalIgnoreCase);

            if (sameInstance && sameName)
                return $"That would restore {SourceDatabase} over itself on {SourceServer.ServerName}. " +
                       "Give the copy a different name, or choose a different server to restore onto.";

            return string.Empty;
        }
    }

    public bool IsRefused => Refusal.Length > 0;

    /// <summary>
    /// True when the copy overwrites a database that is already there.
    ///
    /// Not a refusal - it is usually the whole point, a test environment being refreshed. But it is
    /// the part of this that destroys something, so it is stated rather than buried in a checkbox.
    /// </summary>
    public bool WillOverwriteTheTarget => WithReplace && !string.IsNullOrWhiteSpace(TargetDatabaseName);

    /// <summary>True when the source's own differential schedule is about to be disturbed.</summary>
    public bool WillResetTheDifferentialBase => !CopyOnly;

    // ── the two scripts, together ───────────────────────────────────────────────

    /// <summary>What the source will be asked to do.</summary>
    [ObservableProperty]
    private string _backupScript = string.Empty;

    /// <summary>What the target will be asked to do.</summary>
    [ObservableProperty]
    private string _restoreScript = string.Empty;

    /// <summary>Where the backup goes, and is read back from.</summary>
    [ObservableProperty]
    private ObservableCollection<string> _destinations = [];

    public bool HasScripts => BackupScript.Length > 0 && RestoreScript.Length > 0;

    partial void OnBackupScriptChanged(string value) => OnPropertyChanged(nameof(HasScripts));

    partial void OnRestoreScriptChanged(string value)
    {
        OnPropertyChanged(nameof(HasScripts));
        RunCommand.NotifyCanExecuteChanged();
    }

    public bool CanGenerate =>
        SourceServer != null &&
        TargetServer != null &&
        !string.IsNullOrWhiteSpace(SourceDatabase) &&
        !string.IsNullOrWhiteSpace(TargetDatabaseName) &&
        (MediumIsBlob ? Container != null : !string.IsNullOrWhiteSpace(SharedPathRoot)) &&
        !IsRefused;

    private void Invalidate()
    {
        OnPropertyChanged(nameof(Refusal));
        OnPropertyChanged(nameof(IsRefused));
        OnPropertyChanged(nameof(CanGenerate));
        OnPropertyChanged(nameof(WillOverwriteTheTarget));
        OnPropertyChanged(nameof(WillResetTheDifferentialBase));
        GenerateCommand.NotifyCanExecuteChanged();

        // An armed run that survives a change is armed for something else.
        IsArmed = false;

        if (!HasScripts) return;

        // Regenerate - or, when the inputs no longer support generating, CLEAR (#278).
        // Blanking the target name left the previous scripts standing and CanRun true: the
        // Refusal getter answers nothing for incomplete input, so the screen showed runnable
        // statements naming a target nobody was asking for any more.
        if (CanGenerate) Generate();
        else ClearGenerated();
    }

    /// <summary>Removes every trace of the last generation, so nothing stale can run.</summary>
    private void ClearGenerated()
    {
        BackupScript = string.Empty;
        RestoreScript = string.Empty;
        Destinations = [];
        OnPropertyChanged(nameof(CanRun));
        RunCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// Both statements, shown together.
    ///
    /// Together deliberately: what is about to happen to a production server and what is about to
    /// happen to another one are one decision, and reading them one at a time is how somebody
    /// approves the half they were looking at.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanGenerate))]
    private void Generate()
    {
        if (!CanGenerate) return;

        var takenAt = DateTime.Now;

        var destinations = MediumIsBlob
            ? BackupDestinationBuilder.ForContainer(
                Container!, SourceServer!.ServerName, SourceDatabase!, BackupType.Full, takenAt, 1, CopyOnly)
            : BackupDestinationBuilder.ForSharedPath(
                SharedPathRoot, SourceDatabase!, BackupType.Full, takenAt, 1, CopyOnly);

        Destinations = new ObservableCollection<string>(destinations);

        BackupScript = _backupGenerator.Generate(new BackupOptions
        {
            DatabaseName = SourceDatabase!,
            Medium = Medium,
            Destinations = destinations,
            CopyOnly = CopyOnly,
            Compression = Compression,
            Description = $"Copy of {SourceDatabase} from {SourceServer!.ServerName} " +
                          $"to {TargetServer!.ServerName} as {TargetDatabaseName}"
        });

        RestoreScript = _restoreGenerator.Generate(ChainForWhatWasWritten(destinations, takenAt),
            new RestoreOptions
            {
                TargetDatabaseName = TargetDatabaseName,
                WithReplace = WithReplace,
                RecoveryMode = RecoveryMode.Recovery
            });

        SetStatus("Scripts generated.");

        // Fire and forget: the scripts are usable immediately, and the answers arrive when the
        // two instances have answered. A generation that had to wait on them would make the whole
        // screen feel slower for checks that usually say "fine".
        _ = CheckSpaceAsync();
        _ = CheckEncryptionAsync();
        _ = CheckVersionAsync();
    }

    // ── can the target even accept it (#210's law, on the copy screen) ─────────

    [ObservableProperty]
    private string _versionWarning = string.Empty;

    public bool HasVersionWarning => VersionWarning.Length > 0;

    partial void OnVersionWarningChanged(string value) => OnPropertyChanged(nameof(HasVersionWarning));

    /// <summary>
    /// The one-directional law of RESTORE, asked at generation time: a backup taken on a newer
    /// major version can never be restored onto an older one - error 3169, no exceptions. The
    /// restore screen has refused this since #210; the copy screen ran its restore half OUTSIDE
    /// those preflights, so the refusal arrived from the server after the backup half had
    /// already run. Both versions are one SERVERPROPERTY away, and the copy knows both servers
    /// before anything starts.
    ///
    /// A warning rather than a block, like the screen's other checks - but a warning in the
    /// STRONGEST terms, because unlike disk space nothing can change between generating and
    /// running that makes this legal.
    /// </summary>
    private async Task CheckVersionAsync()
    {
        VersionWarning = string.Empty;

        if (SourceServer == null || TargetServer == null) return;

        try
        {
            var source = await _sql.GetProductMajorVersionAsync(SourceServer);
            var target = await _sql.GetProductMajorVersionAsync(TargetServer);

            if (VersionCompatibility.Check(source, target) == VersionVerdict.Refuse)
            {
                VersionWarning =
                    $"This copy cannot work: {SourceServer.ServerName} is " +
                    $"{VersionCompatibility.Describe(source!.Value)} and {TargetServer.ServerName} " +
                    $"is {VersionCompatibility.Describe(target!.Value)}, and SQL Server can never " +
                    "restore a newer version's backup onto an older server - error 3169, with no " +
                    "exceptions. The backup half would run and the restore half would fail. Copy " +
                    "the other way, or onto a newer target.";

                _log.Warn($"[version] copy: {VersionWarning}");
            }
        }
        catch
        {
            // An instance that will not say its version has not said the copy is illegal.
        }
    }

    // ── will the target be able to READ it (#222) ───────────────────────────────

    [ObservableProperty]
    private string _encryptionWarning = string.Empty;

    public bool HasEncryptionWarning => EncryptionWarning.Length > 0;

    partial void OnEncryptionWarningChanged(string value) => OnPropertyChanged(nameof(HasEncryptionWarning));

    /// <summary>
    /// Whether the source database's TDE certificate exists on the target, asked BEFORE the
    /// backup half runs (#222).
    ///
    /// TDE rides along inside every backup of an encrypted database, so a copy of one is only as
    /// good as the target's ability to decrypt it - and without this check that is discovered by
    /// the restore half, with the backup already taken and error 33111 on screen. The copy knows
    /// its source, so sys.databases answers in one column.
    ///
    /// A warning rather than a block, same stance as the space check: certificates can be created
    /// between generating and running, and this app is not the authority on somebody else's
    /// security estate. Failing to answer raises nothing - warn on evidence, not on silence.
    /// </summary>
    private async Task CheckEncryptionAsync()
    {
        EncryptionWarning = string.Empty;

        if (SourceServer == null || TargetServer == null || string.IsNullOrWhiteSpace(SourceDatabase))
            return;

        try
        {
            var (isEncrypted, certName) = await _sql.GetDatabaseTdeInfoAsync(SourceServer, SourceDatabase!);
            if (!isEncrypted) return;

            // The certificate itself, by thumbprint, when the source will say which it is - the
            // name alone cannot be checked on the target, because names are not identity.
            if (certName != null)
            {
                try
                {
                    var thumbprint = await _sql.GetCertificateThumbprintAsync(SourceServer, certName);
                    if (thumbprint != null &&
                        await _sql.FindCertificateByThumbprintAsync(TargetServer, thumbprint) != null)
                        return;
                }
                catch
                {
                    // Fall through to the warning - TDE is confirmed even when the certificate
                    // comparison could not be completed.
                }
            }

            EncryptionWarning = EncryptionGuidance.ExplainCopyOfTdeDatabase(
                SourceDatabase!, TargetServer.ServerName, certName);

            _log.Warn($"[tde] copy: {EncryptionWarning}");
        }
        catch
        {
            // An instance that will not say has not said the database is encrypted.
        }
    }

    // ── will it fit (#206) ──────────────────────────────────────────────────────

    [ObservableProperty]
    private ObservableCollection<VolumeSpace> _volumeSpace = [];

    [ObservableProperty]
    private string _spaceWarning = string.Empty;

    public bool HasSpaceWarning => SpaceWarning.Length > 0;

    partial void OnSpaceWarningChanged(string value) => OnPropertyChanged(nameof(HasSpaceWarning));

    /// <summary>
    /// Whether the restore half can physically fit on the target, asked BEFORE the copy starts.
    ///
    /// The restore screen has had this since #182; the copy - which ends in exactly the same
    /// RESTORE - did not, so the screen where somebody pays the least attention was the one that
    /// never warned. A copy that runs out of disk fails in the restore half, with the source's
    /// backup already taken and the target's database already dropped by WITH REPLACE.
    ///
    /// There is no backup to FILELISTONLY yet, and no need: the database is live on the source, so
    /// its catalog gives the same sizes up front. The copy's restore carries no MOVE clauses, so
    /// the files land at the paths the source recorded - meaning the volumes to check on the
    /// target are the source's own drive letters.
    ///
    /// Failing to answer is not a warning, same stance as the restore screen: this is a courtesy
    /// check over somebody else's storage.
    /// </summary>
    private async Task CheckSpaceAsync()
    {
        VolumeSpace = [];
        SpaceWarning = string.Empty;

        if (SourceServer == null || TargetServer == null || string.IsNullOrWhiteSpace(SourceDatabase))
            return;

        try
        {
            var files = await _sql.GetDatabaseFilesAsync(SourceServer, SourceDatabase!);
            var free = await _sql.GetVolumeFreeSpaceAsync(TargetServer);
            var volumes = RestoreSpaceCheck.Check(files, free);

            VolumeSpace = new ObservableCollection<VolumeSpace>(volumes);
            SpaceWarning = RestoreSpaceCheck.Warn(volumes);

            if (HasSpaceWarning) _log.Warn($"[space] copy: {SpaceWarning}");
        }
        catch
        {
            // An instance that will not report its files or volumes has not said the copy will
            // fail - it has said nothing. Warn on evidence, not on silence.
        }
    }

    /// <summary>
    /// The chain to restore: the one full backup that is about to be written.
    ///
    /// Built from what is being WRITTEN rather than discovered by listing afterwards, because there
    /// is nothing to discover - this app chose the paths, the database and the moment, so re-reading
    /// them back out of a container listing or an msdb query would only be asking a second source to
    /// confirm what is already known, and would introduce a way for the two to disagree.
    ///
    /// Whether the file is actually THERE and readable is a different question, and it is asked
    /// separately, on the target, after the backup has run.
    /// </summary>
    private BackupChain ChainForWhatWasWritten(List<string> destinations, DateTime takenAt) => new()
    {
        FullSet = new BackupSet
        {
            SetId = $"{SourceDatabase}_{takenAt:yyyyMMdd_HHmmss}",
            DatabaseName = SourceDatabase,
            ServerName = SourceServer?.ServerName,
            Type = BackupType.Full,
            Timestamp = takenAt,
            IsCopyOnly = CopyOnly,
            Files = destinations.Select(d => new BackupFileInfo
            {
                // The medium decides how the RESTORE addresses it, exactly as it does everywhere
                // else: a file with a local path is restored FROM DISK, everything else FROM URL.
                LocalPath = MediumIsSharedPath ? d : null,
                BlobUrl = MediumIsBlob ? d : string.Empty,
                BlobName = System.IO.Path.GetFileName(d),
                Type = BackupType.Full,
                InferredDatabaseName = SourceDatabase,
                InferredServerName = SourceServer?.ServerName,
                IsCopyOnly = CopyOnly,

                // Kind is stripped first. takenAt is a local DateTime.Now, and pairing a local
                // reading with a zero offset throws - which is the constructor telling the truth:
                // every time value this app sorts on is a bare wall clock whose zone is recorded
                // separately, so the Kind has to come off rather than be reinterpreted.
                LastModified = new DateTimeOffset(
                    DateTime.SpecifyKind(takenAt, DateTimeKind.Unspecified), TimeSpan.Zero)
            }).ToList()
        }
    };

    [RelayCommand]
    private void CopyScripts() =>
        TryCopyToClipboard(
            $"{BackupScript}{Environment.NewLine}{Environment.NewLine}{RestoreScript}",
            "Both scripts copied to clipboard.");

    // ── running it ──────────────────────────────────────────────────────────────

    [ObservableProperty]
    private bool _isArmed;

    [ObservableProperty]
    private bool _isRunning;

    [ObservableProperty]
    private CopyOutcome _outcome = CopyOutcome.NotStarted;

    /// <summary>What state the two servers were left in, in terms of what to do next.</summary>
    [ObservableProperty]
    private string _outcomeText = string.Empty;

    public bool HasOutcome => OutcomeText.Length > 0;

    partial void OnOutcomeTextChanged(string value) => OnPropertyChanged(nameof(HasOutcome));

    public ObservableCollection<string> Console { get; } = [];

    /// <summary>
    /// The confirmation, naming BOTH servers.
    ///
    /// The whole point of this screen is that two are involved, so a prompt that names one of them
    /// is a prompt somebody can read as being about the other.
    /// </summary>
    public string ConfirmationText =>
        SourceServer == null || TargetServer == null
            ? string.Empty
            : $"Back up {SourceDatabase} on {SourceServer.ServerName}, then restore it onto " +
              $"{TargetServer.ServerName} as {TargetDatabaseName}" +
              (WithReplace ? ", overwriting it if it already exists." : ".");

    public string ButtonText => IsRunning ? "Copying..." : IsArmed ? "Confirm - copy it" : "Copy database";

    partial void OnIsArmedChanged(bool value)
    {
        OnPropertyChanged(nameof(ButtonText));
        OnPropertyChanged(nameof(ConfirmationText));
    }

    partial void OnIsRunningChanged(bool value)
    {
        OnPropertyChanged(nameof(ButtonText));
        RunCommand.NotifyCanExecuteChanged();
    }

    public bool CanRun => HasScripts && !IsRunning && !IsRefused;

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task RunAsync()
    {
        if (!CanRun || SourceServer == null || TargetServer == null) return;

        if (!IsArmed)
        {
            IsArmed = true;
            return;
        }

        IsArmed = false;
        IsRunning = true;
        ClearStatus();
        Console.Clear();
        Outcome = CopyOutcome.NotStarted;
        OutcomeText = string.Empty;

        var ct = _cancellation.Begin();

        // The run executes what was SHOWN, not what is on screen later (#280): these
        // properties are live, the view stays editable while the halves run, and Generate
        // stamps a fresh timestamp into the destinations - so one keystroke mid-backup used
        // to point the restore half at a file that was never written. Same medicine as the
        // restore screen's immutable run record: snapshot at the moment of consent.
        var run = (Backup: BackupScript, Restore: RestoreScript,
                   Destinations: (IReadOnlyList<string>)Destinations.ToList());

        try
        {
            await RunBothHalvesAsync(run.Backup, run.Restore, run.Destinations, ct);
        }
        finally
        {
            _cancellation.End();
            IsRunning = false;
            OutcomeText = CopyOutcomeText.Describe(Outcome, run.Destinations.FirstOrDefault());

            if (Outcome == CopyOutcome.Copied)
                SetStatus($"Copied {SourceDatabase} to {TargetServer.ServerName} as {TargetDatabaseName}.");
            else if (Outcome != CopyOutcome.NotStarted)
                SetError(OutcomeText);
        }
    }

    private async Task RunBothHalvesAsync(
        string backupScript, string restoreScript, IReadOnlyList<string> destinations,
        CancellationToken ct)
    {
        // ── the source half ─────────────────────────────────────────────────────
        Append($"Backing up {SourceDatabase} on {SourceServer!.ServerName}...");
        if (!CopyOnly)
            Append("This is NOT a copy-only backup - the differential base on the source is moving.");

        _log.Info($"Copy started: {SourceDatabase} from {SourceServer.ServerName} " +
                  $"to {TargetServer!.ServerName} as {TargetDatabaseName}");

        var copyStartedAt = DateTime.Now;
        var copyRoute = $"{SourceServer.ServerName} \u2192 {TargetServer.ServerName}";

        _notifier.Notify(new RunNotification(
            RunPhase.Started, "Copy", SourceDatabase!, copyRoute, $"as {TargetDatabaseName}"));

        try
        {
            await _sql.ExecuteWithProgressAsync(SourceServer, backupScript, Append, ct);
        }
        catch (OperationCanceledException)
        {
            Append("Cancelled during the backup.");
            Outcome = CopyOutcome.BackupFailed;
            _notifier.Notify(new RunNotification(
                RunPhase.Problem, "Copy", SourceDatabase!, copyRoute,
                "Cancelled during the backup half.", DateTime.Now - copyStartedAt));
            return;
        }
        catch (Exception ex)
        {
            Append(ex.Message);
            Outcome = CopyOutcome.BackupFailed;
            _log.Error($"Copy failed during backup: {ex.Message}");
            _notifier.Notify(new RunNotification(
                RunPhase.Problem, "Copy", SourceDatabase!, copyRoute,
                $"The backup half failed: {ex.Message}", DateTime.Now - copyStartedAt));
            return;
        }

        Append("Backup complete.");

        // ── can the target reach what was just written ──────────────────────────
        //
        // Between the halves rather than before either, because until the backup has run there is
        // nothing to check. On a shared path this is the question the whole medium turns on: the
        // SOURCE wrote the file as its service account, and the TARGET has to read it as a
        // different one. The two are routinely not the same account, and nothing before this point
        // would have noticed.
        if (MediumIsSharedPath && !await TargetCanReadAsync(destinations, ct))
        {
            Outcome = CopyOutcome.BackupTakenRestoreFailed;
            _notifier.Notify(new RunNotification(
                RunPhase.Problem, "Copy", SourceDatabase!, copyRoute,
                "The backup was taken, but the target cannot read it - usually a share " +
                "permission for the target's service account.", DateTime.Now - copyStartedAt));
            return;
        }

        // ── the target half ─────────────────────────────────────────────────────
        Append($"Restoring onto {TargetServer.ServerName} as {TargetDatabaseName}...");

        try
        {
            await _sql.ExecuteWithProgressAsync(TargetServer, restoreScript, Append, ct);
        }
        catch (OperationCanceledException)
        {
            Append("Cancelled during the restore.");
            Outcome = CopyOutcome.BackupTakenRestoreFailed;
            _notifier.Notify(new RunNotification(
                RunPhase.Problem, "Copy", SourceDatabase!, copyRoute,
                "Cancelled during the restore half - the backup exists, the target is mid-restore.",
                DateTime.Now - copyStartedAt));
            return;
        }
        catch (Exception ex)
        {
            Append(ex.Message);
            Outcome = CopyOutcome.BackupTakenRestoreFailed;
            _log.Error($"Copy failed during restore: {ex.Message}");
            _notifier.Notify(new RunNotification(
                RunPhase.Problem, "Copy", SourceDatabase!, copyRoute,
                $"The restore half failed: {ex.Message}", DateTime.Now - copyStartedAt));
            return;
        }

        Append("Restore complete.");
        Outcome = CopyOutcome.Copied;
        _log.Info($"Copy finished: {TargetDatabaseName} on {TargetServer.ServerName}");

        _notifier.Notify(new RunNotification(
            RunPhase.Succeeded, "Copy", SourceDatabase!, copyRoute,
            $"Now on {TargetServer.ServerName} as {TargetDatabaseName}.",
            DateTime.Now - copyStartedAt));
    }

    /// <summary>
    /// Asks the target instance whether it can read the file the source just wrote.
    ///
    /// A failure here is the most recoverable state this screen produces and the most confusing to
    /// read: the backup is real and fine, and the only thing wrong is a permission on a share. So
    /// it is reported in those terms rather than as a restore that failed.
    /// </summary>
    private async Task<bool> TargetCanReadAsync(
        IReadOnlyList<string> destinations, CancellationToken ct)
    {
        Append($"Checking {TargetServer!.ServerName} can read the backup...");

        try
        {
            var checks = await _sql.CheckBackupFilesAsync(TargetServer, destinations, ct);
            var failed = checks.FirstOrDefault(c => !c.CanBeRestored);

            if (failed == null)
            {
                Append($"{TargetServer.ServerName} can read it.");
                return true;
            }

            Append(failed.Explain(TargetServer.ServerName));
            return false;
        }
        catch (Exception ex)
        {
            // A check that could not be completed is not a check that passed. Going ahead would put
            // the failure back where it was: after WITH REPLACE has dropped the target.
            Append($"Could not confirm {TargetServer.ServerName} can read the backup: {ex.Message}");
            return false;
        }
    }

    [RelayCommand]
    private void Cancel()
    {
        if (!_cancellation.CanCancel) return;

        _cancellation.Cancel();
        SetStatus("Stopping...");
    }

    /// <summary>
    /// The dispatcher of whatever thread constructed this screen - the UI thread in the app,
    /// nothing in a test. Captured at construction, the same rule ConsoleBuffer documents: the
    /// console belongs to the thread that made it. NOT Application.Current, which is process-wide
    /// state another component (the WPF test fixture, a future second window) can own.
    /// </summary>
    private readonly System.Windows.Threading.Dispatcher? _consoleDispatcher =
        System.Windows.Threading.Dispatcher.FromThread(Thread.CurrentThread);

    /// <summary>
    /// The console is bound to the UI, and lines arrive off it: SQL Server's progress callbacks
    /// fire on the connection's worker thread, and adding to a bound collection there tore the
    /// ItemsControl two seconds into a real copy (#233). The restore screen has carried this rule
    /// since its console was built; this screen now carries it too. InvokeAsync, not Invoke - a
    /// blocked connection thread is how "live" output arrives in bursts.
    /// </summary>
    private void Append(string line)
    {
        if (_consoleDispatcher == null || _consoleDispatcher.CheckAccess()) Console.Add(line);
        else _consoleDispatcher.InvokeAsync(() => Console.Add(line));
    }
}
