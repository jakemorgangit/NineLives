using System.Windows;
using System.Windows.Controls;
using Blackcat.NineLives.Models;
using Blackcat.NineLives.ViewModels;
using Blackcat.NineLives.Views;
using Xunit;

namespace Blackcat.NineLives.Tests;

/// <summary>
/// Three screens that failed without saying so (#356, #357, #358).
///
/// The shape is the same each time: the view model wrote a message and the view rendered no
/// surface for it, or rendered one and then collapsed it. Browse Backups had neither an error
/// banner nor a status line, so every SetError in it was a dead call - an expired SAS, a 403, a
/// DNS failure and never having pressed the button all left the identical screen. Connect on the
/// SQL Servers screen wrote a banner that lives inside the edit form, while the button itself
/// lives in the panel shown when the form is closed. And the Backup and Copy consoles were bound
/// to IsRunning, so they vanished at exactly the moment their contents mattered.
/// </summary>
[Collection(WpfCollection.Name)]
public class SilentFailureTests(WpfFixture wpf)
{
    // ── Browse Backups has somewhere to put a failure (#356) ────────────────────

    /// <summary>
    /// Pinned in the view rather than the view model, because the view model was never the
    /// problem: it had been writing SetError all along, into a screen with nothing bound to it.
    /// </summary>
    [Fact]
    public void BrowseBackupsRendersAnErrorBanner()
    {
        wpf.Invoke(() =>
        {
            var vm = new BlobBrowserViewModel(
                new FakeBlobStorageService(), new FakeSqlServerService(), new FakeCredentialStore());

            var view = new BlobBrowserView { DataContext = vm };
            Layout(view);

            // Nothing to show yet, so nothing is showing.
            Assert.Empty(VisibleBanners(view));

            vm.LoadBackupsCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            Layout(view);

            // The refusal reached a surface: pressing Load Backups with nothing selected used to
            // be a button that visibly did nothing at all.
            Assert.True(vm.HasError);
            Assert.Contains("select a container", vm.ErrorMessage);

            Assert.Single(VisibleBanners(view));
        });
    }

    /// <summary>
    /// SetError writes the status line as well as the banner, because not every screen surfaces
    /// both. This one does, and the same sentence in red above itself in grey reads as two
    /// separate things having gone wrong.
    /// </summary>
    [Fact]
    public void AnErrorIsNotPrintedTwiceOnTheSameScreen()
    {
        wpf.Invoke(() =>
        {
            var vm = new BlobBrowserViewModel(
                new FakeBlobStorageService(), new FakeSqlServerService(), new FakeCredentialStore());

            var view = new BlobBrowserView { DataContext = vm };
            vm.LoadBackupsCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            Layout(view);

            // The view model writes both, deliberately - so the check is that the view shows one.
            Assert.Equal(vm.ErrorMessage, vm.StatusMessage);

            var showingIt = FindAll<TextBlock>(view)
                .Count(t => t.Visibility == Visibility.Visible && t.Text == vm.ErrorMessage);

            Assert.Equal(0, showingIt);
        });
    }

    // ── Connect explains itself, like Test does (#357) ──────────────────────────

    /// <summary>
    /// Two buttons on the same screen against the same server, with opposite behaviour: Test
    /// wrote TestResult, which view mode renders, and Connect wrote a banner that view mode does
    /// not. On the last step of first run, a typo'd instance or a wrong password flashed
    /// "Connecting..." and returned to a screen with nothing on it.
    /// </summary>
    [Fact]
    public async Task AFailedConnectSaysWhyOnTheSurfaceViewModeRenders()
    {
        var vm = Servers(new FakeSqlServerService
        {
            TestConnectionThrows = new InvalidOperationException(
                "A network-related or instance-specific error occurred.")
        });

        await vm.ConnectCommand.ExecuteAsync(null);

        Assert.False(vm.IsConnected);
        Assert.False(vm.TestSuccess);
        Assert.Contains("Connection failed", vm.TestResult);
        Assert.Contains("network-related", vm.TestResult);
    }

