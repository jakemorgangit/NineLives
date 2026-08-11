using Blackcat.NineLives.Models;
using Blackcat.NineLives.ViewModels;
using Xunit;

namespace Blackcat.NineLives.Tests;

/// <summary>
/// The front door (#343). The mode cards say how MUCH of the app you get; this screen is the
/// only one that says what the app IS - so choosing a mode lands here, its buttons must
/// genuinely go where they claim, and its feature pointers must show exactly the shape the
/// sidebar shows, or the introduction lies about the thing it introduces.
/// </summary>
public class HomeScreenTests
{
    private static MainViewModel FirstRun(out FakeCredentialStore store)
    {
        store = new FakeCredentialStore();
        return new MainViewModel(store);
    }

    [Fact]
    public void ChoosingAModeLandsOnTheFrontDoor()
    {
        var main = FirstRun(out _);
        Assert.True(main.IsChoosingMode);

        main.ModeSelection.ChooseCommand.Execute(
            main.ModeSelection.Cards.Single(c => c.Mode == AppMode.Standard));

        Assert.False(main.IsChoosingMode);
        Assert.Same(main.Home, main.CurrentView);
        Assert.Equal(MainViewModel.Nav.Home, main.CurrentViewName);
    }

    [Fact]
    public void TheFrontDoorsButtonsGoWhereTheyClaim()
    {
        var main = FirstRun(out _);
        main.ModeSelection.ChooseCommand.Execute(
            main.ModeSelection.Cards.Single(c => c.Mode == AppMode.Pro));

        main.Home.GoCommand.Execute(MainViewModel.Nav.BlobStorage);
        Assert.Same(main.BlobConfig, main.CurrentView);

        main.Home.GoCommand.Execute(MainViewModel.Nav.Restore);
        Assert.Same(main.Restore, main.CurrentView);

        // And back: the sidebar offers Home like any other screen.
        main.NavigateToCommand.Execute(MainViewModel.Nav.Home);
        Assert.Same(main.Home, main.CurrentView);
    }

    [Theory]
    [InlineData(AppMode.Basic)]
    [InlineData(AppMode.Standard)]
    [InlineData(AppMode.Pro)]
    public void TheFrontDoorExistsInEveryMode(AppMode mode)
    {
        var store = new FakeCredentialStore();
        store.Config.Mode = mode;

        Assert.True(new MainViewModel(store).IsViewAvailable(MainViewModel.Nav.Home));
    }

    /// <summary>
    /// The feature pointers mirror the sidebar. A Basic user shown a "Back Up" card whose
    /// screen the sidebar does not offer would be introduced to an app they do not have.
    /// </summary>
    [Fact]
    public void TheFeaturePointersShowTheModesActualShape()
    {
        var store = new FakeCredentialStore();
        store.Config.Mode = AppMode.Basic;
        var main = new MainViewModel(store);

        Assert.False(main.Home.ShowBackup);
        Assert.False(main.Home.ShowBrowseBackups);
        Assert.False(main.Home.ShowCopyDatabase);

        // Changing the mode from Settings reshapes the pointers with the sidebar.
        main.Mode = AppMode.Pro;
        Assert.True(main.Home.ShowBackup);
        Assert.True(main.Home.ShowBrowseBackups);
        Assert.True(main.Home.ShowCopyDatabase);
    }

    /// <summary>
    /// Home is a screen like any other for the carry-on memory (#211): left there, landed
    /// there next launch.
    /// </summary>
    [Fact]
    public void TheFrontDoorIsRememberedLikeAnyOtherScreen()
    {
        var store = new FakeCredentialStore();
        store.Config.Mode = AppMode.Pro;
        store.Config.LastScreen = MainViewModel.Nav.Home;
        var main = new MainViewModel(store);

        main.ModeSelection.CancelCommand.Execute(null);

        Assert.Same(main.Home, main.CurrentView);
    }
}
