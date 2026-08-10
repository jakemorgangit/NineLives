using Blackcat.NineLives.Models;
using Blackcat.NineLives.Services;
using Blackcat.NineLives.ViewModels;
using Xunit;

namespace Blackcat.NineLives.Tests;

/// <summary>
/// The finishing panel's actions gained progress and a spoken outcome: CHECKDB on a real
/// database takes minutes, and "did it find anything?" should not have to be dug out of the
/// console. percent_complete from dm_exec_requests drives the bar; the outcome line stays on
/// the panel after the console has scrolled on.
/// </summary>
public class PanelActionProgressTests
{
    private static (RestoreExecutionViewModel vm, FakeSqlServerService sql) NewAfterRun()
    {
        var sql = new FakeSqlServerService();
        var vm = new RestoreExecutionViewModel(
            sql, new FakeRestoreHistoryStore(), TestLogs.Temp(), new OperationCancellation());

        // A run first, so _lastServer/_lastTarget are aimed - the panel only exists after one.
        var run = new RestoreRun(
            new ServerConnection { Id = ServerConnection.NewId(), Name = "SRV01", ServerName = "SRV01" },
            "RESTORE DATABASE [MyDb] FROM DISK = N'D:\\b.bak' WITH REPLACE, RECOVERY;",
            "MyDb", null, null, "1 full", null, "opts");
        vm.RunAsync(run, _ => Task.FromResult(CredentialPreflight.Proceed)).GetAwaiter().GetResult();

        return (vm, sql);
    }

    [Fact]
    public async Task TheActionRunsThroughThePollingPathAndReportsPercent()
    {
        var (vm, sql) = NewAfterRun();
        sql.PollPercents = [37.5];
        double seen = -1;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(vm.ActionPercent) && vm.ActionPercent > 0) seen = vm.ActionPercent;
        };

        await vm.RunRecoveryActionCommand.ExecuteAsync(PostRestoreAdvice.CheckDb("MyDb"));

        Assert.Contains(sql.PolledScripts, s => s.Contains("DBCC CHECKDB"));
        Assert.Equal(37.5, seen, 1);
    }

    /// <summary>The sentence the click exists to produce, on the panel, after the console moved on.</summary>
    [Fact]
    public async Task ACleanCheckdbSaysSoOnThePanel()
    {
        var (vm, _) = NewAfterRun();

        await vm.RunRecoveryActionCommand.ExecuteAsync(PostRestoreAdvice.CheckDb("MyDb"));

        Assert.Contains("found nothing wrong", vm.ActionOutcome);
        Assert.False(vm.IsRunningAction);
        Assert.True(vm.ActionPercentUnknown);   // the bar reset for the next action
    }

    [Fact]
    public async Task AFailedActionKeepsItsFailureOnThePanel()
    {
        var (vm, sql) = NewAfterRun();
        sql.PollingExecuteThrows = new InvalidOperationException("Msg 8921: CHECKDB aborted");

        await vm.RunRecoveryActionCommand.ExecuteAsync(PostRestoreAdvice.CheckDb("MyDb"));

        Assert.Contains("FAILED", vm.ActionOutcome);
        Assert.Contains("Msg 8921", vm.ActionOutcome);
        Assert.True(vm.HasError);
    }

    [Fact]
    public async Task ANonCheckdbActionGetsItsTitleAsTheOutcome()
    {
        var (vm, _) = NewAfterRun();

        await vm.RunRecoveryActionCommand.ExecuteAsync(new RecoveryAction(
            "Bring the database online", "RESTORE DATABASE [MyDb] WITH RECOVERY", "caution"));

        Assert.Contains("Bring the database online", vm.ActionOutcome);
    }
}
