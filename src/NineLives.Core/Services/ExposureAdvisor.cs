using Blackcat.NineLives.Models;

namespace Blackcat.NineLives.Services;

/// <summary>How urgently a database's backup state needs a human (#239).</summary>
public enum ExposureLevel
{
    Ok = 0,
    Warning = 1,
    Alarm = 2
}

/// <summary>
/// One database's backup state on one server, as msdb tells it (#239).
/// </summary>
public sealed class ExposureRow
{
    public string ServerName { get; init; } = string.Empty;
    public string DatabaseName { get; init; } = string.Empty;
    public string RecoveryModel { get; init; } = string.Empty;
    public string StateDescription { get; init; } = string.Empty;

    /// <summary>
    /// The server would not answer, so this row is an alarm about silence rather than a judged
    /// database (#287). A property rather than a magic string compared in two places.
    /// </summary>
    public bool IsUnreachable { get; init; }

    /// <summary>
    /// The instance's OWN clock when this row was read (#414).
    ///
    /// msdb records backup_finish_date in the server's local time, and the app used to judge it
    /// against DateTime.Now on this machine - so every age was out by the offset between the two,
    /// and with a one-hour warning threshold the offset dominated the verdict. Worse in the
    /// direction that matters: a server ahead of the app made backups look NEWER than they were,
    /// downgrading an alarm to a warning, or to nothing at all when the offset exceeded the age.
    ///
    /// Read from the same query, so it costs nothing and is per-server rather than per-estate.
    /// Null only for a row the app fabricated because the server would not answer.
    /// </summary>
    public DateTime? ServerNow { get; init; }

    public DateTime? LastFull { get; init; }
    public DateTime? LastDifferential { get; init; }
    public DateTime? LastLog { get; init; }

    /// <summary>When a rehearsal last PROVED this database restores, from the app's own receipts.</summary>
    public DateTime? LastProven { get; set; }

    /// <summary>
    /// How long that rehearsal took - restore plus CHECKDB, measured, not estimated. The number
    /// RTO conversations are otherwise made up from, and the rehearsal produces it for free.
    /// </summary>
    public TimeSpan? MeasuredRestore { get; set; }

    /// <summary>The Proven cell: date plus the measured time, or the honest "never rehearsed".</summary>
    public string ProvenDisplay =>
        LastProven == null
            ? "Proven: never rehearsed"
            : MeasuredRestore is { } d
                ? $"Proven: {LastProven:MM-dd HH:mm} (took {ExposureAdvisor.Age(LastProven.Value - d, LastProven.Value)})"
                : $"Proven: {LastProven:MM-dd HH:mm}";

    // Filled by the advisor.
    public ExposureLevel Level { get; set; }
    public string Verdict { get; set; } = string.Empty;

    /// <summary>The moment recovery could reach - the newest backup of any kind.</summary>
    public DateTime? RecoverableTo =>
        new[] { LastLog, LastDifferential, LastFull }.Where(d => d != null).DefaultIfEmpty(null).Max();
}

/// <summary>
/// Turns backup timestamps into the sentence a DBA actually lives with: "if this server died
/// now, you lose up to X" (#239).
///
/// The thresholds are deliberate defaults, not laws: an hour of log silence on a FULL-recovery
/// database is worth a look, a day of it is an alarm - and the silent failures are the loudest:
/// FULL recovery with no log backups EVER is both a runaway log file and an unbounded loss
/// window, and nobody notices silence without a screen that makes silence red.
/// </summary>
public static class ExposureAdvisor
{
    public static readonly TimeSpan LogWarnAfter = TimeSpan.FromHours(1);
    public static readonly TimeSpan LogAlarmAfter = TimeSpan.FromHours(24);

    /// <summary>A day-plus-grace, because a daily full cycle is the ordinary arrangement.</summary>
    public static readonly TimeSpan SimpleWarnAfter = TimeSpan.FromHours(26);
    public static readonly TimeSpan SimpleAlarmAfter = TimeSpan.FromDays(7);

    public static void Judge(ExposureRow row, DateTime now)
    {
        // The server's own clock when it answered, not this machine's (#414). msdb records
        // backup_finish_date in the instance's local time, so judging it against DateTime.Now
        // here was out by the offset between the two - and with a one-hour warning threshold the
        // offset decided the verdict. The caller's clock is the fallback for a row no server
        // answered for, where there is nothing to compare anyway.
        now = row.ServerNow ?? now;

        // No full, no recovery - everything else is arithmetic on top of a full that must exist.
        if (row.LastFull == null)
        {
            row.Level = ExposureLevel.Alarm;
            row.Verdict = "Never backed up. There is no full backup, so nothing of this database " +
                          "is recoverable at all.";
            return;
        }

        var isSimple = string.Equals(row.RecoveryModel, "SIMPLE", StringComparison.OrdinalIgnoreCase);

        if (!isSimple && row.LastLog == null)
        {
            row.Level = ExposureLevel.Alarm;
            row.Verdict = $"{row.RecoveryModel} recovery with no log backup ever taken: the log " +
                          "file grows until the disk fills, and everything since the last full " +
                          $"({Age(row.LastFull.Value, now)} ago) is unprotected. Take log backups " +
                          "or switch to SIMPLE - deliberately, not by default.";
            return;
        }

        var recoverableTo = row.RecoverableTo!.Value;
        var exposure = now - recoverableTo;

        var (warn, alarm) = isSimple
            ? (SimpleWarnAfter, SimpleAlarmAfter)
            : (LogWarnAfter, LogAlarmAfter);

        row.Level = exposure >= alarm ? ExposureLevel.Alarm
            : exposure >= warn ? ExposureLevel.Warning
            : ExposureLevel.Ok;

        var loss = $"If this server died now, everything after {recoverableTo:yyyy-MM-dd HH:mm} " +
                   $"is gone - up to {Age(recoverableTo, now)} of work.";

        row.Verdict = row.Level switch
        {
            ExposureLevel.Alarm when isSimple =>
                $"{loss} SIMPLE recovery relies on its full/differential cycle, and it has stopped.",
            ExposureLevel.Alarm =>
                $"{loss} The log backups have stopped - the chain last moved {Age(recoverableTo, now)} ago.",
            ExposureLevel.Warning =>
                $"{loss} Worth a look at the schedule.",
            _ => loss
        };
    }

    /// <summary>"47m", "3h 20m", "2d 4h" - the units a human compares against a schedule.</summary>
    public static string Age(DateTime then, DateTime now)
    {
        var d = now - then;
        if (d < TimeSpan.Zero) d = TimeSpan.Zero;

        return d.TotalDays >= 1 ? $"{(int)d.TotalDays}d {d.Hours}h"
            : d.TotalHours >= 1 ? $"{(int)d.TotalHours}h {d.Minutes:D2}m"
            : $"{Math.Max(1, (int)d.TotalMinutes)}m";
    }
}
