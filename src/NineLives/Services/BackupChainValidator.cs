using Blackcat.NineLives.Models;

namespace Blackcat.NineLives.Services;

public enum ChainIssueSeverity
{
    /// <summary>Worth knowing about, but the restore may still succeed.</summary>
    Warning,

    /// <summary>The restore cannot succeed as generated.</summary>
    Error
}

/// <summary>A problem found in a restore chain or in the discovered backup inventory.</summary>
public sealed class ChainIssue(ChainIssueSeverity severity, string title, string detail)
{
    public ChainIssueSeverity Severity { get; } = severity;
    public string Title { get; } = title;
    public string Detail { get; } = detail;

    public bool IsError => Severity == ChainIssueSeverity.Error;
    public string SeverityDisplay => Severity == ChainIssueSeverity.Error ? "ERROR" : "WARNING";

    public override string ToString() => $"[{SeverityDisplay}] {Title} - {Detail}";
}

/// <summary>
/// Structural validation of a restore chain, using only what discovery already knows - no server
/// connection required.
///
/// The app otherwise assumes every backup it finds is present and intact. When something is
/// missing - purged by retention, never uploaded, written elsewhere by a second job - the user
/// finds out mid-restore, after WITH REPLACE has already dropped the target. These checks move
/// that discovery to before the script is generated.
///
/// LSN-level validation (the authoritative kind, which catches breaks these checks cannot see)
/// needs RESTORE HEADERONLY and is tracked separately.
/// </summary>
public class BackupChainValidator
{
    /// <summary>
    /// A log interval this many times the chain's median counts as a suspected missing backup.
    ///
    /// 1.5 rather than something rounder: ONE missing backup on a regular schedule shows up as a
    /// 2x interval, and a single missing log is both the most common break and the one most worth
    /// catching. A higher factor would only ever flag two or more consecutive misses. It still
    /// sits well clear of ordinary scheduler jitter, which is seconds against intervals of
    /// minutes.
    /// </summary>
    private const double LogGapFactor = 1.5;

    /// <summary>
    /// Absolute floor for reporting a log gap, so a very frequent schedule does not generate
    /// noise from sub-minute drift.
    /// </summary>
    private static readonly TimeSpan MinimumReportableLogGap = TimeSpan.FromMinutes(5);

    public List<ChainIssue> Validate(BackupChain? chain)
    {
        var issues = new List<ChainIssue>();
        if (chain == null) return issues;

        foreach (var set in chain.AllSets)
        {
            CheckStripes(set, issues);
            CheckEmptyFiles(set, issues);
        }

        CheckLogCadence(chain, issues);
        CheckOrdering(chain, issues);

        return issues;
    }

    /// <summary>
    /// Checks the discovered inventory for a database, independent of any selected chain. Explains
    /// backups that exist but can never be offered - otherwise they simply appear in the browse
    /// list and silently never show up on the timeline.
    /// </summary>
    public List<ChainIssue> ValidateInventory(IReadOnlyList<BackupSet> sets)
    {
        var issues = new List<ChainIssue>();
        if (sets.Count == 0) return issues;

        var fulls = sets.Where(s => s.Type == BackupType.Full && !s.IsCopyOnly)
            .OrderBy(s => s.Timestamp).ToList();

        if (fulls.Count == 0)
        {
            var others = sets.Count(s => s.Type != BackupType.Full);
            if (others > 0)
                issues.Add(new ChainIssue(ChainIssueSeverity.Error,
                    "No full backup found",
                    $"{others} differential/log backup(s) were discovered but no full backup to base them on. " +
                    "A restore must start from a full backup - check retention, or whether the full backups " +
                    "are under a different path than the configured pattern expects."));
            return issues;
        }

        var earliestFull = fulls[0].Timestamp;

        var orphanedDiffs = sets
            .Where(s => s.Type == BackupType.Differential && s.Timestamp < earliestFull)
            .ToList();
        if (orphanedDiffs.Count > 0)
            issues.Add(new ChainIssue(ChainIssueSeverity.Warning,
                $"{orphanedDiffs.Count} differential backup(s) have no base full",
                $"They predate the earliest full backup ({earliestFull:yyyy-MM-dd HH:mm}), so they cannot be " +
                "restored and are not offered as restore points. Usually means the full they belong to has " +
                "been removed by retention."));

        var orphanedLogs = sets
            .Where(s => s.Type == BackupType.TransactionLog && s.Timestamp <= earliestFull)
            .ToList();
        if (orphanedLogs.Count > 0)
            issues.Add(new ChainIssue(ChainIssueSeverity.Warning,
                $"{orphanedLogs.Count} log backup(s) predate the earliest full",
                $"Log backups at or before {earliestFull:yyyy-MM-dd HH:mm} have nothing to roll forward from " +
                "and are not offered as restore points."));

        return issues;
    }

