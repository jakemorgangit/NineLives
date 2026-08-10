using System.Collections.ObjectModel;
using Blackcat.NineLives.Models;
using Blackcat.NineLives.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Blackcat.NineLives.ViewModels;

/// <summary>
/// Taking a backup (#165).
///
/// The half of an orchestrator this app has never had. It restores backups other things took; this
/// takes one, to either medium, with the same rules the restore screen earned: nothing runs that
/// was not on screen first, and the button that touches a server has to be pressed twice.
///
/// The asymmetry worth knowing about: a restore destroys the TARGET, and everybody expects that. A
/// backup looks harmless and can quietly damage the SOURCE - a plain full resets the differential
/// base, so a production server's whole differential schedule starts depending on a file in this
/// app's container. That is why COPY_ONLY is on by default and why turning it off is loud.
/// </summary>
public partial class BackupViewModel : ViewModelBase
{
    private readonly IRunNotifier _notifier;
    private readonly ICredentialStore _store;
    private readonly ISqlServerService _sql;
    private readonly BackupScriptGenerator _generator = new();
    // One instance PER OPERATION (#281), the rule RestoreExecutionViewModel already keeps:
    // with a single shared instance, the List button's Begin() silently cancelled a running
    // backup, and the backup's End() then disposed the listing's token. The Stop button
    // belongs to the run; the list and the verify each own their overlap story.
    private readonly OperationCancellation _listCancellation = new();
    private readonly OperationCancellation _runCancellation = new();
    private readonly OperationCancellation _verifyCancellation = new();
    private readonly OperationLog _log;

    public BackupViewModel(
        ICredentialStore store, ISqlServerService sql, OperationLog? log = null,
        IRunNotifier? notifier = null)
    {
        _notifier = notifier ?? NullRunNotifier.Instance;
        _store = store;
        _sql = sql;
        _log = log ?? App.Log;

        Refresh();
    }

    // ── what is being backed up ─────────────────────────────────────────────────

    [ObservableProperty]
    private ObservableCollection<ServerConnection> _servers = [];

    [ObservableProperty]
    private ServerConnection? _server;

    [ObservableProperty]
    private ObservableCollection<string> _databases = [];

    [ObservableProperty]
    private string? _selectedDatabase;

    public void Refresh()
    {
        try
        {
            var config = _store.LoadConfig();

            var previousServer = Server?.Id;
            Servers = new ObservableCollection<ServerConnection>(config.Servers);
            if (previousServer != null)
                Server = Servers.FirstOrDefault(s => s.Id == previousServer);

            var previousContainer = Container?.Id;
            Containers = new ObservableCollection<BlobContainerConfig>(config.BlobContainers);
            if (previousContainer != null)
                Container = Containers.FirstOrDefault(c => c.Id == previousContainer);
        }
        catch
        {
            // A config that will not load is reported where it is loaded for real. Here it only
            // means empty lists rather than a screen that cannot open.
            Servers = [];
            Containers = [];
        }
    }

    /// <summary>One database in the multi-select list (#208), with its tick.</summary>
    public partial class DatabasePick : ObservableObject
    {
        public DatabasePick(string name, Action changed)
        {
            Name = name;
            _changed = changed;
        }

        private readonly Action _changed;

        public string Name { get; }

        [ObservableProperty]
        private bool _isPicked;

        partial void OnIsPickedChanged(bool value) => _changed();
    }

    /// <summary>
    /// Backing up more than one database in one run (#208).
    ///
    /// "Take a copy-only of everything before we patch" is a completely ordinary request, and the
    /// script for it is just N BACKUP statements sharing the same destination and options. Off by
    /// default: one database is still the common case, and the single picker should not become a
    /// checklist for it.
    /// </summary>
    [ObservableProperty]
    private bool _multiSelect;

    /// <summary>The single picker hides while the list is up - two answers to one question otherwise.</summary>
    public bool SingleSelect => !MultiSelect;

