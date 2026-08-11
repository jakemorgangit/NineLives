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
/// A fresh install says what is missing and where to fix it (#406).
///
/// Rendering every screen with no containers and no servers showed three that did not. Browse
/// Backups said "Select a container and click Load Backups to browse" when there were no
/// containers to select - an instruction nobody could follow, on the screen that is the natural
/// thing to press when you want to LOOK before committing. Back Up showed two empty dropdowns and
/// no words; Copy Database showed three, and a copy needs two servers, so somebody with one hits
/// the same wall.
///
/// Restore and Exposure already had the sentence. These tests hold all five to it.
/// </summary>
[Collection(WpfCollection.Name)]
public class NothingToPickFromTests(WpfFixture wpf)
{
    private static FakeCredentialStore Furnished()
    {
        var store = new FakeCredentialStore();
        store.Config.Servers.Add(new ServerConnection
        { Id = ServerConnection.NewId(), Name = "SRV01", ServerName = "SRV01" });
        store.Config.BlobContainers.Add(new BlobContainerConfig
        {
            Id = BlobContainerConfig.NewId(),
            Name = "backups",
            ContainerUrl = "https://acct.blob.core.windows.net/backups"
        });
        return store;
    }

    // ── Back Up ─────────────────────────────────────────────────────────────────

    [Fact]
    public void BackUpNamesBothMissingThings()
    {
        wpf.Invoke(() =>
        {
            var vm = new BackupViewModel(new FakeCredentialStore(), new FakeSqlServerService());
            var view = new BackupView { DataContext = vm };
            Layout(view);

            Assert.True(vm.HasNoServers);
            Assert.True(vm.HasNoContainers);

            var shown = string.Join(" | ", Shown(view));
            Assert.Contains("SQL Servers screen", shown);
            Assert.Contains("Blob Storage screen", shown);
        });
    }

    [Fact]
    public void BackUpSaysNothingOnceThereIsSomethingToPick()
    {
        wpf.Invoke(() =>
        {
            var vm = new BackupViewModel(Furnished(), new FakeSqlServerService());
            var view = new BackupView { DataContext = vm };
            Layout(view);

            Assert.False(vm.HasNoServers);
            Assert.False(vm.HasNoContainers);

            var shown = string.Join(" | ", Shown(view));
            Assert.DoesNotContain("No saved servers yet", shown);
            Assert.DoesNotContain("No storage configured yet", shown);
        });
    }

    // ── Copy Database ───────────────────────────────────────────────────────────

    [Fact]
    public void CopySaysACopyNeedsTwoServers()
    {
        wpf.Invoke(() =>
        {
            var vm = new CopyDatabaseViewModel(new FakeCredentialStore(), new FakeSqlServerService());
            var view = new CopyDatabaseView { DataContext = vm };
            Layout(view);

            var shown = string.Join(" | ", Shown(view));
            Assert.Contains("a copy needs two", shown);
            Assert.Contains("Blob Storage screen", shown);
        });
    }

    // ── Browse Backups ──────────────────────────────────────────────────────────

    /// <summary>
    /// The one that was actively wrong rather than merely absent: an instruction to select
    /// something that does not exist.
    /// </summary>
    [Fact]
    public void BrowseDoesNotAskForAContainerWhenThereAreNone()
    {
        var vm = new BlobBrowserViewModel(
            new FakeBlobStorageService(), new FakeSqlServerService(), new FakeCredentialStore());

        Assert.True(vm.MediumIsBlob);
        Assert.DoesNotContain("Select a container", vm.BrowseHint);
        Assert.Contains("Blob Storage screen", vm.BrowseHint);
    }

    [Fact]
    public void BrowseAsksForAContainerOnceThereIsOne()
    {
        var vm = new BlobBrowserViewModel(
            new FakeBlobStorageService(), new FakeSqlServerService(), Furnished());
        vm.RefreshContainers();

        Assert.Contains("Select a container", vm.BrowseHint);
    }

    [Fact]
    public void BrowseSwitchesTheSentenceWithTheMedium()
    {
        var vm = new BlobBrowserViewModel(
            new FakeBlobStorageService(), new FakeSqlServerService(), new FakeCredentialStore())
        {
            SelectedMedium = BackupMedium.SharedPath
        };

        Assert.Contains("SQL Servers screen", vm.BrowseHint);
        Assert.DoesNotContain("Blob Storage", vm.BrowseHint);
    }

    // ── and the two that already had it ─────────────────────────────────────────

    [Fact]
    public void RestoreStillSaysItAboutServers()
    {
        var vm = new RestoreViewModel(
            new FakeBlobStorageService(), new FakeSqlServerService(), new BackupChainBuilder(),
            new RestoreScriptGenerator(), new FakeCredentialStore(),
            TestLogs.Temp(), new FakeRestoreHistoryStore());

        Assert.True(vm.HasNoTargetServers);
    }

    [Fact]
    public async Task ExposureStillSaysItAboutServers()
    {
        var vm = new ExposureViewModel(
            new FakeCredentialStore(), new FakeSqlServerService(), new FakeRestoreHistoryStore());

        await vm.RefreshCommand.ExecuteAsync(null);

        Assert.Contains("No servers configured", vm.Summary);
        Assert.Contains("SQL Servers screen", vm.Summary);
    }

    // ── helpers ─────────────────────────────────────────────────────────────────

    private static List<string> Shown(DependencyObject view) =>
        FindAll<TextBlock>(view)
            .Where(t => t.Visibility == Visibility.Visible)
            .Select(t => t.Text)
            .ToList();

    private static void Layout(FrameworkElement element)
    {
        element.Measure(new Size(1280, 1400));
        element.Arrange(new Rect(0, 0, 1280, 1400));
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
