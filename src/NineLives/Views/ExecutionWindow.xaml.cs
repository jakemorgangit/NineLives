using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Blackcat.NineLives.ViewModels;

namespace Blackcat.NineLives.Views;

/// <summary>
/// The console, front and centre while a restore runs.
///
/// Modal on purpose. A restore is the one operation in this app that cannot be undone and should
/// not be competing for attention with the form behind it - and it stops someone changing the
/// options that produced the script currently executing.
/// </summary>
public partial class ExecutionWindow : Window
{
    private const double FollowThreshold = 40;

    private readonly RestoreViewModel _viewModel;
    private ScrollViewer? _scroller;
    private bool _follow = true;

    public ExecutionWindow(RestoreViewModel viewModel)
    {
        InitializeComponent();
        DataContext = _viewModel = viewModel;

        viewModel.Execution.Console.Lines.CollectionChanged += OnConsoleLinesChanged;
        ConsoleList.Loaded += (_, _) => HookScroller();
        Closed += (_, _) => viewModel.Execution.Console.Lines.CollectionChanged -= OnConsoleLinesChanged;
    }

    private void HookScroller()
    {
        _scroller = FindScroller(ConsoleList);
        if (_scroller != null) _scroller.ScrollChanged += OnScrollChanged;
    }

    private static ScrollViewer? FindScroller(DependencyObject root)
    {
        if (root is ScrollViewer found) return found;

        var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var result = FindScroller(System.Windows.Media.VisualTreeHelper.GetChild(root, i));
            if (result != null) return result;
        }
        return null;
    }

    /// <summary>
    /// Follows the tail while the user is at the bottom, and stops the moment they scroll up.
    ///
    /// One scroll per batch of arrivals, not one per line - the viewmodel already flushes output on
    /// a timer, so this rides that rhythm instead of fighting it.
    /// </summary>
    private void OnConsoleLinesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Add || !_follow) return;

        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (ConsoleList.Items.Count > 0)
                ConsoleList.ScrollIntoView(ConsoleList.Items[^1]);
        }), DispatcherPriority.Background);
    }

    private void OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (sender is not ScrollViewer viewer) return;

        // Only reconsider when the user moved, not when new output changed the extent.
        if (e.ExtentHeightChange != 0) return;

        _follow = viewer.ExtentHeight - viewer.VerticalOffset - viewer.ViewportHeight <= FollowThreshold;
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    /// <summary>
    /// Refuses to close mid-restore. Closing would not stop anything - it would just hide it - and
    /// the Stop button is right there for someone who actually wants it to end.
    /// </summary>
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_viewModel.IsExecuting) e.Cancel = true;
    }
}