    partial void OnMultiSelectChanged(bool value)
    {
        OnPropertyChanged(nameof(SingleSelect));
        Invalidate();
    }

    /// <summary>The tickable list, rebuilt whenever the databases load.</summary>
    [ObservableProperty]
    private ObservableCollection<DatabasePick> _databasePicks = [];

    /// <summary>The ticked names, in the order the instance listed them.</summary>
    public List<string> PickedDatabases =>
        DatabasePicks.Where(p => p.IsPicked).Select(p => p.Name).ToList();

    /// <summary>
    /// Ticks everything the instance listed. The list is already user databases only - system
    /// databases are excluded at the query - so "all" means what the patch-night request means.
    /// </summary>
    [RelayCommand]
    private void PickAll()
    {
        foreach (var pick in DatabasePicks) pick.IsPicked = true;
    }

    [RelayCommand]
    private void PickNone()
    {
        foreach (var pick in DatabasePicks) pick.IsPicked = false;
    }

    /// <summary>The list is not fetchable mid-run: it can wait; the backup cannot re-run (#281).</summary>
    public bool CanLoadDatabases => !IsRunning;

    /// <summary>Asks the chosen instance what it has, rather than making somebody type a name.</summary>
    [RelayCommand(CanExecute = nameof(CanLoadDatabases))]
    private async Task LoadDatabasesAsync()
    {
        if (Server == null)
        {
            SetError("Choose the server to back up from.");
            return;
        }

        var ct = _listCancellation.Begin();
        IsBusy = true;
        ClearStatus();

        try
        {
            var names = await _sql.GetDatabaseListAsync(Server, ct);

            Databases = new ObservableCollection<string>(names);
            DatabasePicks = new ObservableCollection<DatabasePick>(
                names.Select(n => new DatabasePick(n, Invalidate)));

            // The certificates ride along with the database list - same instance, same moment.
            // Best effort: an instance that will not list them still backs up unencrypted.
            try
            {
                EncryptionCertificates = new ObservableCollection<string>(
                    await _sql.ListBackupCertificatesAsync(Server, ct));
            }
            catch
            {
                EncryptionCertificates = [];
            }
            OnPropertyChanged(nameof(EncryptWantedButNoCertificate));

            // Nothing is chosen for the user, the same rule the Restore screen learned - and here
            // the wrong choice means a production database read at full speed for several minutes.
            SelectedDatabase = null;

            SetStatus($"{Server.ServerName} has {names.Count} database(s).");
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
            _listCancellation.End();
            IsBusy = false;
        }
    }

    partial void OnServerChanged(ServerConnection? value)
    {
        // The database list belonged to the previous instance. Leaving it up invites somebody to
        // back up a name that exists on both and mean the other one - and the multi-select
        // ticks and certificate list are the same hazard with the same fix (#278): stale ticks
        // satisfied CanGenerate, so the regenerated statements named the OLD server's databases
        // with destinations claiming the new one, encrypted by a certificate it does not hold.
        Databases = [];
        SelectedDatabase = null;
        DatabasePicks = [];
        EncryptionCertificates = [];
        SelectedEncryptionCertificate = null;
        Invalidate();
    }

    partial void OnSelectedDatabaseChanged(string? value) => Invalidate();

    // ── where it is written ─────────────────────────────────────────────────────

    [ObservableProperty]
    private BackupMedium _medium = BackupMedium.AzureBlob;

    public bool MediumIsBlob => Medium == BackupMedium.AzureBlob;
    public bool MediumIsSharedPath => Medium == BackupMedium.SharedPath;

    partial void OnMediumChanged(BackupMedium value)
    {
        OnPropertyChanged(nameof(MediumIsBlob));
        OnPropertyChanged(nameof(MediumIsSharedPath));
        Invalidate();
    }

    [ObservableProperty]
    private ObservableCollection<BlobContainerConfig> _containers = [];

    [ObservableProperty]
    private BlobContainerConfig? _container;

