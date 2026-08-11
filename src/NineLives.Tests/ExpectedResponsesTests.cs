using Blackcat.NineLives.Models;
using Blackcat.NineLives.Services;
using Blackcat.NineLives.ViewModels;
using Xunit;

namespace Blackcat.NineLives.Tests;

/// <summary>
/// Things the app should say, and keys it should answer (#370).
///
/// A copy overwrites a database, and said so only through a ticked checkbox - while the
/// property that would have said it out loud existed, documented, bound to nothing. And Esc
/// reached the restore but not the two other screens that write, so the stop key worked
/// everywhere except two of the three places something is being written.
/// </summary>
public class ExpectedResponsesTests
{
    // ── the copy says what it will overwrite ────────────────────────────────────

    private static CopyDatabaseViewModel Copy()
    {
        var store = new FakeCredentialStore();
        store.Config.Servers.Add(new ServerConnection
        { Id = ServerConnection.NewId(), Name = "SRV01", ServerName = "SRV01" });
        store.Config.Servers.Add(new ServerConnection
        { Id = ServerConnection.NewId(), Name = "SRV02", ServerName = "SRV02" });

        return new CopyDatabaseViewModel(store, new FakeSqlServerService());
    }

    [Fact]
    public void AnOverwritingCopyNamesWhatItWillReplace()
    {
        var vm = Copy();
        vm.TargetServer = vm.Servers.Last();
        vm.TargetDatabaseName = "SalesTest";
        vm.WithReplace = true;

        Assert.True(vm.WillOverwriteTheTarget);

        // The database and the server both, because the risk of this screen is doing it to the
        // wrong one - a generic "the target will be overwritten" is what eyes slide off.
        Assert.Contains("SalesTest", vm.OverwriteWarning);
        Assert.Contains("SRV02", vm.OverwriteWarning);
    }

    [Fact]
    public void ACopyThatOverwritesNothingSaysNothing()
    {
        var vm = Copy();
        vm.TargetServer = vm.Servers.Last();
        vm.TargetDatabaseName = "SalesTest";
        vm.WithReplace = false;

        Assert.False(vm.WillOverwriteTheTarget);
    }

    // ── Esc reaches everything that writes ──────────────────────────────────────
    //
    // The backup and copy branches are deliberately not pinned here. Cancel is a no-op without
    // a live cancellation token, and a running backup cannot be staged without running one - so
    // any test of that routing would assert something that is true whether or not the branch
    // exists. A test that cannot fail is worse than no test: it reads like cover.


    /// <summary>F5 on the one screen whose content is a snapshot of a moving estate.</summary>
    [Fact]
    public void ReloadReachesTheExposureDashboard()
    {
        var main = new MainViewModel(new FakeCredentialStore());
        main.NavigateToCommand.Execute(MainViewModel.Nav.Exposure);

        Assert.Equal(MainViewModel.Nav.Exposure, main.CurrentViewName);
        Assert.True(main.Exposure.RefreshCommand.CanExecute(null));
    }
}