    [Fact]
    public async Task ASuccessfulConnectClearsAPreviousFailure()
    {
        var sql = new FakeSqlServerService
        {
            TestConnectionThrows = new InvalidOperationException("Login failed for user 'sa'.")
        };
        var vm = Servers(sql);

        await vm.ConnectCommand.ExecuteAsync(null);
        Assert.Contains("Login failed", vm.TestResult);

        sql.TestConnectionThrows = null;
        await vm.ConnectCommand.ExecuteAsync(null);

        Assert.True(vm.IsConnected);
        Assert.True(vm.TestSuccess);
        Assert.DoesNotContain("Login failed", vm.TestResult);
        Assert.Contains("Connected to", vm.TestResult);
    }

    private static ServerManagerViewModel Servers(FakeSqlServerService sql)
    {
        var store = new FakeCredentialStore();
        store.Config.Servers.Add(new ServerConnection
        {
            Id = ServerConnection.NewId(),
            Name = "SRV01",
            ServerName = "SRV01"
        });

        var vm = new ServerManagerViewModel(store, sql);
        vm.SelectedServer = vm.Servers.First();
        return vm;
    }

    // ── the console outlives the run that wrote it (#358) ───────────────────────

    /// <summary>
    /// The error text left on screen when a striped backup partly fails says "see the console for
    /// each failure". Bound to IsRunning, the console it names was gone by the time anyone read
    /// that sentence.
    /// </summary>
    [Fact]
    public void TheBackupConsoleStaysUpAfterTheRunEnds()
    {
        var vm = new BackupViewModel(new FakeCredentialStore(), new FakeSqlServerService());

        Assert.False(vm.HasConsoleOutput);

        vm.AppendConsoleForTests("Msg 3201, Level 16, State 1");

        Assert.True(vm.HasConsoleOutput);
        Assert.False(vm.IsRunning);
        Assert.NotEmpty(vm.Console);
    }

    /// <summary>
    /// The copy screen is the worse of the two: this console is where the target's recovery
    /// explanation and the literal RESTORE ... WITH RECOVERY statements are printed when the
    /// restore half fails, so collapsing it hid the exact statements needed to get the target out
    /// of RESTORING - and the only way back to them was to re-run a production operation.
    /// </summary>
    [Fact]
    public void TheCopyConsoleStaysUpAfterTheRunEnds()
    {
        var vm = new CopyDatabaseViewModel(new FakeCredentialStore(), new FakeSqlServerService());

        Assert.False(vm.HasConsoleOutput);

        vm.AppendConsoleForTests("RESTORE DATABASE [Sales] WITH RECOVERY;");

        Assert.True(vm.HasConsoleOutput);
        Assert.False(vm.IsRunning);
        Assert.Contains(vm.Console, l => l.Contains("WITH RECOVERY"));
    }

    // ── helpers ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// The error banners on screen. Matched on the exact type rather than with an "is" test,
    /// because Button, RadioButton and CheckBox all derive from ContentControl - the banner is
    /// the only bare one in these views.
    /// </summary>
    private static List<ContentControl> VisibleBanners(DependencyObject view) =>
        FindAll<ContentControl>(view)
            .Where(c => c.GetType() == typeof(ContentControl) && c.Visibility == Visibility.Visible)
            .ToList();

    private static void Layout(FrameworkElement element)
    {
        element.Measure(new Size(1600, 1200));
        element.Arrange(new Rect(0, 0, 1600, 1200));
        element.UpdateLayout();
    }

    private static IEnumerable<T> FindAll<T>(DependencyObject root) where T : DependencyObject
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root))
        {
            if (child is not DependencyObject node) continue;
            if (node is T match) yield return match;
            foreach (var descendant in FindAll<T>(node)) yield return descendant;
        }
    }
}
