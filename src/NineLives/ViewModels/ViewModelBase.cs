using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

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

    /// <summary>
    /// Transient: what the app is doing, or has just done.
    ///
    /// Deliberately does NOT clear the error any more (#117 item 6). The two shared one line, so
    /// the next thing that happened painted over the last thing that went wrong - "no valid
    /// restore points" vanishing under "Loaded 1 files" was this, and it was fixed narrowly at
    /// that one call site rather than here. An error now outlives the status messages that follow
    /// it.
    /// </summary>
    protected void SetStatus(string message) => StatusMessage = message;

    /// <summary>
    /// Sticky: survives until another error supersedes it, the user dismisses it, or the next
    /// operation starts and calls <see cref="ClearStatus"/>.
    ///
    /// Still writes the status line as well, because not every screen surfaces both - the History
    /// screen binds only the status line, and an error there would otherwise be silent.
    /// </summary>
    protected void SetError(string message)
    {
        HasError = true;
        ErrorMessage = message;
        StatusMessage = message;
    }

    /// <summary>
    /// Both surfaces, for the start of an operation. Called by every long-running command before
    /// it does anything, which is what stops a fixed-and-retried failure leaving its error behind.
    /// </summary>
    protected void ClearStatus()
    {
        StatusMessage = string.Empty;
        HasError = false;
        ErrorMessage = string.Empty;
    }

    /// <summary>Dismisses the error without touching the status line.</summary>
    [RelayCommand]
    private void DismissError()
    {
        HasError = false;
        ErrorMessage = string.Empty;
    }

    /// <summary>
    /// Puts text on the clipboard, reporting failure through the status surface instead of
    /// throwing out of a synchronous command and taking the process with it (#13).
    ///
    /// Clipboard.SetText throwing is an ordinary Windows condition, not an exceptional one -
    /// another process holding the clipboard lock is routine with remote desktop sessions and
    /// clipboard managers, and it was reproduced while the issue was being written. A failed copy
    /// deserves a line in the status bar, not a crash.
    /// </summary>
    protected bool TryCopyToClipboard(string? text, string successMessage)
    {
        if (string.IsNullOrEmpty(text)) return false;

        try
        {
            Clipboard.SetText(text);
            SetStatus(successMessage);
            return true;
        }
        catch (Exception ex)
        {
            SetError(
                $"Could not copy to the clipboard: {ex.Message}. " +
                "Another application may be holding it - try again in a moment.");
            return false;
        }
    }
}
