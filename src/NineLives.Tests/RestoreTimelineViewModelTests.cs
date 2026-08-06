using System.Globalization;
using Blackcat.NineLives.Models;
using Blackcat.NineLives.ViewModels;
using Xunit;

namespace Blackcat.NineLives.Tests;

/// <summary>
/// The timeline's layout, filters and selection, now that they are their own type rather than 300
/// lines in the middle of RestoreViewModel (#115 seam 2).
///
/// Most of these tests moved here from RestoreViewModelTests, where each one stood up a ViewModel,
/// a fake blob service and a fake credential store, and awaited a container listing - to assert
/// where a dot sits on a track. None of that is needed: the timeline is handed restore points and
/// decides how to show them.
/// </summary>
public class RestoreTimelineViewModelTests
{
    private static readonly DateTime T0 = new(2026, 1, 10, 22, 0, 0);

    private static BackupSet Set(BackupType type, DateTime timestamp) => new()
    {
        Type = type,
        Timestamp = timestamp,
        SetId = $"{type}-{timestamp:yyyyMMddHHmmss}",
        DatabaseName = "MyDb",
        Files = [new BackupFileInfo { BlobName = "backup.bak", SizeBytes = 1024, Type = type }]
    };

    private static RestorePoint Point(DateTime timestamp, BackupType type = BackupType.TransactionLog)
    {
        var own = Set(type, timestamp);
        return new RestorePoint
        {
            Timestamp = timestamp,
            Type = type,
            PrimarySet = own,
            RequiredFullSet = type == BackupType.Full ? own : Set(BackupType.Full, T0)
        };
    }

    /// <summary>A full at T0 plus <paramref name="logCount"/> logs at hourly intervals after it.</summary>
    private static List<RestorePoint> FullPlusLogs(int logCount)
    {
        var points = new List<RestorePoint> { Point(T0, BackupType.Full) };
        for (int i = 1; i <= logCount; i++) points.Add(Point(T0.AddHours(i)));
        return points;
    }

    private static RestoreTimelineViewModel Loaded(int logCount = 3)
    {
        var timeline = new RestoreTimelineViewModel();
        timeline.Load(FullPlusLogs(logCount));
        return timeline;
    }

    // ── loading ─────────────────────────────────────────────────────────────────

    [Fact]
    public void LoadingShowsEveryPointAndSelectsTheMostRecent()
    {
        var timeline = Loaded();

        Assert.True(timeline.HasPoints);
        Assert.True(timeline.HasVisiblePoints);
        Assert.Equal(4, timeline.Points.Count);
        Assert.Equal(T0.AddHours(3), timeline.SelectedPoint!.Timestamp);
    }

    [Fact]
    public void LoadingNothingLeavesTheTimelineEmpty()
    {
        var timeline = new RestoreTimelineViewModel();

        timeline.Load([]);

        Assert.False(timeline.HasPoints);
        Assert.False(timeline.HasVisiblePoints);
        Assert.Empty(timeline.Points);
        Assert.Empty(timeline.CountText);
    }

    /// <summary>
    /// Loading a database with no restore points has to drop the selection, not just the points.
    ///
    /// Everything downstream hangs off the selected point - the chain, the generated script, the
    /// verification results. Keeping one belonging to the previous database leaves a complete,
    /// authoritative-looking RESTORE on screen underneath a timeline that has nothing on it.
    /// </summary>
    [Fact]
    public void LoadingADatabaseWithNoPointsDropsThePreviousSelection()
    {
        var timeline = Loaded();
        Assert.NotNull(timeline.SelectedPoint);

        timeline.Load([]);

        Assert.Null(timeline.SelectedPoint);
    }

