namespace Blackcat.NineLives.Models;

/// <summary>How a restore ended.</summary>
public enum RestoreOutcome
{
    Succeeded,
    Failed,

    /// <summary>Stopped by the user. Not a failure, but the database is mid-restore either way.</summary>
    Cancelled
}

/// <summary>
/// One execution, kept so it can be read back after the app closes (#31).
///
/// A DBA who has just restored production needs this for the change ticket or the incident
/// write-up, and reconstructing it from memory afterwards is exactly when detail goes missing.
///
/// Deliberately holds no secret: the script is the same token-free text the app generates and
/// shows, and everything written here goes through <see cref="Services.LogRedactor"/> anyway.
/// </summary>
public sealed class RestoreHistoryEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("n");

    public DateTime StartedAt { get; set; }
    public DateTime CompletedAt { get; set; }

    public string ServerName { get; set; } = string.Empty;
    public string TargetDatabase { get; set; } = string.Empty;

    public string? ContainerName { get; set; }
    public string? SourceDatabase { get; set; }

    /// <summary>The point restored to, as shown on the timeline.</summary>
    public DateTime? RestorePointTimestamp { get; set; }

    /// <summary>e.g. "1 Full + 2 Log(s)".</summary>
    public string ChainSummary { get; set; } = string.Empty;

    /// <summary>
    /// "Restore" or "Rehearsal" (#238). A string, not an enum, so records survive both
    /// directions across versions; entries written before this field existed read as "Restore",
    /// which is what they were.
    /// </summary>
    public string Kind { get; set; } = "Restore";

    /// <summary>
    /// "App" or "CLI" (#303) - which front end acted. A 3am CLI restore reads differently in
    /// an incident review than a clicked one. A string for the same cross-version tolerance
    /// as Kind; entries written before this field existed read as "App", which they were.
    /// </summary>
    public string Origin { get; set; } = "App";

    public RestoreOutcome Outcome { get; set; }

    /// <summary>What went wrong, when something did.</summary>
    public string? ErrorMessage { get; set; }

    /// <summary>The exact script that was run, so a repeat does not have to be rebuilt by hand.</summary>
    public string Script { get; set; } = string.Empty;

    /// <summary>The whole console output, which is what a bug report or a ticket actually wants.</summary>
    public string Log { get; set; } = string.Empty;

    public TimeSpan Duration => CompletedAt > StartedAt ? CompletedAt - StartedAt : TimeSpan.Zero;

    public string OutcomeDisplay => Outcome switch
    {
        RestoreOutcome.Succeeded => "Succeeded",
        RestoreOutcome.Failed => "Failed",
        RestoreOutcome.Cancelled => "Cancelled",
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

    /// <summary>One line for the list: what was restored, where to, and how it went.</summary>
    public string Summary => $"{TargetDatabase} on {ServerName} - {OutcomeDisplay}";

    public string Detail =>
        $"{ChainSummary}{(RestorePointTimestamp.HasValue ? $"  |  point {RestorePointTimestamp:yyyy-MM-dd HH:mm:ss}" : "")}  |  {DurationDisplay}" +
        (Origin is "App" or "" ? string.Empty : $"  |  via {Origin}");
}
