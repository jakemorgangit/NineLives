using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Blackcat.NineLives.Models;
using Blackcat.NineLives.Services;

namespace Blackcat.NineLives.ViewModels;

public partial class ServerManagerViewModel : ViewModelBase
{
    private readonly ICredentialStore _credentialStore;
    private readonly ISqlServerService _sqlService;

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

    public ServerManagerViewModel(ICredentialStore credentialStore, ISqlServerService sqlService)
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

    /// <summary>
    /// Captures a server's persisted fields and returns the action that puts them back. Used to
    /// undo an in-place edit when the config write is refused.
    /// </summary>
    private static Action Snapshot(ServerConnection server)
    {
        var name = server.Name;
        var serverName = server.ServerName;
        var authMode = server.AuthMode;
        var username = server.Username;
        var timeout = server.ConnectionTimeoutSeconds;
        var trust = server.TrustServerCertificate;
        var encrypt = server.Encrypt;
        var tags = server.Tags.ToList();

        return () =>
        {
            server.Name = name;
            server.ServerName = serverName;
            server.AuthMode = authMode;
            server.Username = username;
            server.ConnectionTimeoutSeconds = timeout;
            server.TrustServerCertificate = trust;
            server.Encrypt = encrypt;
            ReplaceTags(server.Tags, tags);
        };
    }

    /// <summary>
    /// Persists the server list. Returns false and sets the error when the write failed, so
    /// callers do not go on to report success - the config save used to swallow everything and
    /// the UI said "saved successfully" whether or not anything reached the disk.
    /// </summary>
    private bool SaveServers()
    {
        try
        {
            var config = _credentialStore.LoadConfig();
            config.Servers = [.. Servers];
            _credentialStore.SaveConfig(config);
            return true;
        }
        catch (Exception ex)
        {
            SetError($"Could not save configuration: {ex.Message}");
            return false;
        }
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

        // Undoes the edit if the save is refused. Null for a new server, which is removed instead.
        Action? restore = null;

        if (IsNew)
        {
            if (Servers.Any(s => s.Name.Equals(EditName, StringComparison.OrdinalIgnoreCase)))
            {
                SetError("A server with this name already exists.");
                return;
            }
            // Id assigned here rather than defaulted on the model - see the note on it.
            server = new ServerConnection { Id = ServerConnection.NewId() };
            Servers.Add(server);
        }
        else
        {
            server = SelectedServer!;

            // Snapshot before mutating, so a refused save can put the model back rather than
            // leaving it showing values that were never persisted.
            restore = Snapshot(server);
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

        // Config FIRST, password second.
        //
        // The other order wrote the password to Credential Manager and then found the config could
        // not be saved. Change a login's username and password together with config.json briefly
        // locked, and the vault holds the NEW password while the file still has the OLD username -
        // so every connection fails auth, and the form never shows a stored password, so nothing
        // on screen says why. Delete already follows this rule.
        if (!SaveServers())
        {
            // Nothing reached the disk, so do not leave the list showing a server that is not
            // really there. Stay in the edit form so the save can be retried.
            if (IsNew) Servers.Remove(server);
            else restore?.Invoke();
            return;
        }

        if (EditAuthMode == AuthMode.SqlAuth && !string.IsNullOrWhiteSpace(EditPassword))
        {
            try
            {
                _credentialStore.SaveSqlPassword(server, EditPassword);
            }
            catch (Exception ex)
            {
                SetError($"The server was saved, but the password could not be stored: {ex.Message}");
                return;
            }
        }

        SelectedServer = server;
        IsEditing = false;
        SetStatus("Server saved successfully.");
    }

    [RelayCommand]
    private void Delete()
    {
        if (SelectedServer == null) return;

        // Same reasoning as deleting a container: a stored SQL password is removed from Credential
        // Manager and the app never displays it, so a single click should not be enough (#42).
        var secretNote = SelectedServer.AuthMode == AuthMode.SqlAuth
            ? "\n\nIts stored SQL password will be deleted from Windows Credential Manager."
            : string.Empty;

        var confirm = MessageBox.Show(
            $"Remove the server \"{SelectedServer.Name}\"?{secretNote}\n\n" +
            "Nothing on the SQL Server itself is affected.",
            "Nine Lives", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);

        if (confirm != MessageBoxResult.Yes) return;

        // Take the server out of the config first and only destroy its stored password once that
        // write has landed. The other order threw the password away and then, if the save failed,
        // left the server in config.json with no credential behind it.
        var removed = SelectedServer;
        var index = Servers.IndexOf(removed);
        Servers.Remove(removed);
        if (!SaveServers())
        {
            Servers.Insert(Math.Clamp(index, 0, Servers.Count), removed);
            SelectedServer = removed;
            return;
        }

        if (removed.AuthMode == AuthMode.SqlAuth)
            _credentialStore.DeleteSecret(removed.CredentialKey);

        if (IsConnected && ConnectedServerDisplay == removed.DisplayText)
        {
            IsConnected = false;
            MarkConnected(null);
            ConnectedServerDisplay = string.Empty;
            ConnectionChanged?.Invoke(this, new ServerConnectionChangedEventArgs
            {
                IsConnected = false,
                ServerName = string.Empty
            });
        }

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

            // While we are here, find out whether trusting the certificate is actually needed.
            // See WouldConnectWithCertificateValidationAsync - the point is to replace a warning
            // people learn to ignore with a fact they can act on (#17).
            if (server.TrustServerCertificate)
            {
                var wouldValidate = await _sqlService.WouldConnectWithCertificateValidationAsync(server);
                TestResult += wouldValidate switch
                {
                    true => "\n\nThis server's certificate validates. You can untick "
                          + "\"Trust server certificate\" - nothing will break, and the password "
                          + "and SAS token will no longer be sent over an unverified connection.",
                    false => "\n\nNote: the certificate does not validate, so \"Trust server "
                           + "certificate\" is doing real work here. The connection is still "
                           + "encrypted, but it is not verified - a machine-in-the-middle on the "
                           + "network path could read the password and SAS token.",
                    null => string.Empty
                };
            }

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
            MarkConnected(SelectedServer);
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

    /// <summary>
    /// Exactly one server carries the connected marker, so the list can never show two.
    /// </summary>
    private void MarkConnected(ServerConnection? connected)
    {
        foreach (var server in Servers)
            server.IsConnectedServer = ReferenceEquals(server, connected);
    }

    [RelayCommand]
    private void Disconnect()
    {
        IsConnected = false;
        ConnectedServerDisplay = string.Empty;
        MarkConnected(null);
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
            // In memory only. This used to call SaveSqlPassword, which is a durable write to
            // Credential Manager - so testing a mistyped password overwrote the working one
            // before the user had agreed to anything, and Cancel could not undo it (#12).
            if (EditAuthMode == AuthMode.SqlAuth && !string.IsNullOrWhiteSpace(EditPassword))
                server.UnsavedPassword = EditPassword;
            else if (EditAuthMode == AuthMode.SqlAuth && !IsNew)
                // No new password typed: test against the one already saved for this entry.
                server.Id = SelectedServer?.Id;

            return server;
        }
        return SelectedServer;
    }
}
