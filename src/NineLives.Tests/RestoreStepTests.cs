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
        Assert.StartsWith("4.", steps.Execute.Title);
    }

    // ── the accordion ───────────────────────────────────────────────────────────

    /// <summary>
    /// One open at a time. Collapsing the finished steps was only half of it: with four panes free
    /// to be open at once the screen could still be as long as it ever was, and step 4 - script,
    /// console and recovery actions - is tall enough to bury everything above it.
    /// </summary>
    [Fact]
    public void OpeningAStepClosesTheOthers()
    {
        var steps = new RestoreStepsViewModel();
        steps.Report(steps.Source, true, "a source");
        steps.Point.CanConfirm = true;
        steps.Point.ConfirmCommand.Execute(null);

        // The hand-over has already opened step 3, so reopen step 2 - going BACK is the case
        // worth pinning anyway, and it must still close whatever was open.
        steps.Point.ToggleCommand.Execute(null);

        Assert.True(steps.Point.IsExpanded);
        Assert.False(steps.Source.IsExpanded);
        Assert.False(steps.Options.IsExpanded);
        Assert.False(steps.Execute.IsExpanded);
    }

    [Fact]
    public void OnlyTheFirstStepIsOpenToBeginWith()
    {
        var steps = new RestoreStepsViewModel();

        Assert.True(steps.Source.IsExpanded);
        Assert.Equal(1, steps.All.Count(s => s.IsExpanded));
    }

    /// <summary>
    /// Finishing a step puts you in the next one still to do, rather than in front of nothing.
    /// That is what makes it read as a sequence instead of four independent drawers.
    /// </summary>
    [Fact]
    public void FinishingAStepOpensTheNextOneStillToDo()
    {
        var steps = new RestoreStepsViewModel();

        steps.Report(steps.Source, isComplete: true, summary: "backups, MyDb on SRV01");

        Assert.False(steps.Source.IsExpanded);
        Assert.True(steps.Point.IsExpanded);
    }

    /// <summary>Already-done steps are skipped over - going back and changing step 1 should not
    /// drop somebody into a step 2 they finished ten minutes ago.</summary>
    [Fact]
    public void GoingBackAndReanswveringStepOneReturnsToStepTwo()
    {
        var steps = Confirmed();

        // Choosing a different database: step 1 goes incomplete, which withdraws everything after
        // it, and answering it again puts the user back at the restore point rather than at the
        // end of a sequence whose answers have just been thrown away.
        steps.Report(steps.Source, isComplete: false, summary: string.Empty);
        Assert.False(steps.Point.IsVisible);

        steps.Report(steps.Source, isComplete: true, summary: "backups, OtherDb on SRV01");

        Assert.True(steps.Point.IsVisible);
        Assert.True(steps.Point.IsExpanded);
        Assert.False(steps.Point.IsComplete);
    }

    /// <summary>
    /// The options step describes itself without ever claiming to be finished.
    ///
    /// The target database name is derived from the chosen source database, so treating a
    /// non-empty one as completion folded this step away the moment a database was picked - and
    /// the hand-over from step 2 then skipped over it to step 4, so the options were never seen.
    /// </summary>
    [Fact]
    public void DescribingAStepUpdatesItsSummaryWithoutFoldingIt()
    {
        var step = Step();

        step.Describe("as MyDb_Restored, RECOVERY");

        Assert.Equal("as MyDb_Restored, RECOVERY", step.Summary);
        Assert.True(step.IsExpanded);
        Assert.False(step.IsComplete);
    }

    /// <summary>Confirming a restore point hands over to the options, not past them.</summary>
    [Fact]
    public void TheHandOverFromThePointLandsOnTheOptions()
    {
        var steps = new RestoreStepsViewModel();
        steps.Report(steps.Source, true, "backups, MyDb on SRV01");

        steps.Point.CanConfirm = true;
        steps.Point.ConfirmCommand.Execute(null);

        Assert.True(steps.Options.IsExpanded);
        Assert.False(steps.Execute.IsExpanded);
    }

    /// <summary>
    /// Step 4 is never reported on - it is the action, with no completed state - so it is where
    /// the hand-over lands once 1 to 3 are done, which is exactly where somebody wants to be.
    /// </summary>
    [Fact]
    public void TheLastStepIsWhereTheHandOverEnds()
    {
        var steps = Confirmed();

        Assert.True(steps.Execute.IsExpanded);
        Assert.Equal(1, steps.All.Count(s => s.IsExpanded));
    }

    // ── only what can be acted on ───────────────────────────────────────────────

    /// <summary>Walks the whole sequence the way a user does.</summary>
    private static RestoreStepsViewModel Confirmed()
    {
        var steps = new RestoreStepsViewModel();
        steps.Report(steps.Source, true, "backups, MyDb on SRV01");
        steps.Point.CanConfirm = true;
        steps.Point.ConfirmCommand.Execute(null);
        steps.Options.CanConfirm = true;
        steps.Options.ConfirmCommand.Execute(null);
        return steps;
    }

    /// <summary>
    /// A step nobody can act on yet is noise - and on this screen it was noise between somebody
    /// and the dropdown they were trying to use. Loading a container used to reveal steps 2, 3 and
    /// 4 immediately, before a database had even been chosen.
    /// </summary>
    [Fact]
    public void OnlyTheFirstStepIsOfferedUntilItIsAnswered()
    {
        var steps = new RestoreStepsViewModel();

        Assert.True(steps.Source.IsVisible);
        Assert.False(steps.Point.IsVisible);
        Assert.False(steps.Options.IsVisible);
        Assert.False(steps.Execute.IsVisible);
    }

    [Fact]
    public void EachStepIsOfferedOnlyOnceTheOneBeforeItIsAnswered()
    {
        var steps = new RestoreStepsViewModel();

        steps.Report(steps.Source, true, "backups, MyDb on SRV01");
        Assert.True(steps.Point.IsVisible);
        Assert.False(steps.Options.IsVisible);

        steps.Point.CanConfirm = true;
        steps.Point.ConfirmCommand.Execute(null);
        Assert.True(steps.Options.IsVisible);
        Assert.False(steps.Execute.IsVisible);

        steps.Options.CanConfirm = true;
        steps.Options.ConfirmCommand.Execute(null);
        Assert.True(steps.Execute.IsVisible);
    }

    // ── confirmation ────────────────────────────────────────────────────────────

    /// <summary>
    /// Choosing a restore point is the decision this application exists to support. Folding the
    /// step away the instant a dot is clicked takes the timeline from somebody who is still
    /// comparing points - so it stays open until they say they are done.
    /// </summary>
    [Fact]
    public void ChoosingAPointDoesNotFinishTheStep()
    {
        var steps = new RestoreStepsViewModel();
        steps.Report(steps.Source, true, "a source");

        steps.Point.Describe("2026-01-10 22:00, Full + 2 logs");
        steps.Point.CanConfirm = true;

        Assert.False(steps.Point.IsComplete);
        Assert.True(steps.Point.IsExpanded);
        Assert.False(steps.Options.IsVisible);
    }

    [Fact]
    public void ConfirmingIsRefusedUntilThereIsSomethingToConfirm()
    {
        var steps = new RestoreStepsViewModel();
        steps.Report(steps.Source, true, "a source");

        steps.Point.ConfirmCommand.Execute(null);

        Assert.False(steps.Point.IsComplete);
    }

    /// <summary>
    /// Changing a confirmed point takes the confirmation back, and the steps that stood on it go
    /// with it. A script and a summary describing a moment nobody chose is exactly the failure
    /// this screen cannot have.
    /// </summary>
    [Fact]
    public void ChangingWhatWasConfirmedWithdrawsTheConfirmation()
    {
        var steps = Confirmed();
        Assert.True(steps.Execute.IsVisible);

        steps.Point.Invalidate();

        Assert.False(steps.Point.IsComplete);
        Assert.False(steps.Options.IsVisible);
        Assert.False(steps.Execute.IsVisible);
    }
}
