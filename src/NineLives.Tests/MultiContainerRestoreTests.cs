using Blackcat.NineLives.Models;
using Blackcat.NineLives.Services;
using Blackcat.NineLives.ViewModels;
using Xunit;

namespace Blackcat.NineLives.Tests;

/// <summary>
/// A chain whose parts live in different containers (#32).
///
/// The layout this exists for is asymmetric: full backups archived to cool storage while the logs
/// that carry them forward stay in the hot container. So the model is primary-plus-additional
/// rather than a set of peers - there is a container somebody is working in, which the credential
/// panel points at and the script header names, and others that happen to hold parts of the chain.
///
/// The restore itself needed no change. RESTORE ... FROM URL matches its credential by URL prefix
/// and BlobUrl is already absolute, so a chain spanning containers restores correctly as long as
/// each container has a credential on the instance. That last clause is the whole of the risk, and
/// most of what is tested here.
/// </summary>
public class MultiContainerRestoreTests
{
    private static readonly DateTime T0 = new(2026, 8, 7, 22, 0, 0);

    private static BlobContainerConfig Container(string id, string name) => new()
    {
        Id = id,
        Name = name,
        ContainerUrl = $"https://acct.blob.core.windows.net/{name}"
    };

    private static BackupFileInfo File_(string container, string database, string name, BackupType type) => new()
    {
        BlobName = $"{database}/{name}",
        BlobUrl = $"https://acct.blob.core.windows.net/{container}/{database}/{name}",
        ETag = $"\"{container}-{name}\"",
        Type = type,
        InferredDatabaseName = database,
        InferredServerName = "SRV01",
        LastModified = new DateTimeOffset(T0, TimeSpan.Zero)
    };

    /// <summary>Fulls in "archive", logs in "hot" - the layout the issue describes.</summary>
    private static FakeBlobStorageService SplitAcrossContainers() => new()
    {
        FilesByContainer =
        {
            ["archive"] = [File_("archive", "Sales", "Sales_20260807_220000.bak", BackupType.Full)],
            ["hot"] = [File_("hot", "Sales", "Sales_20260807_223000.trn", BackupType.TransactionLog)]
        }
    };

    // ── loading ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task EveryChosenContainerIsRead()
    {
        var blob = SplitAcrossContainers();
        var vm = new BackupInventoryViewModel(blob, new FakeSqlServerService(), TestLogs.Temp(), TestAuditStores.Temp());

        await vm.LoadAsync(BackupLocation.Blob([Container("c1", "archive"), Container("c2", "hot")]));

        Assert.Equal(2, vm.AllSets.Count);
    }

    /// <summary>
    /// Every file knows which container it came from. This is what the credential check reads, and
    /// without it a chain spanning two containers has no way to say which one is missing a
    /// credential.
    /// </summary>
    [Fact]
    public async Task EveryFileKnowsWhichContainerItCameFrom()
    {
        var blob = SplitAcrossContainers();
        var vm = new BackupInventoryViewModel(blob, new FakeSqlServerService(), TestLogs.Temp(), TestAuditStores.Temp());

        await vm.LoadAsync(BackupLocation.Blob([Container("c1", "archive"), Container("c2", "hot")]));

        var files = vm.AllSets.SelectMany(s => s.Files).ToList();

        Assert.Equal(2, files.Count);
        Assert.All(files, f => Assert.False(string.IsNullOrEmpty(f.ContainerId)));
        Assert.Equal(["c1", "c2"], files.Select(f => f.ContainerId!).OrderBy(x => x));
    }

    /// <summary>One container behaves exactly as it always did.</summary>
    [Fact]
    public async Task OneContainerIsUnchanged()
    {
        var blob = SplitAcrossContainers();
        var vm = new BackupInventoryViewModel(blob, new FakeSqlServerService(), TestLogs.Temp(), TestAuditStores.Temp());

        await vm.LoadAsync(BackupLocation.Blob(Container("c1", "archive")));

        Assert.Single(vm.AllSets);
    }

    // ── the location itself ─────────────────────────────────────────────────────

    /// <summary>The first is the primary - what the credential panel and script header use.</summary>
    [Fact]
    public void TheFirstContainerIsThePrimary()
    {
        var location = BackupLocation.Blob([Container("c1", "archive"), Container("c2", "hot")]);

        Assert.Equal("c1", location.Container?.Id);
        Assert.Equal(2, location.Containers.Count);
    }

