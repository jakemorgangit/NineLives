namespace Blackcat.NineLives.Models;

/// <summary>
/// The kind of media a backup lives on (#165).
///
/// Not a property of the app - a choice per operation. Neither is the right default in every
/// estate: blob when the two hosts have no network path to each other, which is often the real
/// blocker when source and target sit in different environments; a shared path when they do,
/// because it is faster, costs no egress, needs no SAS with write, and does not make the restore
/// wait on an upload.
/// </summary>
public enum BackupMedium
{
    /// <summary>Azure Blob Storage. <c>RESTORE ... FROM URL</c>, and a server-side credential.</summary>
    AzureBlob,

    /// <summary>
    /// A path both instances can see - SMB, NFS, a mount. <c>RESTORE ... FROM DISK</c>, and no
    /// credential at all, because SQL Server reaches it as its own service account.
    /// </summary>
    SharedPath
}

/// <summary>
/// Where a restore's backups are being listed from (#149, #165).
///
/// The seam the whole widening turns on. Everything downstream of the listing - the working set,
/// the chain, the timeline, the restore points, the script, the execute path - operates on
/// <see cref="BackupSet"/> and has never cared where the sets came from. Only two things ever did:
/// where the list comes from, and how a RESTORE addresses a file. This type is the first of those;
/// <see cref="BackupFileInfo.IsOnDisk"/> is the second.
///
/// Deliberately one type with a medium on it rather than a class hierarchy. The alternative was a
/// second screen with its own chain, options and script - which is what was built first, and it
/// produced two half-copies of a restore workflow that had taken #110, #45 and #44 to get right.
/// </summary>
public sealed record BackupLocation
{
    public required BackupMedium Medium { get; init; }

    // ── blob ────────────────────────────────────────────────────────────────────

    /// <summary>The container to list. Set when <see cref="Medium"/> is AzureBlob.</summary>
    public BlobContainerConfig? Container { get; init; }

    // ── shared path ─────────────────────────────────────────────────────────────

    /// <summary>
    /// The instance whose <c>msdb</c> is read. Set when <see cref="Medium"/> is SharedPath.
    ///
    /// A share cannot be listed the way a container can - a directory of .bak files says nothing
    /// about which database each belongs to, what type it is, or which full a differential was
    /// taken against, and inferring that from filenames is exactly what #130 exists because of.
    /// The instance that TOOK the backups recorded all of it, so that is what gets read.
    /// </summary>
    public ServerConnection? SourceServer { get; init; }

    /// <summary>
    /// How the target reaches the files, when it reaches them by a different path from the one the
    /// source wrote. <see cref="BackupPathMapping.None"/> when both use the same path.
    /// </summary>
    public BackupPathMapping Mapping { get; init; } = BackupPathMapping.None;

    public static BackupLocation Blob(BlobContainerConfig container) =>
        new() { Medium = BackupMedium.AzureBlob, Container = container };

    public static BackupLocation Shared(ServerConnection sourceServer, BackupPathMapping? mapping = null) =>
        new()
        {
            Medium = BackupMedium.SharedPath,
            SourceServer = sourceServer,
            Mapping = mapping ?? BackupPathMapping.None
        };

    public bool IsBlob => Medium == BackupMedium.AzureBlob;
    public bool IsSharedPath => Medium == BackupMedium.SharedPath;

    /// <summary>What this location is, for a status line or a history entry.</summary>
    public string Describe() => Medium switch
    {
        BackupMedium.AzureBlob => Container?.Name ?? "a container",
        BackupMedium.SharedPath => SourceServer == null
            ? "a shared path"
            : $"{SourceServer.ServerName}'s backup history",
        _ => "an unknown location"
    };

    /// <summary>
    /// Whether two locations are the same place, so a chain built from one can be checked against
    /// what is selected now rather than assumed to match it - the mistake #112 was.
    /// </summary>
    public bool SamePlaceAs(BackupLocation? other)
    {
        if (other == null || other.Medium != Medium) return false;

        return Medium switch
        {
            BackupMedium.AzureBlob => Container?.Id == other.Container?.Id,

            // The mapping is part of the identity, not decoration: the same instance's history read
            // with a different substitution names different files, and a chain from one is not a
            // chain from the other.
            BackupMedium.SharedPath =>
                SourceServer?.Id == other.SourceServer?.Id && Mapping == other.Mapping,

            _ => false
        };
    }
}
