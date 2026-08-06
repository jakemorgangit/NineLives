using System.Globalization;
using Blackcat.NineLives.ViewModels;
using Xunit;

namespace Blackcat.NineLives.Tests;

/// <summary>
/// The point-in-time (STOPAT) target, extracted from RestoreViewModel in #115 seam 3.
///
/// None of this had a test. It was ~130 lines in the middle of a 2,500-line class and reaching it
/// meant a container, a listing, a chain and a selected log restore point - so the rules that decide
/// whether a restore stops at 14:23:41 or discards the afternoon were only ever checked by hand.
///
/// The bounds matter: the target is constrained to the LAST log's window because every earlier log
/// then applies in full. A target inside an earlier log would need the chain truncated to that log,
/// or the later logs restore across a gap and fail with error 4305.
/// </summary>
public class PointInTimeViewModelTests
{
    private static readonly DateTime Earliest = new(2026, 1, 10, 22, 0, 0);
    private static readonly DateTime Latest = new(2026, 1, 10, 22, 15, 0);

    private static PointInTimeViewModel WithWindow()
    {
        var vm = new PointInTimeViewModel();
        vm.SetWindow((Earliest, Latest));
        return vm;
    }

    // ── the window ──────────────────────────────────────────────────────────────

    [Fact]
    public void AWindowOffersTheTargetPrefilledWithTheLatestUsableTime()
    {
        var vm = WithWindow();

        Assert.True(vm.CanUse);
        Assert.Equal(Earliest, vm.Earliest);
        Assert.Equal(Latest, vm.Latest);
        Assert.Equal("2026-01-10 22:15:00", vm.StopAtText);

        // Prefilled but not ticked - showing the bounds is not the same as committing to them.
        Assert.False(vm.Use);
        Assert.Contains("Valid range", vm.Message);
    }

    [Fact]
    public void NoWindowTurnsTheWholeThingOff()
    {
        var vm = WithWindow();
        vm.Use = true;

        // A full or differential restore point: stopping partway through means nothing.
        vm.SetWindow(null);

        Assert.False(vm.CanUse);
        Assert.False(vm.Use);
        Assert.Null(vm.Earliest);
        Assert.Null(vm.Latest);
        Assert.Empty(vm.StopAtText);
        Assert.Null(vm.StopAt);
        Assert.Empty(vm.Message);
        Assert.False(vm.HasError);
        Assert.Null(vm.Effective);
    }

    /// <summary>
    /// Moving to a different log has to drop the previous one's tick as well as its bounds. A
    /// target carried across would be validated against the new window and, if it happened to fall
    /// inside it, would silently discard transactions nobody asked to discard.
    /// </summary>
    [Fact]
    public void ANewWindowStartsUnticked()
    {
        var vm = WithWindow();
        vm.Use = true;

        vm.SetWindow((Earliest.AddHours(1), Latest.AddHours(1)));

        Assert.False(vm.Use);
        Assert.Null(vm.Effective);
        Assert.Equal("2026-01-10 23:15:00", vm.StopAtText);
    }

    // ── bounds ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Exclusive: at exactly the previous set's time nothing from this log has been applied yet,
    /// which is the EARLIER restore point, not this one.
    /// </summary>
    [Fact]
    public void TheLowerBoundIsExclusive()
    {
        var vm = WithWindow();
        vm.Use = true;

        vm.StopAtText = "2026-01-10 22:00:00";

        Assert.Null(vm.StopAt);
        Assert.True(vm.HasError);
        Assert.Contains("Must be after", vm.Message);
        Assert.Contains("select an earlier restore point", vm.Message);
    }

    [Fact]
    public void OneSecondIntoTheLogIsAccepted()
    {
        var vm = WithWindow();
        vm.Use = true;

        vm.StopAtText = "2026-01-10 22:00:01";

        Assert.Equal(new DateTime(2026, 1, 10, 22, 0, 1), vm.StopAt);
        Assert.False(vm.HasError);
    }

    /// <summary>Inclusive: the end of the log is exactly what restoring the whole log gives.</summary>
    [Fact]
    public void TheUpperBoundIsInclusive()
    {
        var vm = WithWindow();
        vm.Use = true;

        vm.StopAtText = "2026-01-10 22:15:00";

        Assert.Equal(Latest, vm.StopAt);
        Assert.False(vm.HasError);
    }

