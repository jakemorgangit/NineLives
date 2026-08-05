using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Blackcat.NineLives.Models;
using Blackcat.NineLives.Services;

namespace Blackcat.NineLives.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    private readonly CredentialStore _credentialStore;
    private readonly BlobStorageService _blobService;
    private readonly SqlServerService _sqlService;
    private readonly BackupChainBuilder _chainBuilder;
    private readonly RestoreScriptGenerator _scriptGenerator;

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

    public BlobConfigViewModel BlobConfig { get; }
    public ServerManagerViewModel ServerManager { get; }
    public BlobBrowserViewModel BlobBrowser { get; }
    public RestoreViewModel Restore { get; }
    public AboutViewModel About { get; }

    public MainViewModel()
    {
        _credentialStore = new CredentialStore();
        _blobService = new BlobStorageService(_credentialStore);
        _sqlService = new SqlServerService(_credentialStore);
        _chainBuilder = new BackupChainBuilder();
        _scriptGenerator = new RestoreScriptGenerator();

        BlobConfig = new BlobConfigViewModel(_credentialStore, _blobService);
        ServerManager = new ServerManagerViewModel(_credentialStore, _sqlService);
        BlobBrowser = new BlobBrowserViewModel(_blobService, _credentialStore);
        Restore = new RestoreViewModel(_blobService, _sqlService, _chainBuilder, _scriptGenerator, _credentialStore);
        About = new AboutViewModel();

        ServerManager.ConnectionChanged += OnSqlConnectionChanged;

        CurrentView = BlobConfig;

        // Fire and forget - startup must not wait on the network.
        _ = CheckForUpdatesAsync();
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

    [RelayCommand]
    private void NavigateTo(string viewName)
    {
        CurrentViewName = viewName;
        CurrentView = viewName switch
        {
            "Blob Storage" => BlobConfig,
            "SQL Servers" => ServerManager,
            "Browse Backups" => BlobBrowser,
            "Restore" => Restore,
            "About" => About,
            _ => BlobConfig
        };

        // Refresh container lists when navigating to views that depend on them
        if (viewName is "Browse Backups")
            BlobBrowser.RefreshContainers();
        else if (viewName is "Restore")
            Restore.RefreshContainers();
    }
}

public class ServerConnectionChangedEventArgs : EventArgs
{
    public bool IsConnected { get; init; }
    public string ServerName { get; init; } = string.Empty;
    public ServerConnection? ConnectedServer { get; init; }
}
