using Blackcat.NineLives.Models;
using Blackcat.NineLives.Services;
using Blackcat.NineLives.ViewModels;
using Xunit;

namespace Blackcat.NineLives.Tests;

/// <summary>
/// A generated script must not survive the change that invalidated it (#278). Both the backup
/// and copy screens rest on "nothing runs that was not on screen first" - and both used to
/// leave the OLD script displayed and runnable when an input change made regeneration
/// impossible: generate for a database on one server, switch the server box, and two presses
/// executed the old statements against the new instance.
/// </summary>
public class StaleGeneratedStateTests
{
    private static (BackupViewModel vm, FakeSqlServerService sql) BackupStage()
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

        var sql = new FakeSqlServerService { DatabaseList = ["MyDb", "OtherDb"] };
        var vm = new BackupViewModel(store, sql, TestLogs.Temp());
        vm.Server = vm.Servers.First(s => s.Name == "SRV01");
        vm.Container = vm.Containers.Single();
        return (vm, sql);
    }

    [Fact]
    public async Task SwitchingServersAfterGeneratingLeavesNothingRunnable()
    {
        var (vm, _) = BackupStage();
        await vm.LoadDatabasesCommand.ExecuteAsync(null);
        vm.SelectedDatabase = "MyDb";
        vm.GenerateCommand.Execute(null);
        Assert.True(vm.HasScript);

        vm.Server = vm.Servers.First(s => s.Name == "SRV02");

        Assert.False(vm.HasScript);
        Assert.Equal(string.Empty, vm.GeneratedScript);
        Assert.Empty(vm.Destinations);
        Assert.False(vm.CanExecute);
    }

    /// <summary>The multi-select ticks belonged to the previous instance too.</summary>
    [Fact]
    public async Task SwitchingServersClearsTheMultiSelectTicksAndCertificates()
    {
        var (vm, _) = BackupStage();
        await vm.LoadDatabasesCommand.ExecuteAsync(null);
        vm.MultiSelect = true;
        vm.PickAllCommand.Execute(null);
        Assert.True(vm.CanGenerate);

        vm.Server = vm.Servers.First(s => s.Name == "SRV02");

        Assert.Empty(vm.DatabasePicks);
        Assert.Empty(vm.EncryptionCertificates);
        Assert.Null(vm.SelectedEncryptionCertificate);
        Assert.False(vm.CanGenerate);
    }

    [Fact]
    public void BlankingTheCopyTargetLeavesNothingRunnable()
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
        vm.SourceServer = vm.Servers.First();
        vm.TargetServer = vm.Servers.Last();
        vm.Container = vm.Containers.Single();
        vm.SourceDatabases = ["MyDb"];
        vm.SourceDatabase = "MyDb";
        vm.TargetDatabaseName = "MyDb_Copy";
        vm.GenerateCommand.Execute(null);
        Assert.True(vm.HasScripts);

        vm.TargetDatabaseName = "";

        Assert.False(vm.HasScripts);
        Assert.Equal(string.Empty, vm.BackupScript);
        Assert.Equal(string.Empty, vm.RestoreScript);
        Assert.Empty(vm.Destinations);
        Assert.False(vm.CanRun);
    }
}
