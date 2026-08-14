using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Blackcat.NineLives.ViewModels;
using Blackcat.NineLives.Views;
using Xunit;

namespace Blackcat.NineLives.Tests;

/// <summary>
/// The restore summary stays on screen while the rest scrolls.
///
/// "THIS RESTORE WILL ..." is the one sentence saying what is about to happen to a database. It was
/// moved out of the accordion in #117 because it was invisible whenever another step was open - and
/// it was then invisible whenever anybody scrolled, which on this screen is constantly: the four
/// steps run well past a screen height, so the sentence describing the restore was off-screen at
/// exactly the moment somebody was setting it up.
///
/// The test is structural rather than visual, because that is what can actually be checked: the
/// banner must not be inside the thing that scrolls.
/// </summary>
[Collection(WpfCollection.Name)]
public class RestoreSummaryStaysPutTests(WpfFixture wpf)
{
    [Fact]
    public void TheSummaryIsNotInsideTheScroller()
    {
        wpf.Invoke(() =>
        {
            var view = Realised();

            var summary = FindSummaryBanner(view);
            Assert.NotNull(summary);

            Assert.False(HasAncestorOfType<ScrollViewer>(summary),
                "the summary scrolls away with the content it is describing");
        });
    }

    /// <summary>
    /// And the steps still scroll. Pinning everything would be the same bug the other way round -
    /// four steps in a fixed panel simply do not fit.
    /// </summary>
    [Fact]
    public void TheStepsStillScroll()
    {
        wpf.Invoke(() =>
        {
            var view = Realised();

            var stepHeader = FindAll<TextBlock>(view)
                .FirstOrDefault(t => t.Text.Contains("Select a backup source", StringComparison.Ordinal));

            Assert.NotNull(stepHeader);
            Assert.True(HasAncestorOfType<ScrollViewer>(stepHeader),
                "the page content has to scroll - the steps do not fit a screen");
        });
    }

    /// <summary>
    /// Docked rather than overlaid, so it never covers the controls underneath. A banner floating
    /// on top of the first step would hide the container dropdown at exactly the wrong moment.
    /// </summary>
    [Fact]
    public void TheSummaryDoesNotOverlapTheContent()
    {
        wpf.Invoke(() =>
        {
            var view = Realised();

            var summary = FindSummaryBanner(view)!;
            var scroller = FindAll<ScrollViewer>(view).First();

            var summaryBottom = summary.TranslatePoint(new Point(0, summary.ActualHeight), view).Y;
            var scrollerTop = scroller.TranslatePoint(new Point(0, 0), view).Y;

            Assert.True(summaryBottom <= scrollerTop + 1,
                $"the summary ends at {summaryBottom} and the content starts at {scrollerTop}");
        });
    }

    // ── helpers ─────────────────────────────────────────────────────────────────

    private static FrameworkElement Realised()
    {
        var store = new FakeCredentialStore();
        var vm = new RestoreViewModel(
            new FakeBlobStorageService(), new FakeSqlServerService(),
            new Blackcat.NineLives.Services.BackupChainBuilder(),
            new Blackcat.NineLives.Services.RestoreScriptGenerator(),
            store, TestLogs.Temp(), new FakeOperationHistoryStore(), TestAuditStores.Temp())
        {
            // The banner is collapsed with nothing to say, and a collapsed element is not somewhere
            // a person can fail to see - so it needs something to say before any of this means
            // anything. A first attempt at these tests measured the collapsed one and proved
            // nothing.
            RestoreSummaryText = "Restore 'MyDb' as 'MyDb_Restored' using 1 Full + 2 Log(s)."
        };

        var view = new RestoreView { DataContext = vm };
        view.ApplyTemplate();
        view.Measure(new Size(1280, 900));
        view.Arrange(new Rect(0, 0, 1280, 900));
        view.UpdateLayout();

        return view;
    }

    /// <summary>
    /// The banner itself, not its label - the label is a few pixels tall wherever it sits, so
    /// measuring it would say nothing about where the banner is.
    /// </summary>
    private static FrameworkElement? FindSummaryBanner(DependencyObject root)
    {
        var label = FindAll<TextBlock>(root).FirstOrDefault(t => t.Text == "THIS RESTORE WILL");
        if (label == null) return null;

        for (DependencyObject? node = label; node != null; node = VisualTreeHelper.GetParent(node))
            if (node is Border border) return border;

        return null;
    }

    private static bool HasAncestorOfType<T>(DependencyObject node) where T : DependencyObject
    {
        for (var current = VisualTreeHelper.GetParent(node); current != null;
             current = VisualTreeHelper.GetParent(current))
        {
            if (current is T) return true;
        }
        return false;
    }

    private static IEnumerable<T> FindAll<T>(DependencyObject root) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var descendant in FindAll<T>(child)) yield return descendant;
        }
    }
}
