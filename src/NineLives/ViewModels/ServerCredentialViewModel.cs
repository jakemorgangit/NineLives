using Blackcat.NineLives.Models;
using Blackcat.NineLives.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Blackcat.NineLives.ViewModels;

/// <summary>
/// Whether the connected instance can reach the container, and what to do when it cannot.
///
/// Fifth seam out of RestoreViewModel (#115). Small, but it was spread across four places that had
/// to agree: the panel's status text, the button's label, what Execute does before it runs, and the
/// sequencing that keeps a slow check from answering for a container the user has already left.
/// They disagreed - #145 was the panel calling a managed identity broken and Execute then acting on
/// that, which are two of those four places.
///
/// RESTORE FROM URL authenticates through a credential on the SERVER, named for the container URL
/// and matched by prefix. Nothing here is about how this app reaches the container: that is the SAS
/// token or Entra sign-in on the container config, and the two are independent - browsing with
/// Entra while the instance restores with its own managed identity is an ordinary pairing.
/// </summary>
public partial class ServerCredentialViewModel : ObservableObject
{
    private readonly ISqlServerService _sql;
    private readonly ICredentialStore _store;
    private readonly OperationLog _log;

    /// <summary>
    /// Shared with the parent's other server calls, so the Stop button stops a credential write
    /// too (#111). It is the parent's object: these are mutually exclusive buttons, and starting
    /// one while another runs should stop the first.
    /// </summary>
    private readonly OperationCancellation _writeCancellation;

    /// <summary>
    /// The background check has its own source. It is not user-initiated, has no Stop button, and
    /// races itself: two overlapping checks would leave the panel showing whichever finished last,
    /// which could be the container the user just moved away from - and that panel is what someone
    /// reads before deciding whether to create a credential (#111).
    /// </summary>
    private readonly OperationCancellation _checkCancellation = new();

    public ServerCredentialViewModel(
        ISqlServerService sql, ICredentialStore store, OperationLog log,
        OperationCancellation writeCancellation)
    {
        _sql = sql;
        _store = store;
        _log = log;
        _writeCancellation = writeCancellation;
    }

    /// <summary>
    /// Something the user should see on the app's status line; true when it is an error.
    ///
    /// An event rather than a call back into the parent, so this type can be exercised without one.
    /// <see cref="PrepareForRestoreAsync"/> deliberately does NOT raise it - a refusal there stops
    /// the restore, and the execute path reports it alongside everything else it has to unwind.
    /// </summary>
    public event Action<string, bool>? Reported;

    /// <summary>The instance the panel is describing, or null when not connected.</summary>
    [ObservableProperty]
    private ServerConnection? _server;

    /// <summary>The container whose URL names the credential.</summary>
    [ObservableProperty]
    private BlobContainerConfig? _container;

    /// <summary>
    /// The credential name, which is the container URL by default and editable free text on screen.
    /// Editable because the credential may already exist under a name somebody else chose.
    /// </summary>
    [ObservableProperty]
    private string _name = string.Empty;

    /// <summary>Null until a check has run, so "not checked yet" does not render as "missing".</summary>
    [ObservableProperty]
    private bool? _existsOnServer;

    // What the credential of that name actually authenticates as. The three views below are derived
    // rather than stored: they used to be one bool that conflated "not a SAS credential" with
    // "unusable", which is how a managed identity came to be treated as damage (#145).
    [ObservableProperty]
    private BlobCredentialIdentity _identityKind = BlobCredentialIdentity.Missing;

    /// <summary>A restore can authenticate with what is on the server as it stands.</summary>
    public bool IsUsable =>
        IdentityKind is BlobCredentialIdentity.SharedAccessSignature
            or BlobCredentialIdentity.ManagedIdentity;

    /// <summary>The credential holds a SAS, so the stored token is the thing that can go stale.</summary>
    public bool IsSharedAccessSignature =>
        IdentityKind == BlobCredentialIdentity.SharedAccessSignature;

    /// <summary>The instance authenticates to storage as itself, and this app must not touch it.</summary>
    public bool IsManagedIdentity => IdentityKind == BlobCredentialIdentity.ManagedIdentity;

    partial void OnIdentityKindChanged(BlobCredentialIdentity value)
    {
        OnPropertyChanged(nameof(IsUsable));
        OnPropertyChanged(nameof(IsSharedAccessSignature));
        OnPropertyChanged(nameof(IsManagedIdentity));
    }

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private bool _isChecking;

