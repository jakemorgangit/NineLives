using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Blackcat.NineLives.Models;
using Blackcat.NineLives.ViewModels;
using Blackcat.NineLives.Views;
using Xunit;

namespace Blackcat.NineLives.Tests;

/// <summary>
/// The saved-containers list stays legible while the Add Container form is open.
///
/// It is disabled mid-edit, deliberately (#354) - the form writes to whichever container is
/// selected, so clicking another one while editing used to save this container's details over
/// that one. What nobody looked at afterwards was what "disabled" makes a ListBox LOOK like.
///
/// WPF's default ListBox template swaps its border background to SystemColors.ControlBrushKey the
/// moment IsEnabled goes false, and a template trigger beats a Background set on the control - so
/// the explicit Background="Transparent" lost. The panel turned WHITE in every theme, and the rows
/// draw their names in PrimaryTextBrush, which is white in the dark and high-contrast themes.
/// White on white. Reported from the high-contrast theme, where it is unmissable, but it was
/// wrong everywhere.
///
/// Being unavailable for a moment is not a reason to stop being readable, so the rule pinned here
/// is the general one: disabling this list must not RECOLOUR it. Dimming it is fine and is how
/// the state is now carried.
/// </summary>
[Collection(WpfCollection.Name)]
public class DisabledListStaysLegibleTests(WpfFixture wpf)
{
    [Fact]
    public void DisablingTheContainerListDoesNotRecolourIt()
    {
        wpf.Invoke(() =>
        {
            var vm = Screen();
            var view = new BlobConfigView { DataContext = vm };
            Layout(view);

            var list = ContainerList(view, vm);
            Assert.True(list.IsEnabled, "the list should start selectable");

            var enabled = BackgroundOfChrome(list);

            // Opening Add Container is what disables it.
            vm.AddNewCommand.Execute(null);
            Layout(view);

            Assert.False(list.IsEnabled, "opening the form should stop the list being selectable");

            var disabled = BackgroundOfChrome(list);

            Assert.Equal(Describe(enabled), Describe(disabled));
        });
    }

    /// <summary>
    /// And specifically not the system colour, which is the exact brush the default template
    /// reaches for and the one that is white under a dark theme.
    /// </summary>
    [Fact]
    public void TheDisabledListIsNotPaintedInASystemColour()
    {
        wpf.Invoke(() =>
        {
            var vm = Screen();
            var view = new BlobConfigView { DataContext = vm };
            vm.AddNewCommand.Execute(null);
            Layout(view);

            var list = ContainerList(view, vm);
            Assert.False(list.IsEnabled);

            var chrome = BackgroundOfChrome(list);

            Assert.NotEqual(Describe(SystemColors.ControlBrush), Describe(chrome));
            Assert.NotEqual(Describe(SystemColors.WindowBrush), Describe(chrome));
        });
    }

    // ── helpers ─────────────────────────────────────────────────────────────────

    private static BlobConfigViewModel Screen()
    {
        var store = new FakeCredentialStore();
        store.Config.BlobContainers.Add(new BlobContainerConfig
        {
            Id = BlobContainerConfig.NewId(),
            Name = "prod-backups",
            ContainerUrl = "https://acct.blob.core.windows.net/prod-backups"
        });

        var vm = new BlobConfigViewModel(store, new FakeBlobStorageService());
        vm.SelectedContainer = vm.Containers.FirstOrDefault();
        return vm;
    }

    /// <summary>
    /// The saved-containers list specifically. Matched on what it is bound to rather than by
    /// type - this screen has other ListBoxes in it, inside the combo boxes' own templates.
    /// </summary>
    private static ListBox ContainerList(DependencyObject view, BlobConfigViewModel vm) =>
        FindAll<ListBox>(view).Single(l => ReferenceEquals(l.ItemsSource, vm.Containers));

    /// <summary>The Border the list's own template paints - the one that used to go white.</summary>
    private static Brush? BackgroundOfChrome(ListBox list) =>
        FindAll<Border>(list).FirstOrDefault()?.Background;

    /// <summary>
    /// Brushes are compared by their painted result rather than by reference: a DynamicResource
    /// resolves to a different instance each time the theme is applied, so Assert.Same would fail
    /// on two brushes that are the same colour.
    /// </summary>
    private static string Describe(Brush? brush) => brush switch
    {
        null => "(null)",
        SolidColorBrush solid => solid.Color.ToString(),
        _ => brush.GetType().Name
    };

    private static void Layout(FrameworkElement element)
    {
        element.Measure(new Size(1280, 900));
        element.Arrange(new Rect(0, 0, 1280, 900));
        element.UpdateLayout();
    }

    private static IEnumerable<T> FindAll<T>(DependencyObject root) where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var node = VisualTreeHelper.GetChild(root, i);
            if (node is T match) yield return match;
            foreach (var descendant in FindAll<T>(node)) yield return descendant;
        }
    }
}
