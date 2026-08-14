using System.Collections.ObjectModel;
using Blackcat.NineLives.Models;
using Blackcat.NineLives.ViewModels;
using Xunit;

namespace Blackcat.NineLives.Tests;

/// <summary>
/// Choosing a server is the request to see its databases.
///
/// Both screens made you pick a server and then press "List databases" - a second step whose
/// answer was never in doubt, in front of the dropdown you cannot use until it runs. The listing
/// now starts on selection, and the button stays as a refresh: a database created since, or a list
/// the instance could not give the first time.
///
/// The cost of firing automatically is that being overtaken stops being exotic. Changing your mind
/// about the server used to need a deliberate double press; now it is one dropdown click, and the
/// superseded listing must neither write its answer over the newer one nor run the shared cleanup
/// - OperationCancellation.End() disposes whatever source is current, which would be the live run's.
/// </summary>
public class AutoListDatabasesTests
{
    private static FakeCredentialStore StoreWithTwoServers()
    {
        var store = new FakeCredentialStore();
        store.Config.Servers.Add(new ServerConnection
        { Id = ServerConnection.NewId(), Name = "SRV01", ServerName = "SRV01" });
        store.Config.Servers.Add(new ServerConnection
        { Id = ServerConnection.NewId(), Name = "SRV02", ServerName = "SRV02" });
        store.Config.BlobContainers.Add(new BlobContainerConfig
        {
            Id = "c1",
            Name = "backups",
            ContainerUrl = "https://acct.blob.core.windows.net/backups"
        });
        return store;
    }

    // ── the step that went away ─────────────────────────────────────────────────

    [Fact]
    public void TheBackupScreenListsOnSelection()
    {
        var sql = new FakeSqlServerService { DatabaseList = ["Sales", "Archive"] };
        var vm = new BackupViewModel(StoreWithTwoServers(), sql, TestLogs.Temp());

        vm.Server = vm.Servers.First(s => s.Name == "SRV01");

        Assert.Equal(["Sales", "Archive"], vm.Databases);
        Assert.Null(vm.SelectedDatabase);   // listing is not choosing
    }

    [Fact]
    public void TheCopyScreenListsOnSourceSelection()
    {
        var sql = new FakeSqlServerService { DatabaseList = ["Sales", "Archive"] };
        var vm = new CopyDatabaseViewModel(StoreWithTwoServers(), sql, TestLogs.Temp());

        vm.SourceServer = vm.Servers.First(s => s.Name == "SRV01");

        Assert.Equal(["Sales", "Archive"], vm.SourceDatabases);
        Assert.Null(vm.SourceDatabase);
    }

    /// <summary>Clearing the dropdown asks for nothing; it must not fire a listing at no server.</summary>
    [Fact]
    public void ClearingTheServerListsNothing()
    {
        var sql = new FakeSqlServerService { DatabaseList = ["Sales"] };
        var vm = new BackupViewModel(StoreWithTwoServers(), sql, TestLogs.Temp());
        vm.Server = vm.Servers.First();
        Assert.NotEmpty(vm.Databases);

        vm.Server = null;

        Assert.Empty(vm.Databases);
        Assert.False(vm.HasError);   // not "Choose the server to back up from"
    }

    // ── being overtaken ─────────────────────────────────────────────────────────

