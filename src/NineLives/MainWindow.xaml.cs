using System.Windows;
using System.Windows.Media.Imaging;
using Blackcat.NineLives.ViewModels;

namespace Blackcat.NineLives;

public partial class MainWindow : Window
{
    public MainWindow() : this(new MainViewModel()) { }

    /// <summary>
    /// Takes the viewmodel so a test can supply one built against a temp config directory. The
    /// DataContext used to be declared in XAML, which made that impossible - the real one was
    /// constructed during InitializeComponent whatever the caller did.
    /// </summary>
    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        try
        {
            Icon = new BitmapImage(new Uri("pack://application:,,,/Assets/app.ico", UriKind.Absolute));
        }
        catch
        {
            // Icon load failure is non-fatal
        }

        // Where the window was last time (#211): applied before first paint, and only when the
        // position still puts a grabbable title bar on a screen - a monitor that was unplugged
        // since must not swallow the window.
        SourceInitialized += (_, _) =>
        {
            var saved = viewModel.SavedGeometry;
            if (saved == null) return;

            if (saved.IsUsableOn(
                    SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                    SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight))
            {
                Left = saved.Left;
                Top = saved.Top;
                Width = saved.Width;
                Height = saved.Height;
            }

            // Maximised is remembered even when the position was not usable - it lands maximised
            // on whichever screen Windows chose, which is what anyone means by "it was maximised".
            if (saved.IsMaximized) WindowState = WindowState.Maximized;
        };

        Closing += (_, _) =>
        {
            // RestoreBounds, not Left/Top: a maximised window's own coordinates describe the
            // maximised rectangle, and restoring those on next launch would recreate a
            // maximised-sized window that is not actually maximised.
            var bounds = WindowState == WindowState.Normal
                ? new Rect(Left, Top, Width, Height)
                : RestoreBounds;

            viewModel.SaveShutdownState(new Models.WindowGeometry
            {
                Left = bounds.Left,
                Top = bounds.Top,
                Width = bounds.Width,
                Height = bounds.Height,
                IsMaximized = WindowState == WindowState.Maximized
            });
        };
    }
}
