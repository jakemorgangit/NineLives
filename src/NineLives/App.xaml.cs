using System.Windows;
using Blackcat.NineLives.Views;

namespace Blackcat.NineLives;

public partial class App : Application
{
    /// <summary>How long the splash stays up once the main window has been built.</summary>
    private static readonly TimeSpan SplashDwell = TimeSpan.FromMilliseconds(2500);

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Before anything else. An unhandled exception on the dispatcher thread otherwise takes
        // the process down instantly, and the worst moment for that is mid-restore: the execution
        // log is the only record of how far the chain got, and it exists only in the window that
        // just vanished (#13).
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

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

    /// <summary>
    /// Keeps the app alive after an unhandled UI-thread exception.
    ///
    /// Staying up is the right call here specifically because of what this tool does. If a restore
    /// has partially run, the execution log on screen is the only record of which statements
    /// completed - and that is exactly what someone needs in order to work out what state the
    /// target database is in. Killing the window to be tidy destroys it.
    /// </summary>
    private void OnDispatcherUnhandledException(
        object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;

        MessageBox.Show(
            "Nine Lives hit an unexpected error.\n\n" +
            $"{e.Exception.GetType().Name}: {e.Exception.Message}\n\n" +
            "The app is still running. If a restore was in progress, the execution log on screen " +
            "is still the record of how far it got - check the target database's state before " +
            "retrying.",
            "Nine Lives", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    /// <summary>
    /// A non-UI-thread exception cannot be swallowed - the runtime is already tearing down - so
    /// this only makes sure the user sees something rather than the window disappearing silently.
    /// </summary>
    private void OnAppDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        var message = e.ExceptionObject is Exception ex
            ? $"{ex.GetType().Name}: {ex.Message}"
            : "Unknown error.";

        MessageBox.Show(
            $"Nine Lives has to close.\n\n{message}",
            "Nine Lives", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    /// <summary>
    /// Fire-and-forget work - the update check, the arm countdown - should not be able to bring
    /// the process down when its exception is finally collected. Observing it is enough.
    /// </summary>
    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
        => e.SetObserved();
}
