using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Blackcat.NineLives.Models;
using Blackcat.NineLives.Services;
using Blackcat.NineLives.ViewModels;
using Blackcat.NineLives.Views;
using Xunit;

namespace Blackcat.NineLives.Tests;

/// <summary>
/// The gap check can be found (#466).
///
/// It shipped in v1.7.0 inside the restore options - which is to say behind a step that only
/// appears once a restore point has been confirmed, and which starts collapsed. A feature whose
/// entire purpose is telling somebody the chain is shorter than they think cannot require them to
/// already suspect it: that is the same fault as the bug it exists to fix. It was reported as
/// missing by the first person to look for it, which is about as clear as evidence gets.
///
/// Pinned in the view rather than the view model, because the view model was never the problem.
/// Every assertion here is about what is on screen and when.
/// </summary>
[Collection(WpfCollection.Name)]
public class TheGapCheckIsReachableTests(WpfFixture wpf)
{
    private const string Header = "BACKUPS THIS CONTAINER IS MISSING";

    private static RestoreViewModel Screen(AppMode mode)
    {
        var store = new FakeCredentialStore();
        store.Config.Mode = mode;
        store.Config.Servers.Add(new ServerConnection
        { Id = "s1", Name = "SRV01", ServerName = "SRV01" });
        store.Config.BlobContainers.Add(new BlobContainerConfig
        {
            Id = "c1",
            Name = "sqlbackups",
            ContainerUrl = "https://acct.blob.core.windows.net/sqlbackups"
        });

        var vm = new RestoreViewModel(
            new FakeBlobStorageService(), new FakeSqlServerService(), new BackupChainBuilder(),
            new RestoreScriptGenerator(), store, new FakeOperationHistoryStore(),
            TestLogs.Temp());

        vm.Mode = mode;
        vm.SelectedContainer = store.Config.BlobContainers[0];
        return vm;
    }

    private static TextBlock? FindHeader(DependencyObject view) =>
        FindAll<TextBlock>(view).FirstOrDefault(t => t.Text == Header);

    /// <summary>
    /// The one that would have caught the report: nothing chosen but a container, no target, no
    /// confirmed restore point - and it is on screen.
    /// </summary>
    [Fact]
    public void ItIsOnScreenBeforeATargetOrARestorePointIsChosen()
    {
        wpf.Invoke(() =>
        {
            var vm = Screen(AppMode.Pro);
            var view = new RestoreView { DataContext = vm };
            Layout(view);

            Assert.Null(vm.SelectedTargetServer);
            Assert.False(vm.Steps.Options.IsVisible);

            var header = FindHeader(view);
            Assert.NotNull(header);
            Assert.Equal(Visibility.Visible, header!.Visibility);
        });
    }

    /// <summary>
    /// In every mode. It was gated on ShowAdvancedOptions, which happens to return true for all of
    /// them - so the gate did nothing except imply this was a Pro-only tool to anybody reading it.
    /// </summary>
    [Theory]
    [InlineData(AppMode.Basic)]
    [InlineData(AppMode.Standard)]
    [InlineData(AppMode.Pro)]
    public void ItIsOnScreenInEveryMode(AppMode mode)
    {
        wpf.Invoke(() =>
        {
            var view = new RestoreView { DataContext = Screen(mode) };
            Layout(view);

            var header = FindHeader(view);
            Assert.NotNull(header);
            Assert.Equal(Visibility.Visible, header!.Visibility);
        });
    }

    /// <summary>
    /// And it sits in the SOURCE step, beside the other tool that asks an instance about these same
    /// backups. Proven by walking up from the header to the step that contains it, rather than by
    /// trusting a line number - the previous two placements both compiled and both hid it.
    /// </summary>
    [Fact]
    public void ItSitsInTheSourceStepBesideTheAuditPanel()
    {
        wpf.Invoke(() =>
        {
            var view = new RestoreView { DataContext = Screen(AppMode.Pro) };
            Layout(view);

            var header = FindHeader(view);
            Assert.NotNull(header);

            var audit = FindAll<TextBlock>(view)
                .FirstOrDefault(t => t.Text == "AUDIT THESE BACKUPS");
            Assert.NotNull(audit);

            // Both inside one container, which is the source step's own panel.
            Assert.True(SharesAnAncestorWithin(header!, audit!, generations: 4),
                "The gap check is no longer beside the audit panel - check which step it landed in.");
        });
    }

    // ── helpers ─────────────────────────────────────────────────────────────────

    private static bool SharesAnAncestorWithin(
        DependencyObject a, DependencyObject b, int generations)
    {
        var ancestors = new List<DependencyObject>();
        var walk = a;
        for (int i = 0; i < generations && walk != null; i++)
        {
            walk = VisualTreeHelper.GetParent(walk);
            if (walk != null) ancestors.Add(walk);
        }

        walk = b;
        for (int i = 0; i < generations && walk != null; i++)
        {
            walk = VisualTreeHelper.GetParent(walk);
            if (walk != null && ancestors.Contains(walk)) return true;
        }

        return false;
    }

    private static void Layout(FrameworkElement element)
    {
        element.Measure(new Size(1400, 2400));
        element.Arrange(new Rect(0, 0, 1400, 2400));
        element.UpdateLayout();
    }

    private static IEnumerable<T> FindAll<T>(DependencyObject root) where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var node = VisualTreeHelper.GetChild(root, i);
            if (node is T match) yield return match;
            foreach (var d in FindAll<T>(node)) yield return d;
        }
    }
}
