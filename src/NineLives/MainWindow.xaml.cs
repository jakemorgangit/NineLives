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
    }
}
