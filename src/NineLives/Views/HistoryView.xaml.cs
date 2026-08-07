using System.Windows;
using System.Windows.Controls;

namespace Blackcat.NineLives.Views;

public partial class HistoryView : UserControl
{
    public HistoryView()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Puts focus on the list so the arrow keys work on arrival (#117 item 8).
    ///
    /// A ListBox is arrow-navigable already - nothing was ever giving it focus, which is what "no
    /// keyboard navigation" amounted to. Focusing the list rather than an item leaves the current
    /// selection alone; pressing an arrow then moves from wherever it is.
    /// </summary>
    private void OnLoaded(object sender, RoutedEventArgs e) => EntriesList.Focus();
}
