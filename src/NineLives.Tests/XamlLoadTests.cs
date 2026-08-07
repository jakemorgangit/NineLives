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
[Collection(WpfCollection.Name)]
public class XamlLoadTests(WpfFixture wpf)
{
    /// <summary>
    /// In-memory throughout. These tests are about markup, so nothing here should be reading the
    /// machine's credential vault or writing a config file anywhere (#41).
    /// </summary>
    private static ICredentialStore Store() => new FakeCredentialStore();

    /// <summary>Realises the visual tree. Bindings are not evaluated until something lays out.</summary>
    private static void Realise(FrameworkElement element)
    {
        // A Window builds nothing under itself until its template is applied - measuring alone
        // leaves the content unrealised, so nothing inside it can be found or asserted on.
        element.ApplyTemplate();

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

    /// <summary>
    /// The container form under Entra: no SAS box, and the caveat panel on screen (#29).
    ///
    /// Worth its own case because the whole Entra section is collapsed in the default state the
    /// plain load test exercises, so none of its bindings are ever evaluated there.
    /// </summary>
    [Theory]
    [InlineData(BlobAuthMode.EntraInteractive)]
    [InlineData(BlobAuthMode.EntraDefault)]
    public void TheContainerFormHidesTheSasBoxUnderEntra(BlobAuthMode mode)
    {
        wpf.Invoke(() =>
        {
            var vm = new BlobConfigViewModel(Store(), new BlobStorageService(Store()));
            vm.AddNewCommand.Execute(null);
            vm.EditName = "backups";
            vm.EditContainerUrl = "https://mystorageaccount.blob.core.windows.net/backups";
            vm.EditAuthMode = mode;

            var view = new BlobConfigView { DataContext = vm };
            var listener = BindingErrorListener.Attach();
            try
            {
                Realise(view);

                var labels = FindAll<TextBlock>(view).Where(IsShown).Select(t => t.Text).ToList();
                Assert.DoesNotContain("SAS TOKEN", labels);
                Assert.Contains(labels, t => t.Contains("Owner or Contributor is NOT enough", StringComparison.Ordinal));
                Assert.Contains(labels, t => t.Contains("Storage Blob Data Reader", StringComparison.Ordinal));

                listener.AssertNone("BlobConfigView Entra");
            }
            finally
            {
                listener.Detach();
            }
        });
    }

    [Fact]
    public void TheContainerFormShowsTheSasBoxUnderSasAuth()
    {
        wpf.Invoke(() =>
        {
            var vm = new BlobConfigViewModel(Store(), new BlobStorageService(Store()));
            vm.AddNewCommand.Execute(null);

            var view = new BlobConfigView { DataContext = vm };
            Realise(view);

            var labels = FindAll<TextBlock>(view).Where(IsShown).Select(t => t.Text).ToList();
            Assert.Contains("SAS TOKEN", labels);
            Assert.DoesNotContain(labels, t => t.Contains("Owner or Contributor is NOT enough", StringComparison.Ordinal));
        });
    }

    [Fact]
    public void ServerManagerViewLoads()
        => Check("ServerManagerView", () =>
            new ServerManagerView { DataContext = new ServerManagerViewModel(Store(), new SqlServerService(Store())) });

    [Fact]
    public void BlobBrowserViewLoads()
        => Check("BlobBrowserView", () =>
            new BlobBrowserView { DataContext = new BlobBrowserViewModel(new BlobStorageService(Store()), Store()) });

    [Fact]
    public void SettingsViewLoads()
        => Check("SettingsView", () =>
            new SettingsView { DataContext = new SettingsViewModel(Store()) });

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
            // Against a fake store, not the real one. This used to construct a MainViewModel over
            // the actual %LOCALAPPDATA% config and run the secret-key migration across it - a test
            // reaching into the user's own data (#41).
            var vm = new MainViewModel(new FakeCredentialStore());

            // Busy, so the strip that says what the app is doing is actually built and bound - it
            // is collapsed the rest of the time, and a mistyped path there would be silent (#128).
            vm.BlobBrowser.IsBusy = true;

            var window = new MainWindow(vm);

            var listener = BindingErrorListener.Attach();
            try
            {
                Realise(window);
                listener.AssertNone("MainWindow");
            }
            finally
            {
                listener.Detach();
            }
        });
    }

    // The sidebar highlight is likewise not asserted here (#117 item 5). It is bound to
    // CurrentViewName so that Ctrl+1..6 moves it, and the obvious test - navigate by command, then
    // check which RadioButton is checked - finds no RadioButtons at all: an unshown Window has no
    // visual tree, the same limitation as the banner above. What CAN break is covered elsewhere:
    // MainWindowLoads traces a wrong binding path, and KeyboardShortcutTests pins the converter in
    // both directions, including that ConvertBack refuses so a click cannot desync the selection.

    // The update banner is not asserted on beyond MainWindowLoads above, which does cover what
    // can break silently: it parses the banner's markup, its storyboard and its drop shadow, and
    // resolves every {StaticResource} it uses - a missing key would throw there.
    //
    // Whether it is VISIBLE cannot be checked this way. A Window builds nothing under itself until
    // it is shown, and showing one would flash a real window and run the actual startup path. Not
    // worth contorting the harness for a banner whose appearance needs eyes anyway.

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

            // Step 4 holds all of this and is collapsed by default (#117 item 3): a collapsed
            // parent never measures, so its item containers are never generated to be found.
            vm.Steps.Execute.IsExpanded = true;
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
            vm.Credential.SectionVisible = true;

            // Step 4 holds all of this and is collapsed by default (#117 item 3): a collapsed
            // parent never measures, so its item containers are never generated to be found.
            vm.Steps.Execute.IsExpanded = true;
            var view = new RestoreView { DataContext = vm };

            vm.Credential.ExistsOnServer = false;
            vm.Credential.IdentityKind = BlobCredentialIdentity.Missing;
            Realise(view);
            Assert.Contains(FindAll<Button>(view), b => (b.Content as string) == "Create credential on server");

            vm.Credential.ExistsOnServer = true;
            vm.Credential.IdentityKind = BlobCredentialIdentity.SharedAccessSignature;
            Realise(view);
            Assert.Contains(FindAll<Button>(view),
                b => (b.Content as string) == "Refresh credential with stored SAS token");
        });
    }

    /// <summary>
    /// The same button under a managed identity. "Refresh credential with stored SAS token" reads
    /// as a harmless top-up, and the press would convert the instance's managed identity into a
    /// SAS credential - so the label has to say that before it is pressed, not after (#145).
    /// </summary>
    [Fact]
    public void TheCredentialButtonSaysItWouldReplaceAManagedIdentity()
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
            vm.Credential.SectionVisible = true;

            // Step 4 holds all of this and is collapsed by default (#117 item 3): a collapsed
            // parent never measures, so its item containers are never generated to be found.
            vm.Steps.Execute.IsExpanded = true;
            var view = new RestoreView { DataContext = vm };

            vm.Credential.ExistsOnServer = true;
            vm.Credential.IdentityKind = BlobCredentialIdentity.ManagedIdentity;
            Realise(view);

            // Present, not shown: the panel lives inside a section that needs a loaded backup
            // set, which this fixture deliberately does not stand up. That the panel's own
            // visibility binding resolves at all is covered by RestoreViewLoads.
            Assert.Contains(FindAll<Button>(view),
                b => (b.Content as string) == "Replace Managed Identity with stored SAS token");
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
            // Step 4 holds all of this and is collapsed by default (#117 item 3): a collapsed
            // parent never measures, so its item containers are never generated to be found.
            vm.Steps.Execute.IsExpanded = true;
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

            // Step 4 holds all of this and is collapsed by default (#117 item 3): a collapsed
            // parent never measures, so its item containers are never generated to be found.
            vm.Steps.Execute.IsExpanded = true;
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
            vm.Console.Lines =
            [
                new ConsoleLine("Beginning restore execution...", ConsoleLineKind.Step),
                new ConsoleLine("50 percent processed."),
                new ConsoleLine("ERROR: something went wrong", ConsoleLineKind.Error)
            ];
            vm.Console.HasOutput = true;
            vm.IsExecuting = false;
            vm.ExecutionComplete = true;

            // Step 4 holds all of this and is collapsed by default (#117 item 3): a collapsed
            // parent never measures, so its item containers are never generated to be found.
            vm.Steps.Execute.IsExpanded = true;
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
            vm.Console.Lines = [new ConsoleLine("Beginning restore execution...")];
            vm.Console.HasOutput = true;

            // Step 4 holds all of this and is collapsed by default (#117 item 3): a collapsed
            // parent never measures, so its item containers are never generated to be found.
            vm.Steps.Execute.IsExpanded = true;
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
            vm.Console.Lines = [new ConsoleLine("Beginning restore execution...")];
            vm.Console.HasOutput = true;
            vm.IsConsoleDetached = false;   // as if the wiring failed
            vm.IsExecuting = true;

            // Step 4 holds all of this and is collapsed by default (#117 item 3): a collapsed
            // parent never measures, so its item containers are never generated to be found.
            vm.Steps.Execute.IsExpanded = true;
            var view = new RestoreView { DataContext = vm };
            Realise(view);

            Assert.False(FindAll<ListBox>(view).Any(IsShown),
                "Two consoles would be on screen at once during a restore.");
        });
    }

    /// <summary>
    /// The point-in-time panel, and the fact that a rejected target reads as a rejection.
    ///
    /// "Must be after 22:00" was drawn in the same muted grey as "Valid range: ...". Execute is
    /// already blocked at that point, so that message is the only thing on screen saying why
    /// (#115 seam 3).
    /// </summary>
    [Fact]
    public void ARejectedPointInTimeTargetIsShownAsAnError()
    {
        wpf.Invoke(() =>
        {
            var vm = NewRestoreViewModel();
            vm.PointInTime.SetWindow((new DateTime(2026, 1, 10, 22, 0, 0), new DateTime(2026, 1, 10, 22, 15, 0)));

            // Step 4 holds all of this and is collapsed by default (#117 item 3): a collapsed
            // parent never measures, so its item containers are never generated to be found.
            vm.Steps.Execute.IsExpanded = true;
            var view = new RestoreView { DataContext = vm };
            var listener = BindingErrorListener.Attach();
            try
            {
                Realise(view);

                var message = FindAll<TextBlock>(view)
                    .Single(t => t.Text.StartsWith("Valid range", StringComparison.Ordinal));
                var informational = message.Foreground;

                // Ticked, over a target past the end of the log.
                vm.PointInTime.Use = true;
                vm.PointInTime.StopAtText = "2026-01-10 23:59:59";
                Realise(view);

                Assert.True(vm.PointInTime.HasError);
                Assert.StartsWith("Must be at or before", message.Text);
                Assert.NotEqual(informational.ToString(), message.Foreground.ToString());

                listener.AssertNone("RestoreView point-in-time panel");
            }
            finally
            {
                listener.Detach();
            }
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

            // Step 4 holds all of this and is collapsed by default (#117 item 3): a collapsed
            // parent never measures, so its item containers are never generated to be found.
            vm.Steps.Execute.IsExpanded = true;
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

    /// <summary>
    /// The VERIFYONLY results panel (#26). Its rows colour the status through a Style on a Run,
    /// which is only ever built when the template is realised - so an empty collection would leave
    /// that markup completely unexercised.
    /// </summary>
    [Fact]
    public void TheVerifyPanelShowsWhatSqlServerSaidAboutEachBackup()
    {
        wpf.Invoke(() =>
        {
            var vm = NewRestoreViewModel();
            var good = new BackupSet { SetId = "20260110_220000", Type = BackupType.Full };
            var bad = new BackupSet { SetId = "20260110_230000", Type = BackupType.TransactionLog };

            vm.ChainVerifyResults.Add(new ChainVerifyResult
            {
                Set = good,
                Result = new VerifyOnlyResult(true, "The backup set on file 1 is valid.")
            });
            vm.ChainVerifyResults.Add(new ChainVerifyResult
            {
                Set = bad,
                Result = new VerifyOnlyResult(false, "Cannot open backup device.")
            });
            vm.HasVerifyResults = true;
            vm.HasVerifyFailures = true;

            // Step 4 holds all of this and is collapsed by default (#117 item 3): a collapsed
            // parent never measures, so its item containers are never generated to be found.
            vm.Steps.Execute.IsExpanded = true;
            var view = new RestoreView { DataContext = vm };
            var listener = BindingErrorListener.Attach();
            try
            {
                Realise(view);

                var texts = FindAll<TextBlock>(view).Select(t => t.Text).ToList();
                Assert.True(
                    texts.Any(t => t.Contains("Cannot open backup device.", StringComparison.Ordinal)),
                    "The verify panel did not render its results. TextBlocks found: " +
                    string.Join(" | ", texts.Where(t => !string.IsNullOrWhiteSpace(t))));
                // The status sits in its own Run so it can be coloured, so look at the inlines
                // rather than the TextBlock's flattened text.
                var runs = FindAll<TextBlock>(view)
                    .SelectMany(t => t.Inlines.OfType<System.Windows.Documents.Run>())
                    .Select(r => r.Text)
                    .ToList();

                Assert.True(runs.Contains("FAILED"), "A failed backup did not read as failed.");
                Assert.True(runs.Contains("Valid"), "A good backup did not read as valid.");

                listener.AssertNone("RestoreView verify panel");
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

    /// <summary>
    /// The restore point list and its narrowing controls (#27). The list is the only way to pick
    /// one point out of hundreds, so a binding that silently failed would leave the timeline as
    /// the only selector again - which is the problem being fixed.
    /// </summary>
    [Fact]
    public void TheRestorePointListRendersAndBindsItsSelection()
    {
        wpf.Invoke(() =>
        {
            var full = new BackupSet
            {
                SetId = "20260110_220000",
                Type = BackupType.Full,
                Timestamp = new DateTime(2026, 1, 10, 22, 0, 0),
                Files = [new BackupFileInfo { BlobName = "FULL/SRV01/MyDb/20260110_220000.bak", SizeBytes = 1000 }]
            };

            var vm = NewRestoreViewModel();
            vm.Timeline.HasPoints = true;
            vm.Timeline.HasVisiblePoints = true;
            vm.Timeline.CountText = "Showing 1 of 4 restore point(s)";
            vm.Timeline.Points =
            [
                new RestorePoint
                {
                    Timestamp = full.Timestamp,
                    Type = BackupType.Full,
                    PrimarySet = full,
                    RequiredFullSet = full
                }
            ];

            // Step 4 holds all of this and is collapsed by default (#117 item 3): a collapsed
            // parent never measures, so its item containers are never generated to be found.
            vm.Steps.Execute.IsExpanded = true;
            var view = new RestoreView { DataContext = vm };
            var listener = BindingErrorListener.Attach();
            try
            {
                Realise(view);

                var texts = FindAll<TextBlock>(view).Select(t => t.Text).ToList();
                Assert.True(
                    texts.Any(t => t.Contains("2026-01-10 22:00:00", StringComparison.Ordinal)),
                    "The restore point list did not render its rows.");
                Assert.Contains(texts, t => t.Contains("Showing 1 of 4", StringComparison.Ordinal));

                listener.AssertNone("RestoreView point list");
            }
            finally
            {
                listener.Detach();
            }
        });
    }

    /// <summary>
    /// The restore history view (#31). Populated, because its rows colour the outcome through a
    /// Style on a Run and the detail pane only exists once something is selected - none of which
    /// is built when the list is empty.
    /// </summary>
    [Fact]
    public void TheHistoryViewShowsWhatEachRestoreDid()
    {
        wpf.Invoke(() =>
        {
            var history = new FakeRestoreHistoryStore();
            history.Append(new RestoreHistoryEntry
            {
                StartedAt = new DateTime(2026, 1, 10, 22, 0, 0),
                CompletedAt = new DateTime(2026, 1, 10, 22, 4, 30),
                ServerName = "SRV01",
                TargetDatabase = "MyDb_Restored",
                ChainSummary = "1 Full + 2 Log(s)",
                Outcome = RestoreOutcome.Failed,
                ErrorMessage = "RESTORE terminating abnormally.",
                Script = "RESTORE DATABASE [MyDb_Restored] FROM URL = N'https://mystorageaccount.blob.core.windows.net/backups/x.bak'",
                Log = "Beginning restore execution..."
            });

            var view = new HistoryView { DataContext = new HistoryViewModel(history) };
            var listener = BindingErrorListener.Attach();
            try
            {
                Realise(view);

                var texts = FindAll<TextBlock>(view).Select(t => t.Text).ToList();
                Assert.True(
                    texts.Any(t => t.Contains("RESTORE terminating abnormally.", StringComparison.Ordinal)),
                    "The detail pane did not render. TextBlocks found: " +
                    string.Join(" | ", texts.Where(t => !string.IsNullOrWhiteSpace(t))));

                var runs = FindAll<TextBlock>(view)
                    .SelectMany(t => t.Inlines.OfType<System.Windows.Documents.Run>())
                    .Select(r => r.Text)
                    .ToList();
                Assert.Contains("Failed", runs);

                // The script and the log are what someone came here for.
                var boxes = FindAll<System.Windows.Controls.TextBox>(view).Select(b => b.Text).ToList();
                Assert.Contains(boxes, t => t.Contains("RESTORE DATABASE", StringComparison.Ordinal));

                listener.AssertNone("HistoryView");
            }
            finally
            {
                listener.Detach();
            }
        });
    }

    [Fact]
    public void TheHistoryViewLoadsWithNothingRecorded()
        => Check("HistoryView (empty)", () =>
            new HistoryView { DataContext = new HistoryViewModel(new FakeRestoreHistoryStore()) });

    // ── helpers ─────────────────────────────────────────────────────────────────

    private RestoreViewModel NewRestoreViewModel()
    {
        var store = Store();
        return new RestoreViewModel(
            new BlobStorageService(store),
            new SqlServerService(store),
            new BackupChainBuilder(),
            new RestoreScriptGenerator(),
            store,
            new OperationLog(Path.Combine(Path.GetTempPath(), "ninelives-xaml-tests", Guid.NewGuid().ToString("n"))),
            new FakeRestoreHistoryStore());
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
            // A Window that has never been shown reports itself as not visible, which would make
            // every element inside it look hidden. What is being asked here is whether the content
            // would be on screen, so the window itself is not part of the question.
            if (node is Window) break;
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
