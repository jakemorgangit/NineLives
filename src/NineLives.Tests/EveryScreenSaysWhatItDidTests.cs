using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Blackcat.NineLives.ViewModels;
using Blackcat.NineLives.Views;
using Xunit;

namespace Blackcat.NineLives.Tests;

/// <summary>
/// A screen that reports a failure also reports a success (#404).
///
/// The mirror image of #356. Four screens carried an ErrorBanner and bound no StatusMessage, so
/// their failures showed and every confirmation, instruction and consequence did not: a config
/// import told nobody what it had changed, "Enter the new SAS token below" never appeared beside
/// the box it was about, and pressing Stop on an exposure sweep looked like it had done nothing.
///
/// And History, the one view with no banner - deliberately, because SetError writes the status
/// line too so an error there is not silent - drew that error in the same grey as everything
/// else. A refusal to destroy a restore's evidence looked exactly like "Script copied to
/// clipboard".
/// </summary>
[Collection(WpfCollection.Name)]
public class EveryScreenSaysWhatItDidTests(WpfFixture wpf)
{
    private static (FrameworkElement View, ViewModelBase Vm) Screen(string name)
    {
        var store = new FakeCredentialStore();

        return name switch
        {
            "Settings" => Pair(new SettingsView(), new SettingsViewModel(store, TestLogs.Temp())),
            "Servers" => Pair(new ServerManagerView(), new ServerManagerViewModel(store, new FakeSqlServerService())),
            "Blob" => Pair(new BlobConfigView(), new BlobConfigViewModel(store, new FakeBlobStorageService())),
            "Exposure" => Pair(new ExposureView(), new ExposureViewModel(store, new FakeSqlServerService(), new FakeOperationHistoryStore())),
            "History" => Pair(new HistoryView(), new HistoryViewModel(new FakeOperationHistoryStore())),
            _ => throw new ArgumentOutOfRangeException(nameof(name))
        };

        static (FrameworkElement, ViewModelBase) Pair(FrameworkElement v, ViewModelBase vm)
        {
            v.DataContext = vm;
            return (v, vm);
        }
    }

    [Theory]
    [InlineData("Settings")]
    [InlineData("Servers")]
    [InlineData("Blob")]
    [InlineData("Exposure")]
    [InlineData("History")]
    public void AStatusMessageReachesTheScreen(string name)
    {
        wpf.Invoke(() =>
        {
            var (view, vm) = Screen(name);
            const string said = "Nine Lives status probe sentence.";

            Layout(view);
            Assert.DoesNotContain(said, Shown(view));

            vm.SetStatusForTests(said);
            Layout(view);

            Assert.Contains(said, Shown(view));
        });
    }

    /// <summary>
    /// SetError writes the status line as well as the banner, because not every screen surfaces
    /// both. On the four that surface both, the same sentence must not appear twice - once in red
    /// and once in grey reads as two separate things having gone wrong.
    /// </summary>
    [Theory]
    [InlineData("Settings")]
    [InlineData("Servers")]
    [InlineData("Blob")]
    [InlineData("Exposure")]
    public void AnErrorIsNotAlsoPrintedOnTheStatusLine(string name)
    {
        wpf.Invoke(() =>
        {
            var (view, vm) = Screen(name);
            const string wrong = "Nine Lives error probe sentence.";

            vm.SetErrorForTests(wrong);
            Layout(view);

            var appearances = Shown(view).Count(t => t == wrong);

            Assert.True(appearances == 1,
                $"{name} shows the same error {appearances} times.");
        });
    }

    /// <summary>
    /// History is the exception and stays visible - it has no banner, so collapsing its status
    /// line on error is exactly the silence the arrangement exists to prevent. It carries the
    /// error appearance instead.
    /// </summary>
    [Fact]
    public void HistoryShowsItsErrorAndMarksItAsOne()
    {
        wpf.Invoke(() =>
        {
            var vm = new HistoryViewModel(new FakeOperationHistoryStore { CouldNotRead = true });
            var view = new HistoryView { DataContext = vm };

            vm.Refresh();
            Layout(view);

            var line = FindAll<TextBlock>(view)
                .FirstOrDefault(t => t.Visibility == Visibility.Visible
                                     && t.Text.Contains("could not be read"));

            Assert.True(line != null, "History does not show its own refusal.");

            var error = (SolidColorBrush)Application.Current!.Resources["ErrorBrush"];
            Assert.Equal(error.Color, ((SolidColorBrush)line!.Foreground).Color);
        });
    }

    // ── helpers ─────────────────────────────────────────────────────────────────

    private static List<string> Shown(DependencyObject view) =>
        FindAll<TextBlock>(view)
            .Where(t => t.Visibility == Visibility.Visible)
            .Select(t => t.Text)
            .ToList();

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
