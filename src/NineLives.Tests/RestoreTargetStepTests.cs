using Blackcat.NineLives.Models;
using Blackcat.NineLives.Services;
using Blackcat.NineLives.ViewModels;
using Xunit;

namespace Blackcat.NineLives.Tests;

/// <summary>
/// WHERE the restore runs is a step, not an ambient fact (#318). It used to be whatever the SQL
/// Servers screen last connected to - answered on a different screen, invisible in the workflow,
/// and met for the first time as an execute-time error when nothing was connected. Now it is
/// step 2: pre-answered (and still changeable) when a connection already exists, asked outright
/// with the saved-server list when it does not.
/// </summary>
public class RestoreTargetStepTests
{
    private static RestoreViewModel New(FakeCredentialStore? store = null) => new(
        new FakeBlobStorageService(), new FakeSqlServerService(), new BackupChainBuilder(),
        new RestoreScriptGenerator(), store ?? new FakeCredentialStore(), TestLogs.Temp(),
        new FakeOperationHistoryStore(), TestAuditStores.Temp());

    private static ServerConnection Server(string id = "s1", string name = "SRV01") =>
        new() { Id = id, Name = name, ServerName = name };

    [Fact]
    public void TheStepWaitsForTheSourceThenAsks()
    {
        var vm = New();
        Assert.False(vm.Steps.Target.IsVisible);

        vm.Steps.Report(vm.Steps.Source, true, "backups, MyDb on SRV01");

        Assert.True(vm.Steps.Target.IsVisible);
        Assert.False(vm.Steps.Target.IsComplete);
        Assert.False(vm.Steps.Point.IsVisible);
    }

    [Fact]
    public void ChoosingATargetConnectsItAndFinishesTheStep()
    {
        var vm = New();
        vm.Steps.Report(vm.Steps.Source, true, "backups, MyDb on SRV01");
        var server = Server();

        vm.SelectedTargetServer = server;

        Assert.Same(server, vm.ConnectedServer);
        Assert.True(vm.Steps.Target.IsComplete);
        Assert.Equal("SRV01", vm.Steps.Target.Summary);
        Assert.True(vm.Steps.Point.IsVisible);
    }

    /// <summary>
    /// Connecting on the SQL Servers screen is the same decision made elsewhere - the step shows
    /// it as already answered instead of asking again.
    /// </summary>
    [Fact]
    public void AServersScreenConnectionPreAnswersTheStep()
    {
        var vm = New();
        var server = Server(name: "SRV02");

        vm.ConnectedServer = server;                       // what MainViewModel's wiring does
        Assert.Same(server, vm.SelectedTargetServer);

        // Completion only means anything once the step is reachable - the withdrawal cascade
        // clears it while the source is unanswered - so the experience is: answer the source,
        // and the target step arrives already answered.
        vm.Steps.Report(vm.Steps.Source, true, "backups, MyDb on SRV01");

        Assert.True(vm.Steps.Target.IsComplete);
        Assert.Equal("SRV02", vm.Steps.Target.Summary);
        Assert.True(vm.Steps.Point.IsVisible);
    }

    /// <summary>
    /// The target is independent of the source, so changing the source must not throw it away.
    /// The generic withdrawal cascade cannot know that - it un-completes every later step - so
    /// the screen re-reports the answer still sitting in the combo, and the user lands back on
    /// the restore point rather than on a question they already answered.
    /// </summary>
    [Fact]
    public void TheAnswerSurvivesChangingTheSource()
    {
        var vm = New();
        vm.Steps.Report(vm.Steps.Source, true, "backups, MyDb on SRV01");
        vm.SelectedTargetServer = Server();

        vm.Steps.Report(vm.Steps.Source, false, string.Empty);
        Assert.False(vm.Steps.Target.IsVisible);

        vm.Steps.Report(vm.Steps.Source, true, "backups, OtherDb on SRV01");

        Assert.True(vm.Steps.Target.IsComplete);
        Assert.Equal("SRV01", vm.Steps.Target.Summary);
        Assert.True(vm.Steps.Point.IsVisible);
    }

    /// <summary>
    /// The step offers the saved list, and a connection made before the list existed is
    /// re-pointed at the config instance so the combo shows it as the answer.
    /// </summary>
    [Fact]
    public void RefreshOffersTheSavedServersAndRepointsTheAnswer()
    {
        var store = new FakeCredentialStore();
        store.Config.Servers.Add(Server());
        store.Config.Servers.Add(Server("s2", "SRV02"));
        var vm = New(store);

        // Connected on the Servers screen as a distinct object instance of the same saved server.
        vm.ConnectedServer = Server("s2", "SRV02");

        vm.RefreshContainers();

        Assert.Equal(2, vm.TargetServers.Count);
        Assert.Same(vm.TargetServers[1], vm.SelectedTargetServer);

        vm.Steps.Report(vm.Steps.Source, true, "backups, MyDb on SRV01");
        Assert.True(vm.Steps.Target.IsComplete);
    }
}
