using Blackcat.NineLives.Models;
using Blackcat.NineLives.ViewModels;
using Xunit;

namespace Blackcat.NineLives.Tests;

/// <summary>
/// An answer belongs to the thing that was asked (#409).
///
/// `ConnectAsync` read `SelectedServer` again after its await, and so did every consequential
/// line after it. A connection attempt is exactly the operation slow enough for somebody to click
/// another entry in the list beside it - the full timeout against an unreachable host - so the
/// app could prove it could reach one server and then announce the other: mark it connected, hand
/// it out as `ConnectedServer`, and write the proved server's version banner onto the other one's
/// saved entry.
///
/// `ConnectedServer` is the object the restore screen EXECUTES against. `RestoreViewModel` carries
/// a comment about a bug of exactly this shape, fixed at the consuming end while the end that
/// produces the value still had it: "so the restore ran under credentials the user never connected
/// with or tested."
/// </summary>
public class CapturedBeforeTheAwaitTests
{
    private static (ServerManagerViewModel Vm, FakeSqlServerService Sql) Screen()
    {
        var store = new FakeCredentialStore();
        foreach (var name in new[] { "SRV01", "SRV02" })
        {
            store.Config.Servers.Add(new ServerConnection
            { Id = ServerConnection.NewId(), Name = name, ServerName = name });
        }

        var sql = new FakeSqlServerService();
        var vm = new ServerManagerViewModel(store, sql);
        return (vm, sql);
    }

    [Fact]
    public async Task ConnectingAnnouncesTheServerItActuallyProved()
    {
        var (vm, sql) = Screen();
        vm.SelectedServer = vm.Servers.First(s => s.ServerName == "SRV01");

        var gate = new TaskCompletionSource();
        sql.HoldConnection = gate;

        var connecting = vm.ConnectCommand.ExecuteAsync(null);

        // What a person does while a connection hangs: click the other one.
        vm.SelectedServer = vm.Servers.First(s => s.ServerName == "SRV02");

        gate.SetResult();
        await connecting;

        // It proved SRV01 ...
        Assert.Equal(["SRV01"], sql.Connected);

        // ... so SRV01 is what it must say, and what it must hand out.
        Assert.True(vm.IsConnected);
        Assert.Contains("SRV01", vm.ConnectedServerDisplay);
        Assert.Contains("SRV01", vm.TestResult);
    }

    /// <summary>
    /// The one with teeth: this object is what the restore screen runs its script against.
    /// </summary>
    [Fact]
    public async Task TheServerHandedToTheRestScreenIsTheOneThatWasProved()
    {
        var (vm, sql) = Screen();
        vm.SelectedServer = vm.Servers.First(s => s.ServerName == "SRV01");

        ServerConnection? announced = null;
        vm.ConnectionChanged += (_, e) => announced = e.ConnectedServer;

        var gate = new TaskCompletionSource();
        sql.HoldConnection = gate;

        var connecting = vm.ConnectCommand.ExecuteAsync(null);
        vm.SelectedServer = vm.Servers.First(s => s.ServerName == "SRV02");
        gate.SetResult();
        await connecting;

        Assert.NotNull(announced);
        Assert.Equal("SRV01", announced!.ServerName);
    }

    /// <summary>
    /// DetectedVersion is saved to config and is what the S3 capability gate and the
    /// version-compatibility preflight are decided on, so writing one server's banner onto
    /// another's entry outlives the mistake.
    /// </summary>
    [Fact]
    public async Task TheDetectedVersionLandsOnTheServerItWasReadFrom()
    {
        var (vm, sql) = Screen();
        vm.SelectedServer = vm.Servers.First(s => s.ServerName == "SRV01");

        var gate = new TaskCompletionSource();
        sql.HoldConnection = gate;

        var connecting = vm.ConnectCommand.ExecuteAsync(null);
        vm.SelectedServer = vm.Servers.First(s => s.ServerName == "SRV02");
        gate.SetResult();
        await connecting;

        var srv01 = vm.Servers.First(s => s.ServerName == "SRV01");
        var srv02 = vm.Servers.First(s => s.ServerName == "SRV02");

        Assert.NotNull(srv01.DetectedVersion);
        Assert.Null(srv02.DetectedVersion);
    }

    [Fact]
    public async Task NothingChangesWhenTheSelectionStaysPut()
    {
        var (vm, sql) = Screen();
        vm.SelectedServer = vm.Servers.First(s => s.ServerName == "SRV02");

        await vm.ConnectCommand.ExecuteAsync(null);

        Assert.Equal(["SRV02"], sql.Connected);
        Assert.Contains("SRV02", vm.ConnectedServerDisplay);
    }
}
