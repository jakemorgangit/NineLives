using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Blackcat.NineLives.Models;

namespace Blackcat.NineLives.ViewModels;

/// <summary>
/// The restore-point timeline: which of the computed points are visible, where each one sits on
/// the track, the tick marks underneath it, and the selection.
///
/// Second seam out of RestoreViewModel (#115). This part is nearly pure - points in, positions and
/// labels out - and none of it needs a container, a server or a chain to exercise, which is why it
/// comes early in the split.
///
/// It does NOT compute the restore points. Working out which points exist is chain reasoning and
/// belongs to <see cref="Services.BackupChainBuilder"/>; this type is handed the result and decides
/// how to show it.
/// </summary>
public partial class RestoreTimelineViewModel : ObservableObject
{
    /// <summary>
    /// The track's height with nothing on it. Only ever seen for the instant between a load
    /// starting and the points arriving - the whole step is collapsed while there are no points.
    /// </summary>
    private const int EmptyTrackHeight = 50;

    /// <summary>Every computed restore point, before the filters. What is shown is a subset (#27).</summary>
    private List<RestorePoint> _all = [];

    /// <summary>Set while the filters are being changed in bulk, so each one does not re-filter.</summary>
    private bool _suppressRefresh;

    // ── what the view shows ─────────────────────────────────────────────────────

    /// <summary>The points that pass the filters, in time order.</summary>
    [ObservableProperty]
    private ObservableCollection<RestorePoint> _points = [];

    [ObservableProperty]
    private RestorePoint? _selectedPoint;

    /// <summary>True when the database has any restore points at all, filters aside.</summary>
    [ObservableProperty]
    private bool _hasPoints;

    /// <summary>True when the filters leave at least one of them on screen.</summary>
    [ObservableProperty]
    private bool _hasVisiblePoints;

    [ObservableProperty]
    private string _countText = string.Empty;

    /// <summary>The span the visible points cover, as "from to to".</summary>
    [ObservableProperty]
    private string _windowText = string.Empty;

    /// <summary>
    /// The labels at each end of the track. Plain properties rather than the old
    /// {Binding RestorePoints[0].Timestamp}, which threw an index error into WPF's binding trace on
    /// every layout while the collection was empty.
    /// </summary>
    [ObservableProperty]
    private string _startText = string.Empty;

    [ObservableProperty]
    private string _endText = string.Empty;

    [ObservableProperty]
    private ObservableCollection<TimelineTick> _ticks = [];

    [ObservableProperty]
    private int _trackHeight = EmptyTrackHeight;

    // ── filters (#27) ───────────────────────────────────────────────────────────

    [ObservableProperty]
    private bool _showFull = true;

    [ObservableProperty]
    private bool _showDiff = true;

    [ObservableProperty]
    private bool _showLog = true;

    [ObservableProperty]
    private string _fromText = string.Empty;

    [ObservableProperty]
    private string _toText = string.Empty;

    /// <summary>Parsed from the text boxes; null means no bound on that end.</summary>
    [ObservableProperty]
    private DateTime? _from;

    [ObservableProperty]
    private DateTime? _to;

    /// <summary>
    /// Takes a fresh set of restore points and shows them, with the filters wide open.
    ///
    /// Wide open because carrying a previous database's date range over would silently hide points
    /// that have nothing to do with it.
    /// </summary>
    public void Load(IEnumerable<RestorePoint> points)
    {
        _all = points.ToList();

        _suppressRefresh = true;
        ShowFull = ShowDiff = ShowLog = true;
        FromText = string.Empty;
        ToText = string.Empty;
        _suppressRefresh = false;

        if (_all.Count == 0)
        {
            Clear();
            return;
        }

        HasPoints = true;
        ApplyFilters();
    }

    /// <summary>
    /// Empties the timeline, for a container change or an abandoned load.
    ///
    /// Clearing the selection is the point of it: everything downstream - the chain, the generated
    /// script, the verification results - hangs off the selected point, and leaving one behind
    /// leaves a complete, authoritative-looking RESTORE on screen for a database that is no longer
    /// the one selected above it.
    /// </summary>
    public void Clear()
    {
        _all = [];
        Points = [];
        HasPoints = false;
        HasVisiblePoints = false;
        CountText = string.Empty;
        WindowText = string.Empty;
        StartText = string.Empty;
        EndText = string.Empty;
        Ticks.Clear();
        TrackHeight = EmptyTrackHeight;
        SelectedPoint = null;
    }

