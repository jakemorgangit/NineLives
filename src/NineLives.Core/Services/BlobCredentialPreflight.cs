using Blackcat.NineLives.Models;

namespace Blackcat.NineLives.Services;

/// <summary>
/// Whether a run that talks TO URL may start, and what to say when it may not. A refusal here
/// has touched nothing, so the caller has nothing to unwind.
/// </summary>
public readonly record struct CredentialPreflight(bool CanProceed, string? Refusal)
{
    public static CredentialPreflight Proceed => new(true, null);

    public static CredentialPreflight Stop(string reason) => new(false, reason);
}

/// <summary>
/// The credential question, asked BEFORE anything reads or writes a container (#284). This
/// was the restore screen's preflight; BACKUP TO URL needs the credential on the source and
/// the copy needs it on both ends, and neither used to check - a missing or wrong-identity
/// credential surfaced as Msg 3201 after the arm-and-confirm, and on the copy after the
/// source had already been read at full speed. Extracted here so every front end asks the
/// same question the same way.
///
/// The rules it keeps are the ones learned the hard way on the restore screen: only create
/// when genuinely missing (#10); never touch an existing credential that can already restore
/// (#145 - "fixing" a managed identity into a SAS took every other job on the container with
/// it); an existing-but-unusable identity is a refusal, not a conversion.
/// </summary>
public static class BlobCredentialPreflight
{
    public static async Task<CredentialPreflight> EnsureAsync(
        ICredentialStore store, ISqlServerService sql, OperationLog? log,
        BlobContainerConfig container, ServerConnection server, Action<string> appendLog)
    {
        var credentialName = container.ContainerUrl;

        // No stored token means nothing this app could write anyway - an Entra container has
        // none by design. Whatever is on the server is what the run will use. The in-memory
        // copy wins over the vault, same rule as the blob service (#302): an unsaved or
        // ephemeral container carries its secret on the object, never in a store.
        var sasToken = container.UnsavedSasToken ?? store.GetSasToken(container);
        if (string.IsNullOrEmpty(sasToken)) return CredentialPreflight.Proceed;

        var credential = await sql.CredentialExistsAsync(server, credentialName);

        if (credential.CanRestoreFromUrl)
        {
            appendLog(credential.Kind == BlobCredentialIdentity.ManagedIdentity
                ? $"Using the existing SQL credential [{credentialName}] (Managed Identity) on " +
                  $"{server.ServerName}. Server state not modified."
                : $"Using the existing SQL credential [{credentialName}] on {server.ServerName}. " +
                  "Server state not modified.");
            return CredentialPreflight.Proceed;
        }

        if (credential.Exists)
        {
            // Neither usable nor ours to reinterpret. Converting it is a decision somebody
            // makes on the credential panel, not a side effect of pressing a run button.
            var refusal =
                $"Credential [{credentialName}] exists on {server.ServerName} with identity " +
                $"'{credential.Identity}', which TO URL cannot use. Left untouched: replacing " +
                "it with the stored SAS token is what \"Create credential on server\" does, so " +
                "that it stays a deliberate change.";
            appendLog(refusal);
            return CredentialPreflight.Stop(refusal);
        }

        appendLog($"Credential [{credentialName}] is missing on {server.ServerName} - creating it...");

        // The one moment a secret crosses the wire; if the connection trusts an unvalidated
        // certificate, say so here rather than only in settings (#17).
        if (server.TrustServerCertificate)
            appendLog(
                "  Note: this connection trusts the server certificate without validating it, " +
                "and the SAS token is sent over it.");

        var written = await sql.EnsureCredentialExistsAsync(
            server, credentialName, container.ContainerUrl, sasToken);

        appendLog(written == CredentialChange.Created
            ? "Credential created on the server."
            : "Credential updated on the server.");

        // Changing shared state on someone's instance belongs in the file, not only in a
        // console that closes with the app.
        log?.ServerChange(server.ServerName,
            $"credential [{credentialName}] {written.ToString().ToLowerInvariant()}");

        return CredentialPreflight.Proceed;
    }
}
