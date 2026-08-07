using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Blackcat.NineLives.ViewModels;

/// <summary>
/// One numbered step on the Restore screen (#117 item 3).
///
/// The screen was one very long scroll with every step expanded at all times, including the ones
/// already finished and the ones not yet reachable.
/// </summary>
public partial class RestoreStep(string title, bool needsConfirmation = false) : ObservableObject
{
    public string Title { get; } = title;

    /// <summary>
    /// Whether finishing this step is something the user says, rather than something inferred.
    ///
    /// Choosing a restore point is the decision the whole application exists to support, so it is
    /// not treated as made the instant a dot is clicked - somebody is still comparing points at
    /// that moment. The same goes for the options: they are read, adjusted and read again.
    /// </summary>
    public bool NeedsConfirmation { get; } = needsConfirmation;

    [ObservableProperty]
    private bool _isExpanded = true;

    /// <summary>
    /// Hidden until the step before it is done. A step nobody can act on yet is noise, and on this
    /// screen it is noise between somebody and the dropdown they are trying to use.
    /// </summary>
    [ObservableProperty]
    private bool _isVisible;

    /// <summary>
    /// What the step shows instead of its contents once it is done - "backups, MyDb on SRV01"
    /// rather than nothing, so a collapsed step is still an answer rather than a closed drawer.
    /// </summary>
    [ObservableProperty]
    private string _summary = string.Empty;

    [ObservableProperty]
    private bool _isComplete;

    /// <summary>Whether there is yet anything to confirm - a point chosen, say.</summary>
    [ObservableProperty]
    private bool _canConfirm;

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

    /// <summary>Raised when the step is finished, so the screen can move on to the next one.</summary>
    public event Action<RestoreStep>? Completed;

    /// <summary>The user saying they are done here. Only for steps that ask.</summary>
    [RelayCommand]
    private void Confirm()
    {
        if (!CanConfirm) return;

        IsComplete = true;
        IsExpanded = false;
        Completed?.Invoke(this);
    }

    /// <summary>
    /// Updates what the step says when collapsed, without claiming it is finished.
    ///
    /// For a step with no completed state of its own: the options all have defaults, so there is
    /// no moment at which they become "done" by themselves.
    /// </summary>
    public void Describe(string summary) => Summary = summary;

    /// <summary>
    /// Records progress for a step that finishes by itself, collapsing it the first time it does.
    ///
    /// Only on the transition into complete: re-running it on every keystroke would fold the step
    /// away while somebody was still working in it.
    /// </summary>
    public void Report(bool isComplete, string summary)
    {
        Summary = summary;

        var justCompleted = isComplete && !IsComplete;
        IsComplete = isComplete;

        if (justCompleted)
        {
            if (!_userDecided) IsExpanded = false;
            Completed?.Invoke(this);
            return;
        }

        // Going incomplete reopens it whatever happened before - the contents are the only way to
        // put it right, and hiding them behind a chevron at that moment helps nobody.
        if (!isComplete) IsExpanded = true;
    }

    /// <summary>
    /// Takes back a confirmation because what was confirmed has changed.
    ///
    /// Changing the restore point after confirming it must not leave the later steps standing on
    /// the old answer: a script and a summary describing a moment nobody chose is exactly the
    /// failure this screen cannot have.
    /// </summary>
    public void Invalidate()
    {
        if (!IsComplete) return;
        IsComplete = false;
    }
}

/// <summary>
/// The Restore screen's steps, as a sequence rather than four independent drawers.
///
/// Three rules, all of which came out of using it:
///   - a step is hidden until the one before it is done, so the screen only ever offers what can
///     actually be acted on;
///   - opening one closes the others, because four panes open at once is the long scroll again;
///   - the two steps that decide what gets restored are finished by the user saying so, not by the
///     app inferring it from a click.
/// </summary>
public sealed class RestoreStepsViewModel
{
    public RestoreStep Source { get; } = new("1. SELECT SOURCE");
    public RestoreStep Point { get; } = new("2. SELECT RESTORE POINT", needsConfirmation: true);
    public RestoreStep Options { get; } = new("3. RESTORE OPTIONS", needsConfirmation: true);
    public RestoreStep Execute { get; } = new("4. GENERATE & EXECUTE");

    public IReadOnlyList<RestoreStep> All { get; }

    /// <summary>Guards the closing of the others from reopening this one, and so on.</summary>
    private bool _settling;

    public RestoreStepsViewModel()
    {
        All = [Source, Point, Options, Execute];

        // One open, and it is the only one there is anything to do in yet. RefreshVisibility only
        // acts on a CHANGE, so the steps that start hidden have to start closed too - otherwise
        // they arrive already expanded the moment they become visible.
        Source.IsVisible = true;
        Source.IsExpanded = true;
        Point.IsExpanded = false;
        Options.IsExpanded = false;
        Execute.IsExpanded = false;

        foreach (var step in All)
        {
            var self = step;

            self.Completed += _ => Advance(self);

            self.PropertyChanged += (_, e) =>
            {
                switch (e.PropertyName)
                {
                    case nameof(RestoreStep.IsExpanded) when self.IsExpanded && !_settling:
                        CloseTheOthers(self);
                        break;

                    // Visibility runs off completion: a step is offered once the one before it is
                    // answered, and taken away again if that answer is withdrawn.
                    case nameof(RestoreStep.IsComplete):
                        RefreshVisibility();
                        break;
                }
            };
        }

        RefreshVisibility();
    }

    private void CloseTheOthers(RestoreStep opened)
    {
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
    }

    /// <summary>
    /// Each step is offered once the one before it is done, and taken away again if that answer is
    /// withdrawn.
    ///
    /// Withdrawing CASCADES. A step that is no longer reachable cannot still be complete: leaving
    /// step 3 flagged as answered while it was hidden kept step 4 on screen, so changing a
    /// confirmed restore point left the execute button standing on a chain that no longer existed.
    ///
    /// One forward pass, guarded against re-entry, because clearing a completion raises the very
    /// event that calls this.
    /// </summary>
    private void RefreshVisibility()
    {
        if (_refreshing) return;

        _refreshing = true;
        try
        {
            for (var i = 1; i < All.Count; i++)
            {
                var step = All[i];
                var visible = All[i - 1].IsComplete;

                if (step.IsVisible != visible) step.IsVisible = visible;
                if (visible) continue;

                step.IsExpanded = false;
                step.Invalidate();
            }
        }
        finally
        {
            _refreshing = false;
        }
    }

    private bool _refreshing;

    /// <summary>Opens the next step that is available and not yet answered.</summary>
    private void Advance(RestoreStep from)
    {
        RefreshVisibility();

        var next = All
            .SkipWhile(s => !ReferenceEquals(s, from))
            .Skip(1)
            .FirstOrDefault(s => s.IsVisible && !s.IsComplete);

        if (next != null) next.IsExpanded = true;
    }

    /// <summary>Records progress for a step that finishes by itself.</summary>
    public void Report(RestoreStep step, bool isComplete, string summary)
        => step.Report(isComplete, summary);
}