    /// <summary>
    /// The hazard the automatic trigger creates: pick a slow server, change your mind, and the
    /// first listing lands after the second. The answer on screen must be the server on screen.
    /// </summary>
    [Fact]
    public async Task ASupersededListingDoesNotWriteItsAnswer()
    {
        var sql = new FakeSqlServerService { DatabaseListIgnoresCancellation = true };
        sql.DatabaseListByServer["SRV01"] = ["OldServerDb"];
        sql.DatabaseListByServer["SRV02"] = ["NewServerDb"];

        var vm = new BackupViewModel(StoreWithTwoServers(), sql, TestLogs.Temp());

        // SRV01's listing is held open, then overtaken by SRV02.
        var gate = new TaskCompletionSource<bool>();
        sql.DatabaseListGate = gate;
        vm.Server = vm.Servers.First(s => s.Name == "SRV01");

        sql.DatabaseListGate = null;                 // SRV02 answers immediately
        vm.Server = vm.Servers.First(s => s.Name == "SRV02");

        gate.SetResult(true);                        // SRV01 answers anyway, too late
        await Task.Delay(50);

        Assert.Equal(["NewServerDb"], vm.Databases);
        Assert.DoesNotContain("OldServerDb", vm.Databases);
    }

    /// <summary>
    /// The subtler half. The superseded run's finally must not call End(), which disposes whatever
    /// source is current - by then the live listing's. Left alone it would tear down a running
    /// operation's cancellation and drop IsBusy while it was still going.
    /// </summary>
    [Fact]
    public async Task ASupersededListingDoesNotTidyUpAfterTheLiveOne()
    {
        var sql = new FakeSqlServerService { DatabaseListIgnoresCancellation = true };
        sql.DatabaseListByServer["SRV01"] = ["OldServerDb"];
        sql.DatabaseListByServer["SRV02"] = ["NewServerDb"];

        var vm = new BackupViewModel(StoreWithTwoServers(), sql, TestLogs.Temp());

        var first = new TaskCompletionSource<bool>();
        sql.DatabaseListGate = first;
        vm.Server = vm.Servers.First(s => s.Name == "SRV01");

        // SRV02's listing is still running when SRV01's lands.
        var second = new TaskCompletionSource<bool>();
        sql.DatabaseListGate = second;
        vm.Server = vm.Servers.First(s => s.Name == "SRV02");

        first.SetResult(true);
        await Task.Delay(50);

        // SRV02 is still in flight, and says so. Without the generation guard SRV01's finally has
        // just dropped this and disposed SRV02's token source out from under it.
        Assert.True(vm.IsBusy);

        second.SetResult(true);
        await Task.Delay(50);

        Assert.False(vm.IsBusy);
        Assert.Equal(["NewServerDb"], vm.Databases);
    }

    /// <summary>A listing that fails after being overtaken is not the new server's problem.</summary>
    [Fact]
    public async Task ASupersededFailureIsNotReportedOverTheNewServer()
    {
        var sql = new FakeSqlServerService { DatabaseListIgnoresCancellation = true };
        sql.DatabaseListByServer["SRV02"] = ["NewServerDb"];

        var vm = new BackupViewModel(StoreWithTwoServers(), sql, TestLogs.Temp());

        var gate = new TaskCompletionSource<bool>();
        sql.DatabaseListGate = gate;
        vm.Server = vm.Servers.First(s => s.Name == "SRV01");

        sql.DatabaseListGate = null;
        vm.Server = vm.Servers.First(s => s.Name == "SRV02");

        gate.SetException(new InvalidOperationException("SRV01 is unreachable"));
        await Task.Delay(50);

        Assert.False(vm.HasError);
        Assert.Equal(["NewServerDb"], vm.Databases);
    }

    // ── the run still owns the screen ───────────────────────────────────────────

    /// <summary>
    /// The list is deliberately unfetchable mid-run (#281) - it can wait, the backup cannot re-run.
    /// The automatic trigger must respect that rather than route around it.
    /// </summary>
    [Fact]
    public void NothingIsListedWhileABackupIsRunning()
    {
        var sql = new FakeSqlServerService { DatabaseList = ["Sales"] };
        var vm = new BackupViewModel(StoreWithTwoServers(), sql, TestLogs.Temp())
        {
            IsRunning = true
        };

        vm.Server = vm.Servers.First(s => s.Name == "SRV01");

        Assert.False(vm.CanLoadDatabases);
        Assert.Empty(vm.Databases);
    }
}
