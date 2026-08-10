using System.Text;
using Blackcat.NineLives.Models;

namespace Blackcat.NineLives.Services;

/// <summary>What a retention rule would do to one backup set (#241).</summary>
public enum RetentionVerdict
{
    /// <summary>Inside the window - the rule keeps it.</summary>
    Keep = 0,

    /// <summary>
    /// Outside the window, but a kept restore depends on it: the base full of a kept
    /// differential, or a log that carries the chain from that base into the window. Deleting it
    /// breaks restores the rule promised to keep.
    /// </summary>
    KeepDespiteAge = 1,

    /// <summary>Outside the window and nothing kept depends on it - provably safe to delete.</summary>
    Deletable = 2,

    /// <summary>Already broken - a differential or log whose base full is gone entirely.</summary>
    Broken = 3
}

/// <summary>One set, judged.</summary>
public sealed record RetentionFinding(BackupSet Set, RetentionVerdict Verdict, string Reason);

/// <summary>
/// Referees the fight between the storage bill and restorability (#241).
///
/// A lifecycle rule deletes blobs by age, and nobody checks whether the full it just deleted was
/// the base of a differential somebody still needs - the first sign is error 3136 mid-restore.
/// The app knows the chains, so it can say what a rule WOULD do: what goes, what must stay
/// despite its age, and what is already broken.
///
/// Report-only, deliberately. The app that exists to make restores safe should not also be the
/// app that deletes backups; deleting stays a human act, done elsewhere, with this report in
/// hand.
/// </summary>
public static class RetentionAdvisor
{
    /// <summary>Judges every set against a keep-window, per database, LSN-aware.</summary>
    public static List<RetentionFinding> Advise(
        IReadOnlyList<BackupSet> sets, int keepDays, DateTime now)
    {
        var windowStart = now.AddDays(-keepDays);
        var findings = new List<RetentionFinding>();

        foreach (var group in sets.GroupBy(s => s.DatabaseName ?? "", StringComparer.OrdinalIgnoreCase))
        {
            findings.AddRange(AdviseDatabase(group.OrderBy(s => s.Timestamp).ToList(), windowStart));
        }

        return findings
            .OrderBy(f => f.Set.DatabaseName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(f => f.Set.Timestamp)
            .ToList();
    }

    private static IEnumerable<RetentionFinding> AdviseDatabase(
        List<BackupSet> sets, DateTime windowStart)
    {
        var fulls = sets.Where(s => s.Type == BackupType.Full).ToList();

        // The base full: the newest full at or before the window opens. Restoring to the OLDEST
        // kept moment starts from it, so the window's own promise depends on a set outside it.
        var baseFull = fulls.LastOrDefault(f => f.Timestamp <= windowStart);

        // The newest differential between the base full and the window - it shortens the log
        // bridge the same way it shortens a restore.
        var baseDiff = baseFull == null
            ? null
            : sets.LastOrDefault(s =>
                s.Type == BackupType.Differential &&
                s.Timestamp > baseFull.Timestamp && s.Timestamp <= windowStart &&
                BelongsTo(s, baseFull));

        var bridgeFrom = baseDiff?.Timestamp ?? baseFull?.Timestamp;

        foreach (var set in sets)
        {
            if (set.Timestamp > windowStart)
            {
                // Inside the window - but a diff or log whose base full does not exist at all is
                // not kept, it is already lost.
                if (set.Type != BackupType.Full && FindBase(set, fulls) == null && fulls.Count > 0)
                {
                    yield return new RetentionFinding(set, RetentionVerdict.Broken,
                        "Its base full is not in this listing - it cannot be restored from now, " +
                        "whatever the retention rule says.");
                    continue;
                }

                yield return new RetentionFinding(set, RetentionVerdict.Keep, "Inside the window.");
                continue;
            }

            // Outside the window: the rule wants it gone. Does anything kept depend on it?
            if (ReferenceEquals(set, baseFull))
            {
                yield return new RetentionFinding(set, RetentionVerdict.KeepDespiteAge,
                    "The base full for the oldest kept moment - restores inside the window " +
                    "start from it. Deleting it is error 3136 on a restore the rule promised.");
                continue;
            }

            if (ReferenceEquals(set, baseDiff))
            {
                yield return new RetentionFinding(set, RetentionVerdict.KeepDespiteAge,
                    "The newest differential under the window - it carries the chain from the " +
                    "base full to the window's edge.");
                continue;
            }

            if (set.Type == BackupType.TransactionLog && bridgeFrom != null &&
                set.Timestamp >= bridgeFrom)
            {
                yield return new RetentionFinding(set, RetentionVerdict.KeepDespiteAge,
                    "A bridge log: it carries the chain from the base into the window. Without " +
                    "it, point-in-time stops working at the window's own edge.");
                continue;
            }

            yield return new RetentionFinding(set, RetentionVerdict.Deletable,
                "Outside the window, and no kept restore passes through it.");
        }
    }

    /// <summary>LSN pairing when both sides know it; the latest-full-before fallback otherwise.</summary>
    private static bool BelongsTo(BackupSet dependent, BackupSet full)
    {
        if (dependent.DatabaseBackupLsn is { } baseLsn && full.CheckpointLsn is { } checkpoint)
            return baseLsn == checkpoint;

        return true; // timestamp ordering already put it after this full
    }

    private static BackupSet? FindBase(BackupSet dependent, List<BackupSet> fulls)
    {
        if (dependent.DatabaseBackupLsn is { } baseLsn)
        {
            var byLsn = fulls.LastOrDefault(f => f.CheckpointLsn == baseLsn);
            if (byLsn != null) return byLsn;
            if (fulls.Any(f => f.CheckpointLsn != null)) return null; // LSNs known, none match
        }

        return fulls.LastOrDefault(f => f.Timestamp <= dependent.Timestamp);
    }

    // ── the report ──────────────────────────────────────────────────────────────

    public static string Summarise(IReadOnlyList<RetentionFinding> findings, int keepDays)
    {
        var deletable = findings.Where(f => f.Verdict == RetentionVerdict.Deletable).ToList();
        var kept = findings.Count(f => f.Verdict == RetentionVerdict.Keep);
        var despite = findings.Count(f => f.Verdict == RetentionVerdict.KeepDespiteAge);
        var broken = findings.Count(f => f.Verdict == RetentionVerdict.Broken);
        var reclaim = deletable.Sum(f => f.Set.TotalSizeBytes);

        var sb = new StringBuilder();
        sb.Append($"A {keepDays}-day rule keeps {kept} set(s), must ALSO keep {despite} older " +
                  $"set(s) that kept restores depend on, and can delete {deletable.Count} set(s)" +
                  (reclaim > 0 ? $" reclaiming {ByteSize.Format(reclaim)}." : "."));

        if (broken > 0)
            sb.Append($" {broken} set(s) are already broken - their base full is gone.");

        return sb.ToString();
    }
}
