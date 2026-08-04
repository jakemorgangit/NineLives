using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Blackcat.NineLives.Models;
using Blackcat.NineLives.ViewModels;

namespace Blackcat.NineLives.Views;

public partial class BlobConfigView : UserControl
{
    private Point _dragStartPoint;
    private bool _isDragging;

    public BlobConfigView()
    {
        InitializeComponent();
    }

    private void PathElement_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStartPoint = e.GetPosition(null);
        _isDragging = false;
    }

    private void PathElement_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;

        var pos = e.GetPosition(null);
        var diff = pos - _dragStartPoint;
        if (Math.Abs(diff.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(diff.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        if (_isDragging) return;
        _isDragging = true;

        var listBox = sender as ListBox;
        var element = FindAncestor<ListBoxItem>((DependencyObject)e.OriginalSource);
        if (listBox == null || element == null) return;

        var data = element.DataContext as PathElement;
        if (data == null) return;

        var dragData = new DataObject("PathElement", data);
        DragDrop.DoDragDrop(element, dragData, DragDropEffects.Move);
        _isDragging = false;
    }

    private void PathDragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent("PathElement"))
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }
        e.Effects = DragDropEffects.Move;
        e.Handled = true;
    }

    private void PathDrop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent("PathElement")) return;
        if (DataContext is not BlobConfigViewModel vm) return;

        var droppedElement = e.Data.GetData("PathElement") as PathElement;
        if (droppedElement == null) return;

        var fromIndex = vm.ActivePathElements.IndexOf(droppedElement);
        if (fromIndex < 0) return;

        int toIndex = GetDropIndex(ActivePathList, e);
        if (toIndex < 0) toIndex = vm.ActivePathElements.Count - 1;

        vm.MovePathElement(fromIndex, toIndex);
    }

    private void AgPathElement_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStartPoint = e.GetPosition(null);
        _isDragging = false;
    }

    private void AgPathElement_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;

        var pos = e.GetPosition(null);
        var diff = pos - _dragStartPoint;
        if (Math.Abs(diff.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(diff.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        if (_isDragging) return;
        _isDragging = true;

        var listBox = sender as ListBox;
        var element = FindAncestor<ListBoxItem>((DependencyObject)e.OriginalSource);
        if (listBox == null || element == null) return;

        var data = element.DataContext as PathElement;
        if (data == null) return;

        var dragData = new DataObject("PathElement", data);
        DragDrop.DoDragDrop(element, dragData, DragDropEffects.Move);
        _isDragging = false;
    }

    private void AgPathDragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent("PathElement"))
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }
        e.Effects = DragDropEffects.Move;
        e.Handled = true;
    }

    private void AgPathDrop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent("PathElement")) return;
        if (DataContext is not BlobConfigViewModel vm) return;

        var droppedElement = e.Data.GetData("PathElement") as PathElement;
        if (droppedElement == null) return;

        var fromIndex = vm.AgActivePathElements.IndexOf(droppedElement);
        if (fromIndex < 0) return;

        int toIndex = GetDropIndex(AgActivePathList, e);
        if (toIndex < 0) toIndex = vm.AgActivePathElements.Count - 1;

        vm.MoveAgPathElement(fromIndex, toIndex);
    }

    private int GetDropIndex(ListBox listBox, DragEventArgs e)
    {
        var pos = e.GetPosition(listBox);

        for (int i = 0; i < listBox.Items.Count; i++)
        {
            if (listBox.ItemContainerGenerator.ContainerFromIndex(i) is not ListBoxItem container)
                continue;

            var itemPos = container.TranslatePoint(new Point(0, 0), listBox);
            var itemCenter = itemPos.X + container.ActualWidth / 2;

            if (pos.X < itemCenter) return i;
        }

        return listBox.Items.Count - 1;
    }

    private static T? FindAncestor<T>(DependencyObject current) where T : DependencyObject
    {
        while (current != null)
        {
            if (current is T t) return t;
            current = VisualTreeHelper.GetParent(current);
        }
        return null;
    }
}
