using Blackcat.NineLives.Models;
using Blackcat.NineLives.Services;
using Blackcat.NineLives.ViewModels;
using Xunit;

namespace Blackcat.NineLives.Tests;

/// <summary>
/// Config first, secret second (#113).
///
/// The other order wrote a durable secret to Windows Credential Manager and only then found the
/// config could not be saved - so the vault held the new token or password while config.json still
/// held the old name, url or username. Nothing on screen shows a stored secret, so the mismatch is
/// invisible: connections just start failing.
///
/// config.json being briefly locked is the ordinary case here - antivirus, a backup agent, a sync
/// client mid-upload. It is the same condition that caused #7.
/// </summary>
public class SaveOrderingTests
{
    private static InvalidOperationException Locked()
        => new("config.json is in use by another process");

    // ── containers ──────────────────────────────────────────────────────────────

    private static (BlobConfigViewModel vm, FakeCredentialStore store) NewContainerVm()
    {
        var store = new FakeCredentialStore();
        return (new BlobConfigViewModel(store, new FakeBlobStorageService()), store);
    }

    [Fact]
    public void ARefusedSaveDoesNotStoreTheSasToken()
    {
        var (vm, store) = NewContainerVm();
        store.SaveConfigThrows = Locked();

        vm.AddNewCommand.Execute(null);
        vm.EditName = "backups";
        vm.EditContainerUrl = "https://mystorageaccount.blob.core.windows.net/backups";
        vm.EditSasToken = "sv=2026-01-01&sig=never-persisted";
        vm.SaveCommand.Execute(null);

        Assert.True(vm.HasError);
        Assert.Empty(store.ListCredentialKeys("NineLives:Blob:"));
    }

    [Fact]
    public void ARefusedSaveLeavesTheContainerAsItWas()
    {
        var (vm, store) = NewContainerVm();

        var existing = new BlobContainerConfig
        {
            Id = BlobContainerConfig.NewId(),
            Name = "backups",
            ContainerUrl = "https://mystorageaccount.blob.core.windows.net/backups"
        };
        existing.Tags.Add("prod");
        store.Config.BlobContainers.Add(existing);

        vm = new BlobConfigViewModel(store, new FakeBlobStorageService());
        vm.SelectedContainer = vm.Containers.Single();
        vm.EditCommand.Execute(null);

        store.SaveConfigThrows = Locked();

        vm.EditName = "renamed";
        vm.EditTags = "staging";
        vm.SaveCommand.Execute(null);

        // The in-memory container must not keep values that never reached the disk - the next save
        // would write them without the user knowing they were pending.
        var container = vm.Containers.Single();
        Assert.Equal("backups", container.Name);
        Assert.Equal(["prod"], container.Tags);
    }

    [Fact]
    public void ASuccessfulSaveStillStoresTheToken()
    {
        var (vm, store) = NewContainerVm();

        vm.AddNewCommand.Execute(null);
        vm.EditName = "backups";
        vm.EditContainerUrl = "https://mystorageaccount.blob.core.windows.net/backups";
        vm.EditSasToken = "sv=2026-01-01&sig=persisted";
        vm.SaveCommand.Execute(null);

        Assert.False(vm.HasError);
        var saved = Assert.Single(store.Config.BlobContainers);
        Assert.Equal("sv=2026-01-01&sig=persisted", store.GetSasToken(saved));
    }

    // ── servers ─────────────────────────────────────────────────────────────────

    private static (ServerManagerViewModel vm, FakeCredentialStore store) NewServerVm()
    {
        var store = new FakeCredentialStore();
        return (new ServerManagerViewModel(store, new FakeSqlServerService()), store);
    }

    [Fact]
    public void ARefusedSaveDoesNotStoreTheSqlPassword()
    {
        var (vm, store) = NewServerVm();
        store.SaveConfigThrows = Locked();

        vm.AddNewCommand.Execute(null);
        vm.EditName = "SRV01";
        vm.EditServerName = "SRV01";
        vm.EditAuthMode = AuthMode.SqlAuth;
        vm.EditUsername = "restoreadmin";
        vm.EditPassword = "never-persisted";
        vm.SaveCommand.Execute(null);

        Assert.True(vm.HasError);
        Assert.Empty(store.ListCredentialKeys("NineLives:SQL:"));
    }

    /// <summary>
    /// The shape that actually bites: username and password changed together. With the old
    /// ordering the vault took the new password and the file kept the old username, so every
    /// connection failed authentication with nothing on screen to explain it.
    /// </summary>
    [Fact]
    public void ARefusedSaveLeavesTheServerAsItWas()
    {
        var store = new FakeCredentialStore();
        var existing = new ServerConnection
        {
            Id = ServerConnection.NewId(),
            Name = "SRV01",
            ServerName = "SRV01",
            AuthMode = AuthMode.SqlAuth,
            Username = "olduser"
        };
        store.Config.Servers.Add(existing);
        store.SaveSqlPassword(existing, "oldpassword");

        var vm = new ServerManagerViewModel(store, new FakeSqlServerService());
        vm.SelectedServer = vm.Servers.Single();
        vm.EditCommand.Execute(null);

        store.SaveConfigThrows = Locked();

        vm.EditUsername = "newuser";
        vm.EditPassword = "newpassword";
        vm.SaveCommand.Execute(null);

        Assert.True(vm.HasError);

        var server = vm.Servers.Single();
        Assert.Equal("olduser", server.Username);
        Assert.Equal("oldpassword", store.GetSqlPassword(server));
    }
}
