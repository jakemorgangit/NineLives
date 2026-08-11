using Blackcat.NineLives.Models;
using Blackcat.NineLives.Services;
using Blackcat.NineLives.ViewModels;
using Xunit;

namespace Blackcat.NineLives.Tests;

/// <summary>
/// The bucket's region has to reach the generated statement, not just the listing (#361).
///
/// It was stored, and it was used when the app listed a container, and the generators knew how
/// to emit it - but only the app's RESTORE path ever put it on the options object. Every other
/// path left it null, so the statement went out signed for the provider's default region.
///
/// The shape of that failure is the nasty one: the container tests perfectly, `list` works,
/// everything says healthy - and then the backup or restore fails, because listing is signed by
/// this app and the statement is signed by SQL Server. It bites exactly the providers the
/// feature exists for, since AWS-style hosts carry the region in the name and Wasabi, Backblaze
/// B2, R2 and appliances generally do not.
/// </summary>
public class S3RegionReachesTheStatementTests
{
    private const string Bucket = "s3://storage.example.com/backups";
    private const string Region = "eu-central-2";

    private static FakeCredentialStore StoreWithBucket()
    {
        var store = new FakeCredentialStore();
        store.Config.Servers.Add(new ServerConnection
        { Id = ServerConnection.NewId(), Name = "SRV01", ServerName = "SRV01" });
        store.Config.BlobContainers.Add(new BlobContainerConfig
        {
            Id = "c1",
            Name = "bucket",
            ContainerUrl = Bucket,
            S3Region = Region
        });
        return store;
    }

    [Fact]
    public void TheBackupScreenPutsTheRegionInTheStatement()
    {
        var store = StoreWithBucket();
        var vm = new BackupViewModel(
            store, new FakeSqlServerService { DatabaseList = ["MyDb"] }, TestLogs.Temp())
        {
            Medium = BackupMedium.AzureBlob
        };

        vm.Server = vm.Servers.Single();
        vm.Container = vm.Containers.Single();
        vm.SelectedDatabase = "MyDb";

        vm.GenerateCommand.Execute(null);

        Assert.Contains(Region, vm.GeneratedScript);
    }

    /// <summary>
    /// And an Azure container does not acquire one, which is the guard the generators already
    /// have - a stale region must not leak into a statement that has no bucket in it.
    /// </summary>
    [Fact]
    public void AnAzureContainerGetsNoRegion()
    {
        var store = new FakeCredentialStore();
        store.Config.Servers.Add(new ServerConnection
        { Id = ServerConnection.NewId(), Name = "SRV01", ServerName = "SRV01" });
        store.Config.BlobContainers.Add(new BlobContainerConfig
        {
            Id = "c1",
            Name = "azure",
            ContainerUrl = "https://acct.blob.core.windows.net/backups",
            S3Region = Region
        });

        var vm = new BackupViewModel(
            store, new FakeSqlServerService { DatabaseList = ["MyDb"] }, TestLogs.Temp())
        {
            Medium = BackupMedium.AzureBlob
        };

        vm.Server = vm.Servers.Single();
        vm.Container = vm.Containers.Single();
        vm.SelectedDatabase = "MyDb";

        vm.GenerateCommand.Execute(null);

        Assert.DoesNotContain(Region, vm.GeneratedScript);
    }

    /// <summary>
    /// A backup to a path the server can write to has no region either, whatever the selected
    /// container happens to say.
    /// </summary>
    [Fact]
    public void ASharedPathBackupGetsNoRegion()
    {
        var store = StoreWithBucket();
        var vm = new BackupViewModel(
            store, new FakeSqlServerService { DatabaseList = ["MyDb"] }, TestLogs.Temp())
        {
            Medium = BackupMedium.SharedPath,
            SharedPathRoot = @"\\nas01\sql"
        };

        vm.Server = vm.Servers.Single();
        vm.SelectedDatabase = "MyDb";

        vm.GenerateCommand.Execute(null);

        Assert.DoesNotContain(Region, vm.GeneratedScript);
    }
}