    /// <summary>
    /// Narrows the track and the list to the points the filters allow, then lays the track out over
    /// just those.
    ///
    /// Laying out over the VISIBLE set rather than all of them is what makes the date range behave
    /// like a zoom: narrow to a two-hour window and those points spread across the whole track
    /// instead of staying bunched in the sliver they occupied before (#27).
    /// </summary>
    private void ApplyFilters()
    {
        var previous = SelectedPoint;
        var visible = _all.Where(Matches).ToList();

        LayOut(visible);

        Points = new ObservableCollection<RestorePoint>(visible);
        HasVisiblePoints = visible.Count > 0;

        CountText = visible.Count == _all.Count
            ? $"{_all.Count} restore point(s)"
            : $"Showing {visible.Count} of {_all.Count} restore point(s)";

        if (visible.Count == 0)
        {
            WindowText = string.Empty;
            return;
        }

        // Keep the selection when it survives the filter - changing a type toggle should not throw
        // away the point someone had already chosen.
        //
        // A FRESH set of points selects nothing. Landing on the latest point looks helpful and is
        // not: it is the app deciding which moment to restore to, on a screen whose purpose is
        // choosing that moment, and everything downstream - the chain, the script, the summary -
        // then describes a restore nobody asked for. The one thing this tool must never do is
        // answer that question on somebody's behalf.
        SelectedPoint = previous != null && visible.Contains(previous) ? previous : null;
    }

    private bool Matches(RestorePoint p)
    {
        var typeAllowed = p.Type switch
        {
            BackupType.Full => ShowFull,
            BackupType.Differential => ShowDiff,
            BackupType.TransactionLog => ShowLog,
            _ => true
        };
        if (!typeAllowed) return false;

        if (From.HasValue && p.Timestamp < From.Value) return false;
        if (To.HasValue && p.Timestamp > To.Value) return false;

        return true;
    }

    private void LayOut(List<RestorePoint> points)
    {
        if (points.Count > 1)
        {
            var minTicks = points[0].Timestamp.Ticks;
            var maxTicks = points[^1].Timestamp.Ticks;
            var range = (double)(maxTicks - minTicks);
            foreach (var p in points)
                p.TimelinePosition = range > 0 ? (p.Timestamp.Ticks - minTicks) / range : 0.5;
        }
        else if (points.Count == 1)
        {
            points[0].TimelinePosition = 0.5;
        }

        // Vertical stacking, so points too close together to draw side by side are not drawn on
        // top of each other.
        ComputeRows(points);

        if (points.Count == 0)
        {
            Ticks.Clear();
            TrackHeight = EmptyTrackHeight;
            return;
        }

        var first = points[0].Timestamp;
        var last = points[^1].Timestamp;
        WindowText = $"{first:yyyy-MM-dd HH:mm} to {last:yyyy-MM-dd HH:mm}";
        StartText = $"{first:yyyy-MM-dd HH:mm}";
        EndText = $"{last:yyyy-MM-dd HH:mm}";

        Ticks = new ObservableCollection<TimelineTick>(ComputeTicks(first, last));

        int maxRow = points.Max(p => p.Row);
        TrackHeight = Math.Max(EmptyTrackHeight, 30 + (maxRow + 1) * 18);
    }

    private void OnFilterChanged()
    {
        if (_suppressRefresh || _all.Count == 0) return;
        ApplyFilters();
    }

    partial void OnShowFullChanged(bool value) => OnFilterChanged();
    partial void OnShowDiffChanged(bool value) => OnFilterChanged();
    partial void OnShowLogChanged(bool value) => OnFilterChanged();

    partial void OnFromTextChanged(string value)
    {
        From = ParseFilterDate(value);
        OnFilterChanged();
    }

    partial void OnToTextChanged(string value)
    {
        To = ParseFilterDate(value);
        OnFilterChanged();
    }

