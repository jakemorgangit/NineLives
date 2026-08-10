using Blackcat.NineLives.Models;
using Blackcat.NineLives.Services;
using Blackcat.NineLives.ViewModels;
using Xunit;

namespace Blackcat.NineLives.Tests;

/// <summary>
/// The database list waits for an instance (#319). Two instances that both back up a database
/// called Sales are the everyday DR pair; a pre-filled list mixed them, and picking from it
/// meant guessing whose history the timeline would show. One instance answers its own filter;
/// several make the databases wait; none at all means there is no instance dimension and
/// everything is offered.
/// </summary>
public class DatabaseCascadeTests
{
    private static BackupInventoryViewModel New(FakeBlobStorageService blob) =>
        new(blob, new FakeSqlServerService(), TestLogs.Temp(), TestAuditStores.Temp());

    private static BlobContainerConfig Container() => new()
    {
        Id = "c1",
        Name = "backups",
        ContainerUrl = "https://acct.blob.core.windows.net/backups"
    };

    private static BackupFileInfo File(string server, string db) => new()
    {
        BlobName = $"FULL/{server}/{db}/20260801_220000.bak",
        BlobUrl = $"https://acct.blob.core.windows.net/backups/FULL/{server}/{db}/20260801_220000.bak",
        Type = BackupType.Full,
        InferredServerName = server,
        InferredDatabaseName = db,
        SizeBytes = 1024,
        LastModified = new DateTimeOffset(2026, 8, 1, 22, 0, 0, TimeSpan.Zero)
    };

    [Fact]
    public async Task TwoInstancesMakeTheDatabasesWait()
    {
        var blob = new FakeBlobStorageService
        {
            Files = [File("SRV01", "Sales"), File("SRV02", "Sales"), File("SRV02", "Payroll")]
        };
        var vm = New(blob);

        await vm.LoadAsync(BackupLocation.Blob(Container()));

        Assert.Equal(2, vm.DiscoveredServers.Count);
        Assert.Null(vm.SelectedServerName);
        Assert.Empty(vm.DiscoveredDatabases);
        Assert.False(vm.HasDatabaseChoices);
    }

    [Fact]
    public async Task ChoosingTheInstanceFillsItsOwnDatabases()
    {
        var blob = new FakeBlobStorageService
        {
            Files = [File("SRV01", "Sales"), File("SRV02", "Sales"), File("SRV02", "Payroll")]
        };
        var vm = New(blob);
        await vm.LoadAsync(BackupLocation.Blob(Container()));

        vm.SelectedServerName = vm.DiscoveredServers.First(s => s.Contains("SRV02"));

        Assert.True(vm.HasDatabaseChoices);
        Assert.Equal(2, vm.DiscoveredDatabases.Count);
        Assert.Contains("Payroll", vm.DiscoveredDatabases);
    }

    [Fact]
    public async Task DeselectingTheInstanceEmptiesTheListAgain()
    {
        var blob = new FakeBlobStorageService
        {
            Files = [File("SRV01", "Sales"), File("SRV02", "Payroll")]
        };
        var vm = New(blob);
        await vm.LoadAsync(BackupLocation.Blob(Container()));
        vm.SelectedServerName = vm.DiscoveredServers[0];
        Assert.NotEmpty(vm.DiscoveredDatabases);

        vm.SelectedServerName = null;

        Assert.Empty(vm.DiscoveredDatabases);
    }

    /// <summary>A single instance answers its own filter - one-answer questions gate nothing.</summary>
    [Fact]
    public async Task ASingleInstanceIsChosenAutomatically()
    {
        var blob = new FakeBlobStorageService
        {
            Files = [File("SRV01", "Sales"), File("SRV01", "Payroll")]
        };
        var vm = New(blob);

        await vm.LoadAsync(BackupLocation.Blob(Container()));

        Assert.NotNull(vm.SelectedServerName);
        Assert.Equal(2, vm.DiscoveredDatabases.Count);
        // The DATABASE - the decision that arms anything - stays unmade.
        Assert.Null(vm.SelectedDatabaseName);
    }
}
