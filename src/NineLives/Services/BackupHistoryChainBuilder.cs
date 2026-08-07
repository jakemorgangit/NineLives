using Blackcat.NineLives.Models;

namespace Blackcat.NineLives.Services;

/// <summary>
/// One restorable chain, assembled from what the source instance recorded (#149).
/// </summary>
/// <param name="Full">The full to restore first.</param>
/// <param name="Differential">The differential to apply on top, if one belongs to that full.</param>
/// <param name="Logs">The logs to roll forward, in order.</param>
public sealed record BackupHistoryChain(
    BackupHistoryEntry Full,
    BackupHistoryEntry? Differential,
    IReadOnlyList<BackupHistoryEntry> Logs)
{
    /// <summary>Every backup in the chain, in the order a restore applies them.</summary>
    public IEnumerable<BackupHistoryEntry> All
    {
        get
        {
            yield return Full;
            if (Differential != null) yield return Differential;
            foreach (var log in Logs) yield return log;
        }
    }

    /// <summary>Every file the restore needs, in order, stripes included.</summary>
    public IEnumerable<string> Files => All.SelectMany(e => e.Files);

    /// <summary>The moment this chain restores to.</summary>
    public DateTime RestoresTo => All.Max(e => e.FinishedAt);

    public string Summary
    {
        get
        {
            var parts = new List<string> { Full.Files.Count > 1 ? $"1 Full ({Full.Files.Count} files)" : "1 Full" };
            if (Differential != null) parts.Add("1 Diff");
            if (Logs.Count > 0) parts.Add($"{Logs.Count} Log(s)");
            return string.Join(" + ", parts);
        }
    }
}

/// <summary>
/// Builds restore chains from msdb history, using LSNs rather than timestamps (#149, and #130's
/// question answered for this source).
///
/// The blob path has to infer type and database from filenames and assemble by time, because that
/// is all a container listing offers - and #130 exists because that inference has been wrong in
/// several ways this repo has fixed. None of that applies here. The source instance recorded which
/// full each differential belongs to, and which log follows which, and those relationships are
/// exact:
///
///   - a differential belongs to a full when its DatabaseBackupLSN equals that full's CheckpointLSN;
///   - a log follows the chain when its LastLSN is past the point already restored to.
///
/// Timestamps are used for ORDER and for nothing else. Two backups a second apart, a clock that
/// went backwards over a daylight-saving change, a full restored and re-backed-up out of sequence -
/// none of them can put the wrong backup in a chain here, because none of them move an LSN.
/// </summary>
public class BackupHistoryChainBuilder
{
    /// <summary>
    /// The chains that can be restored from this history, newest full first.
    ///
    /// Copy-only fulls are offered as chain bases but never take differentials: a copy-only backup
    /// does not reset the differential base, so a differential taken afterwards still belongs to
    /// the previous ordinary full (#49). Getting that wrong pairs a differential with a full it
    /// cannot be applied to, and SQL Server rejects it at restore time with error 3136.
    /// </summary>
    public List<BackupHistoryChain> Build(IEnumerable<BackupHistoryEntry> history)
    {
        // Only what a restore can actually use. An entry with no files is a record of a backup
        // whose files have since been deleted, archived or pruned - msdb keeps those.
        var usable = history.Where(e => e.HasFiles).ToList();

        var fulls = usable
            .Where(e => e.Type == BackupType.Full)
            .OrderByDescending(e => e.StartedAt)
            .ToList();

        var diffs = usable.Where(e => e.Type == BackupType.Differential).ToList();
        var logs = usable.Where(e => e.Type == BackupType.TransactionLog)
            .OrderBy(e => e.StartedAt)
            .ToList();

        var chains = new List<BackupHistoryChain>();

        foreach (var full in fulls)
        {
            // The definitive test, and the reason this builder exists: DatabaseBackupLSN names the
            // full a differential was taken against. A differential whose base is missing from the
            // history is simply not offered - it cannot be restored without that full.
            var differential = full.IsCopyOnly || full.CheckpointLsn == null
                ? null
                : diffs
                    .Where(d => d.DatabaseBackupLsn == full.CheckpointLsn)
                    .OrderByDescending(d => d.StartedAt)
                    .FirstOrDefault();

            var restoredTo = differential?.LastLsn ?? full.LastLsn;

            // A log belongs on top when it carries the chain PAST where the restore has reached.
            // Comparing LastLSN rather than the timestamp is what makes this exact: a log taken
            // before the full but finishing after it - which happens on a busy instance - contains
            // nothing the restore still needs.
            var following = restoredTo == null
                ? []
                : logs.Where(l => l.LastLsn > restoredTo).ToList();

            chains.Add(new BackupHistoryChain(full, differential, following));
        }

        return chains;
    }

    /// <summary>
    /// The chain that restores to <paramref name="pointInTime"/>, or null when nothing reaches it.
    ///
    /// The newest full at or before the moment asked for, then only the logs needed to roll forward
    /// to it - not every log after the full. Restoring logs past the point asked for would take the
    /// database beyond the moment somebody chose, which on a recovery from a bad DELETE is the
    /// whole thing they were trying to avoid.
    /// </summary>
    public BackupHistoryChain? BuildTo(IEnumerable<BackupHistoryEntry> history, DateTime pointInTime)
    {
        var candidates = Build(history)
            .Where(c => c.Full.FinishedAt <= pointInTime)
            .OrderByDescending(c => c.Full.StartedAt)
            .ToList();

        foreach (var chain in candidates)
        {
            var differential = chain.Differential?.FinishedAt <= pointInTime ? chain.Differential : null;

            // Every log that finishes at or before the target, plus the one that spans it - that
            // last one is where STOPAT lands, and without it the restore stops short of the moment
            // asked for.
            var logs = new List<BackupHistoryEntry>();
            foreach (var log in chain.Logs)
            {
                logs.Add(log);
                if (log.FinishedAt >= pointInTime) break;
            }

            var reached = logs.Count > 0
                ? logs[^1].FinishedAt
                : differential?.FinishedAt ?? chain.Full.FinishedAt;

            if (reached >= pointInTime || logs.Count > 0)
                return new BackupHistoryChain(chain.Full, differential, logs);
        }

        return null;
    }
}
