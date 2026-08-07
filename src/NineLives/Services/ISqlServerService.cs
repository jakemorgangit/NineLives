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

    /// <summary>
    /// Reads several backup headers over ONE connection (#130).
    ///
    /// Measured on a real container: three HEADERONLY statements over nine striped files took
    /// 17.4 seconds - about 5.8 seconds each, every one of them opening its own connection first.
    /// Anything that reads more than one header is therefore paying the connect cost per read, and
    /// that is the part worth removing before deciding what an audit over a whole database costs.
    ///
    /// Each request is one statement covering one set's files, because a stripe on its own is not a
    /// readable backup. Results come back in request order, null where the header could not be read.
    /// </summary>
    Task<List<BackupFileInfo?>> RestoreHeaderOnlyBatchAsync(
        ServerConnection server,
        IReadOnlyList<IReadOnlyList<string>> requests,
        IProgress<int>? progress = null,
        Action<string>? timing = null,
        CancellationToken ct = default);

    Task<VerifyOnlyResult> RestoreVerifyOnlyAsync(
        ServerConnection server,
        IReadOnlyList<string> blobUrls,
        bool withChecksum = false,
        IReadOnlyList<FileMoveOption>? fileMoves = null,
        CancellationToken ct = default);

    Task ExecuteNonQueryAsync(
        ServerConnection server, string sql,
        Action<string>? messageCallback = null, CancellationToken ct = default);

    /// <summary>
    /// Runs a script statement by statement, reporting SQL Server's own progress as it goes.
    ///
    /// Not restore-specific despite where it started: a BACKUP reports STATS through exactly the
    /// same InfoMessage channel, so both halves of the orchestrator share one execution path (#165).
    /// </summary>
    Task ExecuteWithProgressAsync(
        ServerConnection server, string sql,
        Action<string>? messageCallback = null, CancellationToken ct = default);

    /// <summary>What this instance recorded backing up, from msdb (#149).</summary>
    Task<List<BackupHistoryEntry>> ReadBackupHistoryAsync(
        ServerConnection server, string? databaseName = null, CancellationToken ct = default);

    /// <summary>Whether THIS instance can read a backup file, and why not (#149).</summary>
    Task<BackupFileCheck> CheckBackupFileAsync(
        ServerConnection server, string path, CancellationToken ct = default);

    /// <summary>Checks every file a chain needs, stopping at the first that cannot be read.</summary>
    Task<List<BackupFileCheck>> CheckBackupFilesAsync(
        ServerConnection server, IEnumerable<string> paths, CancellationToken ct = default);

    Task<BlobCredentialStatus> CredentialExistsAsync(
        ServerConnection server, string credentialName, CancellationToken ct = default);

    Task<CredentialChange> EnsureCredentialExistsAsync(
        ServerConnection server, string credentialName, string storageAccountUrl, string sasToken,
        CancellationToken ct = default);
}
