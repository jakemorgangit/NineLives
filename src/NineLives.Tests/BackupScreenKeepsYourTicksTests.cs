using Blackcat.NineLives.Models;
using Blackcat.NineLives.ViewModels;
using Xunit;

namespace Blackcat.NineLives.Tests;

/// <summary>
/// Revisiting the Back Up screen keeps what was ticked (#476).
///
/// The same defect as #457 on the Copy screen, on the screen where it costs more. Navigation calls
/// Refresh on every visit - deliberately, so a server added elsewhere appears - and Refresh
/// re-assigns Server to whichever config entry matches by id. That is a different OBJECT each
/// time, because the real store deserializes on every read, so the change handler fired on arrival
/// and did what it should do when somebody genuinely picks a different server: emptied the list,
/// the ticks and the certificates, and opened a connection to re-read them.
///
/// This is the screen for backing up forty databases before a patch window. Ticking twelve of them
/// and glancing at another screen emptied the list, silently, because from the screen's point of
/// view nothing had gone wrong.
/// </summary>
public class BackupScreenKeepsYourTicksTests
{
    private static (BackupViewModel vm, FakeSqlServerService sql, FakeCredentialStore store) Screen(
        params string[] databases)
    {
        var store = new FakeCredentialStore();
        store.Config.Servers.Add(new ServerConnection
        { Id = "s1", Name = "SRV01", ServerName = "SRV01" });
        store.Config.Servers.Add(new ServerConnection
        { Id = "s2", Name = "SRV02", ServerName = "SRV02" });
        store.Config.BlobContainers.Add(new BlobContainerConfig
        {
            Id = "c1",
            Name = "backups",
            ContainerUrl = "https://acct.blob.core.windows.net/backups"
        });

        var sql = new FakeSqlServerService
        {
            DatabaseList = databases.Length > 0
                ? databases.ToList()
                : ["Sales", "Payroll", "Warehouse"]
        };

        var vm = new BackupViewModel(store, sql, TestLogs.Temp(), history: new FakeOperationHistoryStore());
        return (vm, sql, store);
    }

    private static async Task<BackupViewModel> TickedAsync(params string[] toTick)
    {
        var (vm, _, _) = Screen();
        vm.Refresh();
        vm.Server = vm.Servers.First(s => s.Id == "s1");
        vm.Container = vm.Containers[0];
        await vm.LoadDatabasesCommand.ExecuteAsync(null);

        foreach (var name in toTick)
            vm.DatabasePicks.First(p => p.Name == name).IsPicked = true;

        return vm;
    }

    /// <summary>The reported behaviour: tick two, come back, both gone.</summary>
    [Fact]
    public async Task RevisitingTheScreenKeepsWhatWasTicked()
    {
        var vm = await TickedAsync("Sales", "Payroll");
        Assert.Equal(2, vm.DatabasePicks.Count(p => p.IsPicked));

        // What navigating away and back does.
        vm.Refresh();
        await vm.WaitForDatabaseListForTests();

        Assert.Equal(2, vm.DatabasePicks.Count(p => p.IsPicked));
        Assert.Contains(vm.DatabasePicks, p => p.Name == "Sales" && p.IsPicked);
        Assert.Contains(vm.DatabasePicks, p => p.Name == "Payroll" && p.IsPicked);
        Assert.Contains(vm.DatabasePicks, p => p.Name == "Warehouse" && !p.IsPicked);
    }

    /// <summary>
    /// The list is still re-read, because a database created since the last visit should appear.
    /// Only the ticks survive, not the list.
    /// </summary>
    [Fact]
    public async Task TheListIsStillRefreshedSoANewDatabaseAppears()
    {
        var (vm, sql, _) = Screen();
        vm.Refresh();
        vm.Server = vm.Servers.First(s => s.Id == "s1");
        await vm.LoadDatabasesCommand.ExecuteAsync(null);
        vm.DatabasePicks.First(p => p.Name == "Sales").IsPicked = true;

        sql.DatabaseList = ["Sales", "Payroll", "Warehouse", "Reporting"];

        vm.Refresh();
        await vm.WaitForDatabaseListForTests();

        Assert.Contains(vm.DatabasePicks, p => p.Name == "Reporting");
        Assert.Contains(vm.DatabasePicks, p => p.Name == "Sales" && p.IsPicked);
    }

    /// <summary>
    /// A tick restored against a database that has gone would arm the run for something the
    /// instance cannot back up.
    /// </summary>
    [Fact]
    public async Task ATickForADatabaseThatHasGoneIsNotRestored()
    {
        var (vm, sql, _) = Screen();
        vm.Refresh();
        vm.Server = vm.Servers.First(s => s.Id == "s1");
        await vm.LoadDatabasesCommand.ExecuteAsync(null);
        vm.DatabasePicks.First(p => p.Name == "Payroll").IsPicked = true;

        sql.DatabaseList = ["Sales", "Warehouse"];

        vm.Refresh();
        await vm.WaitForDatabaseListForTests();

        Assert.DoesNotContain(vm.DatabasePicks, p => p.Name == "Payroll");
        Assert.DoesNotContain(vm.DatabasePicks, p => p.IsPicked);
    }

    /// <summary>
    /// And a genuine switch still clears everything. The existing comment explains why in full:
    /// stale ticks satisfied CanGenerate, so the regenerated statements named the OLD server's
    /// databases with destinations claiming the new one.
    /// </summary>
    [Fact]
    public async Task SwitchingToADifferentServerStillClearsTheTicks()
    {
        var vm = await TickedAsync("Sales", "Payroll");

        vm.Server = vm.Servers.First(s => s.Id == "s2");
        await vm.WaitForDatabaseListForTests();

        Assert.DoesNotContain(vm.DatabasePicks, p => p.IsPicked);
    }
}