    [Fact]
    public void ClearingEmptiesEverything()
    {
        var timeline = Loaded();

        timeline.Clear();

        Assert.False(timeline.HasPoints);
        Assert.False(timeline.HasVisiblePoints);
        Assert.Empty(timeline.Points);
        Assert.Empty(timeline.Ticks);
        Assert.Empty(timeline.WindowText);
        Assert.Empty(timeline.StartText);
        Assert.Empty(timeline.EndText);
        Assert.Null(timeline.SelectedPoint);
    }

    /// <summary>
    /// A clear has to forget the points as well as hide them, otherwise a filter change afterwards
    /// brings the previous container's points back.
    /// </summary>
    [Fact]
    public void AFilterChangeAfterAClearBringsNothingBack()
    {
        var timeline = Loaded();

        timeline.Clear();
        timeline.ShowLog = false;
        timeline.ShowLog = true;

        Assert.Empty(timeline.Points);
        Assert.Null(timeline.SelectedPoint);
    }

    // ── layout ──────────────────────────────────────────────────────────────────

    [Fact]
    public void PositionsSpanTheWholeTrack()
    {
        var timeline = Loaded();

        var ordered = timeline.Points.OrderBy(p => p.Timestamp).ToList();
        Assert.Equal(0.0, ordered.First().TimelinePosition, 6);
        Assert.Equal(1.0, ordered.Last().TimelinePosition, 6);

        // Evenly spaced points must stay evenly spaced, and monotonic.
        Assert.All(ordered, p => Assert.InRange(p.TimelinePosition, 0.0, 1.0));
        for (int i = 1; i < ordered.Count; i++)
            Assert.True(ordered[i].TimelinePosition >= ordered[i - 1].TimelinePosition);
    }

    [Fact]
    public void ASinglePointSitsInTheMiddleRatherThanAtZero()
    {
        var timeline = Loaded(logCount: 0);

        var only = Assert.Single(timeline.Points);
        Assert.Equal(0.5, only.TimelinePosition, 6);
    }

    [Fact]
    public void PointsTooCloseToDrawSideBySideAreStackedIntoRows()
    {
        // 60 hourly logs over a fixed track: the gap between neighbours falls below the separation
        // the row packer allows, so they cannot all sit on one row.
        var timeline = Loaded(logCount: 60);

        Assert.True(timeline.Points.Max(p => p.Row) > 0,
            "Every point was placed on row 0, so they would be drawn on top of each other.");

        // The track grows to fit however many rows were needed, otherwise the upper rows are drawn
        // outside the control.
        Assert.True(timeline.TrackHeight > 50);
    }

    /// <summary>
    /// The two labels sit at each END of the track, so each one names one end. The right-hand one
    /// was bound to the whole range - "start to end" - which put the start time at both ends and
    /// buried the end time in the middle of a label nobody read as a label.
    /// </summary>
    [Fact]
    public void EachEndOfTheTrackIsLabelledWithItsOwnTime()
    {
        var timeline = Loaded();

        Assert.Equal("2026-01-10 22:00", timeline.StartText);
        Assert.Equal("2026-01-11 01:00", timeline.EndText);

        // The whole range, for the header above the track.
        Assert.Equal("2026-01-10 22:00 to 2026-01-11 01:00", timeline.WindowText);
    }

    /// <summary>
    /// Tick marks sit strictly inside the track, so a label does not overprint the start and end
    /// labels drawn at the ends.
    /// </summary>
    [Fact]
    public void TicksAreLabelledAndSitInsideTheTrack()
    {
        var timeline = Loaded();

        Assert.NotEmpty(timeline.Ticks);
        Assert.All(timeline.Ticks, t =>
        {
            Assert.InRange(t.Position, 0.02, 0.98);
            Assert.NotEmpty(t.Label);
        });
    }