    /// <summary>The panel only means anything once a container is selected.</summary>
    [ObservableProperty]
    private bool _sectionVisible;

    /// <summary>
    /// Point this at a container: the credential is named for its URL, and everything on the panel
    /// belongs to it. Clears the previous container's answer before asking about the new one, so a
    /// stale verdict is never on screen under a different container's name.
    /// </summary>
    public Task PointAtAsync(BlobContainerConfig? container)
    {
        Container = container;
        SectionVisible = container != null;
        if (container != null) Name = container.ContainerUrl;
        return RefreshAsync();
    }

    /// <summary>Call when the connected server or the selected container changes.</summary>
    public async Task RefreshAsync()
    {
        SectionVisible = Container != null;
        if (Server == null || Container == null)
        {
            ExistsOnServer = null;
            StatusMessage = string.Empty;
            IdentityKind = BlobCredentialIdentity.Missing;
            return;
        }

        // Cancels any check already in flight. See _checkCancellation.
        var ct = _checkCancellation.Begin();

        IsChecking = true;
        StatusMessage = "Checking...";
        var server = Server;
        try
        {
            var credential = await _sql.CredentialExistsAsync(server, Name, ct);
            ExistsOnServer = credential.Exists;
            IdentityKind = credential.Kind;
            StatusMessage = Describe(credential);
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer check - which is about to write its own answer here. Saying
            // anything would just be the older check having the last word again.
            return;
        }
        catch (Exception ex)
        {
            ExistsOnServer = null;
            IdentityKind = BlobCredentialIdentity.Missing;
            StatusMessage = $"Could not check credential: {ex.Message}";
        }
        finally
        {
            // Only when this check is still the current one. Ending the newer check's source, or
            // clearing its "Checking..." state, is how the previous version left the panel
            // reporting the container the user had already moved away from.
            if (!ct.IsCancellationRequested)
            {
                _checkCancellation.End();
                IsChecking = false;
                CreateOnServerCommand.NotifyCanExecuteChanged();
            }
        }
    }

    /// <summary>
    /// The panel's one line about what is on the server. An unusable credential is named rather
    /// than just rejected: "not a SAS credential" was the same sentence for a managed identity
    /// that would have restored perfectly well and for a Windows account that never could (#145).
    /// </summary>
    internal static string Describe(BlobCredentialStatus credential) => credential.Kind switch
    {
        BlobCredentialIdentity.SharedAccessSignature =>
            "Credential is present and valid (SHARED ACCESS SIGNATURE).",
        BlobCredentialIdentity.ManagedIdentity =>
            "Credential is present and valid (Managed Identity). The restore authenticates as the " +
            "instance's own identity, so no SAS token is involved and this app will not modify it.",
        BlobCredentialIdentity.Other =>
            $"Credential exists with identity '{credential.Identity}', which a restore from URL " +
            "cannot use. Replace it below, or point at a different credential.",
        _ => "Credential is not present on this server. Restore will fail unless you create it."
    };

    /// <summary>Create or update the credential with the container's stored SAS token.</summary>
    [RelayCommand(CanExecute = nameof(CanCreate))]
    private async Task CreateOnServerAsync()
    {
        if (Server == null || Container == null) return;

        var sasToken = _store.GetSasToken(Container);
        if (string.IsNullOrEmpty(sasToken))
        {
            Reported?.Invoke(
                "No SAS token stored for this container. Add or refresh the token in Blob Storage config.",
                true);
            return;
        }

        // What is about to be overwritten, read before the write. This is the only route by which
        // a managed identity gets replaced by a SAS token, and it should say so afterwards rather
        // than reporting a routine "updated" for a change to how the instance authenticates (#145).
        var replaced = IdentityKind;

        try
        {
            var change = await _sql.EnsureCredentialExistsAsync(
                Server, Name, Container.ContainerUrl, sasToken, _writeCancellation.Begin());
            await RefreshAsync();
            Reported?.Invoke(change switch
            {
                CredentialChange.Created => "Credential created on server.",
                _ when replaced == BlobCredentialIdentity.ManagedIdentity =>
                    "Credential replaced on server: it authenticated as the instance's managed " +
                    "identity and now holds the stored SAS token.",
                _ => "Credential updated on server with the stored SAS token."
            }, false);

            if (replaced == BlobCredentialIdentity.ManagedIdentity)
                _log.ServerChange(Server.ServerName,
                    $"credential [{Name}] identity changed from Managed Identity to SAS");
        }
        catch (Exception ex)
        {
            Reported?.Invoke($"Failed to create credential: {ex.Message}", true);
        }
    }

