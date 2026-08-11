using Blackcat.NineLives.Models;
using Blackcat.NineLives.Services;
using Blackcat.NineLives.ViewModels;
using Xunit;

namespace Blackcat.NineLives.Tests;

/// <summary>
/// Two ways the app said yes when the answer was neither (#368, #369).
///
/// A container that answers and holds nothing rendered "Connected! 0 files found (0 B)" - the
/// commonest new-user failure there is, presented in the voice of a tick, and the user meets an
/// inexplicably empty Browse Backups two screens later with nothing to connect it to. And
/// Ctrl+1..9 stayed live behind the first-run mode cards, which the code called a gate there was
/// no way past.
/// </summary>
public class ReachedButEmptyTests
{
    // ── a container that answers and holds nothing ──────────────────────────────

    private static (BlobConfigViewModel vm, FakeBlobStorageService blob) Screen(
        params BackupFileInfo[] files)
    {
        var store = new FakeCredentialStore();
        store.Config.BlobContainers.Add(new BlobContainerConfig
        {
            Id = BlobContainerConfig.NewId(),
            Name = "backups",
            ContainerUrl = "https://acct.blob.core.windows.net/backups"
        });

        var blob = new FakeBlobStorageService { Files = files.ToList() };
        var vm = new BlobConfigViewModel(store, blob);
        vm.SelectedContainer = vm.Containers.First();
        return (vm, blob);
    }

    private static BackupFileInfo File(string name) => new()
    {
        BlobName = name,
        BlobUrl = $"https://acct.blob.core.windows.net/backups/{name}",
        Type = BackupType.Full,
        SizeBytes = 1024
    };

    [Fact]
    public async Task AContainerThatAnswersAndHoldsNothingIsNotReportedAsASuccess()
    {
        var (vm, _) = Screen();

        await vm.TestConnectionCommand.ExecuteAsync(null);

        Assert.True(vm.TestFoundNothing);
        Assert.DoesNotContain("0 files found", vm.TestResult);

        // The credential still gets its sentence - it IS proven, and sending somebody off to
        // re-check a SAS token that works is its own wasted afternoon.
        Assert.Contains("credential works", vm.TestResult);

        // And the actual next move, which is the one thing the old message did not have.
        Assert.Contains("base path", vm.TestResult);
    }

    [Fact]
    public async Task AContainerWithBackupsInItStillReadsAsASuccess()
    {
        var (vm, _) = Screen(File("20260804_220000.bak"), File("20260804_230000.trn"));

        await vm.TestConnectionCommand.ExecuteAsync(null);

        Assert.False(vm.TestFoundNothing);
        Assert.True(vm.TestSuccess);
        Assert.Contains("Connected!", vm.TestResult);
        Assert.Contains("2 files found", vm.TestResult);
    }

    /// <summary>
    /// The warning state does not outlive the test that raised it. The refusals in this command
    /// return before the result is written, so a stale flag would have painted the next test's
    /// error message as a warning.
    /// </summary>
    [Fact]
    public async Task AnEmptyResultDoesNotColourTheNextTest()
    {
        var (vm, blob) = Screen();

        await vm.TestConnectionCommand.ExecuteAsync(null);
        Assert.True(vm.TestFoundNothing);

        blob.Files = [File("20260804_220000.bak")];
        await vm.TestConnectionCommand.ExecuteAsync(null);

        Assert.False(vm.TestFoundNothing);
    }

    [Fact]
    public async Task AFailedTestIsNotDressedAsAnEmptyOne()
    {
        var (vm, blob) = Screen();
        await vm.TestConnectionCommand.ExecuteAsync(null);
        Assert.True(vm.TestFoundNothing);

        blob.ListThrows = new InvalidOperationException("403 forbidden");
        await vm.TestConnectionCommand.ExecuteAsync(null);

        Assert.False(vm.TestFoundNothing);
        Assert.False(vm.TestSuccess);
    }

    // ── the keyboard behind the first-run cards ─────────────────────────────────

    /// <summary>
    /// Ctrl+1 on the mode cards used to land on Blob Storage with the sidebar collapsed to zero
    /// width, no mode chosen, no navigation and no way back - an app that had to be restarted.
    /// </summary>
    [Fact]
    public void NavigationIsRefusedWhileTheModeCardsAreUp()
    {
        // A store with no mode chosen puts the cards up on construction - the first-run case.
        var main = new MainViewModel(new FakeCredentialStore());

        Assert.True(main.IsChoosingMode);
        var before = main.CurrentView;

        main.NavigateToCommand.Execute(MainViewModel.Nav.BlobStorage);

        Assert.True(main.IsChoosingMode);
        Assert.Same(before, main.CurrentView);
        Assert.Equal(new System.Windows.GridLength(0), main.SidebarWidth);
    }

    /// <summary>Choosing a mode still navigates - the gate closes on the keyboard, not on itself.</summary>
    [Fact]
    public void ChoosingAModeStillLands()
    {
        var main = new MainViewModel(new FakeCredentialStore());
        Assert.True(main.IsChoosingMode);

        main.ModeSelection.ChooseCommand.Execute(
            main.ModeSelection.Cards.Single(c => c.Mode == AppMode.Standard));

        Assert.False(main.IsChoosingMode);
        Assert.Equal(MainViewModel.Nav.Home, main.CurrentViewName);
    }

    /// <summary>
    /// A mode that hides a screen hides it from the keyboard too. Basic mode's sidebar offers
    /// neither Back Up nor Browse Backups, and Ctrl+4 and Ctrl+3 reached both.
    /// </summary>
    [Theory]
    [InlineData(MainViewModel.Nav.Backup)]
    [InlineData(MainViewModel.Nav.BrowseBackups)]
    [InlineData(MainViewModel.Nav.CopyDatabase)]
    public void AScreenTheModeHidesIsNotReachableByKeyboard(string hidden)
    {
        var main = Launched.App(AppMode.Basic);

        Assert.False(main.IsViewAvailable(hidden));

        main.NavigateToCommand.Execute(hidden);

        Assert.Equal(MainViewModel.Nav.Home, main.CurrentViewName);
    }

    [Fact]
    public void AScreenTheModeOffersIsStillReachable()
    {
        var main = Launched.App(AppMode.Basic);

        main.NavigateToCommand.Execute(MainViewModel.Nav.Restore);

        Assert.Equal(MainViewModel.Nav.Restore, main.CurrentViewName);
    }
}
