using Blackcat.NineLives.Models;
using Blackcat.NineLives.Services;
using Blackcat.NineLives.ViewModels;
using Xunit;

namespace Blackcat.NineLives.Tests;

/// <summary>
/// The browse/exposure handoff tells the truth (#288): it refuses to wipe a running restore's
/// record, it loads exactly what the browser showed, and a filter it cannot apply is SAID
/// rather than silently dropped.
/// </summary>
public class HandoffHonestyTests
{
    private static readonly DateTime T0 = new(2026, 8, 7, 22, 0, 0);

    private static BlobContainerConfig Container(string id = "c1", string name = "backups") => new()
    {
        Id = id,
        Name = name,
        ContainerUrl = $"https://acct.blob.core.windows.net/{name}"
    };

    private static BackupFileInfo File_(string database, string server = "SRV01") => new()
    {
        BlobName = $"FULL/{server}/{database}/20260807_220000.bak",
        BlobUrl = $"https://acct.blob.core.windows.net/backups/FULL/{server}/{database}/20260807_220000.bak",
        Type = BackupType.Full,
        InferredDatabaseName = database,
        InferredServerName = server,
        LastModified = new DateTimeOffset(T0, TimeSpan.Zero)
    };

    private static RestoreViewModel Restore(FakeCredentialStore store, FakeBlobStorageService blob) => new(
        blob, new FakeSqlServerService(), new BackupChainBuilder(),
        new RestoreScriptGenerator(), store, TestLogs.Temp(),
        new FakeRestoreHistoryStore(), TestAuditStores.Temp())
    { Mode = AppMode.Pro };

    private static FakeCredentialStore Store(params BlobContainerConfig[] containers)
    {
        var store = new FakeCredentialStore();
        foreach (var c in containers) store.Config.BlobContainers.Add(c);
        return store;
    }

    /// <summary>
    /// The screen already refuses to regenerate the script mid-restore because it is the record
    /// of the run; the handoff may not do what the regenerate button may not.
    /// </summary>
    [Fact]
    public async Task AHandoffIsRefusedWhileARestoreRuns()
    {
        var blob = new FakeBlobStorageService { Files = [File_("Sales"), File_("Payroll")] };
        var vm = Restore(Store(Container()), blob);
        vm.RefreshContainers();
        await vm.AcceptHandoffAsync(new BrowseHandoff(
            BackupMedium.AzureBlob, Container(), null, null, "Sales"));
        Assert.Equal("Sales", vm.Inventory.SelectedDatabaseName);

        vm.Execution.IsExecuting = true;
        await vm.AcceptHandoffAsync(new BrowseHandoff(
            BackupMedium.AzureBlob, Container(), null, null, "Payroll"));

        // The record of the run is untouched, and the refusal is explained.
        Assert.Equal("Sales", vm.Inventory.SelectedDatabaseName);
        Assert.Contains("restore is running", vm.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Extra containers ticked on an earlier visit would make the load see MORE than the
    /// browser showed - a chain could assemble from backups the user never looked at.
    /// </summary>
    [Fact]
    public async Task ExtraTickedContainersDoNotSurviveTheHandoff()
    {
        var blob = new FakeBlobStorageService();
        blob.FilesByContainer["backups"] = [File_("Sales")];
        blob.FilesByContainer["archive"] = [File_("Sales")];
        var vm = Restore(Store(Container(), Container("c2", "archive")), blob);
        vm.RefreshContainers();

        var extra = Assert.Single(vm.AdditionalContainers);
        extra.IsSelected = true;
        Assert.Equal(2, vm.ContainersToRead.Count);

        await vm.AcceptHandoffAsync(new BrowseHandoff(
            BackupMedium.AzureBlob, Container(), null, null, "Sales"));

        Assert.All(vm.AdditionalContainers, c => Assert.False(c.IsSelected));
        Assert.Single(vm.ContainersToRead);
        Assert.Equal("c1", vm.ContainersToRead[0].Id);
    }

    /// <summary>The filter matches the way every other server comparison does - by identity.</summary>
    [Fact]
    public async Task TheInstanceFilterLandsAcrossACaseDifference()
    {
        var blob = new FakeBlobStorageService
        {
            Files = [File_("Sales", "SRV01"), File_("Sales", "SRV02")]
        };
        var vm = Restore(Store(Container()), blob);
        vm.RefreshContainers();

        await vm.AcceptHandoffAsync(new BrowseHandoff(
            BackupMedium.AzureBlob, Container(), null, "srv01", "Sales"));

        Assert.Equal("SRV01", vm.Inventory.SelectedServerName);
        Assert.Equal("Sales", vm.Inventory.SelectedDatabaseName);
    }

    /// <summary>
    /// A filter that cannot be applied is a different question than the one clicked - the
    /// screen says so instead of silently answering it.
    /// </summary>
    [Fact]
    public async Task ADroppedInstanceFilterIsSaidOutLoud()
    {
        var blob = new FakeBlobStorageService { Files = [File_("Sales")] };
        var vm = Restore(Store(Container()), blob);
        vm.RefreshContainers();

        // The dashboard hands over msdb's name for the machine; the blob paths inferred another.
        await vm.AcceptHandoffAsync(new BrowseHandoff(
            BackupMedium.AzureBlob, Container(), null, "localhost", "Sales"));

        Assert.Contains("not among this source's instances", vm.StatusMessage);
        Assert.Equal("Sales", vm.Inventory.SelectedDatabaseName);
    }
}
