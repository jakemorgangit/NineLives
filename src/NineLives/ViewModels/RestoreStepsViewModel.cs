using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Blackcat.NineLives.ViewModels;

/// <summary>
/// One numbered step on the Restore screen: whether it is open, and what it says when it is not.
///
/// The screen was one very long scroll with every step expanded at all times, including the ones
/// already finished and the ones not yet reachable (#117 item 3).
/// </summary>
public partial class RestoreStep(string title) : ObservableObject
{
    public string Title { get; } = title;

    [ObservableProperty]
    private bool _isExpanded = true;

    /// <summary>
    /// What the step shows instead of its contents once it is done - "backups, MyDb on SRV01"
    /// rather than nothing, so a collapsed step is still an answer rather than a closed drawer.
    /// </summary>
    [ObservableProperty]
    private string _summary = string.Empty;

    /// <summary>Has enough been chosen here for the next step to mean anything.</summary>
    [ObservableProperty]
    private bool _isComplete;

    /// <summary>
    /// Set the moment the user opens or closes this step themselves, and never cleared.
    ///
    /// After that, completion stops collapsing it. Somebody who opened a step back up is reading
    /// it, and a screen that folds it away again the instant they change something they came back
    /// to change is worse than one that never folded at all.
    /// </summary>
    private bool _userDecided;

    [RelayCommand]
    private void Toggle()
    {
        _userDecided = true;
        IsExpanded = !IsExpanded;
    }

    /// <summary>
    /// Records progress, collapsing the step the first time it is finished.
    ///
    /// Only on the transition into complete: re-running it on every keystroke would fold the step
    /// away while somebody was still working in it.
    /// </summary>
    public void Report(bool isComplete, string summary)
    {
        Summary = summary;

        var justCompleted = isComplete && !IsComplete;
        IsComplete = isComplete;

        if (justCompleted && !_userDecided) IsExpanded = false;

        // Going incomplete reopens it whatever happened before - the contents are the only way to
        // put it right, and hiding them behind a chevron at that moment helps nobody.
        if (!isComplete) IsExpanded = true;
    }
}

/// <summary>
/// The Restore screen's steps. Numbering and titles live here rather than in the markup so the
/// summaries and the headings cannot drift apart.
/// </summary>
public sealed class RestoreStepsViewModel
{
    public RestoreStep Source { get; } = new("1. SELECT SOURCE");
    public RestoreStep Point { get; } = new("2. SELECT RESTORE POINT");
    public RestoreStep Options { get; } = new("3. RESTORE OPTIONS");
}