    /// <summary>
    /// A striped set is only restorable with EVERY stripe present - SQL Server rejects a partial
    /// set. Stripe numbers should run 1..N with no holes.
    ///
    /// Note this cannot detect a uniformly truncated set (stripes 1 and 2 of an original 4), since
    /// nothing in the filename records how many there were. It catches holes, which is the common
    /// shape of a failed or partial upload.
    /// </summary>
    private static void CheckStripes(BackupSet set, List<ChainIssue> issues)
    {
        if (set.FileCount <= 1) return;

        var stripes = set.Files
            .Select(f => BackupSet.ParseFileName(f.FileName).stripe)
            .Where(n => n > 0)
            .OrderBy(n => n)
            .ToList();

        // Not a numbered stripe set - nothing to verify.
        if (stripes.Count != set.FileCount) return;

        var missing = Enumerable.Range(1, stripes[^1])
            .Where(n => !stripes.Contains(n))
            .ToList();

        if (missing.Count > 0)
            issues.Add(new ChainIssue(ChainIssueSeverity.Error,
                $"{set.TypeDisplay} backup at {set.Timestamp:yyyy-MM-dd HH:mm} is missing stripe(s) " +
                string.Join(", ", missing),
                $"The set has {set.FileCount} file(s) numbered up to {stripes[^1]}. A striped backup cannot be " +
                "restored unless every file is present, so this restore will fail."));
    }

    private static void CheckEmptyFiles(BackupSet set, List<ChainIssue> issues)
    {
        var empty = set.Files.Where(f => f.SizeBytes == 0).ToList();
        if (empty.Count == 0) return;

        issues.Add(new ChainIssue(ChainIssueSeverity.Error,
            $"{set.TypeDisplay} backup at {set.Timestamp:yyyy-MM-dd HH:mm} contains {empty.Count} empty file(s)",
            $"Zero-byte blob(s): {string.Join(", ", empty.Select(f => f.FileName))}. Usually an interrupted or " +
            "failed upload. SQL Server cannot read these."));
    }

    /// <summary>
    /// Looks for a hole in the log sequence. A missing .trn is invisible to timestamp-based chain
    /// building - the surrounding logs still look perfectly regular - but the restore fails at
    /// that point with error 4305.
    /// </summary>
    private static void CheckLogCadence(BackupChain chain, List<ChainIssue> issues)
    {
        var logs = chain.LogSets.OrderBy(s => s.Timestamp).ToList();
        if (logs.Count < 3) return; // too few to establish a cadence

        var intervals = new List<TimeSpan>();
        for (int i = 1; i < logs.Count; i++)
            intervals.Add(logs[i].Timestamp - logs[i - 1].Timestamp);

        var ordered = intervals.OrderBy(t => t).ToList();
        var median = ordered[ordered.Count / 2];
        if (median <= TimeSpan.Zero) return;

        var threshold = TimeSpan.FromTicks((long)(median.Ticks * LogGapFactor));
        if (threshold < MinimumReportableLogGap) threshold = MinimumReportableLogGap;

        for (int i = 1; i < logs.Count; i++)
        {
            var gap = logs[i].Timestamp - logs[i - 1].Timestamp;
            if (gap <= threshold) continue;

            issues.Add(new ChainIssue(ChainIssueSeverity.Warning,
                $"Possible missing log backup around {logs[i - 1].Timestamp:yyyy-MM-dd HH:mm}",
                $"A {FormatGap(gap)} gap between log backups, against a typical interval of {FormatGap(median)}. " +
                "If a log backup is missing the restore will stop there with error 4305. A schedule change or " +
                "maintenance window is the other likely explanation."));
        }
    }

    private static void CheckOrdering(BackupChain chain, List<ChainIssue> issues)
    {
        foreach (var diff in chain.DiffSets.Where(d => d.Timestamp < chain.FullSet.Timestamp))
            issues.Add(new ChainIssue(ChainIssueSeverity.Error,
                "Differential predates its full backup",
                $"The differential at {diff.Timestamp:yyyy-MM-dd HH:mm} is older than the full at " +
                $"{chain.FullSet.Timestamp:yyyy-MM-dd HH:mm}, so it cannot be applied to it."));

        var logs = chain.LogSets.OrderBy(s => s.Timestamp).ToList();
        var chainStart = chain.DiffSets.Count > 0
            ? chain.DiffSets[^1].Timestamp
            : chain.FullSet.Timestamp;

        foreach (var log in logs.Where(l => l.Timestamp < chainStart))
            issues.Add(new ChainIssue(ChainIssueSeverity.Error,
                "Log backup predates the base of the chain",
                $"The log at {log.Timestamp:yyyy-MM-dd HH:mm} is older than the point the database is restored " +
                $"to ({chainStart:yyyy-MM-dd HH:mm}) and cannot be applied."));
    }

    private static string FormatGap(TimeSpan gap)
        => gap.TotalHours >= 1
            ? $"{gap.TotalHours:F1} hour"
            : $"{gap.TotalMinutes:F0} minute";
}
