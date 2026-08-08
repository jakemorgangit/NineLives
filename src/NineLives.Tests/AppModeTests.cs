using Blackcat.NineLives.Models;
using Blackcat.NineLives.Services;
using Blackcat.NineLives.ViewModels;
using Xunit;

namespace Blackcat.NineLives.Tests;

/// <summary>
/// How much of the app is on screen (#176).
///
/// The app grew from "restore a database from a blob container" into a backup and restore
/// orchestrator with two media, a copy action, a header audit and a credential panel - all of it
/// visible to everybody. Somebody whose job is "restore last night's full onto the test box" should
/// not read past four steps and seven sidebar entries to do it.
///
/// The promise the picker makes, and the one these mostly guard: narrowing NEVER deletes anything.
/// If that stopped being true it would become a decision people are afraid to make, and the whole
/// idea would do nothing.
/// </summary>
public class AppModeTests
{
    // ── what each mode turns on ─────────────────────────────────────────────────

    /// <summary>Basic is what the app originally was: restore, from a container.</summary>
    [Fact]
    public void BasicIsTheOriginalApp()
    {
        Assert.False(AppModeCapabilities.CanBackUp(AppMode.Basic));
        Assert.False(AppModeCapabilities.CanUseSharedPath(AppMode.Basic));
        Assert.False(AppModeCapabilities.CanCopyBetweenServers(AppMode.Basic));
        Assert.False(AppModeCapabilities.CanVerifyAndAudit(AppMode.Basic));
        Assert.False(AppModeCapabilities.CanRestoreToAPointInTime(AppMode.Basic));
    }

    [Fact]
    public void StandardAddsTheSecondMediumAndTakingBackups()
    {
        Assert.True(AppModeCapabilities.CanBackUp(AppMode.Standard));
        Assert.True(AppModeCapabilities.CanUseSharedPath(AppMode.Standard));
        Assert.True(AppModeCapabilities.CanRestoreToAPointInTime(AppMode.Standard));
        Assert.True(AppModeCapabilities.CanRelocateFiles(AppMode.Standard));
    }

    /// <summary>
    /// The Pro-only ones are the features that answer a question somebody has to know to ask - and
    /// the ones that cost minutes and reach across the network.
    /// </summary>
    [Fact]
    public void ProAddsTheChecksAndTheThingsThatTouchTwoServers()
    {
        Assert.True(AppModeCapabilities.CanCopyBetweenServers(AppMode.Pro));
        Assert.True(AppModeCapabilities.CanVerifyAndAudit(AppMode.Pro));
        Assert.True(AppModeCapabilities.CanManageServerCredentials(AppMode.Pro));
        Assert.True(AppModeCapabilities.CanScriptAsAgentJob(AppMode.Pro));

        Assert.False(AppModeCapabilities.CanCopyBetweenServers(AppMode.Standard));
        Assert.False(AppModeCapabilities.CanVerifyAndAudit(AppMode.Standard));
    }

    /// <summary>
    /// Every capability is monotonic: nothing available in a narrower mode disappears in a wider
    /// one. A card that says "everything in Standard" has to be true, and somebody widening the
    /// mode to get one feature must not silently lose another.
    /// </summary>
    [Fact]
    public void NothingAvailableInANarrowerModeVanishesInAWiderOne()
    {
        Func<AppMode, bool>[] capabilities =
        [
            AppModeCapabilities.CanBackUp,
            AppModeCapabilities.CanCopyBetweenServers,
            AppModeCapabilities.CanBrowseBackups,
            AppModeCapabilities.CanUseSharedPath,
            AppModeCapabilities.CanRestoreToAPointInTime,
            AppModeCapabilities.CanRelocateFiles,
            AppModeCapabilities.CanVerifyAndAudit,
            AppModeCapabilities.CanManageServerCredentials,
            AppModeCapabilities.CanScriptAsAgentJob,
            AppModeCapabilities.CanUseAdvancedRestoreOptions
        ];

        foreach (var can in capabilities)
        {
            if (can(AppMode.Basic)) Assert.True(can(AppMode.Standard));
            if (can(AppMode.Standard)) Assert.True(can(AppMode.Pro));
        }
    }

    /// <summary>Restoring is what the app is for, and no mode takes it away.</summary>
    [Theory]
    [InlineData(AppMode.Basic)]
    [InlineData(AppMode.Standard)]
    [InlineData(AppMode.Pro)]
    public void EveryModeCanStillRestore(AppMode mode)
    {
        var vm = Restore(mode);

        Assert.NotNull(vm.LoadBackupsCommand);
        Assert.NotNull(vm.ExecuteScriptCommand);
    }

    // ── the first-run screen ────────────────────────────────────────────────────

    /// <summary>
    /// Asked once. Being asked on every launch would be a worse problem than the clutter this
    /// exists to fix.
    /// </summary>
    [Fact]
    public void WithNoModeChosenTheCardsAreShown()
    {
        var main = new MainViewModel(new FakeCredentialStore());

        Assert.True(main.IsChoosingMode);
        Assert.Same(main.ModeSelection, main.CurrentView);
    }

    [Fact]
    public void OnceChosenTheCardsDoNotComeBack()
    {
        var store = new FakeCredentialStore();
        store.Config.Mode = AppMode.Basic;

        var main = new MainViewModel(store);

        Assert.False(main.IsChoosingMode);
        Assert.Equal(AppMode.Basic, main.Mode);
        Assert.NotSame(main.ModeSelection, main.CurrentView);
    }

