using System.Windows;
using System.Windows.Media.Imaging;

namespace Blackcat.NineLives;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
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
