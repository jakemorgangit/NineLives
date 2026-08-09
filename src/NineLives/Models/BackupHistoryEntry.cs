namespace Blackcat.NineLives.Models;

/// <summary>
/// One backup as the source instance recorded it in <c>msdb</c>, with the files it was written to.
///
/// The first piece of #149: restores from a shared backup location, where the backups were never in
/// blob storage and there is no container to list. What there IS, on the instance that took them,
/// is a complete record of what was backed up, when, to which files, and - crucially - the LSNs.
///
/// That also answers #130 for this source without any of its cost. Chains from blob storage are
/// built by inferring type and database from the path and assembling by timestamp, with headers
/// consulted only on demand because reading them means a round trip per file. Here the authoritative
/// values are already in a table: no filename convention to parse, no header to read, no guessing.
/// </summary>
public sealed class BackupHistoryEntry
{
    public string DatabaseName { get; init; } = string.Empty;

    public BackupType Type { get; init; } = BackupType.Unknown;

    /// <summary>When the backup STARTED, which is what the source instance orders history by.</summary>
    public DateTime StartedAt { get; init; }

    public DateTime FinishedAt { get; init; }

    /// <summary>
    /// A copy-only full does not reset the differential base, so it can never be the base for a
    /// differential restore - the same rule the blob path already applies (#49).
    /// </summary>
    public bool IsCopyOnly { get; init; }

    // ── the LSNs, which are the whole point ─────────────────────────────────────

    /// <summary>The first LSN in this backup.</summary>
    public decimal? FirstLsn { get; init; }

    /// <summary>The last LSN in this backup.</summary>
    public decimal? LastLsn { get; init; }

    /// <summary>The checkpoint a full was taken at. A differential's DatabaseBackupLsn matches it.</summary>
    public decimal? CheckpointLsn { get; init; }

    /// <summary>
    /// Which full this backup belongs to. Matching it against a full's <see cref="CheckpointLsn"/>
    /// is the definitive test of whether a differential belongs to that full - timestamps only
    /// suggest it.
    /// </summary>
    public decimal? DatabaseBackupLsn { get; init; }

    /// <summary>
    /// Every file this backup was written to, in stripe order.
    ///
    /// More than one means a striped set, and all of them are needed: a restore given three of four
    /// stripes fails, and #62 was written because a missing stripe used to be discovered at restore
    /// time rather than before it.
    /// </summary>
    public IReadOnlyList<string> Files { get; init; } = [];

    public long? BackupSizeBytes { get; init; }

    /// <summary>The server that took it, as msdb recorded it.</summary>
    public string? ServerName { get; init; }

    /// <summary>
    /// Where this backup sits within its file, when it was read from one (#203).
    ///
    /// A backup FILE can hold several backup SETS - every BACKUP ... NOINIT appends another - and
    /// HEADERONLY reports one row per set with its Position. A restore that wants any but the
    /// first must say WITH FILE = n, or SQL Server silently restores position 1: the oldest
    /// backup in the file, presented as a success.
    ///
    /// Null for anything read from msdb, where each history row is its own backup and the file
    /// position is not in play.
    /// </summary>
    public int? Position { get; init; }

    /// <summary>
    /// How many files make up the media set this backup was written across (#203).
    ///
    /// More than one means this file is one stripe of a striped set, and restoring needs every
    /// member. HEADERONLY on a single member happily describes the set, which is exactly why this
    /// has to be carried and checked - the header looks complete when the media is not.
    /// </summary>
    public int FamilyCount { get; init; } = 1;

    /// <summary>
    /// A record with no files cannot be restored from, whatever else it says.
    ///
    /// msdb keeps history for backups whose files have since been deleted, archived or pruned by a
    /// retention job, so "it is in the history" and "it is on disk" are different questions - which
    /// is exactly why #149 verifies the files separately, on the target, before offering them.
    /// </summary>
    public bool HasFiles => Files.Count > 0;

    public override string ToString() =>
        $"{Type} of {DatabaseName} at {StartedAt:yyyy-MM-dd HH:mm:ss} ({Files.Count} file(s))";
}
