using Blackcat.NineLives.Models;
using Blackcat.NineLives.ViewModels;
using Xunit;

namespace Blackcat.NineLives.Tests;

/// <summary>
/// The inventory seam (#115 seam 4).
///
/// Its point is not the line count: the loaded backups now carry the container they came FROM, so
/// nothing downstream has to assume that whatever is loaded belongs to whatever is selected above
/// it. That assumption produced #112 - changing the container left the previous one's chain and
/// script armed and executable - and, when the preselection was removed, a working set that fell
/// back to every backup in the container.
/// </summary>
public class BackupInventorySeamTests
{
    private static readonly DateTime T0 = new(2026, 8, 1, 22, 0, 0);

    private static BackupFileInfo File(string db, string server, BackupType type, DateTime at) => new()
    {
        BlobName = $"{type}/{server}/{db}/{at:yyyyMMdd_HHmmss}.bak",
        BlobUrl = $"https://acct/backups/{type}/{server}/{db}/{at:yyyyMMdd_HHmmss}.bak",
        Type = type,
        InferredServerName = server,
        InferredDatabaseName = db,
        SizeBytes = 1000,
        LastModified = new DateTimeOffset(at, TimeSpan.Zero)
    };

    private static BlobContainerConfig Container(string name = "backups") => new()
    {
        Id = BlobContainerConfig.NewId(),
        Name = name,
        ContainerUrl = $"https://acct/{name}"
    };

    private static (BackupInventoryViewModel vm, FakeBlobStorageService blob) New()
    {
        var blob = new FakeBlobStorageService
        {
            Files =
            [
                File("MyDb", "SRV01", BackupType.Full, T0),
                File("MyDb", "SRV01", BackupType.TransactionLog, T0.AddHours(1)),
                File("OtherDb", "SRV01", BackupType.Full, T0)
            ]
        };
        return (new BackupInventoryViewModel(blob, new FakeSqlServerService(), TestLogs.Temp()), blob);
    }

    [Fact]
    public async Task ALoadRecordsWhichContainerItCameFrom()
    {
        var (vm, _) = New();
        var container = Container("prod-backups");

        await vm.LoadAsync(BackupLocation.Blob(container));

        Assert.Same(container, vm.LoadedFrom!.Container);
        Assert.True(vm.BackupsLoaded);
    }

    /// <summary>
    /// The lists are offered, not answered - and until one is answered the working set is EMPTY,
    /// not everything. Falling back to every set drew a timeline of every backup of every database
    /// on every server.
    /// </summary>
    [Fact]
    public async Task NothingIsChosenAndNothingIsWorkedFromUntilItIs()
    {
        var (vm, _) = New();

        await vm.LoadAsync(BackupLocation.Blob(Container()));

        Assert.Null(vm.SelectedServerName);
        Assert.Null(vm.SelectedDatabaseName);
        Assert.Empty(vm.WorkingSet);
        Assert.Equal(0, vm.SetCount);
        Assert.NotEmpty(vm.DiscoveredDatabases);
    }

    [Fact]
    public async Task ChoosingADatabaseNarrowsTheWorkingSetToIt()
    {
        var (vm, _) = New();
        await vm.LoadAsync(BackupLocation.Blob(Container()));

        vm.SelectedDatabaseName = "MyDb";

        Assert.Equal(2, vm.SetCount);
        Assert.Equal(1, vm.FullCount);
        Assert.Equal(1, vm.LogCount);
        Assert.All(vm.WorkingSet, s => Assert.Equal("MyDb", s.DatabaseName));
    }

    /// <summary>Clearing forgets where it came from, so nothing can be shown under a container it
    /// did not come from - the shape of #112.</summary>
    [Fact]
    public async Task ClearingForgetsTheContainerAndEverythingFromIt()
    {
        var (vm, _) = New();
        await vm.LoadAsync(BackupLocation.Blob(Container()));
        vm.SelectedDatabaseName = "MyDb";

        vm.Clear();

        Assert.Null(vm.LoadedFrom);
        Assert.False(vm.BackupsLoaded);
        Assert.Empty(vm.WorkingSet);
        Assert.Empty(vm.DiscoveredDatabases);
        Assert.Null(vm.SelectedDatabaseName);
    }

    [Fact]
    public async Task TheWorkingSetChangeIsAnnouncedSoTheChainCanFollow()
    {
        var (vm, _) = New();
        await vm.LoadAsync(BackupLocation.Blob(Container()));

        var announced = 0;
        vm.WorkingSetChanged += () => announced++;

        vm.SelectedDatabaseName = "MyDb";

        Assert.True(announced > 0);
    }

    /// <summary>
    /// A second load scopes down to what is already chosen, instead of walking the whole container
    /// again - about 1,075ms unscoped versus 233ms for one database on a real container (#28).
    /// </summary>
    [Fact]
    public async Task AReloadWithADatabaseChosenScopesTheListing()
    {
        var (vm, blob) = New();
        await vm.LoadAsync(BackupLocation.Blob(Container()));
        Assert.Null(blob.LastScope);

        vm.SelectedServerName = "SRV01";
        vm.SelectedDatabaseName = "MyDb";
        await vm.LoadAsync(BackupLocation.Blob(Container()));

        Assert.NotNull(blob.LastScope);
        Assert.Equal("MyDb", blob.LastScope!.DatabaseName);
        Assert.Equal("SRV01", blob.LastScope.ServerName);
    }
}
