using Blackcat.NineLives.Models;
using Blackcat.NineLives.Services;
using Microsoft.Data.SqlClient;
using Xunit;

namespace Blackcat.NineLives.Tests;

/// <summary>
/// Live proof that a credential name cannot break out of its bracket quoting.
///
/// CREATE/DROP CREDENTIAL take the name as an identifier, so it cannot be parameterised and has
/// to be quoted. It was previously interpolated raw, and the name is auto-populated from the
/// container URL in config.json - a plain-text file with no integrity check - so a single local
/// file write turned into arbitrary T-SQL on whichever instance the DBA connected to.
///
/// These tests create and drop credentials on the target instance, so they are gated on
/// NINELIVES_TEST_SQL like the other live tests and clean up after themselves. The payload used
/// below is deliberately harmless: if the injection were still open it would create a second,
/// clearly-named credential rather than change any permissions.
/// </summary>
public class CredentialInjectionTests
{
    private static string? TestServerName =>
        Environment.GetEnvironmentVariable("NINELIVES_TEST_SQL");

    private static ServerConnection TestServer() => new()
    {
        Name = "ninelives-test",
        ServerName = TestServerName!,
        AuthMode = AuthMode.WindowsAuth,
        Encrypt = EncryptMode.No,
        TrustServerCertificate = true,
        ConnectionTimeoutSeconds = 15
    };

    private static SqlServerService Service() => new(new CredentialStore());

    private const string DecoyName = "nl-decoy";
    private const string FakeSas = "sv=2026-01-01&sig=not-a-real-token";

    /// <summary>
    /// A name that, unescaped, closes the bracket, completes the CREATE CREDENTIAL statement,
    /// runs a second statement, and opens a decoy CREATE CREDENTIAL to absorb the trailing
    /// WITH IDENTITY / SECRET lines the caller appends. Verified to execute both statements
    /// against SQL Server when interpolated raw.
    ///
    /// Kept under 128 characters because a credential name is sysname: a longer payload is
    /// rejected by SQL Server as a single over-long identifier, which proves the quoting works
    /// but does so by erroring rather than by letting the assertion below run.
    /// </summary>
    private static string InjectionPayload =>
        $"nl] WITH IDENTITY='SHARED ACCESS SIGNATURE', SECRET='z'; CREATE CREDENTIAL [{DecoyName}";

    [RequiresSqlFact]
    public async Task EnsureCredentialExists_InjectionPayloadName_CreatesExactlyOneCredential()
    {
        var payload = InjectionPayload;
        try
        {
            await Service().EnsureCredentialExistsAsync(
                TestServer(), payload, "https://acct.blob.core.windows.net/backups", FakeSas);

            // The decoy is the tell: if the payload had escaped its quoting, SQL Server would
            // have executed a second CREATE CREDENTIAL and this would exist.
            Assert.False(
                await CredentialExists(DecoyName),
                "Injection succeeded: the payload created a second credential.");

            // ...and the whole payload should have been stored as one literal credential name.
            Assert.True(
                await CredentialExists(payload),
                "The payload should be stored verbatim as a single credential name.");
        }
        finally
        {
            await DropCredentialIfExists(payload);
            await DropCredentialIfExists(DecoyName);
        }
    }

    [RequiresSqlFact]
    public async Task EnsureCredentialExists_Ipv6EndpointName_Succeeds()
    {
        // The benign half of the same bug: an IPv6-literal endpoint is a legal URL containing a
        // ']', and previously failed with an opaque syntax error.
        const string name = "https://[fe80::1]:10000/devstoreaccount1/backups";
        try
        {
            await Service().EnsureCredentialExistsAsync(TestServer(), name, name, FakeSas);
            Assert.True(await CredentialExists(name));
        }
        finally
        {
            await DropCredentialIfExists(name);
        }
    }

    [RequiresSqlFact]
    public async Task EnsureCredentialExists_IsIdempotentForAwkwardNames()
    {
        // Second call takes the DROP-then-CREATE path, which is the other interpolation site.
        const string name = "ninelives-test]awkward]name";
        try
        {
            await Service().EnsureCredentialExistsAsync(TestServer(), name, name, FakeSas);
            await Service().EnsureCredentialExistsAsync(TestServer(), name, name, FakeSas);
            Assert.True(await CredentialExists(name));
        }
        finally
        {
            await DropCredentialIfExists(name);
        }
    }

    [RequiresSqlFact]
    public async Task EnsureCredentialExists_NameWithLineBreak_IsRejectedBeforeReachingTheServer()
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            Service().EnsureCredentialExistsAsync(
                TestServer(), "name\nGO\nDROP DATABASE [x]", "https://acct/x", FakeSas));
    }

    // ── helpers (parameterised, so they cannot themselves be a vector) ───────────

    private static async Task<bool> CredentialExists(string name)
    {
        await using var conn = new SqlConnection(Service().BuildConnectionString(TestServer()));
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sys.credentials WHERE name = @name";
        cmd.Parameters.AddWithValue("@name", name);
        return (int)(await cmd.ExecuteScalarAsync())! > 0;
    }

    private static async Task DropCredentialIfExists(string name)
    {
        if (!await CredentialExists(name)) return;

        await using var conn = new SqlConnection(Service().BuildConnectionString(TestServer()));
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"DROP CREDENTIAL {TSql.QuoteName(name)}";
        await cmd.ExecuteNonQueryAsync();
    }
}
