using System.Windows;
using Blackcat.NineLives.ViewModels;

namespace Blackcat.NineLives.Views;

public partial class LogViewerWindow : Window
{
    public LogViewerWindow(LogViewerViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