    /// <summary>
    /// The folder the backup is written to on a share.
    ///
    /// The instance writes it as its own service account, so this has to be somewhere THAT account
    /// can write - which is not the same as somewhere this app can see.
    /// </summary>
    [ObservableProperty]
    private string _sharedPathRoot = string.Empty;

    partial void OnContainerChanged(BlobContainerConfig? value) => Invalidate();
    partial void OnSharedPathRootChanged(string value) => Invalidate();

    // ── how ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// COPY_ONLY, on by default and deliberately awkward to turn off.
    ///
    /// A plain full backup resets the differential base on the source. On a production server with
    /// a differential schedule that silently makes every subsequent differential depend on this
    /// file - which lives wherever this app put it, under a name nobody else knows.
    /// </summary>
    [ObservableProperty]
    private bool _copyOnly = true;

    [ObservableProperty]
    private bool _compression = true;

    [ObservableProperty]
    private bool _checksum = true;

    /// <summary>
    /// How many files to write.
    ///
    /// More than one is faster on a large database, and for blob it is the only way past the 195 GB
    /// per-blob limit - a single-blob backup of anything larger fails partway through, which is a
    /// bad way to find out how big the database was.
    /// </summary>
    [ObservableProperty]
    private int _stripes = 1;

    [ObservableProperty]
    private string _description = string.Empty;

    // ── encryption (#222) ───────────────────────────────────────────────────────

    /// <summary>Whether to take the backup WITH ENCRYPTION. Off by default - it is a real choice.</summary>
    [ObservableProperty]
    private bool _encrypt;

    /// <summary>Certificates on the source that can encrypt a backup, loaded with the databases.</summary>
    [ObservableProperty]
    private ObservableCollection<string> _encryptionCertificates = [];

    [ObservableProperty]
    private string? _selectedEncryptionCertificate;

    /// <summary>
    /// Encryption asked for on a server that offered no usable certificate - the checkbox is
    /// honest about why the script is not appearing.
    /// </summary>
    public bool EncryptWantedButNoCertificate =>
        Encrypt && EncryptionCertificates.Count == 0;

    partial void OnEncryptChanged(bool value)
    {
        // The obvious default when there is exactly one thing to choose.
        if (value && SelectedEncryptionCertificate == null && EncryptionCertificates.Count == 1)
            SelectedEncryptionCertificate = EncryptionCertificates[0];

        OnPropertyChanged(nameof(EncryptWantedButNoCertificate));
        Invalidate();
    }

    partial void OnSelectedEncryptionCertificateChanged(string? value) => Invalidate();

    partial void OnCopyOnlyChanged(bool value) => Invalidate();
    partial void OnCompressionChanged(bool value) => Invalidate();
    partial void OnChecksumChanged(bool value) => Invalidate();
    partial void OnStripesChanged(int value)
    {
        // SQL Server allows at most 64 backup devices; below 1 means nothing. Clamped here
        // rather than refused at run time - the free-text box accepts any int, and 640 used
        // to become a statement the server rejects after the arm-and-confirm (#293).
        var clamped = Math.Clamp(value, 1, 64);
        if (clamped != value) { Stripes = clamped; return; }
        Invalidate();
    }
    partial void OnDescriptionChanged(string value) => Invalidate();

    /// <summary>True when the differential base on a production database is about to move.</summary>
    public bool WillResetTheDifferentialBase => !CopyOnly;

    // ── the script ──────────────────────────────────────────────────────────────

    [ObservableProperty]
    private string _generatedScript = string.Empty;

    public bool HasScript => !string.IsNullOrWhiteSpace(GeneratedScript);

