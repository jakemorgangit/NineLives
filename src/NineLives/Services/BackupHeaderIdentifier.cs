using Blackcat.NineLives.Models;

namespace Blackcat.NineLives.Services;

/// <summary>
/// Asks SQL Server what a backup file actually is, for the files whose NAME did not say (#130).
///
/// Chains are built from path structure and filenames: the pattern gives server, database and type,
/// the filename gives the time. That is cheap, works with no connection at all, and is right the
/// large majority of the time - which is why it stays the primary mechanism.
///
/// But it fails, and this repo's history is largely a list of the ways: a database whose name
/// contains "diff" (#44), a full misread as a differential leaving a container with no restore
/// points at all (#45), a wrong path pattern, a file copied into the wrong folder. The header is
/// what the RESTORE itself reads, so it is immune to every one of them.
///
/// The reason this is scoped to the UNCLASSIFIED files rather than run over everything is cost: one
/// HEADERONLY per file, each a network read. Across a 4,400-blob container that is thousands of
/// round trips. Across the handful the pattern could not place, it is seconds - and those are
/// precisely the files where the answer is worth paying for, because they are the ones the app
/// currently cannot offer at all.
/// </summary>
public class BackupHeaderIdentifier(ISqlServerService sql)
{
    /// <summary>
    /// Whether a file is one the filename could not place.
    ///
    /// Either of these means it cannot reach a restore chain: an unknown type never enters the
    /// fulls, diffs or logs collections, and a file with no database is filtered out of every
    /// working set. So they are not merely untidy - they are invisible.
    /// </summary>
    public static bool NeedsIdentifying(BackupFileInfo file) =>
        file.Type == BackupType.Unknown || string.IsNullOrWhiteSpace(file.InferredDatabaseName);

    /// <summary>
    /// Reads the header of each unidentified file and writes back what SQL Server says.
    ///
    /// Files are asked about ONE AT A TIME rather than as a striped set, because a set is exactly
    /// what is not known yet: grouping happens on the inferred database and type, which are the
    /// things being established. A stripe of a striped backup answers with its own header anyway.
    /// </summary>
    /// <returns>How many files the header settled.</returns>
    public async Task<int> IdentifyAsync(
        ServerConnection server,
        IReadOnlyList<BackupFileInfo> files,
        IProgress<int>? progress = null,
        CancellationToken ct = default)
    {
        var identified = 0;
        var done = 0;

        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();

            // Blob only. A file discovered through an instance's msdb was never unidentified -
            // msdb recorded its database, its type and its LSNs - so this path is only ever reached
            // by a container listing.
            if (file.IsOnDisk || string.IsNullOrWhiteSpace(file.BlobUrl))
            {
                progress?.Report(++done);
                continue;
            }

            try
            {
                var header = await sql.RestoreHeaderOnlyMultiAsync(
                    server, [BlobUrlEncoder.Encode(file.BlobUrl)], ct);

                if (header != null && Apply(file, header)) identified++;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // One unreadable file does not stop the rest. A container legitimately holds things
                // that are not backups at all, and a file this cannot read is simply one the app
                // still cannot place - which is where it started, not a worse position.
            }

            progress?.Report(++done);
        }

        return identified;
    }

    /// <summary>
    /// Writes the header's answer onto the file, and says whether it settled anything.
    ///
    /// The header wins over the filename wherever the two disagree, because it is what the restore
    /// reads. Nothing here is merged or averaged: a value the header gives replaces the guess.
    /// </summary>
    internal static bool Apply(BackupFileInfo file, BackupFileInfo header)
    {
        var settled = false;

        if (header.Type != BackupType.Unknown && header.Type != file.Type)
        {
            file.Type = header.Type;
            file.BackupTypeCode = header.BackupTypeCode;
            settled = true;
        }

        if (!string.IsNullOrWhiteSpace(header.DatabaseName) &&
            !string.Equals(header.DatabaseName, file.InferredDatabaseName, StringComparison.Ordinal))
        {
            file.DatabaseName = header.DatabaseName;
            file.InferredDatabaseName = header.DatabaseName;
            settled = true;
        }

        // Carried across whether or not anything was settled. These are the whole reason a header
        // read is worth its round trip beyond the classification: a set that knows its LSNs is one
        // the chain builder can pair definitively rather than by proximity in time.
        file.FirstLsn ??= header.FirstLsn;
        file.LastLsn ??= header.LastLsn;
        file.CheckpointLsn ??= header.CheckpointLsn;
        file.DatabaseBackupLsn ??= header.DatabaseBackupLsn;

        // The instance's own record of when the backup ran, on its own clock. Stronger than a
        // filename and far stronger than the blob's LastModified - which is when the UPLOAD
        // finished, in UTC, and is what a file with an unreadable name falls back to today.
        if (header.BackupStartDate.HasValue)
        {
            file.BackupStartDate = header.BackupStartDate;
            file.BackupFinishDate = header.BackupFinishDate;
        }

        return settled;
    }
}
