using System.Windows;
using Blackcat.NineLives.Views;

namespace Blackcat.NineLives;

public partial class App : Application
{
    /// <summary>How long the splash stays up once the main window has been built.</summary>
    private static readonly TimeSpan SplashDwell = TimeSpan.FromMilliseconds(1400);

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Explicit for the whole startup sequence: while the splash is briefly the only window,
        // the default OnLastWindowClose would quit the app the moment it closed. Handed back to
        // the main window once that exists.
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        SplashWindow? splash = null;
        try
        {
            splash = new SplashWindow();
            splash.Show();

            // Let the splash paint before the main window is constructed - building it first
            // would leave an unpainted frame on screen.
            await Task.Yield();

            var main = new MainWindow();
            await Task.Delay(SplashDwell);

            main.Show();
            MainWindow = main;
            ShutdownMode = ShutdownMode.OnMainWindowClose;

            await splash.FadeOutAsync();
            splash = null;
        }
        catch (Exception ex)
        {
            // Startup decoration must never be able to leave the app running with no window -
            // that is indistinguishable from a silent crash. Close the splash, say what happened,
            // and open the main window regardless.
            splash?.Close();

            MessageBox.Show(
                $"Nine Lives could not complete startup.\n\n{ex.Message}",
                "Nine Lives", MessageBoxButton.OK, MessageBoxImage.Warning);

            if (MainWindow == null)
            {
                var fallback = new MainWindow();
                fallback.Show();
                MainWindow = fallback;
            }

            ShutdownMode = ShutdownMode.OnMainWindowClose;
        }
    }
}
