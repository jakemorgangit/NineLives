using Blackcat.NineLives.Models;

namespace Blackcat.NineLives.Services;

/// <summary>
/// Turns a chain discovered through a source instance's msdb into the shape the restore script
/// generator already understands (#149).
///
/// Deliberately an adapter rather than a second generator. Everything a restore script has to get
/// right - WITH REPLACE dropping the target, NORECOVERY on every step but the last, STOPAT on every
/// log rather than only the final one, the MOVE clauses, disconnecting and reconnecting sessions -
/// is the same whether the backups came from a container or a file share. That logic has been
/// through #110, #45 and #44 and has the tests to show for it; the only thing that differs is how a
/// file is named, and the file itself now says which it is.
///
/// So a shared backup location gets the whole of that behaviour for free, and a fix to any of it
/// applies to both sources at once.
/// </summary>
public static class BackupHistoryChainAdapter
{
    /// <summary>
    /// The chain as a <see cref="BackupChain"/>, with every file carrying its path so the script
    /// addresses them as DISK.
    /// </summary>
    public static BackupChain ToRestorableChain(BackupHistoryChain chain, DateTime? stopAt = null)
    {
        return new BackupChain
        {
            FullSet = ToSet(chain.Full),
            DiffSets = chain.Differential == null ? [] : [ToSet(chain.Differential)],
            LogSets = chain.Logs.Select(ToSet).ToList(),
            StopAt = stopAt
        };
    }

    private static BackupSet ToSet(BackupHistoryEntry entry) => new()
    {
        // msdb has no notion of the set ids this app derives from blob names, so the identity is
        // what a person would recognise it by: what it is and when it ran.
        SetId = $"{entry.DatabaseName}_{entry.StartedAt:yyyyMMdd_HHmmss}",
        DatabaseName = entry.DatabaseName,
        ServerName = entry.ServerName,
        Type = entry.Type,
        Timestamp = entry.StartedAt,
        IsCopyOnly = entry.IsCopyOnly,
        Files = entry.Files.Select((path, index) => ToFile(entry, path, index)).ToList()
    };

    private static BackupFileInfo ToFile(BackupHistoryEntry entry, string path, int index) => new()
    {
        // The path is what the restore names. BlobName is kept human-readable for the panels that
        // show a file, and BlobUrl is deliberately left empty - there is no URL, and filling one in
        // with a path would be the kind of quiet lie that produces a restore from the wrong device.
        LocalPath = path,
        BlobName = System.IO.Path.GetFileName(path),
        Type = entry.Type,
        InferredDatabaseName = entry.DatabaseName,
        InferredServerName = entry.ServerName,
        IsCopyOnly = entry.IsCopyOnly,
        LastModified = new DateTimeOffset(entry.FinishedAt, TimeSpan.Zero),

        // Size is per SET in msdb, not per stripe, so it is recorded once rather than repeated on
        // every file - a striped backup would otherwise report several times its real size.
        SizeBytes = index == 0 ? entry.BackupSizeBytes ?? 0 : 0
    };
}
