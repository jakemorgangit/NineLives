using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Blackcat.NineLives.Models;
using Blackcat.NineLives.Services;

namespace Blackcat.NineLives.ViewModels;

public partial class ServerManagerViewModel : ViewModelBase
{
    private readonly CredentialStore _credentialStore;
    private readonly SqlServerService _sqlService;

    public event EventHandler<ServerConnectionChangedEventArgs>? ConnectionChanged;

    [ObservableProperty]
    private ObservableCollection<ServerConnection> _servers = [];

    [ObservableProperty]
    private ServerConnection? _selectedServer;

    [ObservableProperty]
    private string _editName = string.Empty;

    /// <summary>Comma-separated tag list as typed by the user; parsed on save.</summary>
    [ObservableProperty]
    private string _editTags = string.Empty;

    [ObservableProperty]
    private string _editServerName = string.Empty;

    [ObservableProperty]
    private AuthMode _editAuthMode = AuthMode.WindowsAuth;

    public bool IsSqlAuth => EditAuthMode == AuthMode.SqlAuth;

    partial void OnEditAuthModeChanged(AuthMode value) => OnPropertyChanged(nameof(IsSqlAuth));

    [ObservableProperty]
    private string _editUsername = string.Empty;

    [ObservableProperty]
    private string _editPassword = string.Empty;

    [ObservableProperty]
    private int _editTimeout = 15;

    [ObservableProperty]
    private bool _editTrustServerCert = true;

    [ObservableProperty]
    private EncryptMode _editEncrypt = EncryptMode.Yes;

    [ObservableProperty]
    private bool _isEditing;

    [ObservableProperty]
    private bool _isNew;

    [ObservableProperty]
    private string _testResult = string.Empty;

    [ObservableProperty]
    private bool _testSuccess;

    [ObservableProperty]
    private string _serverVersion = string.Empty;

    [ObservableProperty]
    private bool _isConnected;

    [ObservableProperty]
    private string _connectedServerDisplay = string.Empty;

    public ServerManagerViewModel(CredentialStore credentialStore, SqlServerService sqlService)
    {
        _credentialStore = credentialStore;
        _sqlService = sqlService;
        LoadServers();
    }

    private void LoadServers()
    {
        var config = _credentialStore.LoadConfig();
        Servers = new ObservableCollection<ServerConnection>(config.Servers);
    }

    private void SaveServers()
    {
        var config = _credentialStore.LoadConfig();
        config.Servers = [.. Servers];
        _credentialStore.SaveConfig(config);
    }

    /// <summary>
    /// Records the product version parsed from a @@VERSION banner against a saved server, and
    /// refreshes its row so the automatic tag appears at once.
    ///
    /// Populated only when the user actually connects or tests - the app never probes saved
    /// servers on its own. Reaching out to every configured instance at startup, some of which
    /// are production, is not something a restore tool should do unasked.
    /// </summary>
    private void RecordDetectedVersion(ServerConnection? server, string? banner)
    {
        if (server == null) return;

        var detected = SqlVersionName.FromVersionBanner(banner);
        if (detected == null || detected == server.DetectedVersion) return;

        server.DetectedVersion = detected;
        SaveServers();
        RefreshServerRow(server);
    }

    /// <summary>
    /// Forces one row to re-render after a non-observable property changed on it.
    ///
    /// DetectedVersion is a plain property on a serialised model, so setting it raises nothing a
    /// binding can see. Tags avoid this by being an ObservableCollection mutated in place, but a
    /// scalar has no such escape - so the item is re-seated in the collection, which re-renders
    /// just that row. Selection is preserved because the instance is unchanged.
    /// </summary>
    private void RefreshServerRow(ServerConnection server)
    {
        var index = Servers.IndexOf(server);
        if (index < 0) return;

        var wasSelected = ReferenceEquals(SelectedServer, server);
        Servers.RemoveAt(index);
        Servers.Insert(index, server);
        if (wasSelected) SelectedServer = server;
    }

    [RelayCommand]
    private void AddNew()
    {
        EditName = string.Empty;
        EditTags = string.Empty;
        EditServerName = string.Empty;
        EditAuthMode = AuthMode.WindowsAuth;
        EditUsername = string.Empty;
        EditPassword = string.Empty;
        EditTimeout = 15;
        EditTrustServerCert = true;
        EditEncrypt = EncryptMode.Yes;
        IsNew = true;
        IsEditing = true;
        TestResult = string.Empty;
    }

    [RelayCommand]
    private void Edit()
    {
        if (SelectedServer == null) return;
        EditName = SelectedServer.Name;
        EditTags = TagPalette.FormatTags(SelectedServer.Tags);
        EditServerName = SelectedServer.ServerName;
        EditAuthMode = SelectedServer.AuthMode;
        EditUsername = SelectedServer.Username ?? string.Empty;
        EditPassword = string.Empty;
        EditTimeout = SelectedServer.ConnectionTimeoutSeconds;
        EditTrustServerCert = SelectedServer.TrustServerCertificate;
        EditEncrypt = SelectedServer.Encrypt;
        IsNew = false;
        IsEditing = true;
        TestResult = string.Empty;
    }

    [RelayCommand]
    private void CancelEdit()
    {
        IsEditing = false;
        ClearStatus();
    }

