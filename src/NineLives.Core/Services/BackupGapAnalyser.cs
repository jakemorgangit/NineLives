using Blackcat.NineLives.Models;

namespace Blackcat.NineLives.Services;

/// <summary>One backup the source instance recorded that the container does not hold.</summary>
public sealed record MissingBackup(BackupHistoryEntry Entry, string Folder)
{
    public BackupType Type => Entry.Type;
    public DateTime TakenAt => Entry.StartedAt;
    public IReadOnlyList<string> Files => Entry.Files;
    public long SizeBytes => Entry.BackupSizeBytes ?? 0;
}

/// <summary>
/// The missing backups that share one folder - what a copy script would be written against.
/// </summary>
public sealed class MissingLocation(string folder, IReadOnlyList<MissingBackup> backups)
{
    /// <summary>The directory the files were written to, as the source instance named it.</summary>
    public string Folder { get; } = folder;

    public IReadOnlyList<MissingBackup> Backups { get; } = backups;

    public int FileCount => Backups.Sum(b => b.Files.Count);

    public long TotalSizeBytes => Backups.Sum(b => b.SizeBytes);

    public DateTime Earliest => Backups.Min(b => b.TakenAt);

    public DateTime Latest => Backups.Max(b => b.TakenAt);

    public string SizeDisplay => ByteSize.Format(TotalSizeBytes);

    /// <summary>"23 log backups" / "1 differential backup" - counted by kind, in one phrase.</summary>
    public string Summary
    {
        get
        {
            var byKind = Backups
                .GroupBy(b => b.Type)
                .OrderBy(g => g.Key)
                .Select(g => $"{g.Count()} {Describe(g.Key, g.Count())}");

            return string.Join(", ", byKind);
        }
    }

    private static string Describe(BackupType type, int count) => type switch
    {
        BackupType.Full => count == 1 ? "full backup" : "full backups",
        BackupType.Differential => count == 1 ? "differential backup" : "differential backups",
        BackupType.TransactionLog => count == 1 ? "log backup" : "log backups",
        _ => count == 1 ? "backup" : "backups"
    };
}

