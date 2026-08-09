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
    private readonly ICredentialStore _store;
    private readonly ISqlServerService _sql;
    private readonly BackupScriptGenerator _generator = new();
    private readonly OperationCancellation _cancellation = new();
    private readonly OperationLog _log;

    public BackupViewModel(ICredentialStore store, ISqlServerService sql, OperationLog? log = null)
    {
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

    /// <summary>Asks the chosen instance what it has, rather than making somebody type a name.</summary>
    [RelayCommand]
    private async Task LoadDatabasesAsync()
    {
        if (Server == null)
        {
            SetError("Choose the server to back up from.");
            return;
        }

        var ct = _cancellation.Begin();
        IsBusy = true;
        ClearStatus();

        try
        {
            var names = await _sql.GetDatabaseListAsync(Server, ct);

            Databases = new ObservableCollection<string>(names);

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
            _cancellation.End();
            IsBusy = false;
        }
    }

    partial void OnServerChanged(ServerConnection? value)
    {
        // The database list belonged to the previous instance. Leaving it up invites somebody to
        // back up a name that exists on both and mean the other one.
        Databases = [];
        SelectedDatabase = null;
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
    partial void OnStripesChanged(int value) => Invalidate();
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
        !string.IsNullOrWhiteSpace(SelectedDatabase) &&
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

        if (HasScript) Generate();
    }

    [RelayCommand(CanExecute = nameof(CanGenerate))]
    private void Generate()
    {
        if (!CanGenerate) return;

        var takenAt = DateTime.Now;

        var destinations = MediumIsBlob
            ? BackupDestinationBuilder.ForContainer(
                Container!, Server!.ServerName, SelectedDatabase!, BackupType.Full,
                takenAt, Math.Max(1, Stripes), CopyOnly)
            : BackupDestinationBuilder.ForSharedPath(
                SharedPathRoot, SelectedDatabase!, BackupType.Full,
                takenAt, Math.Max(1, Stripes), CopyOnly);

        GeneratedScript = _generator.Generate(new BackupOptions
        {
            DatabaseName = SelectedDatabase!,
            Medium = Medium,
            Destinations = destinations,
            CopyOnly = CopyOnly,
            Compression = Compression,
            Checksum = Checksum,
            EncryptionCertificate = Encrypt ? SelectedEncryptionCertificate : null,
            Description = string.IsNullOrWhiteSpace(Description) ? null : Description
        });

        Destinations = new ObservableCollection<string>(destinations);
        SetStatus("Script generated.");
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

        var ct = _cancellation.Begin();
        var started = DateTime.Now;

        try
        {
            Append($"Backing up {SelectedDatabase} on {Server.ServerName}...");
            if (!CopyOnly)
                Append("This is NOT a copy-only backup - the differential base on this database is moving.");

            _log.Info($"Backup started: {SelectedDatabase} on {Server.ServerName}, " +
                      $"copyOnly={CopyOnly}, files={Destinations.Count}");

            await _sql.ExecuteWithProgressAsync(Server, GeneratedScript, Append, ct);

            var elapsed = DateTime.Now - started;
            Append($"Finished in {elapsed.TotalSeconds:N0}s.");
            SetStatus($"Backed up {SelectedDatabase} in {elapsed.TotalSeconds:N0}s.");
            _log.Info($"Backup finished: {SelectedDatabase} in {elapsed.TotalSeconds:N0}s");

            // What was just written is now the thing to verify (#207) - captured from the
            // statement's own devices, not re-derived, so the verify reads exactly what the
            // backup wrote even after the options change.
            _lastWrittenDevices = Destinations.ToList();
            CanVerifyLastBackup = true;
        }
        catch (OperationCanceledException)
        {
            // A cancelled BACKUP leaves a partial file behind, which is worth saying plainly - it
            // is not a backup, and it will sit there looking like one.
            Append("Cancelled. Any file already written is incomplete and cannot be restored from.");
            SetStatus("Backup cancelled.");
            _log.Info($"Backup cancelled: {SelectedDatabase}");
        }
        catch (Exception ex)
        {
            Append(ex.Message);
            SetError($"The backup did not complete: {ex.Message}");
            _log.Error($"Backup failed: {SelectedDatabase}: {ex.Message}");
        }
        finally
        {
            _cancellation.End();
            IsRunning = false;
        }
    }

    [RelayCommand]
    private void Cancel()
    {
        if (!_cancellation.CanCancel) return;

        _cancellation.Cancel();
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

        var job = SqlAgentJobScript.Wrap(
            GeneratedScript,
            SqlAgentJobScript.SuggestName(SelectedDatabase, DateTime.Now),
            $"Back up {SelectedDatabase}, generated by Nine Lives on {DateTime.Now:yyyy-MM-dd HH:mm}.");

        TryCopyToClipboard(job,
            "Agent job script copied to clipboard. The job is created disabled and unscheduled - " +
            "add a schedule and enable it when it should actually run.");
    }

    // ── proving what was just written (#207) ────────────────────────────────────

    /// <summary>The devices the last successful backup wrote, exactly as the statement named them.</summary>
    private List<string> _lastWrittenDevices = [];

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

        var ct = _cancellation.Begin();
        IsVerifying = true;
        Append($"Verifying {_lastWrittenDevices.Count} file(s) with RESTORE VERIFYONLY...");

        try
        {
            var result = await _sql.RestoreVerifyOnlyAsync(Server, _lastWrittenDevices, Checksum, null, ct);

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
            _cancellation.End();
            IsVerifying = false;
        }
    }

    private void Append(string line) => Console.Add(line);
}
