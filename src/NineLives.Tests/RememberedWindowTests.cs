using Blackcat.NineLives.Models;
using Blackcat.NineLives.ViewModels;
using Xunit;

namespace Blackcat.NineLives.Tests;

/// <summary>
/// The app comes back where it was left (#211): window geometry, and the screen in use.
///
/// The small daily tax everyone pays without mentioning - resize, reposition, renavigate, every
/// launch. The geometry is applied only when it still puts a grabbable title bar on a screen,
/// because a monitor unplugged since must not swallow the window.
/// </summary>
public class RememberedWindowTests
{
    // ── the geometry guard ──────────────────────────────────────────────────────

    private static WindowGeometry At(double left, double top, double w = 1200, double h = 800) =>
        new() { Left = left, Top = top, Width = w, Height = h };

    /// <summary>A single 1920x1080 screen, for the cases below.</summary>
    private static bool Usable(WindowGeometry g) => g.IsUsableOn(0, 0, 1920, 1080);

    [Fact]
    public void APositionOnTheScreenIsUsable()
    {
        Assert.True(Usable(At(100, 100)));
    }

    /// <summary>People park windows half-off deliberately - partly off is fine.</summary>
    [Fact]
    public void PartlyOffTheEdgeIsStillUsable()
    {
        Assert.True(Usable(At(-400, 50)));
        Assert.True(Usable(At(1700, 50, w: 800)));
    }

    /// <summary>The unplugged-monitor case: fully off the virtual screen, nothing to grab.</summary>
    [Fact]
    public void AWindowOnAMonitorThatIsGoneIsNotUsable()
    {
        Assert.False(Usable(At(-2500, 200)));   // was on a screen to the left
        Assert.False(Usable(At(2500, 200)));    // was on a screen to the right
        Assert.False(Usable(At(100, 1200)));    // title bar below the bottom
    }

    /// <summary>A maximised window sits a few pixels above zero - that is not "off screen".</summary>
    [Fact]
    public void TheMaximisedOffsetIsTolerated()
    {
        Assert.True(Usable(At(-8, -8)));
    }

    [Fact]
    public void NonsenseSizesAreNotUsable()
    {
        Assert.False(Usable(At(100, 100, w: 50, h: 40)));
    }

    // ── the landing screen ──────────────────────────────────────────────────────

    private static MainViewModel Shell(AppMode mode = AppMode.Pro, string? lastScreen = null)
    {
        var store = new FakeCredentialStore();
        store.Config.Mode = mode;
        store.Config.LastScreen = lastScreen;
        return new MainViewModel(store);
    }

    /// <summary>Carrying on means carrying ON - the last screen, not a fixed one.</summary>
    [Fact]
    public void CarryingOnLandsOnTheLastScreen()
    {
        var main = Shell(lastScreen: MainViewModel.Nav.History);

        main.ModeSelection.CancelCommand.Execute(null);

        Assert.Same(main.History, main.CurrentView);
    }

    /// <summary>Nothing recorded still lands on Restore, the app's centre (#209).</summary>
    [Fact]
    public void NothingRecordedLandsOnRestore()
    {
        var main = Shell();

        main.ModeSelection.CancelCommand.Execute(null);

        Assert.Same(main.Restore, main.CurrentView);
    }

    /// <summary>
    /// A screen the current mode no longer offers falls back to Restore - a guess at the nearest
    /// cousin would be worse than the app's centre.
    /// </summary>
    [Fact]
    public void AScreenTheModeDoesNotOfferFallsBackToRestore()
    {
        var main = Shell(AppMode.Basic, MainViewModel.Nav.CopyDatabase);

        main.ModeSelection.CancelCommand.Execute(null);

        Assert.Same(main.Restore, main.CurrentView);
    }

    [Fact]
    public void GarbageInTheConfigFallsBackToRestore()
    {
        Assert.Equal(MainViewModel.Nav.Restore, Shell().LandingScreen("No Such Screen"));
    }

    // ── saving at shutdown ──────────────────────────────────────────────────────

    [Fact]
    public void ShutdownFilesGeometryAndScreen()
    {
        var store = new FakeCredentialStore();
        store.Config.Mode = AppMode.Pro;

        var main = new MainViewModel(store);
        main.ModeSelection.CancelCommand.Execute(null);
        main.NavigateToCommand.Execute(MainViewModel.Nav.History);

        main.SaveShutdownState(At(120, 80));

        Assert.Equal(MainViewModel.Nav.History, store.Config.LastScreen);
        Assert.Equal(120, store.Config.Window?.Left);
    }

    /// <summary>
    /// Closing from the launch cards keeps the remembered screen (#290). The cards are a
    /// question, not a place - which is exactly why they must not be RECORDED and equally
    /// why closing on them must not ERASE the real place already remembered. This pin used
    /// to assert null here, and since the cards show at every start, that wiped the memory
    /// for anyone who opened the app and closed it without clicking through.
    /// </summary>
    [Fact]
    public void ClosingFromTheCardsKeepsTheRememberedScreen()
    {
        var store = new FakeCredentialStore();
        store.Config.Mode = AppMode.Pro;
        store.Config.LastScreen = MainViewModel.Nav.History;

        var main = new MainViewModel(store);
        // Still on the cards - never carried on.

        main.SaveShutdownState(At(0, 0));

        Assert.Equal(MainViewModel.Nav.History, store.Config.LastScreen);
    }

    /// <summary>A config that would not load is not written back (#7's rule, respected here too).</summary>
    [Fact]
    public void ABrokenConfigIsNotOverwrittenAtShutdown()
    {
        var store = new FakeCredentialStore();
        store.Config.Mode = AppMode.Pro;
        store.Config.LoadError = "unreadable";

        var main = new MainViewModel(store);
        main.SaveShutdownState(At(0, 0));

        Assert.Equal(0, store.SaveConfigCalls);
    }
}
