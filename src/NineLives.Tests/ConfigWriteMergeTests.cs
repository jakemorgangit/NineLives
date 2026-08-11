using Blackcat.NineLives.Models;
using Blackcat.NineLives.Services;
using Blackcat.NineLives.ViewModels;
using Xunit;

namespace Blackcat.NineLives.Tests;

/// <summary>
/// Config writes merge, never replace (#276). The SQL Servers and Blob Storage screens load
/// their lists once; Settings' import and the CLI's add verbs write entries at any time after
/// that. A screen that saves its stale list wholesale silently deletes theirs - the production
/// path was import, then Connect (which records the detected version), and every imported
/// server was gone. These pins drive a save through each screen's dialog-free edit path and
/// hold the merge to its promises: unseen entries survive, and this screen's edits win for
/// the entries it knows.
/// </summary>
public class ConfigWriteMergeTests
{
    private static ServerConnection Server(string name) => new()
    { Id = ServerConnection.NewId(), Name = name, ServerName = name };

    private static BlobContainerConfig Container(string name) => new()
    {
        Id = BlobContainerConfig.NewId(),
        Name = name,
        ContainerUrl = $"https://a.blob.core.windows.net/{name}"
    };

    // ── servers ─────────────────────────────────────────────────────────────────

    [Fact]
    public void AServerAddedElsewhereSurvivesThisScreensSave()
    {
        var store = new FakeCredentialStore();
        store.Config.Servers.Add(Server("SRV01"));

        var vm = new ServerManagerViewModel(store, new FakeSqlServerService());

        // The import (or 9lives add-server) writes AFTER the screen loaded its list.
        store.Config.Servers.Add(Server("SRV02"));

        // Any save from this screen used to clobber it - a rename is the dialog-free trigger.
        vm.SelectedServer = vm.Servers.Single(s => s.Name == "SRV01");
        vm.EditCommand.Execute(null);
        vm.EditName = "SRV01-renamed";
        vm.SaveCommand.Execute(null);

        var names = store.LoadConfig().Servers.Select(s => s.Name).ToList();
        Assert.Contains("SRV01-renamed", names);
        Assert.Contains("SRV02", names);
        Assert.Equal(2, names.Count);
    }

    /// <summary>For an entry BOTH sides know, the screen actively editing it wins.</summary>
    [Fact]
    public void TheScreensEditWinsForAnEntryItKnows()
    {
        var store = new FakeCredentialStore();
        var original = Server("SRV01");
        store.Config.Servers.Add(original);

        var vm = new ServerManagerViewModel(store, new FakeSqlServerService());

        // Something else rewrote the same entry (same id) behind the screen's back.
        store.Config.Servers.Clear();
        store.Config.Servers.Add(new ServerConnection
        { Id = original.Id, Name = "SRV01", ServerName = "rewritten-elsewhere" });

        vm.SelectedServer = vm.Servers.Single();
        vm.EditCommand.Execute(null);
        vm.EditServerName = "edited-here";
        vm.SaveCommand.Execute(null);

        Assert.Equal("edited-here", store.LoadConfig().Servers.Single().ServerName);
    }

    [Fact]
    public void NavigationRefreshPicksUpServersAddedElsewhere()
    {
        var store = new FakeCredentialStore();
        store.Config.Servers.Add(Server("SRV01"));

        var vm = new ServerManagerViewModel(store, new FakeSqlServerService());
        vm.SelectedServer = vm.Servers.Single();

        store.Config.Servers.Add(Server("SRV02"));
        vm.RefreshFromConfig();

        Assert.Equal(2, vm.Servers.Count);
        // Selection survives the rebuild by id.
        Assert.Equal("SRV01", vm.SelectedServer?.Name);
    }

    /// <summary>Mid-edit, the list must NOT be rebuilt under the open editor.</summary>
    [Fact]
    public void NavigationRefreshLeavesAnOpenEditorAlone()
    {
        var store = new FakeCredentialStore();
        store.Config.Servers.Add(Server("SRV01"));

        var vm = new ServerManagerViewModel(store, new FakeSqlServerService());
        vm.SelectedServer = vm.Servers.Single();
        vm.EditCommand.Execute(null);
        Assert.True(vm.IsEditing);

        store.Config.Servers.Add(Server("SRV02"));
        vm.RefreshFromConfig();

        Assert.Single(vm.Servers);
    }

    // ── containers ──────────────────────────────────────────────────────────────

    [Fact]
    public void AContainerAddedElsewhereSurvivesThisScreensSave()
    {
        var store = new FakeCredentialStore();
        store.Config.BlobContainers.Add(Container("old"));

        var vm = new BlobConfigViewModel(store, new FakeBlobStorageService());

        store.Config.BlobContainers.Add(Container("added-elsewhere"));

        vm.SelectedContainer = vm.Containers.Single(c => c.Name == "old");
        vm.EditCommand.Execute(null);
        vm.EditName = "renamed";
        vm.SaveCommand.Execute(null);

        var names = store.LoadConfig().BlobContainers.Select(c => c.Name).ToList();
        Assert.Contains("renamed", names);
        Assert.Contains("added-elsewhere", names);
        Assert.Equal(2, names.Count);
    }

    [Fact]
    public void NavigationRefreshPicksUpContainersAddedElsewhere()
    {
        var store = new FakeCredentialStore();
        store.Config.BlobContainers.Add(Container("one"));

        var vm = new BlobConfigViewModel(store, new FakeBlobStorageService());
        store.Config.BlobContainers.Add(Container("two"));

        vm.RefreshFromConfig();

        Assert.Equal(2, vm.Containers.Count);
    }
}
