using Blackcat.NineLives.Models;
using Blackcat.NineLives.Services;
using Blackcat.NineLives.ViewModels;
using Xunit;

namespace Blackcat.NineLives.Tests;

/// <summary>
/// The copy's generation-time checks answer for their INPUTS, not for keystrokes (#285).
/// They used to re-fire on every property change - six round trips across two production
/// instances per character typed into the target-name box - with no cancellation, racing
/// each other's warning clears, and nothing holding the run until the verdicts were in.
/// One serialised sweep per input change now, and the run waits for it.
/// </summary>
public class GenerationCheckDisciplineTests
{
    private static (CopyDatabaseViewModel vm, FakeSqlServerService sql, int VersionAsks)
        Stage()
    {
        var store = new FakeCredentialStore();
        store.Config.Servers.Add(new ServerConnection
        { Id = ServerConnection.NewId(), Name = "SRV01", ServerName = "SRV01" });
        store.Config.Servers.Add(new ServerConnection
        { Id = ServerConnection.NewId(), Name = "SRV02", ServerName = "SRV02" });
        store.Config.Servers.Add(new ServerConnection
        { Id = ServerConnection.NewId(), Name = "SRV03", ServerName = "SRV03" });
        store.Config.BlobContainers.Add(new BlobContainerConfig
        {
            Id = "c1",
            Name = "backups",
            ContainerUrl = "https://acct.blob.core.windows.net/backups"
        });

        var sql = new FakeSqlServerService { DatabaseList = ["MyDb"] };
        var vm = new CopyDatabaseViewModel(store, sql, TestLogs.Temp());
        vm.SourceServer = vm.Servers.First(s => s.Name == "SRV01");
        vm.TargetServer = vm.Servers.First(s => s.Name == "SRV02");
        vm.Container = vm.Containers.Single();
        vm.SourceDatabases = ["MyDb"];
        vm.SourceDatabase = "MyDb";
        vm.TargetDatabaseName = "MyDb_Copy";
        return (vm, sql, 0);
    }

    [Fact]
    public void TypingInTheTargetNameDoesNotReAskTheServers()
    {
        var (vm, sql, _) = Stage();
        var asks = 0;
        sql.BeforeMajorVersion = () => { asks++; return Task.CompletedTask; };

        vm.GenerateCommand.Execute(null);
        var afterFirstGenerate = asks;
        Assert.True(afterFirstGenerate > 0);

        // Six keystrokes; every one used to launch a fresh sweep.
        foreach (var ch in "_Test1")
            vm.TargetDatabaseName += ch;

        Assert.Equal(afterFirstGenerate, asks);
    }

    [Fact]
    public void ChangingTheTargetServerDoesReCheck()
    {
        var (vm, sql, _) = Stage();
        var asks = 0;
        sql.BeforeMajorVersion = () => { asks++; return Task.CompletedTask; };

        vm.GenerateCommand.Execute(null);
        var afterFirstGenerate = asks;

        vm.TargetServer = vm.Servers.First(s => s.Name == "SRV03");

        Assert.True(asks > afterFirstGenerate);
    }

    /// <summary>
    /// The confirmation's warning panels must hold the CURRENT answers before anything runs -
    /// a quick Confirm used to run with the panels blank while the checks were in flight.
    /// </summary>
    [Fact]
    public async Task TheRunWaitsForTheChecksVerdict()
    {
        var (vm, sql, _) = Stage();
        var gate = new TaskCompletionSource();
        sql.BeforeMajorVersion = () => gate.Task;

        vm.GenerateCommand.Execute(null);

        var run = Task.Run(async () =>
        {
            await vm.RunCommand.ExecuteAsync(null);
            await vm.RunCommand.ExecuteAsync(null);
        });

        await Task.Delay(150);
        Assert.Empty(sql.ExecutedScripts);

        gate.SetResult();
        await run;

        Assert.Equal(2, sql.ExecutedScripts.Count);
        Assert.Equal(CopyOutcome.Copied, vm.Outcome);
    }

    /// <summary>A stale sweep's answers must not land after a fresh sweep took over.</summary>
    [Fact]
    public async Task AFreshSweepsVerdictOutlivesTheStaleOne()
    {
        var (vm, sql, _) = Stage();

        // First sweep: gated open, so it is still in flight when the inputs change.
        var firstGate = new TaskCompletionSource();
        sql.BeforeMajorVersion = () => firstGate.Task;
        vm.GenerateCommand.Execute(null);

        // Inputs change: the second sweep answers instantly with a REFUSING version pair.
        sql.BeforeMajorVersion = null;
        sql.MajorVersionByServer["SRV01"] = 17;
        sql.MajorVersionByServer["SRV03"] = 16;
        vm.TargetServer = vm.Servers.First(s => s.Name == "SRV03");

        // Now the stale first sweep finally completes - its clean verdict must not clear
        // the fresh warning.
        firstGate.SetResult();
        await Task.Delay(150);

        Assert.Contains("3169", vm.VersionWarning);
    }
}