    /// <summary>
    /// A chain built while a second container was also being read is not a chain from the primary
    /// alone. Treating them as the same place is exactly the assumption #112 was about.
    /// </summary>
    [Fact]
    public void ReadingASecondContainerMakesItADifferentPlace()
    {
        var one = BackupLocation.Blob(Container("c1", "archive"));
        var two = BackupLocation.Blob([Container("c1", "archive"), Container("c2", "hot")]);

        Assert.False(one.SamePlaceAs(two));
        Assert.True(one.SamePlaceAs(BackupLocation.Blob(Container("c1", "archive"))));
    }

    [Fact]
    public void SeveralContainersSaySoWhenNamingThemselves()
    {
        var location = BackupLocation.Blob([Container("c1", "archive"), Container("c2", "hot")]);

        Assert.Contains("archive", location.Describe());
        Assert.Contains("other container", location.Describe());
    }

    // ── what a load will read ───────────────────────────────────────────────────

    /// <summary>The primary is never offered as an addition to itself.</summary>
    [Fact]
    public void ThePrimaryIsNotOfferedAsAnExtra()
    {
        var vm = Restore(out _);

        Assert.DoesNotContain(vm.AdditionalContainers, c => c.Container.Id == vm.SelectedContainer?.Id);
    }

    [Fact]
    public void TickingAContainerAddsItToWhatWillBeRead()
    {
        var vm = Restore(out _);
        Assert.Single(vm.ContainersToRead);

        vm.AdditionalContainers.Single(c => c.Name == "hot").IsSelected = true;

        Assert.Equal(2, vm.ContainersToRead.Count);
        Assert.True(vm.ReadsSeveralContainers);
        Assert.Equal(["archive", "hot"], vm.ContainersToRead.Select(c => c.Name));
    }

    /// <summary>
    /// The primary stays first however the extras are ticked. Everything anchored to one container
    /// - the credential panel, the script header, a copied path - reads that first entry.
    /// </summary>
    [Fact]
    public void ThePrimaryStaysFirst()
    {
        var vm = Restore(out _);
        vm.AdditionalContainers.Single(c => c.Name == "hot").IsSelected = true;

        Assert.Equal(vm.SelectedContainer?.Id, vm.ContainersToRead[0].Id);
    }

    /// <summary>
    /// What is on screen came from the previous set of containers. Leaving a chain there while the
    /// sources under it changed is the mistake that let Execute stay armed against URLs that were
    /// no longer being read.
    /// </summary>
    [Fact]
    public void TickingAContainerClearsWhatWasLoaded()
    {
        var vm = Restore(out _);
        vm.Inventory.BackupsLoaded = true;

        vm.AdditionalContainers.Single(c => c.Name == "hot").IsSelected = true;

        Assert.False(vm.Inventory.BackupsLoaded);
    }

    // ── the mode gate ───────────────────────────────────────────────────────────

    /// <summary>
    /// Every mode. It used to be Pro-only, until the ruling that modes narrow which SCREENS
    /// exist, never which restore options do - and a chain split across containers is a fact
    /// about the backups, not about the person restoring them.
    /// </summary>
    [Theory]
    [InlineData(AppMode.Basic)]
    [InlineData(AppMode.Standard)]
    [InlineData(AppMode.Pro)]
    public void EveryModeOffersIt(AppMode mode)
    {
        var vm = Restore(out _);
        vm.Mode = mode;

        Assert.True(vm.ShowMultipleContainers);
    }

    /// <summary>A tick list with nothing in it is worse than no tick list.</summary>
    [Fact]
    public void WithOnlyOneContainerThereIsNothingToOffer()
    {
        var store = new FakeCredentialStore();
        store.Config.BlobContainers.Add(Container("c1", "archive"));

        var vm = Restore(store);
        vm.Mode = AppMode.Pro;

        Assert.Empty(vm.AdditionalContainers);
        Assert.False(vm.ShowMultipleContainers);
    }

    private static RestoreViewModel Restore(out FakeCredentialStore store)
    {
        store = new FakeCredentialStore();
        store.Config.BlobContainers.Add(Container("c1", "archive"));
        store.Config.BlobContainers.Add(Container("c2", "hot"));

        return Restore(store);
    }

    private static RestoreViewModel Restore(FakeCredentialStore store)
    {
        var vm = new RestoreViewModel(
            new FakeBlobStorageService(), new FakeSqlServerService(), new BackupChainBuilder(),
            new RestoreScriptGenerator(), store, TestLogs.Temp(),
            new FakeOperationHistoryStore(), TestAuditStores.Temp())
        {
            Mode = AppMode.Pro
        };

        vm.RefreshContainers();
        return vm;
    }
}
