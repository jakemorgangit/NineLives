using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Blackcat.NineLives.ViewModels;

namespace Blackcat.NineLives.Views;

public partial class RestoreView : UserControl
{
    private RestoreViewModel? _viewModel;
    private ExecutionWindow? _executionWindow;

    public RestoreView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Unloaded += (_, _) => Detach();
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        Detach();

        if (e.NewValue is RestoreViewModel vm)
        {
            _viewModel = vm;
            vm.PropertyChanged += OnViewModelPropertyChanged;
        }
    }

    private void Detach()
    {
        if (_viewModel != null)
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;

        _viewModel = null;
    }

    /// <summary>
    /// The run's output lives in the ExecutionWindow, and ONLY there. The inline console this
    /// view used to keep as a fallback duplicated every line and panel behind the modal - two
    /// consoles for one run. Reopening is the same window over the same viewmodel, so the full
    /// record and its actions come back exactly as they were; the History screen keeps the
    /// permanent copy.
    ///
    /// Showing a window is the view's job, not the viewmodel's - which is why execution is
    /// watched through a property rather than the viewmodel calling out to a dialog service. The
    /// window is modal because a restore is the one operation here that cannot be undone: nobody
    /// should be editing the options that produced the script currently running.
    /// </summary>
    private void ShowExecutionWindow(RestoreViewModel vm)
    {
        if (_executionWindow != null) return;

        _executionWindow = new ExecutionWindow(vm) { Owner = Window.GetWindow(this) };

        // ShowDialog blocks, so it must not run inside the property-changed notification that the
        // restore itself is unwinding through. Posting lets the execution carry on underneath.
        Dispatcher.BeginInvoke(new Action(() =>
        {
            try { _executionWindow?.ShowDialog(); }
            finally
            {
                _executionWindow = null;
            }
        }), DispatcherPriority.Background);
    }

    private void OnViewLastRunClicked(object sender, RoutedEventArgs e)
    {
        if (_viewModel != null) ShowExecutionWindow(_viewModel);
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(RestoreViewModel.IsExecuting)) return;
        if (_viewModel is not { IsExecuting: true } vm) return;

        ShowExecutionWindow(vm);
    }
}
