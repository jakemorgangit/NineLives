using Blackcat.NineLives.Models;
using Blackcat.NineLives.Services;
using Blackcat.NineLives.ViewModels;
using Xunit;

namespace Blackcat.NineLives.Tests;

/// <summary>
/// The confirmation banner names the instance the restore will actually run against (#479).
///
/// The red panel above Execute is the last thing anybody reads before an irreversible write, and
/// its own comment in RestoreView.xaml says why it is worded the way it is: "blackcatsvr01 and
/// blackcatsvr02 differ by one character".
///
/// It binds ConnectedServerName. Every server call on this screen - including the restore itself -
/// uses ConnectedServer. Those are two different things, and they diverged:
///
///   ConnectedServerName was only ever written after a successful connection probe, or by the
///   shell when the SQL Servers screen connects. Picking a different target on THIS screen sets
///   ConnectedServer, and the probe is skipped when IsConnectedToServer is already true - which it
///   is, because connecting on the Servers screen is how most people get here.
///
/// So: connect to SRV01 on the Servers screen, come to Restore, change the target to SRV02, arm.
/// The banner said SRV01. The restore ran against SRV02.
///
/// The step that changes the target is the one this screen invites you to use - its own subtitle
/// offers it as the equivalent of connecting elsewhere - so this is not an exotic path.
/// </summary>
public class ArmBannerNamesTheRealTargetTests
{
    private static RestoreViewModel Screen()
    {
        var store = new FakeCredentialStore();
        store.Config.Servers.Add(new ServerConnection
        { Id = "s1", Name = "SRV01", ServerName = "SRV01" });
        store.Config.Servers.Add(new ServerConnection
        { Id = "s2", Name = "SRV02", ServerName = "SRV02" });

        return new RestoreViewModel(
            new FakeBlobStorageService(), new FakeSqlServerService(), new BackupChainBuilder(),
            new RestoreScriptGenerator(), store, new FakeOperationHistoryStore(),
            TestLogs.Temp(), TestAuditStores.Temp());
    }

    /// <summary>How the shell announces a connection made on the SQL Servers screen.</summary>
    private static void ConnectedElsewhereTo(RestoreViewModel vm, string id)
    {
        var server = vm.TargetServers.First(s => s.Id == id);
        vm.IsConnectedToServer = true;
        vm.ConnectedServerName = server.ServerName;
        vm.ConnectedServer = server;
    }

    /// <summary>The reported divergence, in the order somebody does it.</summary>
    [Fact]
    public void ChangingTheTargetChangesWhatTheBannerNames()
    {
        var vm = Screen();
        ConnectedElsewhereTo(vm, "s1");

        vm.SelectedTargetServer = vm.TargetServers.First(s => s.Id == "s2");

        // Where the restore will actually run - this was already right.
        Assert.Equal("SRV02", vm.ConnectedServer?.ServerName);

        // What the banner tells the user it will run against.
        Assert.Equal("SRV02", vm.ConnectedServerName);
    }

    /// <summary>
    /// The same through the property the rest of the screen sets, since every server call reads
    /// ConnectedServer and the name has to follow it however it was set.
    /// </summary>
    [Fact]
    public void SettingTheConnectedServerDirectlyRenamesTheBannerToo()
    {
        var vm = Screen();
        ConnectedElsewhereTo(vm, "s1");

        vm.ConnectedServer = vm.TargetServers.First(s => s.Id == "s2");

        Assert.Equal("SRV02", vm.ConnectedServerName);
    }

    /// <summary>Disconnecting leaves nothing named, rather than the last server that was.</summary>
    [Fact]
    public void DisconnectingClearsTheName()
    {
        var vm = Screen();
        ConnectedElsewhereTo(vm, "s1");

        vm.ConnectedServer = null;

        Assert.Equal(string.Empty, vm.ConnectedServerName);
    }
}
