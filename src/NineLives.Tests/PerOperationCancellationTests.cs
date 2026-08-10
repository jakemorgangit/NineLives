using Blackcat.NineLives.Models;
using Blackcat.NineLives.Services;
using Blackcat.NineLives.ViewModels;
using Xunit;

namespace Blackcat.NineLives.Tests;

/// <summary>
/// Every long operation owns its cancellation (#281). Three screens shared one instance
/// across operations that overlap, so the List button's Begin() silently cancelled a running
/// backup or copy, and the finished operation's End() disposed the survivor's token - the
/// audit died with "Cannot access a disposed object" and its Stop button vanished.
/// RestoreExecutionViewModel always kept one instance per concern; now everyone does.
/// </summary>
public class PerOperationCancellationTests
{
    private static (BackupViewModel vm, FakeSqlServerService sql) BackupStage()
    {
        var store = new FakeCredentialStore();
        store.Config.Servers.Add(new ServerConnection
        { Id = ServerConnection.NewId(), Name = "SRV01", ServerName = "SRV01" });
        store.Config.BlobContainers.Add(new BlobContainerConfig
        {
            Id = "c1",
            Name = "backups",
            ContainerUrl = "https://acct.blob.core.windows.net/backups"
        });

        var sql = new FakeSqlServerService { DatabaseList = ["MyDb"] };
        var vm = new BackupViewModel(store, sql, TestLogs.Temp());
        vm.Server = vm.Servers.Single();
        vm.Container = vm.Containers.Single();
        return (vm, sql);
    }

    /// <summary>
    /// The button that killed production backups: mid-run, the list is simply not fetchable -
    /// and the run completes untouched.
    /// </summary>
    [Fact]
    public async Task TheListButtonIsDeadWhileABackupRunsAndTheRunCompletes()
    {
        var (vm, sql) = BackupStage();
        await vm.LoadDatabasesCommand.ExecuteAsync(null);
        vm.SelectedDatabase = "MyDb";
        vm.GenerateCommand.Execute(null);

        bool? gateMidRun = null;
        sql.OnExecute = _ =>
        {
            gateMidRun = vm.CanLoadDatabases || vm.LoadDatabasesCommand.CanExecute(null);
        };

        await vm.ExecuteCommand.ExecuteAsync(null);
        await vm.ExecuteCommand.ExecuteAsync(null);

        Assert.False(gateMidRun);
        Assert.Single(sql.ExecutedScripts);
        Assert.True(vm.CanLoadDatabases);
    }

    [Fact]
    public async Task TheCopysListButtonIsDeadWhileACopyRuns()
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

        var sql = new FakeSqlServerService { DatabaseList = ["MyDb"] };
        var vm = new CopyDatabaseViewModel(store, sql, TestLogs.Temp());
        vm.SourceServer = vm.Servers.First(s => s.Name == "SRV01");
        vm.TargetServer = vm.Servers.First(s => s.Name == "SRV02");
        vm.Container = vm.Containers.Single();
        vm.SourceDatabases = ["MyDb"];
        vm.SourceDatabase = "MyDb";
        vm.TargetDatabaseName = "MyDb_Copy";
        vm.GenerateCommand.Execute(null);

        bool? gateMidRun = null;
        sql.OnExecute = n =>
        {
            if (n == 1) gateMidRun = vm.CanLoadSourceDatabases;
        };

        await vm.RunCommand.ExecuteAsync(null);
        await vm.RunCommand.ExecuteAsync(null);

        Assert.False(gateMidRun);
        Assert.Equal(2, sql.ExecutedScripts.Count);
        Assert.True(vm.CanLoadSourceDatabases);
    }

    /// <summary>Stop with nothing running stays a no-op - three instances or one.</summary>
    [Fact]
    public void CancellingTheIdleInventoryDoesNotThrow()
    {
        var inventory = new BackupInventoryViewModel(
            new FakeBlobStorageService(), new FakeSqlServerService(),
            TestLogs.Temp(), TestAuditStores.Temp());

        var ex = Record.Exception(() => inventory.CancelLoadCommand.Execute(null));

        Assert.Null(ex);
        Assert.False(inventory.CanCancelLoad);
    }
}
