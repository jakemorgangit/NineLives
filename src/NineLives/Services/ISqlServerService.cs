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

    /// <summary>
    /// What each volume on this instance has free, keyed by mount point (#32).
    ///
    /// Asked of the SERVER rather than measured from here: this app's process may not see those
    /// volumes at all, and where it can, what it sees is not what the service account writes to.
    /// </summary>
    /// <summary>
    /// The files of a database that exists on this instance now - name, path and current size.
    ///
    /// For the copy screen's disk-space check (#206): a copy has no backup to FILELISTONLY until
    /// the source half has already run, but the database being copied is live on the source, so
    /// its own catalog answers the same question up front.
    /// </summary>
    Task<List<FileMoveOption>> GetDatabaseFilesAsync(
        ServerConnection server, string database, CancellationToken ct = default);

    /// <summary>
    /// The name of the certificate in this instance's master with the given thumbprint, or null
    /// when there is none (#222). The question error 33111 answers too late.
    /// </summary>
    Task<string?> FindCertificateByThumbprintAsync(
        ServerConnection server, byte[] thumbprint, CancellationToken ct = default);

    /// <summary>
    /// Certificates in this instance's master that can encrypt a backup (#222): private key held
    /// and protected by the database master key. System certificates (##...##) are not offered.
    /// </summary>
    Task<List<string>> ListBackupCertificatesAsync(
        ServerConnection server, CancellationToken ct = default);

    /// <summary>The thumbprint of a named certificate in this instance's master, or null (#222).</summary>
    Task<byte[]?> GetCertificateThumbprintAsync(
        ServerConnection server, string certificateName, CancellationToken ct = default);

    /// <summary>
    /// Whether a database on this instance is TDE-encrypted, and by which certificate (#222).
    /// The certificate name is best effort - it needs VIEW SERVER STATE - and null when unknown.
    /// </summary>
    Task<(bool IsEncrypted, string? CertificateName)> GetDatabaseTdeInfoAsync(
        ServerConnection server, string database, CancellationToken ct = default);

    /// <summary>SERVERPROPERTY('ProductMajorVersion') - 16 is SQL Server 2022 - or null if it did not say (#210).</summary>
    Task<int?> GetProductMajorVersionAsync(ServerConnection server, CancellationToken ct = default);

    /// <summary>
    /// Runs one statement while a second connection polls sys.dm_exec_requests for its
    /// percent_complete - which DBCC CHECKDB and RESTORE both report. Best effort: no
    /// VIEW SERVER STATE means no percentage, and the statement still runs.
    /// </summary>
    Task ExecuteWithPercentPollingAsync(
        ServerConnection server, string sql, IProgress<double>? percent = null,
        CancellationToken ct = default);

    /// <summary>What the restored database looks like now - compat level, recovery model, owner (#205).</summary>
    Task<DatabaseOverview?> GetDatabaseOverviewAsync(
        ServerConnection server, string database, CancellationToken ct = default);

    /// <summary>
    /// Database users whose SIDs match no login on this server (#205). SQL-auth users only -
    /// Windows and Entra principals resolve by directory identity, not by per-server SID.
    /// </summary>
    Task<List<OrphanedUser>> FindOrphanedUsersAsync(
        ServerConnection server, string database, CancellationToken ct = default);

    Task<Dictionary<string, long>> GetVolumeFreeSpaceAsync(
        ServerConnection server, CancellationToken ct = default);

    Task<(string DataPath, string LogPath)> GetDefaultPathsAsync(
        ServerConnection server, CancellationToken ct = default);

    Task<DatabaseRecoveryState> GetDatabaseRecoveryStateAsync(
        ServerConnection server, string databaseName, CancellationToken ct = default);

    Task ExecuteRecoveryActionAsync(ServerConnection server, string sql, CancellationToken ct = default);

    Task<List<FileMoveOption>> RestoreFileListOnlyAsync(
        ServerConnection server, IReadOnlyList<string> blobUrls, CancellationToken ct = default);

    /// <summary>
    /// Everything one backup FILE says about itself: one entry per backup set it holds (#203).
    ///
    /// For the backup nobody's msdb knows - a vendor's .bak, a file off a decommissioned server.
    /// HEADERONLY is asked on <paramref name="server"/>, which must be able to read the path; the
    /// entries carry Position (a file holds several sets whenever NOINIT appended) and FamilyCount
    /// (more than one means the file is a single stripe of a striped set, and cannot be restored
    /// alone).
    /// </summary>
    Task<List<BackupHistoryEntry>> ReadBackupFileHeadersAsync(
        ServerConnection server, string path, CancellationToken ct = default);

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
    /// <summary>
    /// Every user database's backup state in one round trip (#239): recovery model, and the last
    /// full, differential and log as msdb records them. The exposure dashboard's raw material.
    /// </summary>
    Task<List<ExposureRow>> GetBackupExposureAsync(
        ServerConnection server, CancellationToken ct = default);

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

    /// <param name="identity">
    /// Which identity the credential holds. A managed-identity credential carries no SECRET at all
    /// (#147), and writing one over a SAS credential converts it - which is destructive, so nothing
    /// should reach here except because somebody asked for it.
    /// </param>
    Task<CredentialChange> EnsureCredentialExistsAsync(
        ServerConnection server, string credentialName, string storageAccountUrl, string sasToken,
        BlobCredentialIdentity identity = BlobCredentialIdentity.SharedAccessSignature,
        CancellationToken ct = default);

    /// <summary>Whether this instance can authenticate to blob storage with a managed identity (#147).</summary>
    Task<ManagedIdentitySupport> SupportsManagedIdentityCredentialAsync(
        ServerConnection server, CancellationToken ct = default);
}
