using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Blackcat.NineLives.ViewModels;

namespace Blackcat.NineLives.Views;

public partial class RestoreView : UserControl
{
    /// <summary>
    /// How close to the bottom still counts as "following". A couple of lines of slack, so a
    /// trackpad nudge or scrollbar rounding does not silently stop the console following.
    /// </summary>
    private const double FollowThreshold = 40;

    private INotifyCollectionChanged? _observedLines;
    private RestoreViewModel? _viewModel;
    private ExecutionWindow? _executionWindow;
    private bool _follow = true;

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
            _observedLines = vm.ConsoleLines;
            _observedLines.CollectionChanged += OnConsoleLinesChanged;
            vm.PropertyChanged += OnViewModelPropertyChanged;
        }
    }

    private void Detach()
    {
        if (_observedLines != null)
            _observedLines.CollectionChanged -= OnConsoleLinesChanged;
        if (_viewModel != null)
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;

        _observedLines = null;
        _viewModel = null;
    }

    /// <summary>
    /// Brings the console up as a modal window when a restore starts.
    ///
    /// Showing a window is the view's job, not the viewmodel's - which is why this watches a
    /// property rather than the viewmodel calling out to a dialog service. The window is modal
    /// because a restore is the one operation here that cannot be undone: it should not compete
    /// with the form behind it, and nobody should be editing the options that produced the script
    /// currently running.
    ///
    /// The inline console stays behind it, so the record is still there once the window is closed.
    /// </summary>
    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(RestoreViewModel.IsExecuting)) return;
        if (_viewModel is not { IsExecuting: true } vm || _executionWindow != null) return;

        _executionWindow = new ExecutionWindow(vm) { Owner = Window.GetWindow(this) };

        // ShowDialog blocks, so it must not run inside the property-changed notification that the
        // restore itself is unwinding through. Posting lets the execution carry on underneath.
        Dispatcher.BeginInvoke(new Action(() =>
        {
            try { _executionWindow?.ShowDialog(); }
            finally { _executionWindow = null; }
        }), DispatcherPriority.Background);
    }

    /// <summary>
    /// Follows the tail as output arrives, but only while the user is already at the bottom.
    ///
    /// A console that always jumps to the end is unusable during a long restore: scroll up to read
    /// the statement that just failed and the next progress message yanks you away again. Scrolling
    /// back to the bottom resumes following, which is what a terminal does.
    /// </summary>
    private void OnConsoleLinesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Add || !_follow) return;

        // At Background priority, so the new rows have been realised by the time this runs.
        // Scrolling first would land short of the real end.
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (ConsoleList.Items.Count > 0)
                ConsoleList.ScrollIntoView(ConsoleList.Items[^1]);
        }), DispatcherPriority.Background);
    }
}