    /// <summary>
    /// The interval suits the span - hours across an afternoon, dates across a month - otherwise a
    /// year of backups gets an hourly tick every few pixels.
    /// </summary>
    [Theory]
    [InlineData(3, "HH:mm")]      // three hourly logs: within the day
    [InlineData(24 * 20, "MMM")]  // twenty days: dates
    public void TheTickIntervalSuitsTheSpan(int logCount, string expectedFormatMarker)
    {
        var timeline = Loaded(logCount);

        var label = timeline.Ticks.First().Label;
        bool looksLikeATime = label.Contains(':');

        Assert.Equal(expectedFormatMarker == "HH:mm", looksLikeATime);
    }

    [Fact]
    public void ASinglePointHasNoTicksToDraw()
    {
        var timeline = Loaded(logCount: 0);

        Assert.Empty(timeline.Ticks);
    }

    // ── narrowing the points (#27) ──────────────────────────────────────────────

    [Fact]
    public void TurningOffATypeRemovesThosePointsFromBothSelectors()
    {
        var timeline = Loaded();
        Assert.Equal(4, timeline.Points.Count);

        timeline.ShowLog = false;

        // The list and the timeline are the same collection, so this is both.
        var only = Assert.Single(timeline.Points);
        Assert.Equal(BackupType.Full, only.Type);
        Assert.Contains("Showing 1 of 4", timeline.CountText);
    }

    [Fact]
    public void ADateRangeNarrowsToTheWindowAsked()
    {
        var timeline = Loaded(logCount: 5);

        timeline.FromText = T0.AddHours(2).ToString("yyyy-MM-dd HH:mm:ss");
        timeline.ToText = T0.AddHours(4).ToString("yyyy-MM-dd HH:mm:ss");

        Assert.Equal(3, timeline.Points.Count);
        Assert.All(timeline.Points, p => Assert.InRange(p.Timestamp, T0.AddHours(2), T0.AddHours(4)));
    }

    /// <summary>
    /// The point of the range: the surviving points spread across the whole track rather than
    /// staying bunched in the sliver they occupied before. Without this it filters but does not
    /// zoom, and picking one of them is no easier than it was.
    /// </summary>
    [Fact]
    public void NarrowingTheRangeSpreadsTheRemainingPointsAcrossTheTrack()
    {
        var timeline = Loaded(logCount: 20);

        var before = timeline.Points
            .Where(p => p.Timestamp >= T0.AddHours(2) && p.Timestamp <= T0.AddHours(4))
            .Select(p => p.TimelinePosition)
            .ToList();

        // Before narrowing, three of twenty-one points sit inside a tenth of the track.
        Assert.True(before.Max() - before.Min() < 0.2);

        timeline.FromText = T0.AddHours(2).ToString("yyyy-MM-dd HH:mm:ss");
        timeline.ToText = T0.AddHours(4).ToString("yyyy-MM-dd HH:mm:ss");

        var after = timeline.Points.Select(p => p.TimelinePosition).ToList();
        Assert.Equal(0.0, after.Min(), 6);
        Assert.Equal(1.0, after.Max(), 6);
    }

    [Fact]
    public void AHalfTypedDateDoesNotBlankTheTimeline()
    {
        var timeline = Loaded();

        // Mid-keystroke. Unparsable means no bound rather than an error.
        timeline.FromText = "2026-01-";

        Assert.Equal(4, timeline.Points.Count);
        Assert.Null(timeline.From);
    }

    [Fact]
    public void ADateIsReadTheSameWayWhateverTheMachinesCulture()
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            // en-US would read 02/03 as February; en-GB as March. The box is documented as
            // yyyy-MM-dd, which is unambiguous, and it is parsed invariantly so it stays that way.
            CultureInfo.CurrentCulture = new CultureInfo("en-US");

            var timeline = Loaded();
            timeline.FromText = "2026-01-10 23:30:00";

