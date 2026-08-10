using Blackcat.NineLives.Models;
using Blackcat.NineLives.ViewModels;
using Xunit;

namespace Blackcat.NineLives.Tests;

/// <summary>
/// Closing the app on the launch mode-cards must not erase the remembered screen (#290). The
/// cards are up at every start, so writing null there wiped #211's last-screen memory for
/// anyone who started the app and closed it without clicking through - the next launch
/// landed on the default instead of where they work.
/// </summary>
public class ModeCardsShutdownTests
{
    [Fact]
    public void ClosingOnTheModeCardsKeepsTheRememberedScreen()
    {
        var store = new FakeCredentialStore();
        store.Config.Mode = null;
        store.Config.LastScreen = MainViewModel.Nav.Restore;

        var vm = new MainViewModel(store);
        Assert.True(vm.IsChoosingMode);

        vm.SaveShutdownState(new WindowGeometry());

        Assert.Equal(MainViewModel.Nav.Restore, store.LoadConfig().LastScreen);
    }

    [Fact]
    public void ClosingOnARealScreenStillRecordsIt()
    {
        var store = new FakeCredentialStore();
        store.Config.Mode = AppMode.Pro;
        store.Config.LastScreen = MainViewModel.Nav.Restore;

        // The cards show on every launch; choosing is what dismisses them.
        var vm = new MainViewModel(store);
        vm.ModeSelection.ChooseCommand.Execute(
            vm.ModeSelection.Cards.First(c => c.Mode == AppMode.Pro));
        Assert.False(vm.IsChoosingMode);
        vm.NavigateToCommand.Execute(MainViewModel.Nav.History);

        vm.SaveShutdownState(new WindowGeometry());

        Assert.Equal(MainViewModel.Nav.History, store.LoadConfig().LastScreen);
    }
}
