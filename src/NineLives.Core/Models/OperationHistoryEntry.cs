namespace Blackcat.NineLives.Models;

/// <summary>How an operation ended.</summary>
public enum OperationOutcome
{
    Succeeded,
    Failed,

    /// <summary>
    /// Stopped by the user. Not a failure, but the work is half-done either way: a restore leaves
    /// the database mid-restore, and a backup leaves a partial file that cannot be restored from.
    /// </summary>
    Cancelled
}

/// <summary>The operations that leave a receipt. Strings - see <see cref="OperationHistoryEntry.Kind"/>.</summary>
public static class OperationKind
{
    public const string Restore = "Restore";
    public const string Rehearsal = "Rehearsal";
    public const string Backup = "Backup";
    public const string Copy = "Copy";
}

/// <summary>
/// One execution, kept so it can be read back after the app closes (#31, #434).
///
/// A DBA who has just restored production needs this for the change ticket or the incident
/// write-up, and reconstructing it from memory afterwards is exactly when detail goes missing.
/// That argument was never restore-only. A backup is as much a thing that happened to a
/// production server - and an ordinary full backup moves the differential base, so a server's
/// whole differential schedule can come to depend on a file this app wrote, with nothing in the
/// app saying it ever happened. A copy touches two servers and overwrites a database on one.
///
/// Deliberately holds no secret: the script is the same token-free text the app generates and
/// shows, and everything written here goes through <see cref="Services.LogRedactor"/> anyway.
///
/// Field names are load-bearing - they ARE the JSON on disk. Fields added for #434 are nullable
/// so records written by an older build read back cleanly, and older builds ignore what they do
/// not recognise.
/// </summary>
public sealed class OperationHistoryEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("n");

    public DateTime StartedAt { get; set; }
    public DateTime CompletedAt { get; set; }

    /// <summary>
    /// The server the operation ACTED ON. For a restore or a copy that is the one written to; for
    /// a backup it is the one read from, which is the only server involved.
    /// </summary>
    public string ServerName { get; set; } = string.Empty;

    /// <summary>
    /// The database this operation was about, as it ended up: the restored name, the copy's
    /// "restore as" name, or the database that was backed up. Named for the restore it was written
    /// for, and kept that way because renaming it would orphan every record on disk.
    /// </summary>
    public string TargetDatabase { get; set; } = string.Empty;

    public string? ContainerName { get; set; }
    public string? SourceDatabase { get; set; }

    /// <summary>
    /// The server READ FROM, when that is a different machine (#434). Only a copy has one, and it
    /// is the whole reason a copy is not just a restore: two servers, one of them overwritten.
    /// </summary>
    public string? SourceServerName { get; set; }

    /// <summary>
    /// Where the bytes went or came from - "Cloud storage", "S3", "A path both servers can reach"
    /// (#434). Null on records written before this existed.
    /// </summary>
    public string? Medium { get; set; }

    /// <summary>
    /// How many files the operation wrote (#434). A striped backup writing four files is a
    /// different thing to recover from than one writing a single file, and the count is the part
    /// somebody checks first when a restore cannot find its set.
    /// </summary>
    public int? FileCount { get; set; }

    /// <summary>
    /// The options that change what the operation MEANT, as they were when it ran - most of all
    /// whether a backup was copy-only (#434). "Was the differential base moved?" is the question
    /// this field exists to answer months later, and it is unanswerable from anywhere else.
    /// </summary>
    public string? OptionsSummary { get; set; }

    /// <summary>The point restored to, as shown on the timeline.</summary>
    public DateTime? RestorePointTimestamp { get; set; }

    /// <summary>e.g. "1 Full + 2 Log(s)". Empty for a backup, which has no chain.</summary>
    public string ChainSummary { get; set; } = string.Empty;

    /// <summary>
    /// What was done: see <see cref="OperationKind"/> (#238, #434). A string, not an enum, so
    /// records survive both directions across versions; entries written before this field existed
    /// read as "Restore", which is what they were.
    /// </summary>
    public string Kind { get; set; } = OperationKind.Restore;

    /// <summary>
    /// "App" or "CLI" (#303) - which front end acted. A 3am CLI restore reads differently in
    /// an incident review than a clicked one. A string for the same cross-version tolerance
    /// as Kind; entries written before this field existed read as "App", which they were.
    /// </summary>
    public string Origin { get; set; } = "App";

    public OperationOutcome Outcome { get; set; }

    /// <summary>What went wrong, when something did.</summary>
    public string? ErrorMessage { get; set; }

    /// <summary>The exact script that was run, so a repeat does not have to be rebuilt by hand.</summary>
    public string Script { get; set; } = string.Empty;

    /// <summary>The whole console output, which is what a bug report or a ticket actually wants.</summary>
    public string Log { get; set; } = string.Empty;

    public TimeSpan Duration => CompletedAt > StartedAt ? CompletedAt - StartedAt : TimeSpan.Zero;

    public string OutcomeDisplay => Outcome switch
    {
        OperationOutcome.Succeeded => "Succeeded",
        OperationOutcome.Failed => "Failed",
        OperationOutcome.Cancelled => "Cancelled",
        _ => "Unknown"
    };

    public string StartedDisplay => StartedAt.ToString("yyyy-MM-dd HH:mm:ss");

    public string DurationDisplay => Duration.TotalSeconds < 1
        ? "<1s"
        : Duration.TotalHours >= 1
            ? $"{(int)Duration.TotalHours}h {Duration.Minutes}m"
            : Duration.TotalMinutes >= 1
                ? $"{(int)Duration.TotalMinutes}m {Duration.Seconds}s"
                : $"{(int)Duration.TotalSeconds}s";

    /// <summary>
    /// One line for the list: what happened, to what, and how it went.
    ///
    /// A copy names both servers, because "on SRV02" alone hides the machine that was read - and
    /// which server was the source is the first thing anyone asks about a copy afterwards.
    /// </summary>
    public string Summary => Kind == OperationKind.Copy && !string.IsNullOrWhiteSpace(SourceServerName)
        ? $"{TargetDatabase} on {ServerName} from {SourceServerName} - {OutcomeDisplay}"
        : $"{TargetDatabase} on {ServerName} - {OutcomeDisplay}";

    /// <summary>
    /// The second line. Built from whichever parts this kind actually has rather than one format
    /// with empty holes in it - a backup has no chain and no restore point, and printing the
    /// separators for them anyway is how a list starts looking broken.
    /// </summary>
    public string Detail
    {
        get
        {
            var parts = new List<string>();

            if (!string.IsNullOrWhiteSpace(ChainSummary)) parts.Add(ChainSummary);

            if (RestorePointTimestamp.HasValue)
                parts.Add($"point {RestorePointTimestamp:yyyy-MM-dd HH:mm:ss}");

            if (!string.IsNullOrWhiteSpace(Medium)) parts.Add(Medium!);

            // "1 file" reads as prose; the count only earns its place when striping makes it
            // something to check.
            if (FileCount is > 1) parts.Add($"{FileCount} files");

            if (!string.IsNullOrWhiteSpace(OptionsSummary)) parts.Add(OptionsSummary!);

            parts.Add(DurationDisplay);

            if (Origin is not ("App" or "")) parts.Add($"via {Origin}");

            return string.Join("  |  ", parts);
        }
    }
}
