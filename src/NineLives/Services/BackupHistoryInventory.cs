using Blackcat.NineLives.Models;

namespace Blackcat.NineLives.Services;

/// <summary>
/// Turns what an instance recorded in <c>msdb</c> into the inventory the restore workflow already
/// works on (#149, #165).
///
/// This is the whole of what a shared path needs. Everything below the listing - the working set,
/// the chain builder, the restore points, the timeline, the options, the script, the execute path -
/// operates on <see cref="BackupSet"/> and has never cared where the sets came from. Give it sets
/// whose files carry a path instead of a URL and the entire workflow applies unchanged, which is
/// what #149 asked for and what a parallel screen with its own script pane did not do.
///
/// Deliberately not an inference step. A container listing has to guess type, database and server
/// from filenames, and #130 exists because that guessing has been wrong in several ways this repo
/// has since fixed. None of it applies here: the instance that took the backups recorded all of it,
/// including the LSNs, so this only has to carry it across without losing anything.
/// </summary>
public static class BackupHistoryInventory
{
    /// <summary>
    /// The sets an instance's history describes, newest last.
    /// </summary>
    /// <param name="entries">What <c>msdb</c> returned.</param>
    /// <param name="mapping">
    /// How the target reaches the files, when it reaches them by a different path from the one the
    /// source wrote. Applied HERE rather than at restore time so that everything downstream - the
    /// chain, the file list on screen, the script, the readability check - names the same file. A
    /// substitution applied in only some of those places is how a screen ends up checking one path
    /// and restoring another.
    /// </param>
    public static List<BackupSet> ToSets(
        IEnumerable<BackupHistoryEntry> entries,
        BackupPathMapping? mapping = null)
    {
        var map = mapping ?? BackupPathMapping.None;

        return entries
            // Only what a restore could actually use. msdb keeps the record of a backup long after
            // the files themselves have been deleted, archived or pruned, and a row with no files
            // is exactly that. Carried through, it becomes a set with nothing in it - which the
            // chain builder offers as a restore point, and which fails at RESTORE with nothing to
            // read from.
            .Where(e => e.HasFiles)
            .OrderBy(e => e.StartedAt)
            .Select(e => ToSet(e, map))
            .ToList();
    }

    public static BackupSet ToSet(BackupHistoryEntry entry, BackupPathMapping? mapping = null)
    {
        var map = mapping ?? BackupPathMapping.None;
        var files = entry.Files.Select(map.Apply).ToList();

        var (host, instance) = ServerIdentity.Split(entry.ServerName);

        return new BackupSet
        {
            // msdb has no notion of the set ids this app derives from blob names, so the identity
            // is what a person would recognise it by: what it is and when it ran.
            // The position suffix keeps two backups of one database that share a second - which a
            // multi-backup FILE can genuinely hold - from collapsing into one set.
            SetId = entry.Position is int p
                ? $"{entry.DatabaseName}_{entry.StartedAt:yyyyMMdd_HHmmss}_p{p}"
                : $"{entry.DatabaseName}_{entry.StartedAt:yyyyMMdd_HHmmss}",
            Position = entry.Position,
            DatabaseName = entry.DatabaseName,
            ServerName = host,
            InstanceName = instance,
            Type = entry.Type,
            Timestamp = entry.StartedAt,

            // The instance's own record of when it ran, on its own clock - neither parsed out of a
            // name nor read off an upload. That is the strongest reading this app has, so it is not
            // marked approximate and nothing offers to convert it.
            TimestampSource = BackupTimestampSource.BackupHeader,

            IsCopyOnly = entry.IsCopyOnly,

            CheckpointLsn = entry.CheckpointLsn,
            DatabaseBackupLsn = entry.DatabaseBackupLsn,
            FirstLsn = entry.FirstLsn,
            LastLsn = entry.LastLsn,

            Files = files.Select((path, index) => ToFile(entry, path, index)).ToList()
        };
    }

    private static BackupFileInfo ToFile(BackupHistoryEntry entry, string path, int index)
    {
        var (host, instance) = ServerIdentity.Split(entry.ServerName);

        return new BackupFileInfo
        {
            // The path is what the restore names. BlobUrl is deliberately left empty - there is no
            // URL, and filling one in with a path would be the kind of quiet lie that ends in a
            // restore aimed at the wrong device.
            LocalPath = path,
            BlobName = System.IO.Path.GetFileName(path),

            Type = entry.Type,
            InferredDatabaseName = entry.DatabaseName,
            InferredServerName = host,
            InferredInstanceName = instance,
            IsCopyOnly = entry.IsCopyOnly,
            LastModified = new DateTimeOffset(entry.FinishedAt, TimeSpan.Zero),

            // Size is per SET in msdb, not per stripe, so it is recorded once rather than repeated
            // on every file - a striped backup would otherwise report several times its real size.
            SizeBytes = index == 0 ? entry.BackupSizeBytes ?? 0 : 0
        };
    }
}