    partial void OnGeneratedScriptChanged(string value)
    {
        OnPropertyChanged(nameof(HasScript));
        ExecuteCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// Everything needed to write a statement at all. Not a judgement about whether it is a good
    /// idea - that is what the confirmation is for.
    /// </summary>
    public bool CanGenerate =>
        Server != null &&
        (MultiSelect ? DatabasePicks.Any(p => p.IsPicked) : !string.IsNullOrWhiteSpace(SelectedDatabase)) &&
        (MediumIsBlob ? Container != null : !string.IsNullOrWhiteSpace(SharedPathRoot)) &&
        // Encryption asked for needs a certificate chosen - a statement without one is not a
        // weaker statement, it is a different one.
        (!Encrypt || !string.IsNullOrWhiteSpace(SelectedEncryptionCertificate));

    /// <summary>
    /// Rebuilds the script from the current options, or clears it.
    ///
    /// Called by every option, so what is on screen is always what would run. The restore screen
    /// learned this the hard way: a stale script is one that does something different from what it
    /// shows, and here what it shows is the only warning anybody gets.
    /// </summary>
    private void Invalidate()
    {
        OnPropertyChanged(nameof(CanGenerate));
        OnPropertyChanged(nameof(WillResetTheDifferentialBase));
        GenerateCommand.NotifyCanExecuteChanged();

        // Disarm: an armed button that survives an option change is armed for something else.
        IsArmed = false;

        if (!HasScript) return;

        // Regenerate - or, when the inputs no longer support generating, CLEAR. Leaving the
        // old script standing kept the Run button live: generate for a database on one server,
        // switch the server box, and two presses executed the old statements against the new
        // instance (#278). An empty screen is the truthful one here.
        if (CanGenerate) Generate();
        else ClearGenerated();
    }

    /// <summary>Removes every trace of the last generation, so nothing stale can run.</summary>
    private void ClearGenerated()
    {
        _generated = [];
        GeneratedScript = string.Empty;
        Destinations = [];
    }

    /// <summary>
    /// One database's statement and files, at one shared moment.
    ///
    /// The single seam both paths go through (#208): the single-database screen is the one-item
    /// case, and the multi-select run executes exactly these per-database scripts - so what is on
    /// screen (their concatenation) is always what runs.
    /// </summary>
    private (string Script, List<string> Destinations) GenerateFor(string database, DateTime takenAt)
    {
        var destinations = MediumIsBlob
            ? BackupDestinationBuilder.ForContainer(
                Container!, Server!.ServerName, database, BackupType.Full,
                takenAt, Math.Max(1, Stripes), CopyOnly)
            : BackupDestinationBuilder.ForSharedPath(
                SharedPathRoot, database, BackupType.Full,
                takenAt, Math.Max(1, Stripes), CopyOnly);

        var script = _generator.Generate(new BackupOptions
        {
            DatabaseName = database,
            Medium = Medium,
            Destinations = destinations,
            CopyOnly = CopyOnly,
            Compression = Compression,
            Checksum = Checksum,
            EncryptionCertificate = Encrypt ? SelectedEncryptionCertificate : null,
            Description = string.IsNullOrWhiteSpace(Description) ? null : Description
        });

        return (script, destinations);
    }

    /// <summary>What the last generation produced, per database - the multi run executes these.</summary>
    private List<(string Database, string Script, List<string> Destinations)> _generated = [];

    [RelayCommand(CanExecute = nameof(CanGenerate))]
    private void Generate()
    {
        if (!CanGenerate) return;

        var takenAt = DateTime.Now;

        var databases = MultiSelect ? PickedDatabases : [SelectedDatabase!];

        _generated = databases
            .Select(db =>
            {
                var (script, destinations) = GenerateFor(db, takenAt);
                return (db, script, destinations);
            })
            .ToList();

        GeneratedScript = string.Join(Environment.NewLine + Environment.NewLine,
            _generated.Select(g => g.Script));
        Destinations = new ObservableCollection<string>(_generated.SelectMany(g => g.Destinations));

        SetStatus(_generated.Count == 1
            ? "Script generated."
            : $"Script generated - {_generated.Count} databases, in order.");
    }

    /// <summary>Where the files will land, so it can be read before anything is written.</summary>
    [ObservableProperty]
    private ObservableCollection<string> _destinations = [];

    [RelayCommand]
    private void CopyScript() => TryCopyToClipboard(GeneratedScript, "Script copied to clipboard.");

    // ── running it ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Armed on the first press, run on the second.
    ///
    /// The same rule the restore has, for a different reason. A restore destroys the target and
    /// everybody knows it; a backup looks harmless right up until it moves a production database's
    /// differential base, or reads a busy server flat out for twenty minutes in the middle of the
    /// working day.
    /// </summary>
    [ObservableProperty]
    private bool _isArmed;

    [ObservableProperty]
    private bool _isRunning;

    public string ButtonText => IsRunning ? "Backing up..." : IsArmed ? "Confirm - run the backup" : "Run backup";

    partial void OnIsArmedChanged(bool value) => OnPropertyChanged(nameof(ButtonText));
    partial void OnIsRunningChanged(bool value)
    {
        OnPropertyChanged(nameof(ButtonText));
        ExecuteCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanLoadDatabases));
        LoadDatabasesCommand.NotifyCanExecuteChanged();
    }

    /// <summary>SQL Server's own words as it goes, in the order it said them.</summary>
    public ObservableCollection<string> Console { get; } = [];

    public bool CanExecute => HasScript && !IsRunning;

    [RelayCommand(CanExecute = nameof(CanExecute))]
    private async Task ExecuteAsync()
    {
        if (!HasScript || Server == null) return;

        if (!IsArmed)
        {
            IsArmed = true;
            return;
        }

        IsArmed = false;
        IsRunning = true;
        ClearStatus();
        Console.Clear();

        var ct = _runCancellation.Begin();
        var started = DateTime.Now;

        // Captured with the run (#293): Verify must test WITH CHECKSUM exactly when the
        // statements said it - the checkbox is live, and reading it later failed a fine
        // no-checksum backup (or silently degraded a checksum one).
        var ranWithChecksum = Checksum;

        // BACKUP TO URL needs the credential on THIS instance (#284) - asked before the first
        // statement, because Msg 3201 after the arm-and-confirm is the class of late refusal
        // this screen exists to kill.
        if (MediumIsBlob && Container != null)
        {
            var preflight = await BlobCredentialPreflight.EnsureAsync(
                _store, _sql, _log, Container, Server, Append);
            if (!preflight.CanProceed)
            {
                _notifier.Notify(new RunNotification(
                    RunPhase.Problem, "Backup",
                    MultiSelect ? $"{PickedDatabases.Count} database(s)" : SelectedDatabase ?? "backup",
                    Server.ServerName, preflight.Refusal));
                SetError(preflight.Refusal!);
                _runCancellation.End();
                IsRunning = false;
                return;
            }
        }

        // Database-at-a-time, deliberately (#208): a failure on the sixth database names the
        // sixth database and the rest still run - the patch-night semantics. One database is the
        // one-item case of the same loop.
        var run = _generated.Count > 0
            ? _generated
            : [(SelectedDatabase!, GeneratedScript, Destinations.ToList())];

        var succeededDevices = new List<string>();
        var failed = new List<string>();
        string? lastFailureMessage = null;

        try
        {
            if (!CopyOnly)
                Append(run.Count == 1
                    ? "This is NOT a copy-only backup - the differential base on this database is moving."
                    : "These are NOT copy-only backups - every database's differential base is moving.");

            _log.Info($"Backup started: {run.Count} database(s) on {Server.ServerName}, " +
                      $"copyOnly={CopyOnly}, files={Destinations.Count}");

            _notifier.Notify(new RunNotification(
                RunPhase.Started, "Backup",
                run.Count == 1 ? run[0].Database : $"{run.Count} databases",
                Server.ServerName));

            foreach (var (database, script, destinations) in run)
            {
                ct.ThrowIfCancellationRequested();
                Append($"Backing up {database} on {Server.ServerName}...");

                try
                {
                    await _sql.ExecuteWithProgressAsync(Server, script, Append, ct);
                    Append($"{database}: done.");
                    succeededDevices.AddRange(destinations);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // Named, and NOT fatal to the rest: the whole point of backing up everything
                    // before the patch is that everything gets backed up.
                    Append($"{database}: FAILED - {ex.Message}");
                    failed.Add(database);
                    lastFailureMessage = ex.Message;
                    _log.Error($"Backup failed: {database}: {ex.Message}");

                    // At the moment of the problem, not minutes later in the summary (#242): the
                    // rest of the run continues, and whoever is off watching something else can
                    // decide NOW whether this failure changes their evening.
                    _notifier.Notify(new RunNotification(
                        RunPhase.Problem, "Backup", database, Server.ServerName, ex.Message));
                }
            }

            var elapsed = DateTime.Now - started;

            if (failed.Count == 0)
            {
                Append($"Finished in {elapsed.TotalSeconds:N0}s.");
                SetStatus(run.Count == 1
                    ? $"Backed up {run[0].Database} in {elapsed.TotalSeconds:N0}s."
                    : $"Backed up {run.Count} databases in {elapsed.TotalSeconds:N0}s.");
                _log.Info($"Backup finished: {run.Count} database(s) in {elapsed.TotalSeconds:N0}s");

                _notifier.Notify(new RunNotification(
                    RunPhase.Succeeded, "Backup",
                    run.Count == 1 ? run[0].Database : $"{run.Count} databases",
                    Server.ServerName, Duration: elapsed));
            }
            else if (run.Count == 1)
            {
                // One database keeps the direct report - "1 of 1" and "the others finished" would
                // be the multi-run's phrasing wearing the wrong trousers.
                SetError($"The backup did not complete: {lastFailureMessage}");

                // The close of the run, with its duration, exactly as the multi path sends it
                // (#295). The per-database problem already fired as it happened; without this
                // the channel heard the run start and never heard it END.
                _notifier.Notify(new RunNotification(
                    RunPhase.Problem, "Backup", run[0].Database, Server.ServerName,
                    $"The backup did not complete: {lastFailureMessage}", elapsed));
            }
            else
            {
                var summary = $"{failed.Count} of {run.Count} did not complete: {string.Join(", ", failed)}. " +
                              "The others finished and are real backups - see the console for each failure.";
                SetError(summary);

                // The per-database problems already fired as they happened; this is the close of
                // the run, so the channel shows it ENDED and how it ended.
                _notifier.Notify(new RunNotification(
                    RunPhase.Problem, "Backup", $"{run.Count} databases", Server.ServerName,
                    summary, elapsed));
            }

            // What was just written is the thing to verify (#207) - only what actually succeeded,
            // captured from the statements' own devices.
            _lastWrittenDevices = succeededDevices;
            _lastWrittenWithChecksum = ranWithChecksum;
            CanVerifyLastBackup = succeededDevices.Count > 0;
        }
        catch (OperationCanceledException)
        {
            // A cancelled BACKUP leaves a partial file behind, which is worth saying plainly - it
            // is not a backup, and it will sit there looking like one.
            Append("Cancelled. Any file already written is incomplete and cannot be restored from.");
            SetStatus("Backup cancelled.");
            _log.Info("Backup cancelled");

            _notifier.Notify(new RunNotification(
                RunPhase.Problem, "Backup",
                run.Count == 1 ? run[0].Database : $"{run.Count} databases",
                Server.ServerName,
                "Cancelled. Any file already written is incomplete and cannot be restored from."));

            // What finished before the stop is still real.
            _lastWrittenDevices = succeededDevices;
            _lastWrittenWithChecksum = ranWithChecksum;
            CanVerifyLastBackup = succeededDevices.Count > 0;
        }
        finally
        {
            _runCancellation.End();
            IsRunning = false;

            // Told, not interrupted (#289): the restore already flashes the taskbar when it
            // finishes behind another window, and a backup is exactly as worth coming back to.
            WindowAttention.FlashIfInBackground();
        }
    }

    [RelayCommand]
    private void Cancel()
    {
        if (!_runCancellation.CanCancel) return;

        _runCancellation.Cancel();
        SetStatus("Stopping...");
    }

    // ── handing it over instead of running it (#207) ────────────────────────────

    /// <summary>How much of the app is on show (#176). The shell keeps it current.</summary>
    [ObservableProperty]
    private AppMode _mode = AppMode.Pro;

    public bool ShowAgentJob => AppModeCapabilities.CanScriptAsAgentJob(Mode);

    partial void OnModeChanged(AppMode value) => OnPropertyChanged(nameof(ShowAgentJob));

    /// <summary>
    /// The backup as a disabled, unscheduled Agent job (#207) - the same handover the restore
    /// screen has (#186), and backups are the thing people actually schedule. What is copied is
    /// reviewable and inert until somebody deliberately enables it.
    /// </summary>
    [RelayCommand]
    private void CopyAsAgentJob()
    {
        if (!HasScript) return;

        // A BACKUP job called "NineLives restore" was a bad first impression on the exact
        // artefact that outlives the app session - and in multi-select the single-database
        // fields are empty, so the name and description degrade to nonsense (#293).
        var subject = MultiSelect ? $"{PickedDatabases.Count} databases" : SelectedDatabase;
        var job = SqlAgentJobScript.Wrap(
            GeneratedScript,
            SqlAgentJobScript.SuggestName("backup", subject, DateTime.Now),
            $"Back up {subject}, generated by Nine Lives on {DateTime.Now:yyyy-MM-dd HH:mm}.");

        TryCopyToClipboard(job,
            "Agent job script copied to clipboard. The job is created disabled and unscheduled - " +
            "add a schedule and enable it when it should actually run.");
    }

    // ── proving what was just written (#207) ────────────────────────────────────

    /// <summary>The devices the last successful backup wrote, exactly as the statement named them.</summary>
    private List<string> _lastWrittenDevices = [];

    /// <summary>Whether those statements said WITH CHECKSUM - captured with the run (#293).</summary>
    private bool _lastWrittenWithChecksum;

    /// <summary>Whether the last run finished and left something to verify.</summary>
    [ObservableProperty]
    private bool _canVerifyLastBackup;

    [ObservableProperty]
    private bool _isVerifying;

    /// <summary>
    /// RESTORE VERIFYONLY over what was just written (#207).
    ///
    /// A backup nobody has verified is a hope, not a backup, and the cheapest moment to find out
    /// is while the screen that wrote it is still open. Not mode-gated: this is not the audit
    /// machinery, it is checking your own work, and it belongs to everyone who can back up.
    ///
    /// WITH CHECKSUM follows the backup's own setting - verifying checksums that were never
    /// written fails the verify for a backup that is fine.
    /// </summary>
    [RelayCommand]
    private async Task VerifyLastBackupAsync()
    {
        if (Server == null || _lastWrittenDevices.Count == 0) return;

        var ct = _verifyCancellation.Begin();
        IsVerifying = true;
        Append($"Verifying {_lastWrittenDevices.Count} file(s) with RESTORE VERIFYONLY...");

        try
        {
            var result = await _sql.RestoreVerifyOnlyAsync(
                Server, _lastWrittenDevices, _lastWrittenWithChecksum, null, ct);

            if (result.IsValid)
            {
                Append("Verified. The backup is complete and readable.");
                SetStatus("Backup verified.");
            }
            else
            {
                Append(result.Message);
                SetError("The backup did NOT verify - see the console. It should not be relied on.");
            }
        }
        catch (OperationCanceledException)
        {
            Append("Verify cancelled.");
        }
        catch (Exception ex)
        {
            Append(ex.Message);
            SetError($"Could not verify the backup: {ex.Message}");
        }
        finally
        {
            _verifyCancellation.End();
            IsVerifying = false;
        }
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
