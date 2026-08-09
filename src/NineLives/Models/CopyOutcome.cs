namespace Blackcat.NineLives.Models;

/// <summary>
/// How far a copy between servers got (#105).
///
/// The distinction that matters is not success versus failure - it is WHERE it stopped, because the
/// two halves leave completely different states behind and want completely different next steps.
///
/// A backup that succeeded and a restore that failed is not "failed". It left a perfectly good
/// backup sitting in the container, and the right next step is to restore from it - not to run the
/// whole thing again and take a second backup of a production database for no reason. Reporting
/// that as a bare failure is how that second backup gets taken.
/// </summary>
public enum CopyOutcome
{
    /// <summary>Nothing has run yet.</summary>
    NotStarted,

    /// <summary>
    /// Stopped before the source was touched - a guard rail, or a preflight that refused.
    /// Nothing exists anywhere; nothing to clean up.
    /// </summary>
    StoppedBeforeAnythingHappened,

    /// <summary>
    /// The BACKUP did not complete. Any file written is partial, is not a backup, and cannot be
    /// restored from - which is worth saying, because it will sit there looking like one.
    /// </summary>
    BackupFailed,

    /// <summary>
    /// The backup succeeded and the restore did not.
    ///
    /// The one worth telling apart. The backup is real and usable; the target is whatever the
    /// failed restore left it as, which after WITH REPLACE may be nothing.
    /// </summary>
    BackupTakenRestoreFailed,

    /// <summary>Both halves finished.</summary>
    Copied
}

public static class CopyOutcomeText
{
    public static string Describe(CopyOutcome outcome, string? backupLocation = null) => outcome switch
    {
        CopyOutcome.NotStarted => string.Empty,

        CopyOutcome.StoppedBeforeAnythingHappened =>
            "Stopped before anything ran. Nothing was written and neither server was changed.",

        CopyOutcome.BackupFailed =>
            "The backup did not complete, so nothing was restored. Any file already written is " +
            "incomplete and cannot be restored from - delete it rather than keeping it.",

        CopyOutcome.BackupTakenRestoreFailed =>
            $"The backup succeeded and the restore did not. The backup is real and usable{Where(backupLocation)} - " +
            "restore from it rather than running this again, which would take a second backup of " +
            "the source for no reason. The target database is whatever the failed restore left it " +
            "as, which after WITH REPLACE may be nothing.",

        CopyOutcome.Copied => "Copied.",

        _ => string.Empty
    };

    private static string Where(string? location) =>
        string.IsNullOrWhiteSpace(location) ? string.Empty : $" and is at {location}";
}
