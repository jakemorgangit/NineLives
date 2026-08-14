using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Blackcat.NineLives.Models;
using Blackcat.NineLives.ViewModels;
using Blackcat.NineLives.Views;
using Xunit;

namespace Blackcat.NineLives.Tests;

/// <summary>
/// Renders a screen to a PNG so somebody can look at it.
///
/// WPF fails a binding silently: a wrong path, a missing StaticResource on a template applied at
/// runtime, a panel that collapses to nothing - the compiler is happy, every unit test passes, and
/// the screen is simply wrong. This is the cheapest thing that catches that class, and it costs one
/// command rather than a publish, a launch and a manual look.
///
/// Opt-in: set NINELIVES_RENDER to the output directory. It is not a CI gate - there is no
/// assertion about what the pixels should be, and there should not be one. It exists to produce
/// something a human can look at.
///
///     NINELIVES_RENDER=C:\temp\shots dotnet test --filter FullyQualifiedName~ViewRenderHarness
/// </summary>
public sealed class RequiresRenderFactAttribute : FactAttribute
{
    public RequiresRenderFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(ViewRenderHarness.OutputDirectory))
            Skip = "Set NINELIVES_RENDER to an output directory to render the screens.";
    }
}

public class ViewRenderHarness
{
    internal static string? OutputDirectory =>
        Environment.GetEnvironmentVariable("NINELIVES_RENDER");

    /// <summary>
    /// Every screen in one test, on one thread, on purpose: WPF allows a single Application per
    /// process and its resource dictionaries have thread affinity, so a test-per-screen renders the
    /// first and then fails on the rest.
    /// </summary>
    [RequiresRenderFact]
    public void RenderScreens() => Render(new Dictionary<string, Func<(UserControl, object)>>
    {
        ["backup-screen"] = () =>
        {
            var sql = new FakeSqlServerService { DatabaseList = ["Sales", "Archive", "Reporting"] };
            var vm = new BackupViewModel(StoreWithAServer(), sql, TestLogs.Temp());
            vm.Server = vm.Servers.First();
            vm.Container = vm.Containers.First();
            vm.SelectedDatabase = "Sales";
            vm.GenerateCommand.Execute(null);
            return (new BackupView(), vm);
        },

        ["copy-screen"] = () =>
        {
            var sql = new FakeSqlServerService { DatabaseList = ["Sales", "Archive"] };
            var vm = new CopyDatabaseViewModel(StoreWithAServer(), sql, TestLogs.Temp());
            vm.SourceServer = vm.Servers.First();
            vm.TargetServer = vm.Servers.Last();
            vm.Container = vm.Containers.First();
            vm.SourceDatabase = "Sales";
            vm.TargetDatabaseName = "Sales_Copy";
            vm.GenerateCommand.Execute(null);
            return (new CopyDatabaseView(), vm);
        },
    });

    // Deliberately invented names. Nothing real belongs in a file that might be attached to an
    // issue or a pull request.
    private static FakeCredentialStore StoreWithAServer()
    {
        var store = new FakeCredentialStore();
        store.Config.Servers.Add(new ServerConnection
        { Id = ServerConnection.NewId(), Name = "SRV01", ServerName = "SRV01" });
        store.Config.Servers.Add(new ServerConnection
        { Id = ServerConnection.NewId(), Name = "SRV02", ServerName = "SRV02" });
        store.Config.BlobContainers.Add(new BlobContainerConfig
        {
            Id = "c1",
            Name = "backups",
            ContainerUrl = "https://acct.blob.core.windows.net/backups"
        });
        return store;
    }

    private static void Render(Dictionary<string, Func<(UserControl View, object Model)>> screens)
    {
        var directory = OutputDirectory!;
        Exception? failure = null;

        // WPF needs an STA thread, and one Application for the merged theme dictionaries that
        // every StaticResource in these views resolves against.
        var thread = new Thread(() =>
        {
            try
            {
                var app = Application.Current ?? new Application();
                foreach (var source in new[] { "Palette.Dark", "Controls", "Logo" })
                {
                    app.Resources.MergedDictionaries.Add(new ResourceDictionary
                    {
                        Source = new Uri(
                            $"pack://application:,,,/NineLives;component/Themes/{source}.xaml",
                            UriKind.Absolute)
                    });
                }

                Directory.CreateDirectory(directory);

                foreach (var (name, build) in screens)
                {
                    var (view, model) = build();
                    view.DataContext = model;

                    var size = new Size(1500, 1400);
                    view.Measure(size);
                    view.Arrange(new Rect(size));
                    view.UpdateLayout();

                    var bitmap = new RenderTargetBitmap(
                        (int)size.Width, (int)size.Height, 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(view);

                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var stream = File.Create(Path.Combine(directory, $"{name}.png"));
                    encoder.Save(stream);
                }
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (failure != null) throw failure;
    }
}
