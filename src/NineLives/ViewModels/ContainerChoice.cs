using Blackcat.NineLives.Models;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Blackcat.NineLives.ViewModels;

/// <summary>
/// A container offered as an extra place to read, with a tick (#32).
///
/// A wrapper rather than a flag on <see cref="BlobContainerConfig"/> itself: the config is what
/// gets written to config.json, and "is this one currently ticked on the restore screen" is a
/// property of the screen rather than of the container. Putting it on the model would persist a
/// transient choice and leak it into every other screen that shows the same containers.
/// </summary>
public partial class ContainerChoice(BlobContainerConfig container, Action onToggled) : ObservableObject
{
    public BlobContainerConfig Container { get; } = container;

    public string Name => Container.Name;

    /// <summary>The tags, so "which of these is production" is answerable here too (#117 item 8).</summary>
    public IEnumerable<TagChip> TagChips => Container.TagChips;

    [ObservableProperty]
    private bool _isSelected;

    partial void OnIsSelectedChanged(bool value) => onToggled();
}
