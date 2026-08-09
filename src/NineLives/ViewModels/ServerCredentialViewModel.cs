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

        // The button says what a press would DO, which depends on what is already there (#147).
        OnPropertyChanged(nameof(CreateButtonText));
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

    // ── which identity to write (#147) ──────────────────────────────────────────
    //
    // #29 added Entra ID for browsing precisely because many organisations forbid long-lived SAS
    // tokens. Those organisations could then browse a container without one and still not restore
    // without one, because the credential this wrote was always a SAS - so the SAS-free path
    // stopped half way, and on an Entra container the button failed outright with "no SAS token
    // stored", which is true and useless.

    /// <summary>The identity the button will write.</summary>
    [ObservableProperty]
    private BlobCredentialIdentity _identityToCreate = BlobCredentialIdentity.SharedAccessSignature;

    public bool CreatingManagedIdentity => IdentityToCreate == BlobCredentialIdentity.ManagedIdentity;

    partial void OnIdentityToCreateChanged(BlobCredentialIdentity value)
    {
        OnPropertyChanged(nameof(CreatingManagedIdentity));
        OnPropertyChanged(nameof(CreateButtonText));
        CreateOnServerCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// What pressing the button would actually DO.
    ///
    /// It has to read both what is on the server and what is about to be written, because the
    /// interesting presses are the ones that change one into the other - and a label describing
    /// only the current state is exactly the bug #145 was: the same button read as a harmless
    /// refresh while it converted the instance's managed identity to a SAS token.
    ///
    /// Now that the identity is a choice rather than a constant, the reverse conversion needs the
    /// same treatment. It is no less a change to how the whole instance reaches that container.
    /// </summary>
    public string CreateButtonText => (IdentityKind, CreatingManagedIdentity) switch
    {
        (BlobCredentialIdentity.ManagedIdentity, false) =>
            "Replace Managed Identity with stored SAS token",

        (BlobCredentialIdentity.SharedAccessSignature, true) =>
            "Replace SAS token with the instance's managed identity",

        (BlobCredentialIdentity.ManagedIdentity, true) =>
            "Refresh credential as the instance's managed identity",

        (BlobCredentialIdentity.SharedAccessSignature, false) =>
            "Refresh credential with stored SAS token",

        (_, true) => "Create credential as the instance's managed identity",
        _ => "Create credential on server"
    };

    /// <summary>Whether the instance can use one at all, once it has been asked.</summary>
    [ObservableProperty]
    private bool _managedIdentitySupported;

    /// <summary>Why not, when it cannot - named rather than a bare refusal.</summary>
    [ObservableProperty]
    private string _managedIdentityBlockedReason = string.Empty;

    /// <summary>
    /// What creating the credential does NOT buy, shown alongside the option rather than after it
    /// fails. The statement succeeds whether or not the instance has an identity at all.
    /// </summary>
    public static string ManagedIdentityCaveat => BlobCredentialStatement.WhatItStillNeeds;

    /// <summary>
    /// Asks the instance whether a managed-identity credential is worth offering, and picks the
    /// identity that can actually work here.
    ///
    /// An Entra container defaults to managed identity because it is the only thing that CAN work
    /// there - there is no stored token by design. A SAS container keeps the SAS default, since
    /// that is what it has.
    /// </summary>
    private async Task RefreshManagedIdentitySupportAsync(CancellationToken ct = default)
    {
        if (Server == null)
        {
            ManagedIdentitySupported = false;
            ManagedIdentityBlockedReason = string.Empty;
            return;
        }

        try
        {
            var support = await _sql.SupportsManagedIdentityCredentialAsync(Server, ct);

            ManagedIdentitySupported = support.IsSupported;
            ManagedIdentityBlockedReason = support.Explain();

            // An Entra container, and only an Entra container. "Not SAS" is not the same test:
            // with no container chosen yet, null is not SasToken either - which defaulted the
            // identity to managed before there was anything to authenticate against, and an
            // existing test caught it by finding a write where it expected a refusal.
            if (support.IsSupported && Container is { AuthMode: not BlobAuthMode.SasToken })
                IdentityToCreate = BlobCredentialIdentity.ManagedIdentity;
            else if (!support.IsSupported)
                IdentityToCreate = BlobCredentialIdentity.SharedAccessSignature;
        }
        catch
        {
            // Not being able to ask is not the same as the answer being no, but it is the same
            // outcome: without knowing, offering it would risk writing a credential that looks
            // right and fails at restore time.
            ManagedIdentitySupported = false;
            ManagedIdentityBlockedReason =
                "Could not ask this instance whether it supports managed identity, so the option " +
                "is not offered - a credential written blind would look correct and fail at " +
                "restore time.";
        }
    }

    /// <summary>Create or update the credential, with the identity currently chosen.</summary>
    [RelayCommand(CanExecute = nameof(CanCreate))]
    private async Task CreateOnServerAsync()
    {
        if (Server == null || Container == null) return;

        var sasToken = _store.GetSasToken(Container);

        // Only a SAS credential needs one. A managed-identity credential carries no secret at all,
        // which is the entire point for an estate that forbids them.
        if (IdentityToCreate == BlobCredentialIdentity.SharedAccessSignature &&
            string.IsNullOrEmpty(sasToken))
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
                Server, Name, Container.ContainerUrl, sasToken ?? string.Empty,
                IdentityToCreate, _writeCancellation.Begin());

            await RefreshAsync();

            Reported?.Invoke(change switch
            {
                CredentialChange.Created when CreatingManagedIdentity =>
                    "Credential created on server, authenticating as the instance's managed identity. " +
                    BlobCredentialStatement.WhatItStillNeeds,

                CredentialChange.Created => "Credential created on server.",

                // Both directions of a conversion are worth naming. Replacing a managed identity
                // with a SAS was already called out (#145); replacing a SAS with a managed identity
                // discards the stored token on the server, which is equally a change to how the
                // instance authenticates rather than a routine update.
                _ when CreatingManagedIdentity && replaced == BlobCredentialIdentity.SharedAccessSignature =>
                    "Credential replaced on server: it held a SAS token and now authenticates as " +
                    "the instance's managed identity. " + BlobCredentialStatement.WhatItStillNeeds,

                _ when CreatingManagedIdentity =>
                    "Credential updated on server, authenticating as the instance's managed identity.",

                _ when replaced == BlobCredentialIdentity.ManagedIdentity =>
                    "Credential replaced on server: it authenticated as the instance's managed " +
                    "identity and now holds the stored SAS token.",

                _ => "Credential updated on server with the stored SAS token."
            }, false);

            if (replaced == BlobCredentialIdentity.ManagedIdentity && !CreatingManagedIdentity)
                _log.ServerChange(Server.ServerName,
                    $"credential [{Name}] identity changed from Managed Identity to SAS");

            if (CreatingManagedIdentity && replaced == BlobCredentialIdentity.SharedAccessSignature)
                _log.ServerChange(Server.ServerName,
                    $"credential [{Name}] identity changed from SAS to Managed Identity");
        }
        catch (Exception ex) when (BlobCredentialStatement.IsIdentityChangeRefusal(ex.Message))
        {
            // Not a failure of this app, and not something it will work around by dropping the
            // credential - see #10. Reported as the specific thing it is, with the remedy.
            Reported?.Invoke(BlobCredentialStatement.ExplainIdentityChangeRefusal(Name), true);
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
    private bool CanCreate() =>
        Server != null &&
        Container != null &&
        (IdentityToCreate != BlobCredentialIdentity.ManagedIdentity || ManagedIdentitySupported);

    partial void OnServerChanged(ServerConnection? value)
    {
        CreateOnServerCommand.NotifyCanExecuteChanged();

        // A different instance answers differently, and the answer decides what is offered.
        _ = RefreshManagedIdentitySupportAsync();
    }

    partial void OnContainerChanged(BlobContainerConfig? value)
    {
        CreateOnServerCommand.NotifyCanExecuteChanged();

        // Which container it is decides which identity can work: an Entra container has no stored
        // token by design, so a SAS credential is not an option there at all.
        _ = RefreshManagedIdentitySupportAsync();
    }

    partial void OnManagedIdentitySupportedChanged(bool value)
        => CreateOnServerCommand.NotifyCanExecuteChanged();

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
