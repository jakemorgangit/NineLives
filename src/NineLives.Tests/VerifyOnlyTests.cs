using Blackcat.NineLives.Models;
using Blackcat.NineLives.Services;
using Xunit;

namespace Blackcat.NineLives.Tests;

/// <summary>
/// RESTORE VERIFYONLY as a pre-flight: does each backup in the chain actually read back, before
/// an hour is spent finding out that it does not (#26).
///
/// The statement shape is tested here without a server. The failure CONTRACT - a bad backup comes
/// back as a result rather than an exception, so one bad member does not abort the rest - needs a
/// real instance and is gated on NINELIVES_TEST_SQL.
/// </summary>
public class VerifyOnlyTests
{
    private const string Url1 = "https://mystorageaccount.blob.core.windows.net/backups/FULL/MyDb_1.bak";
    private const string Url2 = "https://mystorageaccount.blob.core.windows.net/backups/FULL/MyDb_2.bak";

    [Fact]
    public void ASingleFileVerifiesWithOneUrl()
    {
        var sql = SqlServerService.BuildVerifyOnlyStatement([Url1], withChecksum: false);

        Assert.Equal($"RESTORE VERIFYONLY FROM URL = N'{Url1}'", sql);
    }

    [Fact]
    public void EveryStripeGoesIntoOneStatement()
    {
        // A stripe on its own is not a readable backup. Verifying them one at a time would report
        // failures that are not there.
        var sql = SqlServerService.BuildVerifyOnlyStatement([Url1, Url2], withChecksum: false);

        Assert.Equal($"RESTORE VERIFYONLY FROM URL = N'{Url1}', URL = N'{Url2}'", sql);
    }

    [Fact]
    public void ChecksumIsAddedOnlyWhenAskedFor()
    {
        // Left off, SQL Server's own default applies. Forcing NO_CHECKSUM would be a different
        // instruction from saying nothing.
        Assert.EndsWith("WITH CHECKSUM", SqlServerService.BuildVerifyOnlyStatement([Url1], true));
        Assert.DoesNotContain("CHECKSUM", SqlServerService.BuildVerifyOnlyStatement([Url1], false));
    }

    [Fact]
    public void NoCredentialClauseIsEmitted()
    {
        // SQL Server rejects WITH CREDENTIAL for a SAS credential (Msg 3225) and matches by URL
        // instead - the defect behind #60, which must not come back through this path.
        Assert.DoesNotContain(
            "CREDENTIAL",
            SqlServerService.BuildVerifyOnlyStatement([Url1, Url2], withChecksum: true));
    }

    [Fact]
    public void AUrlWithAnApostropheCannotTerminateTheLiteral()
    {
        var awkward = "https://mystorageaccount.blob.core.windows.net/backups/it's/MyDb.bak";

        var sql = SqlServerService.BuildVerifyOnlyStatement([awkward], withChecksum: false);

        Assert.Equal("RESTORE VERIFYONLY FROM URL = N'https://mystorageaccount.blob.core.windows.net/backups/it''s/MyDb.bak'", sql);
    }

    [Fact]
    public async Task NoFilesIsReportedRatherThanRun()
    {
        var result = await new SqlServerService(new CredentialStore())
            .RestoreVerifyOnlyAsync(new ServerConnection(), []);

        Assert.False(result.IsValid);
    }

    // ---- Live SQL ----------------------------------------------------------

    private static ServerConnection TestServer() => new()
    {
        Name = "ninelives-test",
        ServerName = SqlExecutionFailureTests.TestServerName!,
        AuthMode = AuthMode.WindowsAuth,
        Encrypt = EncryptMode.No,
        TrustServerCertificate = true,
        ConnectionTimeoutSeconds = 15
    };

    [RequiresSqlFact]
    public async Task AnUnreadableBackupComesBackAsAResultNotAnException()
    {
        // The whole point of the pre-flight is to check every member of a chain. If one bad blob
        // threw, the loop would stop at the first problem and the user would fix them one round
        // trip at a time.
        var result = await new SqlServerService(new CredentialStore())
            .RestoreVerifyOnlyAsync(TestServer(), [Url1]);

        Assert.False(result.IsValid);
        Assert.NotEmpty(result.Message);
    }
}