    [Fact]
    public void OneSecondPastTheEndOfTheLogIsRejected()
    {
        var vm = WithWindow();
        vm.Use = true;

        vm.StopAtText = "2026-01-10 22:15:01";

        Assert.Null(vm.StopAt);
        Assert.True(vm.HasError);
        Assert.Contains("Must be at or before", vm.Message);
        Assert.Contains("select a later restore point", vm.Message);
    }

    // ── what was typed ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData("2026-01-10 22:10:30")]
    [InlineData("2026-01-10T22:10:30")]
    [InlineData("2026-01-10 22:10")]
    [InlineData("2026-01-10T22:10")]
    [InlineData("  2026-01-10 22:10:30  ")]
    public void TheAcceptedShapesAllParse(string text)
    {
        var vm = WithWindow();
        vm.Use = true;

        vm.StopAtText = text;

        Assert.NotNull(vm.StopAt);
        Assert.False(vm.HasError);
    }

    [Theory]
    [InlineData("")]
    [InlineData("2026-01-")]
    [InlineData("10/01/2026 22:10")]
    [InlineData("yesterday")]
    public void AnythingElseIsRejectedWithTheShapeItWants(string text)
    {
        var vm = WithWindow();
        vm.Use = true;

        vm.StopAtText = text;

        Assert.Null(vm.StopAt);
        Assert.True(vm.HasError);
        Assert.Contains("yyyy-MM-dd HH:mm:ss", vm.Message);
    }

    /// <summary>
    /// Parsed invariantly and exactly. A target read as 3 February when the user meant 3 March is a
    /// month of transactions silently discarded - and 10/01/2026 is a different day in en-US and
    /// en-GB, so an ambiguous shape is refused outright rather than guessed at.
    /// </summary>
    [Fact]
    public void ADateIsReadTheSameWayWhateverTheMachinesCulture()
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("en-US");

            var vm = WithWindow();
            vm.Use = true;
            vm.StopAtText = "2026-01-10 22:10:30";

            Assert.Equal(new DateTime(2026, 1, 10, 22, 10, 30), vm.StopAt);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    // ── when it counts as an error ──────────────────────────────────────────────

    /// <summary>
    /// Half-typed input in a box nobody has ticked is not a reason to block the restore - the box
    /// is prefilled and editable whether or not STOPAT is being used.
    /// </summary>
    [Fact]
    public void ABadValueIsNotAnErrorUntilItIsBeingUsed()
    {
        var vm = WithWindow();

        vm.StopAtText = "2026-01-";

        Assert.Null(vm.StopAt);
        Assert.False(vm.HasError);
        Assert.Null(vm.Effective);
    }

    [Fact]
    public void TickingTheBoxOverABadValueRaisesTheError()
    {
        var vm = WithWindow();
        vm.StopAtText = "2026-01-";
        Assert.False(vm.HasError);

        vm.Use = true;

        Assert.True(vm.HasError);
    }

    [Fact]
    public void TheMessageSaysWhatWillHappenOnceItIsBeingUsed()
    {
        var vm = WithWindow();
        vm.StopAtText = "2026-01-10 22:10:30";
        Assert.Contains("Valid range", vm.Message);

        vm.Use = true;

        Assert.Contains("Recovery will stop at 2026-01-10 22:10:30", vm.Message);
        Assert.Contains("discarded", vm.Message);
    }

    // ── what reaches the script ─────────────────────────────────────────────────

    [Fact]
    public void NothingIsGeneratedUntilTheBoxIsTicked()
    {
        var vm = WithWindow();

        // A perfectly valid target, sitting in an unticked box.
        vm.StopAtText = "2026-01-10 22:10:30";

        Assert.NotNull(vm.StopAt);
        Assert.Null(vm.Effective);
    }

    [Fact]
    public void ATickedValidTargetIsWhatGetsGenerated()
    {
        var vm = WithWindow();
        vm.Use = true;

        vm.StopAtText = "2026-01-10 22:10:30";

        Assert.Equal(new DateTime(2026, 1, 10, 22, 10, 30), vm.Effective);
    }

    /// <summary>
    /// A ticked box over a rejected target must generate nothing rather than fall back to the whole
    /// log: the user asked to stop somewhere and silently restoring past it is the failure that
    /// matters here.
    /// </summary>
    [Fact]
    public void ATickedInvalidTargetGeneratesNothing()
    {
        var vm = WithWindow();
        vm.Use = true;

        vm.StopAtText = "2026-01-10 23:59:59";

        Assert.True(vm.HasError);
        Assert.Null(vm.Effective);
    }
}
