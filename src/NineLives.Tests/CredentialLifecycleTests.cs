using Blackcat.NineLives.Models;
using Blackcat.NineLives.Services;
using Microsoft.Data.SqlClient;
using Xunit;

namespace Blackcat.NineLives.Tests;

/// <summary>
/// Live proof of how <see cref="SqlServerService.EnsureCredentialExistsAsync"/> touches server
/// state (#10).
///
/// It used to unconditionally DROP and re-CREATE the credential. A credential is server-scoped,
/// so that momentarily removed something other sessions may have been mid-way through using - a
/// backup job writing to the same container being the obvious one - and it happened on every
/// single Execute, whether or not anything needed changing.
///
/// The tell is <c>sys.credentials.create_date</c>: ALTER leaves it alone, DROP-then-CREATE resets
/// it. Asserting on it is what makes these tests fail against the old implementation rather than
/// just describing the new one.
///
/// Gated on NINELIVES_TEST_SQL like the other live tests; each cleans up after itself.
/// </summary>
[Collection("CredentialManager")]
public class CredentialLifecycleTests
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
    private const string FirstSas = "sv=2026-01-01&sig=first-token";
    private const string SecondSas = "sv=2026-01-01&sig=second-token";

    [RequiresSqlFact]
    public async Task FirstCall_CreatesTheCredential_AndSaysSo()
    {
        const string name = "ninelives-test-lifecycle-create";
        try
        {
            await DropCredentialIfExists(name);

            var change = await Service().EnsureCredentialExistsAsync(TestServer(), name, Url, FirstSas);

            Assert.Equal(CredentialChange.Created, change);
            Assert.True(await CredentialExists(name));
        }
        finally
        {
            await DropCredentialIfExists(name);
        }
    }

    /// <summary>
    /// The regression test for #10. Under the old DROP-then-CREATE the credential came back as a
    /// brand new object with a fresh create_date, and for the moment between the two statements
    /// it did not exist at all.
    /// </summary>
    [RequiresSqlFact]
    public async Task SecondCall_AltersInPlace_AndDoesNotRecreateTheCredential()
    {
        const string name = "ninelives-test-lifecycle-alter";
        try
        {
            await DropCredentialIfExists(name);
            await Service().EnsureCredentialExistsAsync(TestServer(), name, Url, FirstSas);
            var createdAt = await CreateDate(name);

            // A second later, so a recreate would land on a visibly different timestamp.
            await Task.Delay(1100);
            var change = await Service().EnsureCredentialExistsAsync(TestServer(), name, Url, SecondSas);

            Assert.Equal(CredentialChange.Updated, change);
            Assert.Equal(createdAt, await CreateDate(name));
            Assert.True(await ModifyDate(name) > createdAt, "The secret should actually have been rewritten.");
        }
        finally
        {
            await DropCredentialIfExists(name);
        }
    }

    /// <summary>
    /// A credential sitting there under some other identity cannot serve a RESTORE FROM URL, so
    /// ALTER resets IDENTITY too rather than leaving a broken credential to fail the restore.
    /// </summary>
    [RequiresSqlFact]
    public async Task NonSasCredential_IsConvertedToSharedAccessSignature()
    {
        const string name = "ninelives-test-lifecycle-identity";
        try
        {
            await DropCredentialIfExists(name);
            await CreateNonSasCredential(name);

            Assert.False((await Service().CredentialExistsAsync(TestServer(), name)).IsSharedAccessSignature);

            var change = await Service().EnsureCredentialExistsAsync(TestServer(), name, Url, FirstSas);

            Assert.Equal(CredentialChange.Updated, change);
            var (exists, isSas) = await Service().CredentialExistsAsync(TestServer(), name);
            Assert.True(exists);
            Assert.True(isSas);
        }
        finally
        {
            await DropCredentialIfExists(name);
        }
    }

    [RequiresSqlFact]
    public async Task CredentialExists_ReportsAbsenceWithoutCreatingAnything()
    {
        // Execute now asks this question before deciding whether to write. If the check itself
        // had a side effect, the "don't touch the server" path would not be true.
        const string name = "ninelives-test-lifecycle-absent";
        await DropCredentialIfExists(name);

        var (exists, isSas) = await Service().CredentialExistsAsync(TestServer(), name);

        Assert.False(exists);
        Assert.False(isSas);
        Assert.False(await CredentialExists(name));
    }

    // ── helpers (parameterised) ──────────────────────────────────────────────────

    private static async Task<SqlConnection> OpenAsync()
    {
        var conn = new SqlConnection(Service().BuildConnectionString(TestServer()));
        await conn.OpenAsync();
        return conn;
    }

    private static async Task<bool> CredentialExists(string name)
    {
        await using var conn = await OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sys.credentials WHERE name = @name";
        cmd.Parameters.AddWithValue("@name", name);
        return (int)(await cmd.ExecuteScalarAsync())! > 0;
    }

    private static Task<DateTime> CreateDate(string name) => DateColumn(name, "create_date");
    private static Task<DateTime> ModifyDate(string name) => DateColumn(name, "modify_date");

    private static async Task<DateTime> DateColumn(string name, string column)
    {
        await using var conn = await OpenAsync();
        await using var cmd = conn.CreateCommand();
        // The column name is a compile-time constant from the two callers above, never user input.
        cmd.CommandText = $"SELECT {column} FROM sys.credentials WHERE name = @name";
        cmd.Parameters.AddWithValue("@name", name);
        var value = await cmd.ExecuteScalarAsync();
        Assert.NotNull(value);
        return (DateTime)value!;
    }

    private static async Task CreateNonSasCredential(string name)
    {
        await using var conn = await OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"CREATE CREDENTIAL {TSql.QuoteName(name)} WITH IDENTITY = 'ninelives-test-identity', SECRET = 'not-a-sas'";
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task DropCredentialIfExists(string name)
    {
        if (!await CredentialExists(name)) return;

        await using var conn = await OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"DROP CREDENTIAL {TSql.QuoteName(name)}";
        await cmd.ExecuteNonQueryAsync();
    }
}