    // Deliberately available even when the credential is present and valid. SQL Server will not
    // hand back a credential's secret, so "present and SAS" says nothing about whether the token
    // inside it still works - a SAS that was rotated or has expired looks identical from here.
    // Since Execute no longer rewrites the credential on every run, this button is the only way
    // to push a fresh token, and hiding it left users with no route at all.
    private bool CanCreate() => Server != null && Container != null;

    partial void OnServerChanged(ServerConnection? value) => CreateOnServerCommand.NotifyCanExecuteChanged();
    partial void OnContainerChanged(BlobContainerConfig? value) => CreateOnServerCommand.NotifyCanExecuteChanged();

    /// <summary>
    /// What the execute path should do about the credential before it starts, and everything it
    /// should say about it. Writes to the server only when there is nothing of that name.
    /// </summary>
    public async Task<CredentialPreflight> PrepareForRestoreAsync(
        ServerConnection server, Action<string> appendLog)
    {
        if (Container == null) return CredentialPreflight.Proceed;

        // No stored token means nothing this app could write anyway - an Entra container has none
        // by design. Whatever is on the server is what the restore will use, and it is not this
        // app's to guess at.
        var sasToken = _store.GetSasToken(Container);
        if (string.IsNullOrEmpty(sasToken)) return CredentialPreflight.Proceed;

        // Only write when the credential is genuinely missing. This used to drop and recreate on
        // every single execute, regardless of the status the panel was displaying, which
        // contradicted the UI's own "creating it is optional" wording and briefly removed a
        // credential that other sessions may have been using (#10).
        //
        // A credential that exists and is SAS is left alone. Its secret could still be a rotated
        // SAS that no longer works - unknowable from here, since the secret cannot be read back -
        // so the fix for that case is the explicit button, not rewriting server state on every run.
        //
        // A managed identity is left alone for a stronger reason: it restores perfectly well, this
        // app cannot create one, and ALTER would reset the identity. Reading "not SAS" as "broken"
        // is what silently converted somebody's working managed identity into a SAS token here,
        // taking every other job on that container with it, under the log line "Credential updated
        // on the server" (#145).
        var credential = await _sql.CredentialExistsAsync(server, Name);

        if (credential.CanRestoreFromUrl)
        {
            appendLog(credential.Kind == BlobCredentialIdentity.ManagedIdentity
                ? $"Using the existing SQL credential [{Name}] (Managed Identity). Server state not modified."
                : $"Using the existing SQL credential [{Name}]. Server state not modified.");
            return CredentialPreflight.Proceed;
        }

        if (credential.Exists)
        {
            // Neither usable nor ours to reinterpret. Converting it is a real option - it is what
            // the button on the panel does - but it must be a decision somebody made, not a side
            // effect of pressing Execute. Stopping costs a restore that was about to fail on blob
            // access anyway.
            var refusal =
                $"Credential [{Name}] exists with identity '{credential.Identity}', which a " +
                "restore from URL cannot use. Left untouched: replacing it with the stored SAS " +
                "token is what \"Create credential on server\" does, so that it is a deliberate change.";
            appendLog(refusal);
            return CredentialPreflight.Stop(refusal);
        }

        appendLog($"Credential [{Name}] is missing - creating it...");

        // This statement carries the SAS token to the server. It is the one moment in a restore
        // where a secret crosses the wire, so if the connection is encrypted but unverified, say so
        // here rather than only in settings (#17).
        if (server.TrustServerCertificate)
            appendLog(
                "  Note: this connection trusts the server certificate without validating it, " +
                "and the SAS token is sent over it.");

        var written = await _sql.EnsureCredentialExistsAsync(
            server, Name, Container.ContainerUrl, sasToken);

        appendLog(written == CredentialChange.Created
            ? "Credential created on the server."
            : "Credential updated on the server.");

        // Changing a credential is a change to shared state on someone's instance. It belongs in
        // the file, not only in a console that closes with the app.
        _log.ServerChange(server.ServerName,
            $"credential [{Name}] {written.ToString().ToLowerInvariant()}");

        return CredentialPreflight.Proceed;
    }
}

/// <summary>
/// Whether a restore may start, and what to say when it may not. A refusal here has touched
/// nothing, so the caller has nothing to unwind.
/// </summary>
public readonly record struct CredentialPreflight(bool CanProceed, string? Refusal)
{
    public static CredentialPreflight Proceed => new(true, null);

    public static CredentialPreflight Stop(string reason) => new(false, reason);
}
