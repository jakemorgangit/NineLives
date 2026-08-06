using Blackcat.NineLives.Models;

namespace Blackcat.NineLives.Services;

/// <summary>
/// Reading a blob container. Implemented by <see cref="BlobStorageService"/>.
///
/// Covers what the ViewModels use, which is deliberately not everything the class exposes: the
/// static filename helpers stay on the class, since nothing needs to substitute them (#41).
/// </summary>
public interface IBlobStorageService
{
    Task<bool> VerifyConnectionAsync(BlobContainerConfig config, CancellationToken ct = default);

    Task<List<string>> ListTopLevelFoldersAsync(BlobContainerConfig config, CancellationToken ct = default);

    Task<List<BackupFileInfo>> ListBackupFilesAsync(BlobContainerConfig config, CancellationToken ct = default);

    Task<List<BackupFileInfo>> ListBackupFilesAsync(
        BlobContainerConfig config,
        BlobListingScope? scope,
        IProgress<int>? progress,
        CancellationToken ct = default);

    ContainerSummary GetContainerSummary(List<BackupFileInfo> files);
    ContainerSummary GetSetBasedSummary(List<BackupSet> sets);
    /// <param name="backupServerTimeZoneId">
    /// The backup server's time zone, when the container says which one it is. Lets a blob's UTC
    /// LastModified be put on the same clock as a filename timestamp (#102). Null leaves them
    /// labelled but unreconciled.
    /// </param>
    List<BackupSet> GroupIntoBackupSets(List<BackupFileInfo> files, string? backupServerTimeZoneId = null);
    List<string> GetDiscoveredDatabases(List<BackupFileInfo> files);
    List<string> GetDiscoveredServers(List<BackupFileInfo> files);
}