            Assert.Equal(new DateTime(2026, 1, 10, 23, 30, 0), timeline.From);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void AFilterThatMatchesNothingSaysSoRatherThanLookingEmpty()
    {
        var timeline = Loaded();

        timeline.ShowFull = false;
        timeline.ShowLog = false;
        timeline.ShowDiff = false;

        Assert.Empty(timeline.Points);
        Assert.False(timeline.HasVisiblePoints);

        // Still true - the database HAS restore points, they are just all filtered out. The two are
        // different states and the view shows different things for them.
        Assert.True(timeline.HasPoints);
    }

    [Fact]
    public void AnExistingSelectionSurvivesAFilterThatStillIncludesIt()
    {
        var timeline = Loaded(logCount: 5);

        var chosen = timeline.Points.First(p => p.Timestamp == T0.AddHours(2));
        timeline.SelectedPoint = chosen;

        // Narrow to a window that still contains it.
        timeline.FromText = T0.AddHours(1).ToString("yyyy-MM-dd HH:mm:ss");

        Assert.Same(chosen, timeline.SelectedPoint);
    }

    [Fact]
    public void ZoomToDayNarrowsToTheSelectedPointsDay()
    {
        // Two days of hourly logs. T0 is 22:00, so they cross midnight - the day being zoomed to is
        // the selected point's, not the first point's.
        var timeline = Loaded(logCount: 30);

        var chosen = timeline.Points.First(p => p.Timestamp == T0.AddHours(5));
        timeline.SelectedPoint = chosen;
        timeline.ZoomToSelectedDayCommand.Execute(null);

        Assert.All(timeline.Points, p => Assert.Equal(chosen.Timestamp.Date, p.Timestamp.Date));
        Assert.True(timeline.Points.Count < 31);
        Assert.Contains(timeline.Points, p => p.Timestamp == chosen.Timestamp);
    }

    [Fact]
    public void ZoomToDayWithNothingSelectedChangesNothing()
    {
        var timeline = Loaded();
        timeline.SelectedPoint = null;

        timeline.ZoomToSelectedDayCommand.Execute(null);

        Assert.Equal(4, timeline.Points.Count);
        Assert.Empty(timeline.FromText);
    }

    [Fact]
    public void ResetPutsEveryPointBack()
    {
        var timeline = Loaded(logCount: 5);

        // Logs off leaves only the full, which sits before the range - so nothing at all.
        timeline.ShowLog = false;
        timeline.FromText = T0.AddHours(3).ToString("yyyy-MM-dd HH:mm:ss");
        Assert.Empty(timeline.Points);

        timeline.ResetFiltersCommand.Execute(null);

        Assert.Equal(6, timeline.Points.Count);
        Assert.Equal(string.Empty, timeline.FromText);
        Assert.True(timeline.ShowLog);
    }

    [Fact]
    public void LoadingAgainDoesNotInheritThePreviousFilters()
    {
        // A range typed for one database would silently hide points belonging to the next.
        var timeline = Loaded(logCount: 5);
        timeline.FromText = T0.AddHours(4).ToString("yyyy-MM-dd HH:mm:ss");
        Assert.Equal(2, timeline.Points.Count);

        timeline.Load(FullPlusLogs(5));

        Assert.Equal(6, timeline.Points.Count);
        Assert.Equal(string.Empty, timeline.FromText);
    }

    // ── selection ───────────────────────────────────────────────────────────────

    [Fact]
    public void ClickingADotSelectsIt()
    {
        var timeline = Loaded();
        var chosen = timeline.Points.First();

        timeline.SelectPointCommand.Execute(chosen);

        Assert.Same(chosen, timeline.SelectedPoint);
    }

    /// <summary>
    /// The dot command is bound to every item in an ItemsControl, so it is worth being sure a null
    /// parameter cannot clear the selection out from under the chain that was built for it.
    /// </summary>
    [Fact]
    public void SelectingNothingLeavesTheSelectionAlone()
    {
        var timeline = Loaded();
        var before = timeline.SelectedPoint;

        timeline.SelectPointCommand.Execute(null);

        Assert.Same(before, timeline.SelectedPoint);
    }
}
