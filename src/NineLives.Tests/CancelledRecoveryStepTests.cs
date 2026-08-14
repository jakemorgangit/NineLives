using Blackcat.NineLives.Models;
using Blackcat.NineLives.Services;
using Blackcat.NineLives.ViewModels;
using Xunit;

namespace Blackcat.NineLives.Tests;

/// <summary>
/// Pressing Stop on a recovery step is a cancellation, and must read as one (#427).
///
/// Reported from the field. A restore has already failed, the user is on the recovery panel with
/// a RESTORE ... WITH RECOVERY running against a large database, and presses Stop. SqlClient
/// raises a cancelled command as a SqlException - "A severe error occurred on the current
/// command" - not an OperationCanceledException, so the panel's cancellation handler was stepped
/// over and the general failure handler answered instead: "FAILED at HH:mm:ss: A severe error
/// occurred". At the one moment somebody is trying to recover a broken database and needs to know
/// its true state, the tool told them a step had failed when it had done exactly what they asked.
///
/// The same trap CancellableAsync was written for, and its doc comment predicted this: writing the
/// guard out by hand at each call site is how sites come to be missing it. This was the fourth.
///
/// Fixed in two places, and pinned here in both:
///   1. the service translates it, so every caller sees an OperationCanceledException;
///   2. the panel also trusts the token, so nothing that reaches it can be called a failure when
///      the user pressed Stop.
/// </summary>
public class CancelledRecoveryStepTests
{
    private static (RestoreExecutionViewModel vm, FakeSqlServerService sql, OperationCancellation queries)
        NewAfterFailedRun()
    {
        var sql = new FakeSqlServerService();
        var queries = new OperationCancellation();
        var vm = new RestoreExecutionViewModel(
            sql, new FakeOperationHistoryStore(), TestLogs.Temp(), queries);

        var run = new RestoreRun(
            new ServerConnection { Id = ServerConnection.NewId(), Name = "SRV01", ServerName = "SRV01" },
            "RESTORE DATABASE [MyDb] FROM DISK = N'D:\\b.bak' WITH REPLACE, RECOVERY;",
            "MyDb", null, null, "1 full", null, "opts");
        vm.RunAsync(run, _ => Task.FromResult(CredentialPreflight.Proceed)).GetAwaiter().GetResult();

        return (vm, sql, queries);
    }

    private static RecoveryAction BringOnline() => new(
        "Bring the database online",
        "RESTORE DATABASE [MyDb] WITH RECOVERY",
        "caution");

    /// <summary>
    /// The reported bug, in the shape it actually arrives: the token is signalled mid-statement
    /// and what comes back is the driver's exception, not an OperationCanceledException.
    /// </summary>
    [Fact]
    public async Task StoppingAStepReadsAsCancelledEvenWhenTheDriverThrowsItsOwnError()
    {
        var (vm, sql, queries) = NewAfterFailedRun();

        // Exactly what the user saw. The type stands in for SqlException, which cannot be
        // constructed here - what matters is that it is not an OperationCanceledException.
        sql.DuringPollingExecute = queries.Cancel;
        sql.PollingExecuteThrows = new InvalidOperationException(
            "A severe error occurred on the current command. The results, if any, should be discarded.");

        await vm.RunRecoveryActionCommand.ExecuteAsync(BringOnline());

        Assert.Contains("Cancelled", vm.ActionOutcome);
        Assert.Contains("unchanged by this step", vm.ActionOutcome);

        // The words the report was actually about.
        Assert.DoesNotContain("FAILED", vm.ActionOutcome);
        Assert.DoesNotContain("severe error", vm.ActionOutcome);
        Assert.False(vm.HasError);
    }

    /// <summary>The ordinary path the service now produces, once it has done the translation.</summary>
    [Fact]
    public async Task AnOperationCanceledExceptionStillReadsAsCancelled()
    {
        var (vm, sql, _) = NewAfterFailedRun();
        sql.PollingExecuteThrows = new OperationCanceledException("The statement was cancelled.");

        await vm.RunRecoveryActionCommand.ExecuteAsync(BringOnline());

        Assert.Contains("Cancelled", vm.ActionOutcome);
        Assert.False(vm.HasError);
    }

    /// <summary>
    /// The other half, and the reason both guards are on the token rather than on the message: a
    /// genuine failure with nobody pressing Stop must still be reported as the failure it is.
    /// Mislabelling a real error as a cancellation on this panel would be the same defect pointing
    /// the other way, and worse - it would tell somebody their database was untouched when a
    /// recovery step had just failed against it.
    /// </summary>
    [Fact]
    public async Task ARealFailureIsStillAFailure()
    {
        var (vm, sql, _) = NewAfterFailedRun();
        sql.PollingExecuteThrows = new InvalidOperationException("Msg 3013: RESTORE DATABASE is terminating abnormally.");

        await vm.RunRecoveryActionCommand.ExecuteAsync(BringOnline());

        Assert.Contains("FAILED", vm.ActionOutcome);
        Assert.Contains("Msg 3013", vm.ActionOutcome);
        Assert.True(vm.HasError);
    }

    /// <summary>The panel must be usable again afterwards - a cancelled step is not a dead end.</summary>
    [Fact]
    public async Task ThePanelIsReadyForTheNextAttemptAfterACancelledStep()
    {
        var (vm, sql, queries) = NewAfterFailedRun();
        sql.DuringPollingExecute = queries.Cancel;
        sql.PollingExecuteThrows = new InvalidOperationException("A severe error occurred on the current command.");

        await vm.RunRecoveryActionCommand.ExecuteAsync(BringOnline());

        Assert.False(vm.IsRunningAction);
        Assert.True(vm.CanRunRecoveryAction);
        Assert.True(vm.RunRecoveryActionCommand.CanExecute(null));
        Assert.True(vm.ActionPercentUnknown);
    }
}
