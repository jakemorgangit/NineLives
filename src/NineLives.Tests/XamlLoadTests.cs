using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Blackcat.NineLives.Models;
using Blackcat.NineLives.Services;
using Blackcat.NineLives.ViewModels;
using Blackcat.NineLives.Views;
using Xunit;

namespace Blackcat.NineLives.Tests;

/// <summary>
/// Loads every view against a real viewmodel and lays it out, so the two failure modes that
/// otherwise need a human looking at the running app get caught here instead.
///
/// 1. A XAML parse failure or a missing {StaticResource} key - throws, so any of these tests fail.
/// 2. A broken binding - does NOT throw. WPF traces it and carries on with an empty value, which
///    is why a wrong RelativeSource renders a perfectly normal-looking button that does nothing
///    when clicked. BindingErrorListener is what makes those visible.
///
/// What this deliberately does not check is whether the result LOOKS right - colours, spacing,
/// whether a panel is where you expect. That still needs eyes on the running app.
/// </summary>
public class XamlLoadTests(WpfFixture wpf) : IClassFixture<WpfFixture>, IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "ninelives-xaml-tests", Guid.NewGuid().ToString("n"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    private CredentialStore Store() => new(_dir);

    /// <summary>Realises the visual tree. Bindings are not evaluated until something lays out.</summary>
    private static void Realise(FrameworkElement element)
    {
        element.Measure(new Size(1600, 1200));
        element.Arrange(new Rect(0, 0, 1600, 1200));
        element.UpdateLayout();
    }

    private void Check(string what, Func<FrameworkElement> build)
    {
        wpf.Invoke(() =>
        {
            var listener = BindingErrorListener.Attach();
            try
            {
                Realise(build());
                listener.AssertNone(what);
            }
            finally
            {
                listener.Detach();
            }
        });
    }

    // ── every view loads and binds ──────────────────────────────────────────────

    [Fact]
    public void AboutViewLoads()
        => Check("AboutView", () => new AboutView { DataContext = new AboutViewModel() });

    [Fact]
    public void BlobConfigViewLoads()
        => Check("BlobConfigView", () =>
            new BlobConfigView { DataContext = new BlobConfigViewModel(Store(), new BlobStorageService(Store())) });

    [Fact]
    public void ServerManagerViewLoads()
        => Check("ServerManagerView", () =>
            new ServerManagerView { DataContext = new ServerManagerViewModel(Store(), new SqlServerService(Store())) });

    [Fact]
    public void BlobBrowserViewLoads()
        => Check("BlobBrowserView", () =>
            new BlobBrowserView { DataContext = new BlobBrowserViewModel(new BlobStorageService(Store()), Store()) });

    [Fact]
    public void RestoreViewLoads()
        => Check("RestoreView", () => new RestoreView { DataContext = NewRestoreViewModel() });

    [Fact]
    public void SplashWindowLoads()
    {
        // Borderless, transparent and animated - the shape of window most likely to be affected by
        // a runtime change. Constructed but never shown.
        wpf.Invoke(() =>
        {
            var splash = new SplashWindow();
            Realise(splash);
        });
    }

    [Fact]
    public void MainWindowLoads()
    {
        wpf.Invoke(() =>
        {
            var window = new MainWindow();
            Realise(window);
        });
    }

    // ── the panels that only appear in a particular state ───────────────────────

    /// <summary>
    /// The recovery panel after a failed restore (#14). Its buttons reach the viewmodel's commands
    /// through RelativeSource AncestorType=ItemsControl from inside a DataTemplate - the exact
    /// shape that renders fine and quietly does nothing when it is wrong.
    ///
    /// The items have to be present and the panel visible, because a collapsed element is never
    /// laid out and its template is never realised, so the binding would never be evaluated.
    /// </summary>
    [Fact]
    public void TheRecoveryPanelBindsItsCommands()
    {
        wpf.Invoke(() =>
        {
            var vm = NewRestoreViewModel();
            vm.RecoveryStateMessage = "[MyDb] is in RESTORING state.";
            vm.RecoveryActions =
            [
                new RecoveryAction("Bring the database online", "RESTORE DATABASE [MyDb] WITH RECOVERY", "Ends the sequence."),
                new RecoveryAction("Allow other connections again", "ALTER DATABASE [MyDb] SET MULTI_USER", "Safe at any point.")
            ];
            vm.HasRecoveryActions = true;
            vm.ExecutionComplete = true;

            var view = new RestoreView { DataContext = vm };
            var listener = BindingErrorListener.Attach();
            try
            {
                Realise(view);

                // Both buttons must have found a command. A failed RelativeSource leaves Command
                // null, which is exactly the silent failure this test exists for.
                var buttons = FindAll<Button>(view)
                    .Where(b => b.Content as string is "Run this" or "Copy")
                    .ToList();

                Assert.Equal(4, buttons.Count);   // two actions, two buttons each
                Assert.All(buttons, b => Assert.NotNull(b.Command));
                Assert.All(buttons, b => Assert.NotNull(b.CommandParameter));

                listener.AssertNone("RestoreView recovery panel");
            }
            finally
            {
                listener.Detach();
            }
        });
    }

    /// <summary>
    /// The credential button relabels itself through a DataTrigger once the credential is valid
    /// (#78). If that trigger is wrong the button keeps saying "Create credential on server" when
    /// what it would actually do is refresh an existing one.
    /// </summary>
    [Fact]
    public void TheCredentialButtonRelabelsWhenTheCredentialIsValid()
    {
        wpf.Invoke(() =>
        {
            var vm = NewRestoreViewModel();
            vm.SelectedContainer = new BlobContainerConfig
            {
                Id = BlobContainerConfig.NewId(),
                Name = "prod",
                ContainerUrl = "https://acct.blob.core.windows.net/backups"
            };
            vm.IsConnectedToServer = true;
            vm.CredentialSectionVisible = true;

            var view = new RestoreView { DataContext = vm };

            vm.CredentialExistsOnServer = false;
            vm.CredentialIsValidSas = false;
            Realise(view);
            Assert.Contains(FindAll<Button>(view), b => (b.Content as string) == "Create credential on server");

            vm.CredentialExistsOnServer = true;
            vm.CredentialIsValidSas = true;
            Realise(view);
            Assert.Contains(FindAll<Button>(view),
                b => (b.Content as string) == "Refresh credential with stored SAS token");
        });
    }

    /// <summary>The inventory warnings panel added with #46 - separate from the chain issues one.</summary>
    [Fact]
    public void TheInventoryPanelShowsItsFindings()
    {
        wpf.Invoke(() =>
        {
            var vm = NewRestoreViewModel();
            vm.InventoryIssues =
            [
                new ChainIssue(ChainIssueSeverity.Warning, "2 log backup(s) are not reachable", "Detail here.")
            ];
            vm.HasInventoryIssues = true;

            var view = new RestoreView { DataContext = vm };
            var listener = BindingErrorListener.Attach();
            try
            {
                Realise(view);

                var texts = FindAll<TextBlock>(view).Select(t => t.Text).ToList();
                Assert.True(
                    texts.Any(t => t.Contains("Detail here.", StringComparison.Ordinal)),
                    "The inventory panel did not render its findings. TextBlocks found: " +
                    string.Join(" | ", texts.Where(t => !string.IsNullOrWhiteSpace(t))));

                listener.AssertNone("RestoreView inventory panel");
            }
            finally
            {
                listener.Detach();
            }
        });
    }

    /// <summary>Tag pills render through one wrapping template; they used to clip (#67).</summary>
    [Fact]
    public void TagPillsRenderForAServerRow()
    {
        wpf.Invoke(() =>
        {
            var vm = new ServerManagerViewModel(Store(), new SqlServerService(Store()));
            var server = new ServerConnection
            {
                Id = ServerConnection.NewId(),
                Name = "SRV01",
                ServerName = "SRV01",
                DetectedVersion = "SQL Server 2022"
            };
            server.Tags.Add("prod");
            server.Tags.Add("uk-south");
            vm.Servers = [server];
            vm.SelectedServer = server;

            var view = new ServerManagerView { DataContext = vm };
            var listener = BindingErrorListener.Attach();
            try
            {
                Realise(view);

                var texts = FindAll<TextBlock>(view).Select(t => t.Text).ToList();
                Assert.Contains("prod", texts);
                Assert.Contains("uk-south", texts);
                Assert.Contains("SQL Server 2022", texts);

                listener.AssertNone("ServerManagerView tag pills");
            }
            finally
            {
                listener.Detach();
            }
        });
    }

    // ── helpers ─────────────────────────────────────────────────────────────────

    private RestoreViewModel NewRestoreViewModel()
    {
        var store = Store();
        return new RestoreViewModel(
            new BlobStorageService(store),
            new SqlServerService(store),
            new BackupChainBuilder(),
            new RestoreScriptGenerator(),
            store);
    }

    private static IEnumerable<T> FindAll<T>(DependencyObject root) where T : DependencyObject
    {
        var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var descendant in FindAll<T>(child)) yield return descendant;
        }
    }
}
