using Blackcat.NineLives.Models;
using Blackcat.NineLives.Services;
using Blackcat.NineLives.ViewModels;
using Xunit;

namespace Blackcat.NineLives.Tests;

/// <summary>
/// From looking to restoring in one move (#202).
///
/// The browser answers "what do I actually have?", and until now it stopped there: having found
/// the exact backup that matters, the only way to restore it was the restore screen and the whole
/// selection again by hand - same source, same server, same database. That is the app repeating a
/// question it already asked.
/// </summary>
public class BrowseToRestoreHandoffTests
{
    private static readonly DateTime T0 = new(2026, 8, 7, 22, 0, 0);

    private static ServerConnection Server(string name = "SRV01") =>
        new() { Id = ServerConnection.NewId(), Name = name, ServerName = name };

    private static BlobContainerConfig Container() => new()
    {
        Id = "c1",
        Name = "backups",
        ContainerUrl = "https://acct.blob.core.windows.net/backups"
    };

    private static BackupFileInfo File_(string database, string name) => new()
    {
        BlobName = $"FULL/SRV01/{database}/{name}",
        BlobUrl = $"https://acct.blob.core.windows.net/backups/FULL/SRV01/{database}/{name}",
        Type = BackupType.Full,
        InferredDatabaseName = database,
        InferredServerName = "SRV01",
        LastModified = new DateTimeOffset(T0, TimeSpan.Zero)
    };

    // ── what the browser hands over ─────────────────────────────────────────────

    /// <summary>The browser on a container hands over the container and the database.</summary>
    [Fact]
    public async Task ABlobBrowseHandsOverItsContainerAndDatabase()
    {
        var store = new FakeCredentialStore();
        store.Config.BlobContainers.Add(Container());

        var blob = new FakeBlobStorageService { Files = [File_("Sales", "Sales_20260807_220000.bak")] };
        var vm = new BlobBrowserViewModel(blob, new FakeSqlServerService(), store);

        await vm.LoadBackupsCommand.ExecuteAsync(null);
        vm.SelectedDatabase = "Sales";

        BrowseHandoff? handoff = null;
        vm.RestoreRequested += h => handoff = h;

        vm.RestoreFromHereCommand.Execute(null);

        Assert.NotNull(handoff);
        Assert.Equal(BackupMedium.AzureBlob, handoff!.Medium);
        Assert.Equal("c1", handoff.Container?.Id);
        Assert.Equal("Sales", handoff.Database);
    }

    /// <summary>A row's context menu names its own database, whatever the filter says.</summary>
    [Fact]
    public async Task ARowHandsOverItsOwnDatabase()
    {
        var store = new FakeCredentialStore();
        store.Config.BlobContainers.Add(Container());

        var blob = new FakeBlobStorageService
        {
            Files = [File_("Sales", "a.bak"), File_("Payroll", "b.bak")]
        };
        var vm = new BlobBrowserViewModel(blob, new FakeSqlServerService(), store);
        await vm.LoadBackupsCommand.ExecuteAsync(null);

        BrowseHandoff? handoff = null;
        vm.RestoreRequested += h => handoff = h;

        vm.RestoreFromHereCommand.Execute("Payroll");

        Assert.Equal("Payroll", handoff?.Database);
    }

    /// <summary>With nothing chosen there is nothing to hand over, and nothing fires.</summary>
    [Fact]
    public void NothingChosenMeansNothingFires()
    {
        var store = new FakeCredentialStore();
        var vm = new BlobBrowserViewModel(
            new FakeBlobStorageService(), new FakeSqlServerService(), store);

        var fired = false;
        vm.RestoreRequested += _ => fired = true;

        vm.RestoreFromHereCommand.Execute(null);

        Assert.False(fired);
        Assert.False(vm.CanRestoreFromHere);
    }

    // ── what the restore screen does with it ────────────────────────────────────

    private static RestoreViewModel Restore(FakeCredentialStore store, FakeBlobStorageService blob)
    {
        var vm = new RestoreViewModel(
            blob, new FakeSqlServerService(), new BackupChainBuilder(),
            new RestoreScriptGenerator(), store, TestLogs.Temp(),
            new FakeOperationHistoryStore(), TestAuditStores.Temp())
        {
            Mode = AppMode.Pro
        };
        vm.RefreshContainers();
        return vm;
    }

    /// <summary>
    /// The whole point: source selected, backups loaded, database landed - the only thing left is
    /// the restore point.
    /// </summary>
    [Fact]
    public async Task TheRestoreScreenArrivesLoadedAndPointedAtTheDatabase()
    {
        var store = new FakeCredentialStore();
        store.Config.BlobContainers.Add(Container());

        var blob = new FakeBlobStorageService
        {
            Files = [File_("Sales", "Sales_20260807_220000.bak"), File_("Payroll", "b.bak")]
        };
        var vm = Restore(store, blob);

        await vm.AcceptHandoffAsync(new BrowseHandoff(
            BackupMedium.AzureBlob, Container(), null, "SRV01", "Sales"));

        Assert.Equal(BackupMedium.AzureBlob, vm.SelectedMedium);
        Assert.Equal("c1", vm.SelectedContainer?.Id);
        Assert.True(vm.Inventory.BackupsLoaded);
        Assert.Equal("Sales", vm.Inventory.SelectedDatabaseName);
    }

    /// <summary>
    /// A database the load did not find is not selected - forcing it would put the working set
    /// into a state the screen cannot reach by hand.
    /// </summary>
    [Fact]
    public async Task ADatabaseTheLoadDidNotFindIsNotForced()
    {
        var store = new FakeCredentialStore();
        store.Config.BlobContainers.Add(Container());

        var blob = new FakeBlobStorageService { Files = [File_("Sales", "a.bak")] };
        var vm = Restore(store, blob);

        await vm.AcceptHandoffAsync(new BrowseHandoff(
            BackupMedium.AzureBlob, Container(), null, null, "Ghost"));

        Assert.True(vm.Inventory.BackupsLoaded);
        Assert.Null(vm.Inventory.SelectedDatabaseName);
    }

    /// <summary>
    /// The shell's half of the contract: the browser's command lands the app on the restore
    /// screen with the handoff's source selected. The load itself fails against the fake
    /// container's URL, which is fine - navigation and selection are what the shell owes.
    /// </summary>
    [Fact]
    public void TheShellLandsOnTheRestoreScreenWithTheSourceSelected()
    {
        var store = new FakeCredentialStore();
        store.Config.Mode = AppMode.Pro;
        store.Config.BlobContainers.Add(Container());

        var main = new MainViewModel(store);
        main.ModeSelection.CancelCommand.Execute(null);
        main.NavigateToCommand.Execute(MainViewModel.Nav.BrowseBackups);

        // The browser's state is the command's input, and both pieces are settable.
        main.BlobBrowser.HasFiles = true;
        main.BlobBrowser.SelectedDatabase = "Sales";

        main.BlobBrowser.RestoreFromHereCommand.Execute(null);

        Assert.Same(main.Restore, main.CurrentView);
        Assert.Equal("c1", main.Restore.SelectedContainer?.Id);
        Assert.Equal(BackupMedium.AzureBlob, main.Restore.SelectedMedium);
    }
}
