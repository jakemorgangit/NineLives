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
            _observedLines = vm.ConsoleLines;
            _observedLines.CollectionChanged += OnConsoleLinesChanged;
        }
    }

    private void Detach()
    {
        if (_observedLines != null)
            _observedLines.CollectionChanged -= OnConsoleLinesChanged;
        _observedLines = null;
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

        // At Background priority, so the new item has been realised and the extent has grown by
        // the time this runs. Scrolling first would land short of the real end.
        Dispatcher.BeginInvoke(new Action(() => ConsoleScroller?.ScrollToEnd()),
            DispatcherPriority.Background);
    }

    private void ConsoleScroller_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (sender is not ScrollViewer viewer) return;

        // Only reconsider when the user moved, not when new output changed the extent - otherwise
        // arriving lines would themselves look like a deliberate scroll away from the bottom.
        if (e.ExtentHeightChange != 0) return;

        var distanceFromBottom = viewer.ExtentHeight - viewer.VerticalOffset - viewer.ViewportHeight;
        _follow = distanceFromBottom <= FollowThreshold;
    }
}