    [RelayCommand]
    private void Save()
    {
        if (string.IsNullOrWhiteSpace(EditName) || string.IsNullOrWhiteSpace(EditServerName))
        {
            SetError("Name and Server are required.");
            return;
        }

        if (EditAuthMode == AuthMode.SqlAuth && string.IsNullOrWhiteSpace(EditUsername))
        {
            SetError("Username is required for SQL Authentication.");
            return;
        }

        ServerConnection server;
        if (IsNew)
        {
            if (Servers.Any(s => s.Name.Equals(EditName, StringComparison.OrdinalIgnoreCase)))
            {
                SetError("A server with this name already exists.");
                return;
            }
            server = new ServerConnection();
            Servers.Add(server);
        }
        else
        {
            server = SelectedServer!;
        }

        server.Name = EditName;
        // Mutate in place - assigning a new collection raises no notification on a POCO, so the
        // pills would not appear until the user navigated away and back.
        ReplaceTags(server.Tags, TagPalette.ParseTags(EditTags));
        server.ServerName = EditServerName;
        server.AuthMode = EditAuthMode;
        server.Username = EditAuthMode == AuthMode.SqlAuth ? EditUsername : null;
        server.ConnectionTimeoutSeconds = EditTimeout;
        server.TrustServerCertificate = EditTrustServerCert;
        server.Encrypt = EditEncrypt;

        if (EditAuthMode == AuthMode.SqlAuth && !string.IsNullOrWhiteSpace(EditPassword))
        {
            _credentialStore.SaveSqlPassword(server, EditPassword);
        }

        SaveServers();
        SelectedServer = server;
        IsEditing = false;
        SetStatus("Server saved successfully.");
    }

    [RelayCommand]
    private void Delete()
    {
        if (SelectedServer == null) return;

        if (SelectedServer.AuthMode == AuthMode.SqlAuth)
            _credentialStore.DeleteSecret(SelectedServer.CredentialKey);

        if (IsConnected && ConnectedServerDisplay == SelectedServer.DisplayText)
        {
            IsConnected = false;
            ConnectedServerDisplay = string.Empty;
            ConnectionChanged?.Invoke(this, new ServerConnectionChangedEventArgs
            {
                IsConnected = false,
                ServerName = string.Empty
            });
        }

        Servers.Remove(SelectedServer);
        SaveServers();
        SelectedServer = Servers.FirstOrDefault();
        SetStatus("Server removed.");
    }

    [RelayCommand]
    private async Task TestConnectionAsync()
    {
        var server = BuildCurrentServer();
        if (server == null) return;

        IsBusy = true;
        TestResult = string.Empty;
        try
        {
            await _sqlService.TestConnectionAsync(server);
            var version = await _sqlService.GetServerVersionAsync(server);
            var firstLine = version.Split('\n').FirstOrDefault()?.Trim() ?? version;
            ServerVersion = firstLine;
            TestSuccess = true;
            TestResult = $"Connected successfully!\n{firstLine}";

            // Test Connection already proves the server and reads the banner, so record the
            // version tag from it too. BuildCurrentServer returns a throwaway object built from
            // the edit form, so the value has to be written back to the SAVED entry - otherwise
            // testing an existing server tells us the version and then discards it.
            RecordDetectedVersion(IsNew ? null : SelectedServer, version);
        }
        catch (Exception ex)
        {
            TestSuccess = false;
            TestResult = $"Connection failed: {ex.Message}";
            ServerVersion = string.Empty;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task ConnectAsync()
    {
        if (SelectedServer == null) return;

        IsBusy = true;
        try
        {
            await _sqlService.TestConnectionAsync(SelectedServer);

            // Derive the product-version tag from the connection we just proved works. Best
            // effort: a server that connects but will not answer @@VERSION should still connect.
            try
            {
                RecordDetectedVersion(SelectedServer, await _sqlService.GetServerVersionAsync(SelectedServer));
            }
            catch
            {
                // Leave any previously detected value alone rather than clearing it.
            }

            IsConnected = true;
            ConnectedServerDisplay = SelectedServer.DisplayText;
            ConnectionChanged?.Invoke(this, new ServerConnectionChangedEventArgs
            {
                IsConnected = true,
                ServerName = SelectedServer.ServerName,
                ConnectedServer = SelectedServer
            });
            SetStatus($"Connected to {SelectedServer.ServerName}");
        }
        catch (Exception ex)
        {
            SetError($"Connection failed: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void Disconnect()
    {
        IsConnected = false;
        ConnectedServerDisplay = string.Empty;
        ConnectionChanged?.Invoke(this, new ServerConnectionChangedEventArgs
        {
            IsConnected = false,
            ServerName = string.Empty
        });
        SetStatus("Disconnected.");
    }

    private ServerConnection? BuildCurrentServer()
    {
        if (IsEditing)
        {
            var server = new ServerConnection
            {
                Name = EditName,
                ServerName = EditServerName,
                AuthMode = EditAuthMode,
                Username = EditUsername,
                ConnectionTimeoutSeconds = EditTimeout,
                TrustServerCertificate = EditTrustServerCert,
                Encrypt = EditEncrypt
            };
            if (EditAuthMode == AuthMode.SqlAuth && !string.IsNullOrWhiteSpace(EditPassword))
                _credentialStore.SaveSqlPassword(server, EditPassword);
            return server;
        }
        return SelectedServer;
    }
}
