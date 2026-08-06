using System.Windows;
using System.Windows.Controls;
using Blackcat.NineLives.Models;
using Blackcat.NineLives.Services;
using Blackcat.NineLives.ViewModels;
using Blackcat.NineLives.Views;
using Xunit;

namespace Blackcat.NineLives.Tests;

/// <summary>
/// The saved servers list marks the one that is connected (#127). Connecting used to change the
/// banner at the top of the window and nothing in the list, so with more than a screenful of
/// servers there was no way to tell which one was live.
/// </summary>
public class ServerConnectedMarkerTests
{
    private readonly FakeCredentialStore _store = new();
    private readonly FakeSqlServerService _sql = new();

    private ServerManagerViewModel NewViewModel() => new(_store, _sql);

    private ServerConnection Add(string name)
    {
        var server = new ServerConnection
        {
            Id = ServerConnection.NewId(),
            Name = name,
            ServerName = name,
            AuthMode = AuthMode.WindowsAuth
        };
        _store.Config.Servers.Add(server);
        return server;
    }

    [Fact]
    public async Task ConnectingMarksThatServerAndOnlyThatServer()
    {
        Add("SRV01");
        Add("SRV02");

        var vm = NewViewModel();
        vm.SelectedServer = vm.Servers.Single(s => s.Name == "SRV02");

        await vm.ConnectCommand.ExecuteAsync(null);

        Assert.False(vm.Servers.Single(s => s.Name == "SRV01").IsConnectedServer);
        Assert.True(vm.Servers.Single(s => s.Name == "SRV02").IsConnectedServer);
    }

    [Fact]
    public async Task ConnectingToADifferentServerMovesTheMarker()
    {
        Add("SRV01");
        Add("SRV02");

        var vm = NewViewModel();
        vm.SelectedServer = vm.Servers.Single(s => s.Name == "SRV01");
        await vm.ConnectCommand.ExecuteAsync(null);

        vm.SelectedServer = vm.Servers.Single(s => s.Name == "SRV02");
        await vm.ConnectCommand.ExecuteAsync(null);

        // Two servers marked connected at once would be worse than none.
        Assert.Single(vm.Servers, s => s.IsConnectedServer);
        Assert.True(vm.Servers.Single(s => s.Name == "SRV02").IsConnectedServer);
    }

    [Fact]
    public async Task DisconnectingClearsTheMarker()
    {
        Add("SRV01");

        var vm = NewViewModel();
        vm.SelectedServer = vm.Servers.Single();
        await vm.ConnectCommand.ExecuteAsync(null);

        vm.DisconnectCommand.Execute(null);

        Assert.DoesNotContain(vm.Servers, s => s.IsConnectedServer);
    }

    [Fact]
    public void TheMarkerIsNotPersisted()
    {
        // It describes this session. Writing it to config.json would have the app claim a
        // connection it does not have on the next launch.
        var server = Add("SRV01");
        server.IsConnectedServer = true;

        var json = System.Text.Json.JsonSerializer.Serialize(server);

        Assert.DoesNotContain("IsConnectedServer", json);
    }
}

/// <summary>
/// The list renders the marker, and no longer repeats the server name and auth mode underneath it.
/// </summary>
[Collection(WpfCollection.Name)]
public class ServerListViewTests(WpfFixture wpf)
{
    [Fact]
    public void TheConnectedServerIsMarkedInTheList()
    {
        wpf.Invoke(() =>
        {
            var store = new FakeCredentialStore();
            store.Config.Servers.Add(new ServerConnection
            {
                Id = ServerConnection.NewId(),
                Name = "SRV01",
                ServerName = "SRV01"
            });

            var vm = new ServerManagerViewModel(store, new FakeSqlServerService());
            vm.Servers.Single().IsConnectedServer = true;

            var view = new ServerManagerView { DataContext = vm };
            var listener = BindingErrorListener.Attach();
            try
            {
                Realise(view);

                var texts = FindAll<TextBlock>(view).Select(t => t.Text).ToList();
                Assert.Contains("connected", texts);

                listener.AssertNone("ServerManagerView connected marker");
            }
            finally
            {
                listener.Detach();
            }
        });
    }

    [Fact]
    public void TheListDoesNotRepeatTheAuthenticationLine()
    {
        wpf.Invoke(() =>
        {
            var store = new FakeCredentialStore();
            store.Config.Servers.Add(new ServerConnection
            {
                Id = ServerConnection.NewId(),
                Name = "SRV01",
                ServerName = "SRV01",
                AuthMode = AuthMode.WindowsAuth
            });

            var vm = new ServerManagerViewModel(store, new FakeSqlServerService());
            var view = new ServerManagerView { DataContext = vm };
            Realise(view);

            // DisplayText - "SRV01 (Windows Auth)" - was shown on every card. It repeats the
            // server name and adds a detail that only matters when editing or when a connection
            // fails, both of which belong to the detail pane.
            var texts = FindAll<TextBlock>(view).Where(IsShown).Select(t => t.Text).ToList();
            Assert.DoesNotContain(texts, t => t == "SRV01 (Windows Auth)");

            // The name itself is still there.
            Assert.Contains("SRV01", texts);
        });
    }

    private static void Realise(FrameworkElement element)
    {
        element.ApplyTemplate();
        element.Measure(new Size(1400, 900));
        element.Arrange(new Rect(0, 0, 1400, 900));
        element.UpdateLayout();
    }

    private static bool IsShown(FrameworkElement element)
    {
        for (DependencyObject? node = element; node != null;
             node = System.Windows.Media.VisualTreeHelper.GetParent(node))
        {
            if (node is Window) break;
            if (node is UIElement { Visibility: not Visibility.Visible }) return false;
        }
        return true;
    }

    private static IEnumerable<T> FindAll<T>(DependencyObject root) where T : DependencyObject
    {
        var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var descendant in FindAll<T>(child)) yield return descendant;
        }
    }
}