    /// <summary>
    /// Invariant, and forgiving about how much of a timestamp was typed - "2026-01-10" is a
    /// perfectly reasonable thing to enter into a date box. Unparsable means no bound rather than
    /// an error, so half-typed input does not blank the timeline mid-keystroke.
    /// </summary>
    private static DateTime? ParseFilterDate(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        return DateTime.TryParse(
            text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? parsed
            : null;
    }

    [RelayCommand]
    private void SelectPoint(RestorePoint? point)
    {
        if (point != null) SelectedPoint = point;
    }

    /// <summary>Back to every point, without reloading anything from the container.</summary>
    [RelayCommand]
    private void ResetFilters()
    {
        _suppressRefresh = true;
        ShowFull = ShowDiff = ShowLog = true;
        FromText = string.Empty;
        ToText = string.Empty;
        _suppressRefresh = false;

        if (_all.Count > 0) ApplyFilters();
    }

    /// <summary>
    /// Narrows the range to the day the selected point falls on. A week of 15-minute logs is
    /// roughly 670 points; picking one precisely means getting to a day first, and doing that by
    /// typing two timestamps is a chore when the answer is nearly always "the day it broke".
    /// </summary>
    [RelayCommand]
    private void ZoomToSelectedDay()
    {
        if (SelectedPoint == null) return;

        var day = SelectedPoint.Timestamp.Date;

        _suppressRefresh = true;
        FromText = day.ToString("yyyy-MM-dd HH:mm:ss");
        ToText = day.AddDays(1).AddSeconds(-1).ToString("yyyy-MM-dd HH:mm:ss");
        _suppressRefresh = false;

        ApplyFilters();
    }

    /// <summary>
    /// Packs points into as few rows as possible, so two backups minutes apart on a month-long
    /// track are drawn one above the other instead of on top of each other.
    /// </summary>
    private static void ComputeRows(List<RestorePoint> points)
    {
        const double minSeparation = 0.025;
        var rows = new List<List<double>>();

        foreach (var p in points)
        {
            bool placed = false;
            for (int row = 0; row < rows.Count; row++)
            {
                bool overlaps = rows[row].Any(pos => Math.Abs(pos - p.TimelinePosition) < minSeparation);
                if (!overlaps)
                {
                    p.Row = row;
                    rows[row].Add(p.TimelinePosition);
                    placed = true;
                    break;
                }
            }
            if (!placed)
            {
                p.Row = rows.Count;
                rows.Add([p.TimelinePosition]);
            }
        }
    }

    /// <summary>
    /// Labelled marks under the track, at an interval chosen to suit the span - hours across an
    /// afternoon, fortnights across a year.
    /// </summary>
    private static List<TimelineTick> ComputeTicks(DateTime first, DateTime last)
    {
        var ticks = new List<TimelineTick>();
        var range = last - first;
        var totalTicks = (double)(last.Ticks - first.Ticks);
        if (totalTicks <= 0) return ticks;

        // Choose interval based on range
        TimeSpan interval;
        string format;
        if (range.TotalDays > 60)
        {
            interval = TimeSpan.FromDays(14);
            format = "MMM dd";
        }
        else if (range.TotalDays > 14)
        {
            interval = TimeSpan.FromDays(7);
            format = "MMM dd";
        }
        else if (range.TotalDays > 3)
        {
            interval = TimeSpan.FromDays(1);
            format = "MMM dd";
        }
        else if (range.TotalHours > 12)
        {
            interval = TimeSpan.FromHours(6);
            format = "HH:mm";
        }
        else
        {
            interval = TimeSpan.FromHours(1);
            format = "HH:mm";
        }

        // Round start to the next clean interval boundary
        var cursor = RoundUp(first, interval);

        while (cursor < last)
        {
            double pos = (cursor.Ticks - first.Ticks) / totalTicks;
            if (pos >= 0.02 && pos <= 0.98)
            {
                ticks.Add(new TimelineTick { Position = pos, Label = cursor.ToString(format) });
            }
            cursor += interval;
        }

        return ticks;
    }

    private static DateTime RoundUp(DateTime dt, TimeSpan interval)
    {
        if (interval.TotalDays >= 1)
        {
            var next = dt.Date.AddDays(1);
            while (next <= dt) next = next.AddDays((int)interval.TotalDays);
            return next;
        }
        var ticks = (dt.Ticks + interval.Ticks - 1) / interval.Ticks * interval.Ticks;
        return new DateTime(ticks);
    }
}
