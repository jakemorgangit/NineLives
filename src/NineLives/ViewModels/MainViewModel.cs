using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Blackcat.NineLives.Models;
using Blackcat.NineLives.Services;

namespace Blackcat.NineLives.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    private readonly ICredentialStore _credentialStore;
    private readonly IBlobStorageService _blobService;
    private readonly ISqlServerService _sqlService;
    private readonly BackupChainBuilder _chainBuilder;
    private readonly RestoreScriptGenerator _scriptGenerator;
    private readonly IRestoreHistoryStore _historyStore;

    [ObservableProperty]
    private ViewModelBase? _currentView;

    [ObservableProperty]
    private string _currentViewName = "Blob Storage";

    [ObservableProperty]
    private string _globalStatus = "Ready";

    [ObservableProperty]
    private bool _isConnectedToSql;

    [ObservableProperty]
    private string _connectedServerName = "Not connected";

    [ObservableProperty]
    private bool _updateAvailable;

    [ObservableProperty]
    private string _updateMessage = string.Empty;

    private string _updateUrl = UpdateChecker.ReleasesPage;
    private string? _updateTag;

    /// <summary>
    /// Shown in the status bar. Read from the assembly so it cannot go stale.
    /// Not named AppVersion - that would shadow the class of the same name inside this type.
    /// </summary>
    public string VersionText => Services.AppVersion.Display;

    public BlobConfigViewModel BlobConfig { get; }
    public ServerManagerViewModel ServerManager { get; }
    public BlobBrowserViewModel BlobBrowser { get; }
    public RestoreViewModel Restore { get; }
    public HistoryViewModel History { get; }
    public AboutViewModel About { get; }

    /// <summary>
    /// The app's one composition point: builds the real services against the real credential
    /// store. Deliberately not a DI container - three services and one place that wires them does
    /// not need one (#41).
    /// </summary>
    public MainViewModel() : this(new CredentialStore()) { }

    /// <summary>
    /// Takes the store so a test can point the whole object graph at a temp directory instead of
    /// the user's actual profile. Constructing this type used to migrate and read the real
    /// %LOCALAPPDATA% config as a side effect of a XAML load test.
    /// </summary>
    public MainViewModel(ICredentialStore credentialStore)
    {
        _credentialStore = credentialStore;

        // Before anything reads the config: move secrets from name-derived keys onto stable ids
        // (#8). Must happen ahead of the child viewmodels, which load containers and servers in
        // their constructors and would otherwise look up keys the migration is about to change.
        MigrateSecretKeys();

        _blobService = new BlobStorageService(_credentialStore);
        _sqlService = new SqlServerService(_credentialStore);
        _chainBuilder = new BackupChainBuilder();
        _scriptGenerator = new RestoreScriptGenerator();

        BlobConfig = new BlobConfigViewModel(_credentialStore, _blobService);
        ServerManager = new ServerManagerViewModel(_credentialStore, _sqlService);
        BlobBrowser = new BlobBrowserViewModel(_blobService, _credentialStore);
        // One store, shared: the Restore screen writes to it and the History screen reads it back,
        // and two instances pointed at the same file would be a way to lose an entry.
        _historyStore = new RestoreHistoryStore();

        Restore = new RestoreViewModel(
            _blobService, _sqlService, _chainBuilder, _scriptGenerator, _credentialStore,
            log: null, history: _historyStore);
        History = new HistoryViewModel(_historyStore);
        About = new AboutViewModel();

        ServerManager.ConnectionChanged += OnSqlConnectionChanged;

        CurrentView = BlobConfig;

        // Fire and forget - startup must not wait on the network.
        _ = CheckForUpdatesAsync();
    }

    /// <summary>
    /// One-off relocation of stored secrets onto stable ids. A no-op on every launch after the
    /// first, and never fatal - if it cannot complete, the old keys are untouched and everything
    /// still works, so the app starts normally and tries again next time.
    /// </summary>
    private void MigrateSecretKeys()
    {
        try
        {
            var config = _credentialStore.LoadConfig();
            var result = new ConfigMigrator(_credentialStore).Migrate(config);

            if (result.Error != null)
                GlobalStatus = $"Stored credentials could not be updated: {result.Error}";
            else if (result.DidWork)
                GlobalStatus = "Ready";
        }
        catch
        {
            // Startup must not depend on this.
        }
    }

    private async Task CheckForUpdatesAsync()
    {
        try
        {
            var config = _credentialStore.LoadConfig();
            if (!config.CheckForUpdates) return;

            var latest = await new UpdateChecker().FetchLatestAsync();
            if (!UpdateChecker.ShouldNotify(
                    AppVersion.Current, latest, config.LastNotifiedReleaseTag, config.CheckForUpdates))
                return;

            _updateUrl = latest!.Url;
            _updateTag = latest.Tag;
            UpdateMessage = $"Nine Lives {latest.Tag} is available. You have {AppVersion.Display}.";
            UpdateAvailable = true;
        }
        catch
        {
            // Never surface an update-check failure. The app works fine without it.
        }
    }

    /// <summary>Opens the releases page. The app downloads nothing itself.</summary>
    [RelayCommand]
    private void OpenReleasesPage()
    {
        try
        {
            Process.Start(new ProcessStartInfo(_updateUrl) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            GlobalStatus = $"Could not open browser: {ex.Message}";
        }

        DismissUpdate();
    }

    /// <summary>Hides the banner and remembers the tag so it is not shown again.</summary>
    [RelayCommand]
    private void DismissUpdate()
    {
        UpdateAvailable = false;

        if (string.IsNullOrWhiteSpace(_updateTag)) return;

        try
        {
            var config = _credentialStore.LoadConfig();
            config.LastNotifiedReleaseTag = _updateTag;
            _credentialStore.SaveConfig(config);
        }
        catch
        {
            // Worst case the banner shows again next launch.
        }
    }

    private void OnSqlConnectionChanged(object? sender, ServerConnectionChangedEventArgs e)
    {
        IsConnectedToSql = e.IsConnected;
        ConnectedServerName = e.IsConnected ? e.ServerName : "Not connected";
        Restore.IsConnectedToServer = e.IsConnected;
        Restore.ConnectedServerName = e.ServerName;
        Restore.ConnectedServer = e.ConnectedServer;
        GlobalStatus = e.IsConnected ? $"Connected to {e.ServerName}" : "Ready";
        _ = Restore.RefreshCredentialStatusAsync();
    }

    [RelayCommand]
    private void DisconnectSql()
    {
        ServerManager.DisconnectCommand.Execute(null);
    }

    /// <summary>
    /// The sidebar's view names. Constants rather than literals scattered across the switch,
    /// because an unrecognised name here silently lands on Blob Storage rather than failing -
    /// so a typo looks like a button that goes to the wrong place (#42).
    ///
    /// The XAML still passes them as strings; <see cref="Views"/> is what a test can check the
    /// switch against.
    /// </summary>
    public static class Nav
    {
        public const string BlobStorage = "Blob Storage";
        public const string SqlServers = "SQL Servers";
        public const string BrowseBackups = "Browse Backups";
        public const string Restore = "Restore";
        public const string History = "History";
        public const string About = "About";

        public static IReadOnlyList<string> Views =>
            [BlobStorage, SqlServers, BrowseBackups, Restore, History, About];
    }

    [RelayCommand]
    private void NavigateTo(string viewName)
    {
        CurrentViewName = viewName;
        CurrentView = viewName switch
        {
            Nav.BlobStorage => BlobConfig,
            Nav.SqlServers => ServerManager,
            Nav.BrowseBackups => BlobBrowser,
            Nav.Restore => Restore,
            Nav.History => History,
            Nav.About => About,
            _ => BlobConfig
        };

        // Refresh container lists when navigating to views that depend on them
        if (viewName is Nav.BrowseBackups)
            BlobBrowser.RefreshContainers();
        else if (viewName is Nav.Restore)
            Restore.RefreshContainers();
        else if (viewName is Nav.History)
            // Re-read on every visit: a restore run since the last look must be here, and the
            // history is written by the Restore screen rather than by this one.
            History.Refresh();
    }
}

public class ServerConnectionChangedEventArgs : EventArgs
{
    public bool IsConnected { get; init; }
    public string ServerName { get; init; } = string.Empty;
    public ServerConnection? ConnectedServer { get; init; }
}
