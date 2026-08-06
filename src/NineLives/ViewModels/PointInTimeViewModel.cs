using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Blackcat.NineLives.ViewModels;

/// <summary>
/// The point-in-time (STOPAT) target: the window it has to fall inside, what the user typed, and
/// whether that is usable.
///
/// Third seam out of RestoreViewModel (#115). Only meaningful for a transaction-log restore point.
/// Without STOPAT the granularity of a "point in time" restore is the end of whichever log backup
/// was selected - so with 15-minute logs, recovering from a bad DELETE at 14:23:41 could only land
/// on 14:15 or 14:30.
///
/// It does NOT work out the window. That is chain reasoning and belongs to
/// <see cref="Models.BackupChain.StopAtWindow"/>; this type is handed the window and validates
/// against it. Constraining the target to the LAST log's window is what keeps the generated chain
/// valid - a target inside an earlier log would need the chain truncated to that log, or the later
/// logs restore across a gap and fail with error 4305.
/// </summary>
public partial class PointInTimeViewModel : ObservableObject
{
    private const string StopAtFormat = "yyyy-MM-dd HH:mm:ss";

    /// <summary>
    /// Parsed exactly, and invariantly: a target read as 3 February when the user meant 3 March is
    /// a month of transactions silently discarded. Seconds are optional because the box is often
    /// filled from a timestamp that has none.
    /// </summary>
    private static readonly string[] AcceptedFormats =
    [
        "yyyy-MM-dd HH:mm:ss",
        "yyyy-MM-ddTHH:mm:ss",
        "yyyy-MM-dd HH:mm",
        "yyyy-MM-ddTHH:mm"
    ];

    /// <summary>
    /// True while <see cref="SetWindow"/> is rewriting several properties at once, so a listener
    /// can skip the intermediate states and act on the finished one.
    /// </summary>
    internal bool IsUpdating { get; private set; }

    /// <summary>True when the selected restore point is a log, so STOPAT applies at all.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Effective))]
    private bool _canUse;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Effective))]
    private bool _use;

    /// <summary>What the user typed, parsed into <see cref="StopAt"/>.</summary>
    [ObservableProperty]
    private string _stopAtText = string.Empty;

    /// <summary>The parsed and validated target, or null when unusable.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Effective))]
    private DateTime? _stopAt;

    /// <summary>Exclusive lower bound: the end of the previous set in the chain.</summary>
    [ObservableProperty]
    private DateTime? _earliest;

    /// <summary>Inclusive upper bound: the end of the selected log backup.</summary>
    [ObservableProperty]
    private DateTime? _latest;

    [ObservableProperty]
    private string _message = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Effective))]
    private bool _hasError;

    /// <summary>The STOPAT value to generate, or null to restore the whole log chain.</summary>
    public DateTime? Effective => CanUse && Use && !HasError ? StopAt : null;

    /// <summary>
    /// Points this at a new restore point's window, or turns it off when the point is not a log.
    ///
    /// Starts unticked with the box pre-filled with the latest usable time, so the box shows what
    /// the bounds are before anyone commits to using it.
    /// </summary>
    public void SetWindow((DateTime Earliest, DateTime Latest)? window)
    {
        IsUpdating = true;
        try
        {
            if (window == null)
            {
                CanUse = false;
                Use = false;
                Earliest = null;
                Latest = null;
                StopAtText = string.Empty;
                StopAt = null;
                Message = string.Empty;
                HasError = false;
                return;
            }

            CanUse = true;
            Earliest = window.Value.Earliest;
            Latest = window.Value.Latest;
            Use = false;
            StopAtText = window.Value.Latest.ToString(StopAtFormat);
            Validate();
        }
        finally
        {
            IsUpdating = false;
        }
    }

    private void Validate()
    {
        if (!CanUse)
        {
            StopAt = null;
            Message = string.Empty;
            HasError = false;
            return;
        }

        if (!DateTime.TryParseExact(
                StopAtText?.Trim(), AcceptedFormats,
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            StopAt = null;
            // Only an error once it is actually being used - half-typed input in a box nobody has
            // ticked is not a reason to block the restore.
            HasError = Use;
            Message = $"Enter a time as {StopAtFormat}.";
            return;
        }

        // Exclusive lower bound: at exactly the previous set's time nothing from this log has been
        // applied yet, which is the earlier restore point, not this one.
        if (parsed <= Earliest)
        {
            StopAt = null;
            HasError = Use;
            Message =
                $"Must be after {Earliest:yyyy-MM-dd HH:mm:ss} — to stop earlier, " +
                "select an earlier restore point on the timeline.";
            return;
        }

        if (parsed > Latest)
        {
            StopAt = null;
            HasError = Use;
            Message =
                $"Must be at or before {Latest:yyyy-MM-dd HH:mm:ss} — to stop later, " +
                "select a later restore point on the timeline.";
            return;
        }

        StopAt = parsed;
        HasError = false;
        Message = Use
            ? $"Recovery will stop at {parsed:yyyy-MM-dd HH:mm:ss}; later transactions in this log are discarded."
            : $"Valid range: after {Earliest:yyyy-MM-dd HH:mm:ss} up to {Latest:yyyy-MM-dd HH:mm:ss}.";
    }

    partial void OnStopAtTextChanged(string value) => Validate();

    partial void OnUseChanged(bool value) => Validate();
}
