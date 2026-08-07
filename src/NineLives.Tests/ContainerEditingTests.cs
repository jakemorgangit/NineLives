using Blackcat.NineLives.Models;
using Blackcat.NineLives.Services;
using Blackcat.NineLives.ViewModels;
using Xunit;

namespace Blackcat.NineLives.Tests;

/// <summary>
/// Editing a saved container without losing anything that was already on it.
/// </summary>
public class ContainerEditingTests
{
    private readonly FakeCredentialStore _store = new();

    private BlobConfigViewModel NewViewModel()
        => new(_store, new FakeBlobStorageService());

    private BlobContainerConfig Tagged()
    {
        var container = new BlobContainerConfig
        {
            Id = BlobContainerConfig.NewId(),
            Name = "backups",
            ContainerUrl = "https://mystorageaccount.blob.core.windows.net/backups",
            PathPattern = "{BackupType}/{ServerName}/{DatabaseName}/{FileName}"
        };
        container.Tags.Add("prod");
        container.Tags.Add("uk-south");

        _store.Config.BlobContainers.Add(container);
        return container;
    }

    /// <summary>
    /// Refresh Token is a separate button from Edit, so replacing an expired SAS is the natural
    /// way to reach it - and it repopulated every field of the edit form EXCEPT the tags, which
    /// Save then wrote back as empty. Silent, persisted, no undo.
    /// </summary>
    [Fact]
    public void RefreshingTheTokenKeepsTheTags()
    {
        var container = Tagged();
        var vm = NewViewModel();
        vm.SelectedContainer = vm.Containers.Single();

        vm.RefreshTokenCommand.Execute(null);
        vm.EditSasToken = "sv=2026-01-01&sig=replacement";
        vm.SaveCommand.Execute(null);

        var saved = Assert.Single(_store.Config.BlobContainers);
        Assert.Equal(["prod", "uk-south"], saved.Tags.OrderBy(t => t));
    }

    [Fact]
    public void RefreshingTheTokenShowsTheExistingTagsInTheBox()
    {
        Tagged();
        var vm = NewViewModel();
        vm.SelectedContainer = vm.Containers.Single();

        vm.RefreshTokenCommand.Execute(null);

        Assert.Contains("prod", vm.EditTags);
        Assert.Contains("uk-south", vm.EditTags);
    }

    [Fact]
    public void ChangingOnlyTheTagsCountsAsAnUnsavedChange()
    {
        Tagged();
        var vm = NewViewModel();
        vm.SelectedContainer = vm.Containers.Single();
        vm.EditCommand.Execute(null);

        Assert.False(vm.HasUnsavedChanges);

        vm.EditTags = "prod, uk-south, restored-here";

        Assert.True(vm.HasUnsavedChanges);
    }

    [Fact]
    public void EditingTheTagsSavesThem()
    {
        Tagged();
        var vm = NewViewModel();
        vm.SelectedContainer = vm.Containers.Single();
        vm.EditCommand.Execute(null);

        vm.EditTags = "staging";
        vm.SaveCommand.Execute(null);

        var saved = Assert.Single(_store.Config.BlobContainers);
        Assert.Equal(["staging"], saved.Tags);
    }
}
