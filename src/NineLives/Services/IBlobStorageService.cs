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
    List<BackupSet> GroupIntoBackupSets(List<BackupFileInfo> files);
    List<string> GetDiscoveredDatabases(List<BackupFileInfo> files);
    List<string> GetDiscoveredServers(List<BackupFileInfo> files);
}
