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

    /// <summary>
    /// The Stop button that appears while a restore is running (#25). It is the only way to
    /// interrupt a restore aimed at the wrong server, so a binding that silently failed would
    /// leave the user back at killing the process.
    /// </summary>
    [Fact]
    public void TheStopRestoreButtonAppearsAndBindsWhileExecuting()
    {
        wpf.Invoke(() =>
        {
            var vm = NewRestoreViewModel();
            // The execute card only exists once backups are loaded, which is correct - there is
            // no script to run before then.
            vm.BackupsLoaded = true;
            vm.HasScript = true;
            vm.IsConnectedToServer = true;
            var view = new RestoreView { DataContext = vm };

            // Not running: nothing to stop, so the button must not be offered.
            vm.CanCancelExecute = false;
            Realise(view);
            Assert.DoesNotContain(VisibleButtons(view), b => (b.Content as string) == "Stop restore");

            vm.CanCancelExecute = true;
            Realise(view);

            var stop = Assert.Single(VisibleButtons(view), b => (b.Content as string) == "Stop restore");
            Assert.NotNull(stop.Command);
        });
    }

    /// <summary>The Cancel button over the loading overlay, for a long container listing (#25).</summary>
    [Fact]
    public void TheCancelLoadButtonAppearsAndBindsWhileLoading()
    {
        wpf.Invoke(() =>
        {
            var vm = NewRestoreViewModel();
            vm.IsBusy = true;
            vm.CanCancelLoad = true;

            var view = new RestoreView { DataContext = vm };
            Realise(view);

            var cancel = Assert.Single(VisibleButtons(view), b => (b.Content as string) == "Cancel");
            Assert.NotNull(cancel.Command);
        });
    }

    [Fact]
    public void TheBrowserCancelButtonAppearsAndBindsWhileLoading()
    {
        wpf.Invoke(() =>
        {
            var vm = new BlobBrowserViewModel(new BlobStorageService(Store()), Store())
            {
                IsBusy = true,
                CanCancelLoad = true
            };

            var view = new BlobBrowserView { DataContext = vm };
            Realise(view);

            var cancel = Assert.Single(VisibleButtons(view), b => (b.Content as string) == "Cancel");
            Assert.NotNull(cancel.Command);
        });
    }

    /// <summary>
    /// The console renders its lines and no longer shares a slot with the generated script - both
    /// are on screen together.
    ///
    /// Checked in the state AFTER a restore, which is when the inline console is the one in use:
    /// during a restore the console lives in its own window and the inline one is deliberately
    /// hidden.
    /// </summary>
    [Fact]
    public void TheConsoleAndTheScriptAreBothVisibleTogether()
    {
        wpf.Invoke(() =>
        {
            var vm = NewRestoreViewModel();
            vm.BackupsLoaded = true;
            vm.HasScript = true;
            vm.GeneratedScript = "RESTORE DATABASE [MyDb] FROM URL = N'https://acct/backups/x.bak'";
            vm.ConsoleLines =
            [
                new ConsoleLine("Beginning restore execution...", ConsoleLineKind.Step),
                new ConsoleLine("50 percent processed."),
                new ConsoleLine("ERROR: something went wrong", ConsoleLineKind.Error)
            ];
            vm.HasConsoleOutput = true;
            vm.IsExecuting = false;
            vm.ExecutionComplete = true;

            var view = new RestoreView { DataContext = vm };
            var listener = BindingErrorListener.Attach();
            try
            {
                Realise(view);

                var texts = FindAll<TextBlock>(view).Select(t => t.Text).ToList();
                Assert.Contains(texts, t => t.Contains("50 percent processed", StringComparison.Ordinal));
                Assert.Contains(texts, t => t.Contains("Beginning restore execution", StringComparison.Ordinal));

                // The script pane used to be hidden the moment execution started, so the two
                // shared one slot. Both must now be on screen at the same time.
                var script = Assert.Single(FindAll<SqlTextBlock>(view));
                Assert.Contains("RESTORE DATABASE [MyDb]", script.Sql, StringComparison.Ordinal);

                // ...and it is actually highlighted, not one undifferentiated run.
                var runs = script.Inlines.OfType<System.Windows.Documents.Run>().ToList();
                Assert.True(runs.Count > 1, "The script rendered as a single run - no highlighting applied.");
                Assert.Contains(runs, r => r.Text == "RESTORE");

                listener.AssertNone("RestoreView console");
            }
            finally
            {
                listener.Detach();
            }
        });
    }

    /// <summary>
    /// The inline console and the execution window are mutually exclusive: while the console is
    /// showing in its own window the inline one must be gone, not sitting behind it.
    /// </summary>
    [Fact]
    public void TheInlineConsoleHidesWhileTheConsoleIsInItsOwnWindow()
    {
        wpf.Invoke(() =>
        {
            var vm = NewRestoreViewModel();
            vm.BackupsLoaded = true;
            vm.ConsoleLines = [new ConsoleLine("Beginning restore execution...")];
            vm.HasConsoleOutput = true;

            var view = new RestoreView { DataContext = vm };

            vm.IsConsoleDetached = false;
            Realise(view);
            Assert.True(FindAll<ListBox>(view).Any(IsShown),
                "The inline console should be visible when it is not shown in its own window.");

            vm.IsConsoleDetached = true;
            Realise(view);
            Assert.False(FindAll<ListBox>(view).Any(IsShown),
                "The inline console is still on screen behind the execution window.");
        });
    }

    /// <summary>
    /// The belt-and-braces half: while a restore is running the console is always in its own
    /// window, so the inline one must be gone even if the detach flag never got set.
    /// </summary>
    [Fact]
    public void TheInlineConsoleHidesWhileARestoreIsRunning()
    {
        wpf.Invoke(() =>
        {
            var vm = NewRestoreViewModel();
            vm.BackupsLoaded = true;
            vm.ConsoleLines = [new ConsoleLine("Beginning restore execution...")];
            vm.HasConsoleOutput = true;
            vm.IsConsoleDetached = false;   // as if the wiring failed
            vm.IsExecuting = true;

            var view = new RestoreView { DataContext = vm };
            Realise(view);

            Assert.False(FindAll<ListBox>(view).Any(IsShown),
                "Two consoles would be on screen at once during a restore.");
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

    /// <summary>
    /// Buttons that would be on screen. A collapsed element still exists in the visual tree, so
    /// asserting a button is absent means asserting it is not shown, not that it is missing.
    ///
    /// Uses Visibility rather than IsVisible: IsVisible is false for everything until the element
    /// is attached to a rendered window, and these tests never show one.
    /// </summary>
    private static List<Button> VisibleButtons(DependencyObject root)
        => FindAll<Button>(root).Where(IsShown).ToList();

    private static bool IsShown(FrameworkElement element)
    {
        for (DependencyObject? node = element; node != null;
             node = System.Windows.Media.VisualTreeHelper.GetParent(node))
        {
            if (node is UIElement { Visibility: not Visibility.Visible }) return false;
        }
        return true;
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