    /// <summary>The choice is remembered, or it would be asked again next time.</summary>
    [Fact]
    public void ChoosingAModeSavesIt()
    {
        var store = new FakeCredentialStore();
        var vm = new ModeSelectionViewModel(store);

        vm.ChooseCommand.Execute(vm.Cards.Single(c => c.Mode == AppMode.Standard));

        Assert.Equal(AppMode.Standard, store.Config.Mode);
    }

    /// <summary>
    /// A config that failed to LOAD must not be written back - saving over it turns a transient read
    /// failure into permanent data loss, which is the whole of #7.
    /// </summary>
    [Fact]
    public void AConfigThatWouldNotLoadIsNotOverwrittenByTheChoice()
    {
        var store = new FakeCredentialStore();
        store.Config.LoadError = "config.json could not be read";

        var vm = new ModeSelectionViewModel(store);
        vm.ChooseCommand.Execute(vm.Cards.First());

        Assert.Equal(0, store.SaveConfigCalls);
    }

    /// <summary>
    /// An unreadable or unrecognised mode lands on Pro, not Basic. Hiding features from somebody
    /// whose config got mangled looks like the app has lost a capability; showing too many is merely
    /// untidy.
    /// </summary>
    [Fact]
    public void AConfigThatWillNotLoadShowsEverythingRatherThanNothing()
    {
        var store = new FakeCredentialStore();
        store.Config.LoadError = "unreadable";

        Assert.Equal(AppMode.Pro, new MainViewModel(store).Mode);
    }

    // ── changing it later ───────────────────────────────────────────────────────

    /// <summary>
    /// The promise the picker makes. If narrowing deleted anything it would become a decision people
    /// are afraid to make.
    /// </summary>
    [Fact]
    public void NarrowingTheModeDeletesNothing()
    {
        var store = new FakeCredentialStore();
        store.Config.Mode = AppMode.Pro;
        store.Config.BlobContainers.Add(new BlobContainerConfig
        { Id = "c1", Name = "backups", ContainerUrl = "https://acct.blob.core.windows.net/backups" });
        store.Config.Servers.Add(new ServerConnection
        { Id = ServerConnection.NewId(), Name = "SRV01", ServerName = "SRV01" });

        var main = new MainViewModel(store);
        main.Mode = AppMode.Basic;

        Assert.Single(store.Config.BlobContainers);
        Assert.Single(store.Config.Servers);
    }

    /// <summary>
    /// A screen the new mode hides must not stay on display underneath a sidebar that no longer
    /// offers it - there would be no way back to it and no way to tell why.
    /// </summary>
    [Fact]
    public void NarrowingTheModeMovesOffAScreenThatIsNoLongerOffered()
    {
        var store = new FakeCredentialStore();
        store.Config.Mode = AppMode.Pro;

        var main = new MainViewModel(store);
        main.NavigateToCommand.Execute(MainViewModel.Nav.CopyDatabase);
        Assert.Same(main.CopyDatabase, main.CurrentView);

        main.Mode = AppMode.Basic;

        Assert.NotSame(main.CopyDatabase, main.CurrentView);
        Assert.Same(main.Restore, main.CurrentView);
    }

    [Fact]
    public void AScreenTheModeStillOffersIsLeftAlone()
    {
        var store = new FakeCredentialStore();
        store.Config.Mode = AppMode.Pro;

        var main = new MainViewModel(store);
        main.NavigateToCommand.Execute(MainViewModel.Nav.History);

        main.Mode = AppMode.Standard;

        Assert.Same(main.History, main.CurrentView);
    }

    /// <summary>
    /// Narrowing out of a shared-path restore must not leave the medium selected with nothing on
    /// screen saying so - the app would be restoring FROM DISK while looking like blob.
    /// </summary>
    [Fact]
    public void NarrowingToBasicPutsTheMediumBackToBlob()
    {
        var vm = Restore(AppMode.Standard);
        vm.SelectedMedium = BackupMedium.SharedPath;

        vm.Mode = AppMode.Basic;

        Assert.Equal(BackupMedium.AzureBlob, vm.SelectedMedium);
        Assert.False(vm.ShowMediumChoice);
    }

    // ── what the cards say ──────────────────────────────────────────────────────

    [Fact]
    public void EveryCardSaysWhoItIsForAndWhatItTurnsOn()
    {
        var vm = new ModeSelectionViewModel(new FakeCredentialStore());

        Assert.Equal(3, vm.Cards.Count);

        foreach (var card in vm.Cards)
        {
            Assert.NotEmpty(card.Title);
            Assert.NotEmpty(card.Tagline);
            Assert.NotEmpty(card.WhoFor);
            Assert.NotEmpty(card.Highlights);
        }
    }

    /// <summary>
    /// Three distinct shades, and deliberately not a traffic light - the modes are more and less,
    /// not better and worse.
    /// </summary>
    [Fact]
    public void EachCardHasItsOwnShade()
    {
        var vm = new ModeSelectionViewModel(new FakeCredentialStore());

        Assert.Equal(3, vm.Cards.Select(c => c.AccentBrushKey).Distinct().Count());
    }

    private static RestoreViewModel Restore(AppMode mode)
    {
        var store = new FakeCredentialStore();

        return new RestoreViewModel(
            new FakeBlobStorageService(), new FakeSqlServerService(), new BackupChainBuilder(),
            new RestoreScriptGenerator(), store, TestLogs.Temp(),
            new FakeRestoreHistoryStore(), TestAuditStores.Temp())
        {
            Mode = mode
        };
    }
}
