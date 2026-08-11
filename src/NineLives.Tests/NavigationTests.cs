using System.Windows.Controls.Primitives;
using Blackcat.NineLives.Models;
using Blackcat.NineLives.ViewModels;
using Xunit;

namespace Blackcat.NineLives.Tests;

/// <summary>
/// The sidebar (#42).
///
/// Navigation is by string, and an unrecognised name falls back to Blob Storage rather than
/// failing - so a typo in the markup or a name missing from the switch shows up as a button that
/// quietly goes to the wrong screen. Nothing throws and nothing logs.
/// </summary>
[Collection(WpfCollection.Name)]
public class NavigationTests(WpfFixture wpf)
{
    [Fact]
    public void EveryNameInTheListReachesItsOwnView()
    {
        // Past the cards first (#369): navigation is refused behind them, so a MainViewModel
        // straight from the constructor cannot navigate anywhere. Pro, because this walks every
        // name and the narrower modes hide three of them.
        var vm = Launched.App(AppMode.Pro);
        var reached = new List<object>();

        foreach (var name in MainViewModel.Nav.Views)
        {
            vm.NavigateToCommand.Execute(name);
            Assert.NotNull(vm.CurrentView);
            Assert.Equal(name, vm.CurrentViewName);
            reached.Add(vm.CurrentView!);
        }

        // Distinct, so a name missing from the switch - which silently lands on Blob Storage -
        // cannot pass by looking like a successful navigation.
        Assert.Equal(MainViewModel.Nav.Views.Count, reached.Distinct().Count());
    }

    [Fact]
    public void AnUnknownNameFallsBackRatherThanCrashing()
    {
        var vm = Launched.App(AppMode.Pro);
        vm.NavigateToCommand.Execute(MainViewModel.Nav.History);

        vm.NavigateToCommand.Execute("Not A View");

        // Landed somewhere real rather than throwing - and specifically NOT left on the screen
        // it was already on, which is what "NotNull" alone would have accepted once navigation
        // learned to refuse things (#369).
        Assert.NotNull(vm.CurrentView);
        Assert.NotSame(vm.History, vm.CurrentView);
    }

    /// <summary>
    /// Every navigation button in the window passes a name the switch actually knows. This is the
    /// half that a ViewModel test cannot reach: the strings live in XAML, and a mistyped one is
    /// invisible at build time and silent at runtime.
    /// </summary>
    [Fact]
    public void EverySidebarButtonPassesAKnownName()
    {
        wpf.Invoke(() =>
        {
            var window = new MainWindow(new MainViewModel(new FakeCredentialStore()));
            window.ApplyTemplate();
            window.Measure(new System.Windows.Size(1600, 1200));
            window.Arrange(new System.Windows.Rect(0, 0, 1600, 1200));
            window.UpdateLayout();

            var all = FindAll<ButtonBase>(window).ToList();

            // Matched by CommandParameter rather than by Command. The window has never been shown,
            // so its bindings have not been evaluated and every Command is still null - but a
            // CommandParameter is a literal set at parse time, and it is the half that can be
            // mistyped. Nothing else in this window passes a string parameter.
            var parameters = all
                .Select(b => b.CommandParameter as string)
                .Where(p => p != null)
                .ToList();

            Assert.True(parameters.Count > 0,
                $"No navigation buttons found. ButtonBase elements in the tree: {all.Count}.");
            Assert.All(parameters, p => Assert.Contains(p, MainViewModel.Nav.Views));

            // And every view is actually reachable from the sidebar, so adding one to the switch
            // without adding a button does not go unnoticed either.
            Assert.Equal(
                MainViewModel.Nav.Views.OrderBy(n => n),
                parameters.Distinct().OrderBy(n => n));
        });
    }

    /// <summary>
    /// Walks the LOGICAL tree, not the visual one.
    ///
    /// A Window that has never been shown has no visual tree at all - the content is parsed and
    /// the elements exist, but nothing has been rendered, so VisualTreeHelper finds precisely
    /// nothing. The logical tree is built at parse time and is what these buttons live in.
    /// </summary>
    private static IEnumerable<T> FindAll<T>(System.Windows.DependencyObject root)
        where T : System.Windows.DependencyObject
    {
        foreach (var child in System.Windows.LogicalTreeHelper.GetChildren(root))
        {
            if (child is not System.Windows.DependencyObject node) continue;
            if (node is T match) yield return match;
            foreach (var descendant in FindAll<T>(node)) yield return descendant;
        }
    }
}
