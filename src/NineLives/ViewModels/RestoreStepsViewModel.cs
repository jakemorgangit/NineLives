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
    /// <returns>True when this call folded the step away, so the caller can move on to the next.</returns>
    public bool Report(bool isComplete, string summary)
    {
        Summary = summary;

        var justCompleted = isComplete && !IsComplete;
        IsComplete = isComplete;

        if (justCompleted && !_userDecided)
        {
            IsExpanded = false;
            return true;
        }

        // Going incomplete reopens it whatever happened before - the contents are the only way to
        // put it right, and hiding them behind a chevron at that moment helps nobody.
        if (!isComplete) IsExpanded = true;

        return false;
    }
}

/// <summary>
/// The Restore screen's steps. Numbering and titles live here rather than in the markup so the
/// summaries and the headings cannot drift apart.
///
/// They behave as an accordion: opening one closes the others. Collapsing the finished steps was
/// only half the fix - with four panes free to be open at once the screen could still be as long
/// as it ever was, and the script pane in particular is tall enough to bury everything above it.
/// </summary>
public sealed class RestoreStepsViewModel
{
    public RestoreStep Source { get; } = new("1. SELECT SOURCE");
    public RestoreStep Point { get; } = new("2. SELECT RESTORE POINT");
    public RestoreStep Options { get; } = new("3. RESTORE OPTIONS");
    public RestoreStep Execute { get; } = new("4. GENERATE & EXECUTE");

    public IReadOnlyList<RestoreStep> All { get; }

    /// <summary>Guards the closing of the others from reopening this one, and so on.</summary>
    private bool _settling;

    public RestoreStepsViewModel()
    {
        All = [Source, Point, Options, Execute];

        // One open to begin with, and it is the one there is anything to do in.
        Point.IsExpanded = false;
        Options.IsExpanded = false;
        Execute.IsExpanded = false;

        foreach (var step in All)
        {
            var opened = step;
            opened.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName != nameof(RestoreStep.IsExpanded)) return;
                if (!opened.IsExpanded || _settling) return;

                _settling = true;
                try
                {
                    foreach (var other in All)
                        if (!ReferenceEquals(other, opened)) other.IsExpanded = false;
                }
                finally
                {
                    _settling = false;
                }
            };
        }
    }

    /// <summary>
    /// Records a step's progress and, when that folds it away, opens the next one still to do.
    ///
    /// This is what makes it read as a sequence rather than four independent drawers: finishing
    /// step 1 puts you in step 2 rather than in front of nothing. Step 4 is never reported on - it
    /// is the action, with no completed state to speak of - so it ends up being the step that is
    /// opened once 1 to 3 are done, which is exactly where somebody wants to be by then.
    /// </summary>
    public void Report(RestoreStep step, bool isComplete, string summary)
    {
        if (!step.Report(isComplete, summary)) return;

        var next = All
            .SkipWhile(s => !ReferenceEquals(s, step))
            .Skip(1)
            .FirstOrDefault(s => !s.IsComplete);

        if (next != null) next.IsExpanded = true;
    }
}
