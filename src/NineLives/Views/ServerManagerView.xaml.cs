using System.Windows;
using System.Windows.Controls;
using Blackcat.NineLives.ViewModels;

namespace Blackcat.NineLives.Views;

public partial class ServerManagerView : UserControl
{
    public ServerManagerView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Wire up password box (can't bind PasswordBox.Password directly for security)
        if (DataContext is ServerManagerViewModel vm)
        {
            PasswordInput.PasswordChanged += (_, _) => vm.EditPassword = PasswordInput.Password;
        }
    }
}
