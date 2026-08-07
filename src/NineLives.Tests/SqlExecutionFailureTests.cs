using Blackcat.NineLives.Models;
using Blackcat.NineLives.Services;
using Microsoft.Data.SqlClient;
using Xunit;

namespace Blackcat.NineLives.Tests;

/// <summary>
/// Marks a test that needs a real SQL Server. Set NINELIVES_TEST_SQL to an instance
/// name (e.g. ".\SQLEXPRESS") to run these; they skip otherwise, so CI and a plain
/// `dotnet test` on a machine without SQL Server stay green.
/// </summary>
public sealed class RequiresSqlFactAttribute : FactAttribute
{
    public RequiresSqlFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(SqlExecutionFailureTests.TestServerName))
            Skip = "Set NINELIVES_TEST_SQL to a SQL Server instance name to run live SQL tests.";
    }
}

/// <summary>
/// Regression tests for the failure-reporting contract of the execution methods.
///
/// These exist because of a defect where FireInfoMessageEventOnUserErrors = true routed
/// every error of severity &lt;= 16 - which is essentially every real RESTORE failure - to the
/// InfoMessage handler instead of throwing. Nothing threw, the statement loop ran to
/// completion, and the app reported "Restore completed successfully!" over a total failure.
///
/// The contract these pin down:
///   1. a failing statement throws, so the caller can report failure;
///   2. execution STOPS at the first failure instead of running the rest of the chain;
///   3. informational messages (RESTORE progress/STATS) still reach the callback.
/// </summary>
public class SqlExecutionFailureTests
{
    internal static string? TestServerName =>
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

    // Severity 16 is the band that matters: SQL Server reports almost every restore failure
    // there (3201 cannot open backup device, 3013 terminating abnormally, 4305 log too recent,
    // 3136 differential base mismatch).
    private const string FailingBatch = "RAISERROR('nine-lives-test-failure', 16, 1);";

    // A real restore failure rather than a synthetic RAISERROR: 3201 + 3013, the exact pair an
    // expired SAS token produces. FROM DISK keeps it local and fast - no network, no container.
    private const string FailingRestore =
        @"RESTORE DATABASE [NineLives_DoesNotExist] FROM DISK = N'C:\NineLives_NoSuchPath\nope.bak' WITH FILE = 1;";

    [RequiresSqlFact]
    public async Task ExecuteRestoreWithProgress_FailingStatement_Throws()
    {
        var messages = new List<string>();

        await Assert.ThrowsAsync<SqlException>(() =>
            Service().ExecuteWithProgressAsync(
                TestServer(), FailingBatch, messages.Add));
    }

    [RequiresSqlFact]
    public async Task ExecuteRestoreWithProgress_FailingRestore_Throws()
    {
        var messages = new List<string>();

        var ex = await Assert.ThrowsAsync<SqlException>(() =>
            Service().ExecuteWithProgressAsync(
                TestServer(), FailingRestore, messages.Add));

        // 3201 = cannot open backup device. This is the error an expired SAS produces.
        Assert.Contains(ex.Errors.Cast<SqlError>(), e => e.Number == 3201);
    }

    [RequiresSqlFact]
    public async Task ExecuteRestoreWithProgress_StopsAtFirstFailure()
    {
        // The heart of the bug: a chain is GO-split and executed statement by statement. When
        // an early statement fails, the remaining restores must NOT run - previously they all
        // ran against a database that was never created.
        var messages = new List<string>();
        var script = string.Join("\n", [
            "PRINT 'nine-lives-statement-1';",
            "GO",
            FailingBatch,
            "GO",
            "PRINT 'nine-lives-statement-3-should-not-run';",
            "GO"
        ]);

        await Assert.ThrowsAsync<SqlException>(() =>
            Service().ExecuteWithProgressAsync(TestServer(), script, messages.Add));

        Assert.Contains(messages, m => m.Contains("nine-lives-statement-1"));
        Assert.DoesNotContain(messages, m => m.Contains("should-not-run"));
    }

    [RequiresSqlFact]
    public async Task ExecuteRestoreWithProgress_InformationalMessages_StillReachTheCallback()
    {
        // The progress console depends on InfoMessage delivery. Severity <= 10 messages - which
        // is what RESTORE's "X percent processed" and PRINT are - must still arrive after the fix.
        var messages = new List<string>();

        await Service().ExecuteWithProgressAsync(
            TestServer(), "PRINT 'nine-lives-progress-message';", messages.Add);

        Assert.Contains(messages, m => m.Contains("nine-lives-progress-message"));
    }

    [RequiresSqlFact]
    public async Task ExecuteRestoreWithProgress_LowSeverityRaiserror_DoesNotFailTheRun()
    {
        // Severity <= 10 is informational by definition and must not be treated as failure,
        // otherwise ordinary restore chatter would abort a healthy restore.
        var messages = new List<string>();

        await Service().ExecuteWithProgressAsync(
            TestServer(), "RAISERROR('nine-lives-informational', 10, 1);", messages.Add);

        Assert.Contains(messages, m => m.Contains("nine-lives-informational"));
    }

    [RequiresSqlFact]
    public async Task ExecuteNonQuery_FailingStatement_Throws()
    {
        // Same contract on the other execution path, which credential creation uses.
        await Assert.ThrowsAsync<SqlException>(() =>
            Service().ExecuteNonQueryAsync(TestServer(), FailingBatch));
    }

    [RequiresSqlFact]
    public async Task ExecuteNonQuery_FailingStatement_ThrowsEvenWithNoMessageCallback()
    {
        // The original defect set the flag even when messageCallback was null, so errors
        // vanished entirely with nowhere to surface.
        await Assert.ThrowsAsync<SqlException>(() =>
            Service().ExecuteNonQueryAsync(TestServer(), FailingBatch, messageCallback: null));
    }
}