/// <summary>
/// What the source instance recorded, set against what is actually in the container (#451).
///
/// The estate shape this exists for: fulls and diffs go to blob, and the transaction logs go
/// somewhere else - a local or cluster disk for throughput, or a share because log shipping
/// already owns them. Pointed at the container, the app builds an honest chain out of what it can
/// see, and that chain stops at the last differential. Nothing says the logs exist, where they
/// went, or that the restore on offer is discarding hours of recoverable time.
///
/// Finding that out requires knowing to go and look, which is precisely the knowledge somebody
/// does not have at 3am on a server they did not build. msdb knows, so this asks it.
/// </summary>
public static class BackupGapAnalyser
{
    /// <summary>
    /// How far apart two timestamps can be and still describe the same backup, when LSNs are not
    /// available on both sides.
    ///
    /// A container set's timestamp is parsed from its file name, which the writer stamps at the
    /// START of the backup; msdb records the start too, so these agree exactly in the ordinary
    /// case. The tolerance covers a writer that stamps a second late, not a genuinely different
    /// backup - two backups of one database two seconds apart is not a real schedule.
    /// </summary>
    private static readonly TimeSpan SameBackupWindow = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Backups the instance recorded that the container does not hold, grouped by where they are.
    ///
    /// Matching is by LSN wherever both sides have one, because that is the only identifier that
    /// is genuinely the backup rather than a description of it: a file can be renamed, moved, or
    /// written to two places, and its LSN range still says which backup it is. Container sets only
    /// carry LSNs once they have been audited, so the fallback is type plus timestamp - weaker,
    /// and the reason a set that is present but renamed could be reported as missing. Reporting a
    /// present backup as missing costs an unnecessary copy; the reverse would leave somebody
    /// restoring to an hour earlier than they could have, so the bias is deliberate.
    /// </summary>
    public static IReadOnlyList<MissingLocation> Compare(
        IReadOnlyList<BackupHistoryEntry> history,
        IReadOnlyList<BackupSet> inContainer,
        string database)
    {
        if (history.Count == 0) return [];

        var mine = history
            .Where(h => string.Equals(h.DatabaseName, database, StringComparison.OrdinalIgnoreCase))
            .Where(h => h.HasFiles)
            .ToList();

        var held = inContainer
            .Where(s => string.Equals(s.DatabaseName, database, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var missing = mine.Where(h => !IsHeld(h, held)).ToList();

        return missing
            .Select(h => new MissingBackup(h, FolderOf(h.Files[0])))
            .GroupBy(m => m.Folder, StringComparer.OrdinalIgnoreCase)
            .Select(g => new MissingLocation(g.Key, g.OrderBy(m => m.TakenAt).ToList()))
            .OrderBy(l => l.Earliest)
            .ToList();
    }

    private static bool IsHeld(BackupHistoryEntry entry, List<BackupSet> held)
    {
        foreach (var set in held)
        {
            if (set.Type != entry.Type) continue;

            // The reliable answer, when both sides have it.
            if (entry.LastLsn is { } mine && set.LastLsn is { } theirs)
            {
                if (mine == theirs) return true;
                continue;
            }

            if (Math.Abs((set.Timestamp - entry.StartedAt).TotalSeconds)
                <= SameBackupWindow.TotalSeconds)
                return true;
        }

        return false;
    }

    /// <summary>
    /// The source's log backups taken after a given moment, grouped by where they live (#451).
    ///
    /// The Copy screen's question, which is not the Restore screen's. A copy writes its own full and
    /// reads no existing chain, so "what is this container missing" means nothing there. What does
    /// mean something is the cutover: the copy restores a full taken at T, and the logs the source
    /// has taken SINCE T are what would roll the target forward to now. The long part happens in
    /// advance and the downtime is only the tail.
    ///
    /// Strictly after, and by the log's own start: a log backup that began before the full was
    /// taken is already inside it.
    /// </summary>
    public static IReadOnlyList<MissingLocation> LogsTakenAfter(
        IReadOnlyList<BackupHistoryEntry> history,
        string database,
        DateTime after)
    {
        var logs = history
            .Where(h => string.Equals(h.DatabaseName, database, StringComparison.OrdinalIgnoreCase))
            .Where(h => h.Type == BackupType.TransactionLog)
            .Where(h => h.HasFiles)
            .Where(h => h.StartedAt > after)
            .ToList();

        return logs
            .Select(h => new MissingBackup(h, FolderOf(h.Files[0])))
            .GroupBy(m => m.Folder, StringComparer.OrdinalIgnoreCase)
            .Select(g => new MissingLocation(g.Key, g.OrderBy(m => m.TakenAt).ToList()))
            .OrderBy(l => l.Earliest)
            .ToList();
    }

    /// <summary>
    /// The directory part of a device path, handling both separators.
    ///
    /// Not Path.GetDirectoryName: these strings come from the SOURCE instance and describe its
    /// file system, not this machine's. A UNC path read on a machine with different rules must
    /// come back out unchanged, because it is about to be printed into a script that runs over
    /// there.
    /// </summary>
    internal static string FolderOf(string devicePath)
    {
        var cut = devicePath.LastIndexOfAny(['\\', '/']);
        return cut <= 0 ? devicePath : devicePath[..cut];
    }

    /// <summary>
    /// How much recovery time the container's own chain is throwing away.
    ///
    /// The gap between the newest backup the container holds and the newest the instance recorded.
    /// Null when the container is not behind - which includes the case where it holds MORE than
    /// msdb knows about, as it will on any instance whose history has been trimmed.
    /// </summary>
    public static TimeSpan? RecoveryTimeNotInContainer(
        IReadOnlyList<BackupHistoryEntry> history,
        IReadOnlyList<BackupSet> inContainer,
        string database)
    {
        var newestRecorded = history
            .Where(h => string.Equals(h.DatabaseName, database, StringComparison.OrdinalIgnoreCase))
            .Select(h => (DateTime?)h.StartedAt)
            .DefaultIfEmpty(null)
            .Max();

        var newestHeld = inContainer
            .Where(s => string.Equals(s.DatabaseName, database, StringComparison.OrdinalIgnoreCase))
            .Select(s => (DateTime?)s.Timestamp)
            .DefaultIfEmpty(null)
            .Max();

        if (newestRecorded is not { } recorded || newestHeld is not { } heldAt) return null;

        var behind = recorded - heldAt;
        return behind > TimeSpan.Zero ? behind : null;
    }
}
