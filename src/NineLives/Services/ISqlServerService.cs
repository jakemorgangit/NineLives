using Blackcat.NineLives.Models;

namespace Blackcat.NineLives.Services;

/// <summary>
/// Everything the ViewModels ask of SQL Server. Implemented by <see cref="SqlServerService"/>.
///
/// <c>CreateConnection</c> and <c>BuildConnectionString</c> are deliberately absent: they hand
/// back a <c>SqlConnection</c>, which would put Microsoft.Data.SqlClient in the signature of the
/// thing a fake has to implement. They stay on the class, where the live tests use them (#41).
/// </summary>
public interface ISqlServerService
{
    Task<bool> TestConnectionAsync(ServerConnection server, CancellationToken ct = default);

    Task<bool?> WouldConnectWithCertificateValidationAsync(
        ServerConnection server, CancellationToken ct = default);

    Task<string> GetServerVersionAsync(ServerConnection server, CancellationToken ct = default);

    Task<List<string>> GetDatabaseListAsync(ServerConnection server, CancellationToken ct = default);

    Task<(string DataPath, string LogPath)> GetDefaultPathsAsync(
        ServerConnection server, CancellationToken ct = default);

    Task<DatabaseRecoveryState> GetDatabaseRecoveryStateAsync(
        ServerConnection server, string databaseName, CancellationToken ct = default);

    Task ExecuteRecoveryActionAsync(ServerConnection server, string sql, CancellationToken ct = default);

    Task<List<FileMoveOption>> RestoreFileListOnlyAsync(
        ServerConnection server, IReadOnlyList<string> blobUrls, CancellationToken ct = default);

    Task<BackupFileInfo?> RestoreHeaderOnlyMultiAsync(
        ServerConnection server, IReadOnlyList<string> blobUrls, CancellationToken ct = default);

    Task<VerifyOnlyResult> RestoreVerifyOnlyAsync(
        ServerConnection server,
        IReadOnlyList<string> blobUrls,
        bool withChecksum = false,
        IReadOnlyList<FileMoveOption>? fileMoves = null,
        CancellationToken ct = default);

    Task ExecuteNonQueryAsync(
        ServerConnection server, string sql,
        Action<string>? messageCallback = null, CancellationToken ct = default);

    Task ExecuteRestoreWithProgressAsync(
        ServerConnection server, string sql,
        Action<string>? messageCallback = null, CancellationToken ct = default);

    Task<BlobCredentialStatus> CredentialExistsAsync(
        ServerConnection server, string credentialName, CancellationToken ct = default);

    Task<CredentialChange> EnsureCredentialExistsAsync(
        ServerConnection server, string credentialName, string storageAccountUrl, string sasToken,
        CancellationToken ct = default);
}
