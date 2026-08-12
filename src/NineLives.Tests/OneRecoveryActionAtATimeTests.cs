using Blackcat.NineLives.Models;
using Blackcat.NineLives.Services;
using Blackcat.NineLives.ViewModels;
using Xunit;

namespace Blackcat.NineLives.Tests;

/// <summary>
/// One recovery action at a time (#411).
///
/// The same defect as #401, on the panel where it costs the most: the one somebody is looking at
/// after a restore has FAILED, trying to get a database back. `IsRunningAction` existed and drove
/// only a progress panel's visibility, so both "Run this" buttons stayed live - and the first
/// thing the handler does is begin a new cancellation, which abandons the action already running.
///
/// The method's own comment explains why it is cancellable: RESTORE ... WITH RECOVERY can take a
/// long time and goes out at CommandTimeout = 0. The same reasoning says an accidental cancel is
/// expensive, and "nothing seems to be happening, press the other button" is how it happens.
/// </summary>
public class OneRecoveryActionAtATimeTests
{
    private static RestoreExecutionViewModel Panel(FakeSqlServerService sql) =>
        new(sql, new FakeRestoreHistoryStore(), TestLogs.Temp(),
            new OperationCancellation(), new FakeRunNotifier());

    [Fact]
    public void AnActionIsOfferedWhenNothingIsRunning()
    {
        var vm = Panel(new FakeSqlServerService());

        Assert.True(vm.CanRunRecoveryAction);
        Assert.True(vm.RunRecoveryActionCommand.CanExecute(null));
    }

    [Fact]
    public void NoSecondActionIsOfferedWhileOneRuns()
    {
        var vm = Panel(new FakeSqlServerService());
        vm.IsRunningAction = true;

        Assert.False(vm.CanRunRecoveryAction);
        Assert.False(vm.RunRecoveryActionCommand.CanExecute(null));
    }

    [Fact]
    public void TheButtonsComeBackWhenTheActionEnds()
    {
        var vm = Panel(new FakeSqlServerService());
        vm.IsRunningAction = true;
        Assert.False(vm.RunRecoveryActionCommand.CanExecute(null));

        vm.IsRunningAction = false;

        Assert.True(vm.RunRecoveryActionCommand.CanExecute(null));
    }

    /// <summary>
    /// And the handler refuses even if something reaches it directly - re-entering here does not
    /// waste a call, it cancels a running RESTORE ... WITH RECOVERY.
    /// </summary>
    [Fact]
    public async Task TheHandlerItselfRefusesToStartASecondAction()
    {
        var sql = new FakeSqlServerService();
        var vm = Panel(sql);
        vm.IsRunningAction = true;

        await vm.RunRecoveryActionCommand.ExecuteAsync(
            new RecoveryAction("Bring the database online",
                               "RESTORE DATABASE [Sales] WITH RECOVERY", "..."));

        Assert.Empty(sql.PolledScripts);
    }
}
