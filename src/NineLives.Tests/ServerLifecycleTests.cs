using Blackcat.NineLives.Models;
using Blackcat.NineLives.Services;
using Blackcat.NineLives.ViewModels;
using Xunit;

namespace Blackcat.NineLives.Tests;

/// <summary>
/// The connected server survives its own lifecycle honestly (#291). Delete recognised the
/// connected entry by display TEXT captured at connect time, and Save mutates the name in
/// place - so renaming then deleting the connected server left the status bar claiming a
/// connection to an object that no longer existed in config. Editing the connected entry's
/// address silently repointed the SAME object at untested settings; renaming onto an existing
/// name was allowed, making every text comparison ambiguous.
/// </summary>
public class ServerLifecycleTests
{
    private static (ServerManagerViewModel vm, FakeCredentialStore store) New(params string[] names)
    {
        var store = new FakeCredentialStore();
        foreach (var name in names)
            store.Config.Servers.Add(new ServerConnection
            {
                Id = ServerConnection.NewId(),
                Name = name,
                ServerName = name,
                AuthMode = AuthMode.WindowsAuth
            });

        var vm = new ServerManagerViewModel(store, new FakeSqlServerService())
        {
            ConfirmDelete = _ => true
        };
        return (vm, store);
    }

    private static void Rename(ServerManagerViewModel vm, string to)
    {
        vm.EditCommand.Execute(null);
        vm.EditName = to;
        vm.SaveCommand.Execute(null);
    }

    [Fact]
    public async Task DeletingTheRenamedConnectedServerStillDropsTheConnection()
    {
        var (vm, _) = New("SRV01");
        vm.SelectedServer = vm.Servers.Single();
        await vm.ConnectCommand.ExecuteAsync(null);
        Assert.True(vm.IsConnected);

        var dropped = false;
        vm.ConnectionChanged += (_, e) => dropped = !e.IsConnected;

        Rename(vm, "SRV01 (primary)");
        Assert.True(vm.IsConnected);   // a rename alone does not unprove the connection

        vm.DeleteCommand.Execute(null);

        Assert.False(vm.IsConnected);
        Assert.True(dropped);
        Assert.Empty(vm.ConnectedServerDisplay);
    }

    /// <summary>The caption names the ADDRESS, which a rename does not change - so the
    /// proven connection and its caption both stand.</summary>
    [Fact]
    public async Task ARenameAloneKeepsTheProvenConnection()
    {
        var (vm, _) = New("SRV01");
        vm.SelectedServer = vm.Servers.Single();
        await vm.ConnectCommand.ExecuteAsync(null);
        var captionBefore = vm.ConnectedServerDisplay;

        Rename(vm, "Production");

        Assert.True(vm.IsConnected);
        Assert.Equal(captionBefore, vm.ConnectedServerDisplay);
        Assert.True(vm.Servers.Single().IsConnectedServer);
    }

    /// <summary>
    /// The old settings were proven; the new ones are not. A connection that survives an
    /// address change is a claim nobody tested.
    /// </summary>
    [Fact]
    public async Task ChangingTheConnectedServersAddressDropsTheConnection()
    {
        var (vm, _) = New("SRV01");
        vm.SelectedServer = vm.Servers.Single();
        await vm.ConnectCommand.ExecuteAsync(null);

        var dropped = false;
        vm.ConnectionChanged += (_, e) => dropped = !e.IsConnected;

        vm.EditCommand.Execute(null);
        vm.EditServerName = "SRV01.other.example";
        vm.SaveCommand.Execute(null);

        Assert.False(vm.IsConnected);
        Assert.True(dropped);
        Assert.Contains("connect again", vm.StatusMessage);
    }

    [Fact]
    public async Task EditingAnUnconnectedServerLeavesTheConnectionAlone()
    {
        var (vm, _) = New("SRV01", "SRV02");
        vm.SelectedServer = vm.Servers.Single(s => s.Name == "SRV01");
        await vm.ConnectCommand.ExecuteAsync(null);

        vm.SelectedServer = vm.Servers.Single(s => s.Name == "SRV02");
        vm.EditCommand.Execute(null);
        vm.EditServerName = "SRV02.other.example";
        vm.SaveCommand.Execute(null);

        Assert.True(vm.IsConnected);
        Assert.Contains("SRV01", vm.ConnectedServerDisplay);
    }

    [Fact]
    public void ARenameOntoAnExistingNameIsRefused()
    {
        var (vm, store) = New("SRV01", "SRV02");
        vm.SelectedServer = vm.Servers.Single(s => s.Name == "SRV02");

        Rename(vm, "srv01");

        Assert.Contains("already exists", vm.ErrorMessage);
        Assert.Equal("SRV02", store.Config.Servers[1].Name);
    }
}
