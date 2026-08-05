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
/// One member of a chain paired with what RESTORE HEADERONLY said about it.
/// <paramref name="Header"/> is null when the read failed or was not attempted.
/// </summary>
public sealed record ChainHeader(BackupSet Set, BackupFileInfo? Header);

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

    /// <summary>
    /// The interval between the data backup and the FIRST log is inherently looser than the
    /// interval between logs: the full itself takes time to run, and the log schedule is
    /// independent of it, so the first log often lands most of an interval later. A tighter
    /// factor here would warn on healthy chains - a full finishing at 22:09 with hourly logs on
    /// the hour leaves a perfectly normal 51 minute gap.
    /// </summary>
    private const double LogCoverageStartFactor = 3.0;

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
        CheckLogCoverageStart(chain, issues);
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

        var medianOrNull = MedianLogInterval(logs);
        if (medianOrNull == null) return;
        var median = medianOrNull.Value;

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

    /// <summary>
    /// Checks the first log follows closely enough behind the data backup to continue from it.
    ///
    /// Retention is the usual cause of a failure here: log backups are commonly kept for days
    /// while fulls and differentials are kept for weeks, so the oldest surviving log can sit long
    /// after the differential that precedes it, with everything in between purged. The chain
    /// looks complete - a full, a differential and a run of regularly spaced logs - but the logs
    /// cannot connect to the data backup, and the restore fails with 4305.
    ///
    /// Found on real data: an offered restore point whose only log began nearly four days after
    /// its differential. The cadence check could not see it, because it only ever compares logs
    /// against each other.
    ///
    /// Needs at least three logs to establish a cadence. With fewer, only the LSN check can tell.
    /// </summary>
    private static void CheckLogCoverageStart(BackupChain chain, List<ChainIssue> issues)
    {
        var logs = chain.LogSets.OrderBy(s => s.Timestamp).ToList();
        if (logs.Count < 3) return;

        var median = MedianLogInterval(logs);
        if (median == null) return;

        var chainBase = chain.DiffSets.Count > 0
            ? chain.DiffSets[^1]
            : chain.FullSet;

        var gap = logs[0].Timestamp - chainBase.Timestamp;
        if (gap <= TimeSpan.Zero) return;

        var threshold = TimeSpan.FromTicks((long)(median.Value.Ticks * LogCoverageStartFactor));
        if (threshold < MinimumReportableLogGap) threshold = MinimumReportableLogGap;
        if (gap <= threshold) return;

        issues.Add(new ChainIssue(ChainIssueSeverity.Warning,
            "Log backups may not reach back to the data backup",
            $"The first log is {FormatGap(gap)} after the {chainBase.TypeDisplay.ToLowerInvariant()} backup at " +
            $"{chainBase.Timestamp:yyyy-MM-dd HH:mm}, against a typical log interval of {FormatGap(median.Value)}. " +
            "Log backups covering that period appear to be missing - commonly because logs are kept for a " +
            "shorter retention than fulls and differentials. The restore would fail with error 4305. " +
            "Validate Chain will confirm against the backup headers."));
    }

    private static TimeSpan? MedianLogInterval(IReadOnlyList<BackupSet> logs)
    {
        if (logs.Count < 2) return null;

        var intervals = new List<TimeSpan>();
        for (int i = 1; i < logs.Count; i++)
            intervals.Add(logs[i].Timestamp - logs[i - 1].Timestamp);

        var ordered = intervals.OrderBy(t => t).ToList();
        var median = ordered[ordered.Count / 2];
        return median > TimeSpan.Zero ? median : null;
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

    /// <summary>
    /// Authoritative validation against RESTORE HEADERONLY metadata.
    ///
    /// The structural checks above reason about filenames and timestamps. This reasons about what
    /// SQL Server itself recorded inside the backups, which catches breaks the others physically
    /// cannot see - a log taken by a different job to a different destination leaves the visible
    /// sequence looking perfectly regular while the LSN chain is broken.
    ///
    /// Rules were confirmed against a real backup set before being encoded:
    ///   - a differential's DatabaseBackupLSN equals its base full's CheckpointLSN
    ///   - consecutive logs meet exactly: log[i].FirstLSN == log[i-1].LastLSN
    ///   - only the FIRST log spans the recovery point; later ones start after it, so requiring
    ///     every log to span it would reject valid chains
    /// </summary>
    public List<ChainIssue> ValidateLsnChain(BackupChain? chain, IReadOnlyList<ChainHeader> headers)
    {
        var issues = new List<ChainIssue>();
        if (chain == null || headers.Count == 0) return issues;

        BackupFileInfo? HeaderFor(BackupSet set) =>
            headers.FirstOrDefault(h => ReferenceEquals(h.Set, set))?.Header;

        var unread = headers.Where(h => h.Header == null).ToList();
        if (unread.Count > 0)
            issues.Add(new ChainIssue(ChainIssueSeverity.Warning,
                $"Could not read {unread.Count} backup header(s)",
                "Those members could not be verified. The blob may be unreadable, or the server's " +
                "credential may not cover it. Validation below is incomplete."));

        CheckDatabaseIdentity(chain, headers, issues);
        CheckDeclaredTypes(headers, issues);

        var fullHeader = HeaderFor(chain.FullSet);

        // Differential base: the only authoritative test that a differential belongs to this full.
        foreach (var diff in chain.DiffSets)
        {
            var diffHeader = HeaderFor(diff);
            if (diffHeader?.DatabaseBackupLsn == null || fullHeader?.CheckpointLsn == null) continue;

            if (diffHeader.DatabaseBackupLsn != fullHeader.CheckpointLsn)
                issues.Add(new ChainIssue(ChainIssueSeverity.Error,
                    $"Differential at {diff.Timestamp:yyyy-MM-dd HH:mm} is not based on this full backup",
                    $"Its DatabaseBackupLSN ({diffHeader.DatabaseBackupLsn}) does not match the full backup's " +
                    $"CheckpointLSN ({fullHeader.CheckpointLsn}). It belongs to a different full - most often " +
                    "because a copy-only or ad-hoc full was taken in between. The restore would fail with " +
                    "error 3136."));
        }

        CheckLogLsnContinuity(chain, HeaderFor, issues);

        return issues;
    }

    private static void CheckDatabaseIdentity(
        BackupChain chain, IReadOnlyList<ChainHeader> headers, List<ChainIssue> issues)
    {
        var names = headers
            .Where(h => h.Header?.DatabaseName != null)
            .Select(h => h.Header!.DatabaseName!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (names.Count > 1)
            issues.Add(new ChainIssue(ChainIssueSeverity.Error,
                "Chain mixes backups of different databases",
                $"The headers report {string.Join(", ", names)}. This usually means the blob path pattern " +
                "is parsing the folder structure incorrectly, so unrelated backups have been grouped " +
                "together. Restoring this chain would fail, or restore the wrong data."));

        // The name discovery inferred from the path should agree with what is inside the backup.
        var expected = chain.FullSet.DatabaseName;
        if (!string.IsNullOrWhiteSpace(expected) && names.Count == 1
            && !string.Equals(expected, names[0], StringComparison.OrdinalIgnoreCase))
            issues.Add(new ChainIssue(ChainIssueSeverity.Warning,
                "Backup contains a different database than the path suggests",
                $"The path indicates '{expected}' but the backup header says '{names[0]}'. The restore will " +
                "use the contents of the backup - check the blob path pattern is correct for this container."));
    }

    private static void CheckDeclaredTypes(IReadOnlyList<ChainHeader> headers, List<ChainIssue> issues)
    {
        // Catches a misparsed filename: a log written with a .bak extension, or a copy-only full
        // classified as a regular one. The header is the truth; the filename is a guess.
        foreach (var h in headers)
        {
            if (h.Header == null || h.Header.Type == BackupType.Unknown) continue;
            if (h.Header.Type == h.Set.Type) continue;

            issues.Add(new ChainIssue(ChainIssueSeverity.Error,
                $"Backup at {h.Set.Timestamp:yyyy-MM-dd HH:mm} is not the type its filename suggests",
                $"It was treated as a {h.Set.TypeDisplay} backup, but the header says it is a " +
                $"{h.Header.TypeDisplay} backup. The chain was built on the wrong assumption."));
        }
    }

    private static void CheckLogLsnContinuity(
        BackupChain chain, Func<BackupSet, BackupFileInfo?> headerFor, List<ChainIssue> issues)
    {
        var logs = chain.LogSets.OrderBy(s => s.Timestamp).ToList();
        if (logs.Count == 0) return;

        // The point the database reaches after the data backups are restored.
        var basePoint = chain.DiffSets.Count > 0
            ? headerFor(chain.DiffSets[^1])?.LastLsn
            : headerFor(chain.FullSet)?.LastLsn;

        var firstHeader = headerFor(logs[0]);
        if (basePoint != null && firstHeader?.FirstLsn != null && firstHeader.LastLsn != null)
        {
            // Only the first log must span the base point - later logs legitimately start after it.
            if (firstHeader.FirstLsn > basePoint)
                issues.Add(new ChainIssue(ChainIssueSeverity.Error,
                    "The log chain starts too late to follow the data backup",
                    $"The first log begins at LSN {firstHeader.FirstLsn}, after the restore point of " +
                    $"{basePoint}. A log backup covering that point is missing, so the restore would fail " +
                    "with error 4305."));
        }

        for (int i = 1; i < logs.Count; i++)
        {
            var previous = headerFor(logs[i - 1]);
            var current = headerFor(logs[i]);
            if (previous?.LastLsn == null || current?.FirstLsn == null) continue;

            if (current.FirstLsn > previous.LastLsn)
                issues.Add(new ChainIssue(ChainIssueSeverity.Error,
                    $"Break in the log chain before {logs[i].Timestamp:yyyy-MM-dd HH:mm}",
                    $"This log begins at LSN {current.FirstLsn} but the previous one ended at " +
                    $"{previous.LastLsn}, leaving a gap. A log backup is missing from the container - " +
                    "possibly taken by another job to a different destination. The restore would fail " +
                    "with error 4305."));
        }
    }

    private static string FormatGap(TimeSpan gap)
        => gap.TotalHours >= 1
            ? $"{gap.TotalHours:F1} hour"
            : $"{gap.TotalMinutes:F0} minute";
}
