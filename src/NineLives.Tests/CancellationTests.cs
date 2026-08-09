using System.Diagnostics;
using System.IO;
using Blackcat.NineLives.Models;
using Blackcat.NineLives.Services;
using Xunit;

namespace Blackcat.NineLives.Tests;

/// <summary>
/// Cancellation of long-running work (#25).
///
/// Every service method already took a CancellationToken and none of them were ever given a real
/// one, so a restore aimed at the wrong server could only be stopped by killing the process -
/// which leaves the database mid-restore and writes nothing down about it.
/// </summary>
public class OperationCancellationTests
{
    [Fact]
    public void NothingRunningMeansNothingToCancel()
    {
        var operation = new OperationCancellation();

        Assert.False(operation.CanCancel);
        Assert.False(operation.IsCancelling);
        operation.Cancel();   // must not throw
    }

    [Fact]
    public void BeginProducesALiveToken()
    {
        var operation = new OperationCancellation();

        var token = operation.Begin();

        Assert.False(token.IsCancellationRequested);
        Assert.True(operation.CanCancel);
    }

    [Fact]
    public void CancelSignalsTheToken()
    {
        var operation = new OperationCancellation();
        var token = operation.Begin();

        operation.Cancel();

        Assert.True(token.IsCancellationRequested);
        Assert.True(operation.IsCancelling);
        Assert.False(operation.CanCancel);   // already asked; the button should go away
    }

    /// <summary>
    /// The bug this type exists to prevent: reusing a cancelled source would cancel the next run
    /// before it started, so the second attempt at a restore would fail instantly and look like a
    /// different problem entirely.
    /// </summary>
    [Fact]
    public void ANewOperationAfterACancelledOneStartsClean()
    {
        var operation = new OperationCancellation();
        operation.Begin();
        operation.Cancel();
        operation.End();

        var second = operation.Begin();

        Assert.False(second.IsCancellationRequested);
        Assert.True(operation.CanCancel);
    }

    [Fact]
    public void BeginCancelsAnOperationThatWasLeftRunning()
    {
        // Belt and braces: if a finally block were ever missed, the abandoned run must not keep
        // going alongside its replacement.
        var operation = new OperationCancellation();
        var first = operation.Begin();

        operation.Begin();

        Assert.True(first.IsCancellationRequested);
    }

    [Fact]
    public void EndIsSafeToCallRepeatedly()
    {
        var operation = new OperationCancellation();
        operation.Begin();

        operation.End();
        operation.End();

        Assert.False(operation.CanCancel);
    }

    [Fact]
    public void CancelAfterEndDoesNotThrow()
    {
        // The order a slow operation unwinds in is not fully under our control, so a Cancel
        // arriving after the finally block has run must be a no-op rather than an ObjectDisposed.
        var operation = new OperationCancellation();
        operation.Begin();
        operation.End();

        operation.Cancel();

        Assert.False(operation.CanCancel);
    }
}

/// <summary>
/// Cancellation against a real SQL Server: the point is that it actually interrupts work in
/// flight, not just that the plumbing compiles.
/// </summary>
public class SqlCancellationTests
{
    private static ServerConnection TestServer() => new()
    {
        Name = "ninelives-test",
        ServerName = Environment.GetEnvironmentVariable("NINELIVES_TEST_SQL")!,
        AuthMode = AuthMode.WindowsAuth,
        Encrypt = EncryptMode.No,
        TrustServerCertificate = true,
        ConnectionTimeoutSeconds = 15
    };

    private static SqlServerService Service() => new(new CredentialStore());

    /// <summary>
    /// A restore of any size runs with CommandTimeout = 0, so without cancellation the only way
    /// out is killing the process. WAITFOR stands in for a long-running statement: if the token is
    /// not reaching the driver, this hangs for 30 seconds instead of stopping in about one.
    ///
    /// It also pins the exception TYPE, which is the part that is easy to get wrong. SqlClient
    /// surfaces a cancelled command as a SqlException - "A severe error occurred on the current
    /// command ... Operation cancelled by user" - so a caller catching OperationCanceledException
    /// to show "cancelled" would miss it and report the user's own Stop as a severe error. The
    /// service translates it; this test is what says so.
    /// </summary>
    [RequiresSqlFact]
    public async Task ARunningStatementIsActuallyInterrupted()
    {
        using var cts = new CancellationTokenSource();
        var messages = new List<string>();

        var started = Stopwatch.StartNew();
        var run = Service().ExecuteWithProgressAsync(
            TestServer(),
            "WAITFOR DELAY '00:00:30'",
            messages.Add,
            cts.Token);

        await Task.Delay(1000);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        started.Stop();

        Assert.True(started.Elapsed < TimeSpan.FromSeconds(15),
            $"Cancellation did not reach the server - the statement ran for {started.Elapsed}.");

        // The user should read "cancelled", not "failed", in the execution console.
        Assert.Contains(messages, m => m.Contains("Cancelled during statement", StringComparison.Ordinal));
        Assert.DoesNotContain(messages, m => m.Contains("FAILED", StringComparison.Ordinal));
    }

    /// <summary>
    /// The other half of the translation: a genuine SqlException must still surface as a failure.
    /// Guarding only on the token means an unrelated severe error is not mislabelled as the user
    /// having pressed Stop.
    /// </summary>
    [RequiresSqlFact]
    public async Task ARealFailureIsStillAFailureNotACancellation()
    {
        using var cts = new CancellationTokenSource();
        var messages = new List<string>();

        await Assert.ThrowsAsync<Microsoft.Data.SqlClient.SqlException>(() =>
            Service().ExecuteWithProgressAsync(
                TestServer(),
                "RESTORE DATABASE [NineLives_NoSuchDb] FROM DISK = 'Z:\\nope.bak'",
                messages.Add,
                cts.Token));

        Assert.Contains(messages, m => m.Contains("FAILED on statement", StringComparison.Ordinal));
    }

    [RequiresSqlFact]
    public async Task ATokenCancelledBeforehandStopsBeforeAnythingRuns()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Service().ExecuteWithProgressAsync(
                TestServer(), "SELECT 1", null, cts.Token));
    }

    [RequiresSqlFact]
    public async Task AnUncancelledRunStillCompletesNormally()
    {
        // The obvious regression: threading a token through must not break the ordinary path.
        using var cts = new CancellationTokenSource();
        var messages = new List<string>();

        await Service().ExecuteWithProgressAsync(
            TestServer(), "SELECT 1", messages.Add, cts.Token);

        Assert.NotEmpty(messages);
    }
}

/// <summary>Cancelling a blob listing, which is the other unbounded operation.</summary>
public class BlobCancellationTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "ninelives-cancel-tests", Guid.NewGuid().ToString("n"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task AListingWithAnAlreadyCancelledTokenDoesNotRun()
    {
        var store = new CredentialStore(_dir);
        var container = new BlobContainerConfig
        {
            Id = BlobContainerConfig.NewId(),
            Name = "ninelives-test-cancel",
            ContainerUrl = "https://acct.blob.core.windows.net/backups",
            UnsavedSasToken = "sv=2024-11-04&sig=not-real"
        };

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        // Cancelled before any network call, so this fails on the token rather than on the
        // unreachable account - which is what proves the token is consulted at all.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new BlobStorageService(store).ListBackupFilesAsync(container, cts.Token));
    }
}
