using System.IO;
using Blackcat.NineLives.Services;
using Blackcat.NineLives.ViewModels;
using Xunit;

namespace Blackcat.NineLives.Tests;

/// <summary>
/// The Settings screen (#117 item 2).
///
/// Two of these settings had no UI at all: the update check was editable only by hand-editing
/// config.json, and log retention was a constant in the source.
/// </summary>
[Collection(WpfCollection.Name)]
public class SettingsViewModelTests
{
    private static OperationLog ThrowawayLog() => new(Path.Combine(
        Path.GetTempPath(), "ninelives-settings-tests", Guid.NewGuid().ToString("n")));

    [Fact]
    public void TheScreenOpensOnWhatIsInTheConfig()
    {
        var store = new FakeCredentialStore();
        store.Config.CheckForUpdates = false;
        store.Config.LogRetentionDays = 7;

        var vm = new SettingsViewModel(store, ThrowawayLog());

        Assert.False(vm.CheckForUpdates);
        Assert.Equal(7, vm.LogRetentionDays);
    }

    /// <summary>
    /// Loading must not write. Every property here saves when it changes, so a constructor that
    /// set them without a guard would save the config as a side effect of opening the screen -
    /// and on a config that failed to load, that is how the file gets overwritten with defaults.
    /// </summary>
    [Fact]
    public void OpeningTheScreenSavesNothing()
    {
        var store = new FakeCredentialStore();
        store.Config.CheckForUpdates = false;

        _ = new SettingsViewModel(store, ThrowawayLog());

        Assert.Equal(0, store.SaveConfigCalls);
    }

    [Fact]
    public void TurningTheUpdateCheckOffIsRemembered()
    {
        var store = new FakeCredentialStore();
        var vm = new SettingsViewModel(store, ThrowawayLog());

        vm.CheckForUpdates = false;

        Assert.False(store.Config.CheckForUpdates);
    }

    [Fact]
    public void ChangingRetentionIsRememberedAndAppliedToTheLiveLog()
    {
        var store = new FakeCredentialStore();
        var log = ThrowawayLog();
        var vm = new SettingsViewModel(store, log);

        vm.LogRetentionDays = 90;

        Assert.Equal(90, store.Config.LogRetentionDays);

        // Applied now rather than at next startup: somebody shortening it on a shared machine
        // means it, and "restart the app" is the kind of instruction that gets skipped.
        Assert.Equal(90, log.RetentionDays);
    }

    /// <summary>
    /// A retention of zero would delete today's log file - the one recording the restore that is
    /// running while somebody fiddles with this box.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void RetentionCannotBeSetLowEnoughToDeleteTodaysLog(int typed)
    {
        var store = new FakeCredentialStore();
        var log = ThrowawayLog();
        var vm = new SettingsViewModel(store, log);

        vm.LogRetentionDays = typed;

        Assert.Equal(OperationLog.MinimumRetentionDays, log.RetentionDays);

        // And the box shows what is actually in force, not what was typed.
        Assert.Equal(OperationLog.MinimumRetentionDays, vm.LogRetentionDays);
    }

    /// <summary>
    /// About is a credits page again. It carried the theme picker only because it was the one
    /// screen that was not a workflow step.
    /// </summary>
    [Fact]
    public void SettingsIsReachableFromTheSidebarAndAboutIsNotASettingsScreen()
    {
        Assert.Contains(MainViewModel.Nav.Settings, MainViewModel.Nav.Views);
        Assert.Null(typeof(AboutViewModel).GetProperty("SelectedTheme"));
        Assert.Null(typeof(AboutViewModel).GetProperty("LogFolder"));
    }
}
