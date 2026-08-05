using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Blackcat.NineLives.ViewModels;

public abstract partial class ViewModelBase : ObservableObject
{
    /// <summary>
    /// Replaces a bound collection's contents IN PLACE.
    ///
    /// The config models are plain serialised objects with no PropertyChanged, so assigning a
    /// new collection to one of their properties is invisible to a bound ItemsControl - the UI
    /// only catches up when the item is re-rendered for some other reason, which is why edited
    /// tags appeared to need a navigate-away-and-back. Clearing and refilling the SAME instance
    /// raises CollectionChanged, which the binding does observe.
    /// </summary>
    protected static void ReplaceTags(ObservableCollection<string> target, IEnumerable<string> values)
    {
        target.Clear();
        foreach (var value in values) target.Add(value);
    }

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private bool _hasError;

    [ObservableProperty]
    private string _errorMessage = string.Empty;

    protected void SetStatus(string message)
    {
        StatusMessage = message;
        HasError = false;
        ErrorMessage = string.Empty;
    }

    protected void SetError(string message)
    {
        HasError = true;
        ErrorMessage = message;
        StatusMessage = message;
    }

    protected void ClearStatus()
    {
        StatusMessage = string.Empty;
        HasError = false;
        ErrorMessage = string.Empty;
    }
}
