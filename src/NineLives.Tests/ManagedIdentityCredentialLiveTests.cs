using Blackcat.NineLives.Models;
using Blackcat.NineLives.Services;
using Microsoft.Data.SqlClient;
using Xunit;

namespace Blackcat.NineLives.Tests;

/// <summary>
/// Live proof of what a managed-identity credential does to server state (#147).
///
/// What these CAN prove against any instance: that the statement is accepted, which conversions SQL
/// Server actually permits, that an allowed one alters in place rather than dropping, and what this
/// particular instance says about its own version.
///
/// One of them was written asserting the opposite of what SQL Server does - the issue assumed ALTER
/// converts a SAS credential to a managed identity, and it does not - and it failed on its first CI
/// run. That is the whole argument for writing live tests even when they cannot prove the last
/// mile.
///
/// What they cannot prove, anywhere on-prem: that a restore then AUTHENTICATES with it. That needs
/// an Azure VM with an identity, an Arc-enabled instance, or SQL MI. So this ships with the
/// statement and the lifecycle proven and the authentication unproven, which is the same position
/// the VERIFYONLY live test was left in by #26.
///
/// Gated on NINELIVES_TEST_SQL like the other live tests; each cleans up after itself.
/// </summary>
[Collection("CredentialManager")]
public class ManagedIdentityCredentialLiveTests
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

    private const string Url = "https://acct.blob.core.windows.net/backups";
    private const string Sas = "sv=2026-01-01&sig=a-token";

    // ── the statement is accepted ───────────────────────────────────────────────

    /// <summary>
    /// Accepted on ANY version, which is exactly why the app gates the option rather than trusting
    /// the server to refuse it. A credential written on SQL Server 2019 looks perfectly correct
    /// here and fails at restore time - the worst kind of deferred failure.
    /// </summary>
    [RequiresSqlFact]
    public async Task AManagedIdentityCredentialCanBeCreated()
    {
        const string name = "ninelives-test-mi-create";
        try
        {
            await DropCredentialIfExists(name);

            var change = await Service().EnsureCredentialExistsAsync(
                TestServer(), name, Url, sasToken: string.Empty,
                BlobCredentialIdentity.ManagedIdentity);

            Assert.Equal(CredentialChange.Created, change);
            Assert.Equal("Managed Identity", await IdentityOf(name));
        }
        finally
        {
            await DropCredentialIfExists(name);
        }
    }

    /// <summary>The app reads back what it wrote - the other half of #145's recognition.</summary>
    [RequiresSqlFact]
    public async Task TheAppRecognisesTheCredentialItJustWrote()
    {
        const string name = "ninelives-test-mi-readback";
        try
        {
            await DropCredentialIfExists(name);

            await Service().EnsureCredentialExistsAsync(
                TestServer(), name, Url, string.Empty, BlobCredentialIdentity.ManagedIdentity);

            var status = await Service().CredentialExistsAsync(TestServer(), name);

            Assert.Equal(BlobCredentialIdentity.ManagedIdentity, status.Kind);
        }
        finally
        {
            await DropCredentialIfExists(name);
        }
    }

    // ── the conversion, in both directions ──────────────────────────────────────

    /// <summary>
    /// SQL Server REFUSES to move a credential off SHARED ACCESS SIGNATURE in place.
    ///
    /// This test was written the other way round - the issue was designed around ALTER converting a
    /// SAS credential across - and it failed on its first CI run. The assumption was simply wrong,
    /// and the error says something misleading about the credential being "used by an active
    /// database file" for a credential nothing has ever touched.
    ///
    /// Pinned as the refusal it is, because the app now has to explain it, and because an engine
    /// that starts allowing it should be noticed rather than quietly changing behaviour.
    /// </summary>
    [RequiresSqlFact]
    public async Task SqlServerWillNotConvertASasCredentialToAManagedIdentity()
    {
        const string name = "ninelives-test-mi-convert";
        try
        {
            await DropCredentialIfExists(name);

            await Service().EnsureCredentialExistsAsync(
                TestServer(), name, Url, Sas, BlobCredentialIdentity.SharedAccessSignature);

            Assert.Equal("SHARED ACCESS SIGNATURE", await IdentityOf(name));

            var refused = await Assert.ThrowsAnyAsync<Exception>(() =>
                Service().EnsureCredentialExistsAsync(
                    TestServer(), name, Url, string.Empty, BlobCredentialIdentity.ManagedIdentity));

            Assert.True(BlobCredentialStatement.IsIdentityChangeRefusal(refused.Message),
                $"the app has to recognise this refusal to explain it; it said: {refused.Message}");

            // Left exactly as it was, which is the point of not working around it by dropping and
            // recreating (#10).
            Assert.Equal("SHARED ACCESS SIGNATURE", await IdentityOf(name));
        }
        finally
        {
            await DropCredentialIfExists(name);
        }
    }

    /// <summary>
    /// And back again, because a SAS-free estate that changes its mind should not have to drop the
    /// credential by hand - and because the reverse conversion is the one #145 was written about.
    /// </summary>
    [RequiresSqlFact]
    public async Task AlteringConvertsAManagedIdentityBackToASas()
    {
        const string name = "ninelives-test-mi-convert-back";
        try
        {
            await DropCredentialIfExists(name);

            await Service().EnsureCredentialExistsAsync(
                TestServer(), name, Url, string.Empty, BlobCredentialIdentity.ManagedIdentity);

            await Service().EnsureCredentialExistsAsync(
                TestServer(), name, Url, Sas, BlobCredentialIdentity.SharedAccessSignature);

            Assert.Equal("SHARED ACCESS SIGNATURE", await IdentityOf(name));
        }
        finally
        {
            await DropCredentialIfExists(name);
        }
    }

    /// <summary>
    /// A credential is server-scoped shared state, so a conversion that IS allowed must not drop
    /// and recreate it - that removes it for the moment in between, from everything else using it.
    /// create_date is what tells the two apart.
    ///
    /// Managed identity to SAS, because that is the direction SQL Server permits.
    /// </summary>
    [RequiresSqlFact]
    public async Task AnAllowedConversionDoesNotDropAndRecreate()
    {
        const string name = "ninelives-test-mi-no-drop";
        try
        {
            await DropCredentialIfExists(name);

            await Service().EnsureCredentialExistsAsync(
                TestServer(), name, Url, string.Empty, BlobCredentialIdentity.ManagedIdentity);

            var created = await CreateDate(name);

            await Service().EnsureCredentialExistsAsync(
                TestServer(), name, Url, Sas, BlobCredentialIdentity.SharedAccessSignature);

            Assert.Equal("SHARED ACCESS SIGNATURE", await IdentityOf(name));
            Assert.Equal(created, await CreateDate(name));
        }
        finally
        {
            await DropCredentialIfExists(name);
        }
    }

    // ── what this instance says about itself ────────────────────────────────────

    /// <summary>
    /// Deliberately asserts behaviour rather than a value. Pinning "this returns true" would pin
    /// whichever version happens to be on the machine running the tests - a live test that fails on
    /// somebody else's SQL Server 2019 is a test about the environment, not about the code.
    /// </summary>
    [RequiresSqlFact]
    public async Task TheInstanceReportsEnoughToDecide()
    {
        var support = await Service().SupportsManagedIdentityCredentialAsync(TestServer());

        Assert.True(support.ProductMajorVersion.HasValue || support.EngineEdition.HasValue,
            "an instance that reports neither leaves the app guessing");

        Assert.Equal(
            BlobCredentialStatement.SupportsManagedIdentity(support.ProductMajorVersion, support.EngineEdition),
            support.IsSupported);

        // Whichever way it went, a refusal has to say something usable.
        if (!support.IsSupported) Assert.NotEqual(string.Empty, support.Explain());
    }

    // ── helpers ─────────────────────────────────────────────────────────────────

    private static async Task<string?> IdentityOf(string name)
    {
        await using var conn = await OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT credential_identity FROM sys.credentials WHERE name = @name";
        cmd.Parameters.AddWithValue("@name", name);
        return (await cmd.ExecuteScalarAsync()) as string;
    }

    private static async Task<DateTime> CreateDate(string name)
    {
        await using var conn = await OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT create_date FROM sys.credentials WHERE name = @name";
        cmd.Parameters.AddWithValue("@name", name);
        return (DateTime)(await cmd.ExecuteScalarAsync())!;
    }

    private static async Task<bool> CredentialExists(string name)
    {
        await using var conn = await OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sys.credentials WHERE name = @name";
        cmd.Parameters.AddWithValue("@name", name);
        return (int)(await cmd.ExecuteScalarAsync())! > 0;
    }

    private static async Task DropCredentialIfExists(string name)
    {
        if (!await CredentialExists(name)) return;

        await using var conn = await OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"DROP CREDENTIAL {TSql.QuoteName(name)}";
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<SqlConnection> OpenAsync()
    {
        var conn = new SqlServerService(new CredentialStore()).CreateConnection(TestServer());
        await conn.OpenAsync();
        return conn;
    }
}
