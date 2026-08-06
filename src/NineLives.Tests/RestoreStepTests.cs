using Blackcat.NineLives.ViewModels;
using Xunit;

namespace Blackcat.NineLives.Tests;

/// <summary>
/// When a step folds away, and when it refuses to (#117 item 3).
///
/// The Restore screen was one long scroll with every step expanded at all times, including the
/// ones already finished and the ones not yet reachable. Collapsing is easy; collapsing without
/// fighting the person using it is the part worth pinning.
/// </summary>
public class RestoreStepTests
{
    private static RestoreStep Step() => new("1. SELECT SOURCE");

    [Fact]
    public void AStepStartsOpen()
    {
        var step = Step();

        Assert.True(step.IsExpanded);
        Assert.False(step.IsComplete);
    }

    [Fact]
    public void FinishingAStepFoldsItAwayAndLeavesItsAnswerOnScreen()
    {
        var step = Step();

        step.Report(isComplete: true, summary: "backups, MyDb on SRV01");

        Assert.False(step.IsExpanded);
        Assert.Equal("backups, MyDb on SRV01", step.Summary);
    }

    /// <summary>
    /// The summary keeps changing while a completed step stays collapsed - editing the target name
    /// updates the heading. Re-collapsing on every one of those would be invisible; re-collapsing
    /// after the user opened it back up would not be.
    /// </summary>
    [Fact]
    public void AlreadyCompleteStepsAreNotCollapsedAgain()
    {
        var step = Step();
        step.Report(isComplete: true, summary: "first");

        step.IsExpanded = true;                              // not via Toggle: no user decision
        step.Report(isComplete: true, summary: "second");

        Assert.True(step.IsExpanded);
        Assert.Equal("second", step.Summary);
    }

    /// <summary>
    /// Somebody who opens a finished step is reading it. A screen that folds it away again the
    /// moment they change the thing they came back to change is worse than one that never folded.
    /// </summary>
    [Fact]
    public void OpeningAStepYourselfStopsItFoldingAwayLater()
    {
        var step = Step();
        step.Report(isComplete: true, summary: "done");
        Assert.False(step.IsExpanded);

        step.ToggleCommand.Execute(null);
        Assert.True(step.IsExpanded);

        // Complete all over again - a different container, say.
        step.Report(isComplete: false, summary: string.Empty);
        step.Report(isComplete: true, summary: "done again");

        Assert.True(step.IsExpanded);
    }

    /// <summary>
    /// Going incomplete reopens it whatever happened before: the contents are the only way to put
    /// it right, and hiding them behind a chevron at that moment helps nobody.
    /// </summary>
    [Fact]
    public void AStepThatBecomesIncompleteOpensAgain()
    {
        var step = Step();
        step.Report(isComplete: true, summary: "done");
        Assert.False(step.IsExpanded);

        step.Report(isComplete: false, summary: string.Empty);

        Assert.True(step.IsExpanded);
    }

    [Fact]
    public void ClosingAStepYourselfIsRespected()
    {
        var step = Step();

        step.ToggleCommand.Execute(null);

        Assert.False(step.IsExpanded);
    }

    [Fact]
    public void TheStepsAreNumberedInTheOrderTheyAreWorkedThrough()
    {
        var steps = new RestoreStepsViewModel();

        Assert.StartsWith("1.", steps.Source.Title);
        Assert.StartsWith("2.", steps.Point.Title);
        Assert.StartsWith("3.", steps.Options.Title);
    }
}
