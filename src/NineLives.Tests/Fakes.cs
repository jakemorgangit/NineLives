using Blackcat.NineLives.Models;
using Blackcat.NineLives.Services;
using Blackcat.NineLives.ViewModels;

namespace Blackcat.NineLives.Tests;

/// <summary>
/// An in-memory <see cref="ICredentialStore"/>. Nothing here reaches the Windows Credential
/// Manager or the user's config file, which is the whole reason the interface exists (#41).
/// </summary>
public sealed class FakeCredentialStore : ICredentialStore
{
    private readonly Dictionary<string, (string username, string secret)> _secrets = [];

    public AppConfig Config { get; set; } = new();

    /// <summary>Set to make SaveConfig throw, standing in for a refused or locked config file.</summary>
    public Exception? SaveConfigThrows { get; set; }

    public int SaveConfigCalls { get; private set; }

    public void SaveSecret(string key, string username, string secret) => _secrets[key] = (username, secret);

    public (string? username, string? secret) ReadSecret(string key)
        => _secrets.TryGetValue(key, out var v) ? (v.username, v.secret) : (null, null);

    public bool DeleteSecret(string key) => _secrets.Remove(key);

    public List<string> ListCredentialKeys(string prefix)
        => _secrets.Keys.Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList();

    public void SaveSasToken(BlobContainerConfig config, string sasToken)
        => SaveSecret(BlobKey(config), "sas", sasToken);

    public string? GetSasToken(BlobContainerConfig config)
        => config.UnsavedSasToken ?? ReadSecret(BlobKey(config)).secret;

    public bool IsSasTokenExpired(BlobContainerConfig config) => false;

    public DateTime? GetSasTokenExpiry(BlobContainerConfig config) => null;

    public SasExpiryInfo ReadSasTokenExpiry(BlobContainerConfig config)
        => config.ReadSasExpiry(GetSasToken(config));

    public void SaveSqlPassword(ServerConnection connection, string password)
        => SaveSecret(SqlKey(connection), connection.Username ?? string.Empty, password);

    public string? GetSqlPassword(ServerConnection connection)
        => connection.UnsavedPassword ?? ReadSecret(SqlKey(connection)).secret;

    public AppConfig LoadConfig() => Config;

    public void SaveConfig(AppConfig config)
    {
        SaveConfigCalls++;
        if (SaveConfigThrows != null) throw SaveConfigThrows;
        Config = config;
    }

    private static string BlobKey(BlobContainerConfig c) => $"NineLives:Blob:{c.Id ?? c.Name}";
    private static string SqlKey(ServerConnection s) => $"NineLives:SQL:{s.Id ?? s.Name}";
}

/// <summary>
/// A blob container that exists only in the test. The grouping and summary methods delegate to the
/// real service - they are pure, and faking them would test the fake rather than the app.
/// </summary>
public sealed class FakeBlobStorageService : IBlobStorageService
{
    private readonly BlobStorageService _real = new(new FakeCredentialStore());

    public List<BackupFileInfo> Files { get; set; } = [];

    /// <summary>
    /// What each container holds, keyed by container NAME (#32).
    ///
    /// <see cref="Files"/> answers for every container, which is fine when there is one and useless
    /// for the case multi-container exists for: a full in one container and the logs that carry it
    /// forward in another. A container with no entry here falls back to Files.
    /// </summary>
    public Dictionary<string, List<BackupFileInfo>> FilesByContainer { get; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Every container listed, in order, so a test can prove which were read.</summary>
    public List<string> ListedContainers { get; } = [];

    /// <summary>Throw instead of listing, for the failure paths.</summary>
    public Exception? ListThrows { get; set; }

    /// <summary>The scope the ViewModel pushed down on the last listing.</summary>
    public BlobListingScope? LastScope { get; private set; }

    public int ListCalls { get; private set; }

    /// <summary>
    /// The config the ViewModel actually handed over. What is on the form has to reach the service
    /// intact - the authentication mode failing to make that trip is what shipped broken in #29.
    /// </summary>
    public BlobContainerConfig? LastConfig { get; private set; }

    /// <summary>What VerifyConnectionAsync answers - false plays a SAS that cannot reach the container.</summary>
    public bool VerifyAnswer { get; set; } = true;

    public Task<bool> VerifyConnectionAsync(BlobContainerConfig config, CancellationToken ct = default)
    {
        LastConfig = config;
        return Task.FromResult(VerifyAnswer);
    }

    /// <summary>What DescribeSignedInIdentityAsync should answer.</summary>
    public string? SignedInIdentity { get; set; }

    public Task<string?> DescribeSignedInIdentityAsync(
        BlobContainerConfig config, CancellationToken ct = default)
        => Task.FromResult(config.AuthMode.IsEntra() ? SignedInIdentity : null);

    public Task<List<string>> ListTopLevelFoldersAsync(BlobContainerConfig config, CancellationToken ct = default)
        => Task.FromResult(new List<string>());

    public Task<List<BackupFileInfo>> ListBackupFilesAsync(BlobContainerConfig config, CancellationToken ct = default)
        => ListBackupFilesAsync(config, null, null, ct);

    /// <summary>
    /// When set, the listing waits here before returning - and honours a cancellation that
    /// arrived while it waited, as the real paged enumeration does. Lets a test hold a load
    /// in flight, change the world, and only then let the result try to land.
    /// </summary>
    public TaskCompletionSource<bool>? BeforeListReturns { get; set; }

    public async Task<List<BackupFileInfo>> ListBackupFilesAsync(
        BlobContainerConfig config, BlobListingScope? scope, IProgress<int>? progress, CancellationToken ct = default)
    {
        ListCalls++;
        LastScope = scope;
        LastConfig = config;
        ListedContainers.Add(config.Name);
        ct.ThrowIfCancellationRequested();
        if (ListThrows != null) throw ListThrows;

        if (BeforeListReturns != null)
        {
            await BeforeListReturns.Task;
            ct.ThrowIfCancellationRequested();
        }

        var files = FilesByContainer.TryGetValue(config.Name, out var forContainer)
            ? forContainer
            : Files;

        progress?.Report(files.Count);

        // A copy per call. The real service returns fresh objects each time, and handing the same
        // instances back twice would let one listing's ContainerId stamp overwrite another's.
        return files.Select(Copy).ToList();
    }

    /// <summary>
    /// A shallow copy, so one listing cannot mutate another's file objects.
    ///
    /// Only the fields the inventory reads. Enough for the tests, and it keeps the fake honest
    /// about the real service returning fresh objects per call.
    /// </summary>
    private static BackupFileInfo Copy(BackupFileInfo f) => new()
    {
        BlobName = f.BlobName,
        BlobUrl = f.BlobUrl,
        ETag = f.ETag,
        LocalPath = f.LocalPath,
        ContainerId = f.ContainerId,
        Type = f.Type,
        BackupTypeCode = f.BackupTypeCode,
        SizeBytes = f.SizeBytes,
        LastModified = f.LastModified,
        DatabaseName = f.DatabaseName,
        InferredDatabaseName = f.InferredDatabaseName,
        InferredServerName = f.InferredServerName,
        InferredInstanceName = f.InferredInstanceName,
        InferredSetId = f.InferredSetId
    };

    public ContainerSummary GetContainerSummary(List<BackupFileInfo> files) => _real.GetContainerSummary(files);
    public ContainerSummary GetSetBasedSummary(List<BackupSet> sets) => _real.GetSetBasedSummary(sets);
    /// <summary>The time zone the last grouping was handed - pins WHOSE zone was used (#286).</summary>
    public string? LastGroupTimeZoneId { get; private set; }

    public List<BackupSet> GroupIntoBackupSets(List<BackupFileInfo> files, string? backupServerTimeZoneId = null)
    {
        LastGroupTimeZoneId = backupServerTimeZoneId;
        return _real.GroupIntoBackupSets(files, backupServerTimeZoneId);
    }
    public List<string> GetDiscoveredDatabases(List<BackupFileInfo> files) => _real.GetDiscoveredDatabases(files);
    public List<string> GetDiscoveredServers(List<BackupFileInfo> files) => _real.GetDiscoveredServers(files);
}

/// <summary>An in-memory restore history, so the tests never touch the real one.</summary>
public sealed class FakeRestoreHistoryStore : IRestoreHistoryStore
{
    public List<RestoreHistoryEntry> Entries { get; } = [];

    public string FilePath => "(in memory)";

    /// <summary>Set to play a history file that exists and cannot be read (#370).</summary>
    public bool CouldNotRead { get; set; }

    public List<RestoreHistoryEntry> Load() => Entries.ToList();

    public void Append(RestoreHistoryEntry entry) => Entries.Insert(0, entry);

    public void Clear() => Entries.Clear();
}

/// <summary>
/// A SQL Server that records what it was asked to do. Nothing opens a connection.
/// </summary>
/// <summary>Records every notification a screen fires (#242), in order.</summary>
public sealed class FakeRunNotifier : IRunNotifier
{
    public List<RunNotification> Sent { get; } = [];

    /// <summary>How many times a caller drained - the CLI's exit path must, or deliveries die with the process (#296).</summary>
    public int DrainCalls { get; private set; }

    public void Notify(RunNotification notification) => Sent.Add(notification);

    public Task DrainAsync(TimeSpan timeout)
    {
        DrainCalls++;
        return Task.CompletedTask;
    }
}

public sealed class FakeSqlServerService : ISqlServerService
{
    /// <summary>Every server instance handed to an execute call, in order.</summary>
    public List<ServerConnection> ExecutedAgainst { get; } = [];

    /// <summary>Every script handed to an execute call, in order.</summary>
    public List<string> ExecutedScripts { get; } = [];

    public List<string> VerifiedUrls { get; } = [];

    /// <summary>What a credential lookup finds. Defaults to the happy path: a SAS credential.</summary>
    public BlobCredentialStatus Credential { get; set; } =
        new(BlobCredentialIdentity.SharedAccessSignature, "SHARED ACCESS SIGNATURE");

    /// <summary>Every name a credential write was asked for, so a test can prove none happened.</summary>
    public List<string> CredentialWrites { get; } = [];

    public VerifyOnlyResult VerifyResult { get; set; } = new(true, "The backup set is valid.");

    public DatabaseRecoveryState RecoveryState { get; set; } = DatabaseRecoveryState.Missing;

    /// <summary>Set to make the restore fail the way a real one does.</summary>
    public Exception? ExecuteThrows { get; set; }

    /// <summary>Refuse the connection, for the screens that have to explain one (#357).</summary>
    public Exception? TestConnectionThrows { get; set; }

    /// <summary>
    /// Holds the connection open, so a test can do what a user does while one is slow: click
    /// something else (#409). Set it, start the connect, change the selection, then release.
    /// </summary>
    public TaskCompletionSource? HoldConnection { get; set; }

    /// <summary>Which server each call was actually made against, in order.</summary>
    public List<string> Connected { get; } = [];

    public async Task<bool> TestConnectionAsync(ServerConnection server, CancellationToken ct = default)
    {
        Connected.Add(server.ServerName);

        if (HoldConnection != null) await HoldConnection.Task;
        if (TestConnectionThrows != null) throw TestConnectionThrows;

        return true;
    }

    public Task<bool?> WouldConnectWithCertificateValidationAsync(ServerConnection server, CancellationToken ct = default)
        => Task.FromResult<bool?>(true);

    public Task<string> GetServerVersionAsync(ServerConnection server, CancellationToken ct = default)
        => Task.FromResult("Microsoft SQL Server 2022");

    /// <summary>What the fake instance says it holds.</summary>
    /// <summary>
    /// Whatever a test puts here - which means the REAL query's user-databases-only filter
    /// (#279) is invisible to the unit suite. The live pin in SqlExecutionFailureTests is
    /// what proves the predicate; tests here should stock this with user databases only.
    /// </summary>
    public List<string> DatabaseList { get; set; } = [];

    public Task<List<string>> GetDatabaseListAsync(ServerConnection server, CancellationToken ct = default)
        => Task.FromResult(DatabaseList);

    /// <summary>What the fake instance says it has free, by mount point.</summary>
    public Dictionary<string, long> VolumeFreeSpace { get; set; } = [];

    /// <summary>What GetDatabaseFilesAsync answers - the live files of whatever database is asked about.</summary>
    public List<FileMoveOption> DatabaseFiles { get; set; } = [];

    /// <summary>Set to make the file listing fail, standing in for a permissions refusal.</summary>
    public Exception? DatabaseFilesThrows { get; set; }

    public Task<List<FileMoveOption>> GetDatabaseFilesAsync(
        ServerConnection server, string database, CancellationToken ct = default)
    {
        if (DatabaseFilesThrows != null) throw DatabaseFilesThrows;
        return Task.FromResult(DatabaseFiles.ToList());
    }

    /// <summary>Set to make asking about volumes fail - which must not become a warning (#32).</summary>
    public Exception? VolumeCheckThrows { get; set; }

    /// <summary>Certificates on each server, keyed by server name then hex thumbprint (#222).</summary>
    public Dictionary<string, Dictionary<string, string>> CertificatesByThumbprint { get; } =
        new(StringComparer.OrdinalIgnoreCase);

    public Task<string?> FindCertificateByThumbprintAsync(
        ServerConnection server, byte[] thumbprint, CancellationToken ct = default)
    {
        var hex = Convert.ToHexString(thumbprint);
        return Task.FromResult(
            CertificatesByThumbprint.TryGetValue(server.ServerName, out var certs) &&
            certs.TryGetValue(hex, out var name)
                ? name
                : (string?)null);
    }

    public Task<byte[]?> GetCertificateThumbprintAsync(
        ServerConnection server, string certificateName, CancellationToken ct = default)
    {
        if (CertificatesByThumbprint.TryGetValue(server.ServerName, out var certs))
            foreach (var (hex, name) in certs)
                if (string.Equals(name, certificateName, StringComparison.OrdinalIgnoreCase))
                    return Task.FromResult<byte[]?>(Convert.FromHexString(hex));

        return Task.FromResult<byte[]?>(null);
    }

    /// <summary>What ListBackupCertificatesAsync answers (#222).</summary>
    public List<string> BackupCertificates { get; set; } = [];

    public Task<List<string>> ListBackupCertificatesAsync(
        ServerConnection server, CancellationToken ct = default)
        => Task.FromResult(BackupCertificates.ToList());

    /// <summary>TDE state per database name (#222).</summary>
    public Dictionary<string, (bool IsEncrypted, string? CertificateName)> TdeByDatabase { get; } =
        new(StringComparer.OrdinalIgnoreCase);

    public Task<(bool IsEncrypted, string? CertificateName)> GetDatabaseTdeInfoAsync(
        ServerConnection server, string database, CancellationToken ct = default)
        => Task.FromResult(TdeByDatabase.TryGetValue(database, out var info) ? info : (false, null));

    /// <summary>What GetProductMajorVersionAsync answers - 16 is SQL Server 2022 (#210).</summary>
    public int? ProductMajorVersion { get; set; } = 16;

    /// <summary>What GetEngineEditionAsync answers - 4 is Express (#51). Default non-Express.</summary>
    public int? EngineEdition { get; set; } = 3;

    public Task<int?> GetEngineEditionAsync(ServerConnection server, CancellationToken ct = default)
        => Task.FromResult(EngineEdition);

    /// <summary>Per-server overrides, for the checks that compare two servers' versions.</summary>
    public Dictionary<string, int?> MajorVersionByServer { get; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Set to make the version ask throw - a server that does not answer at all (#63).</summary>
    public Exception? ThrowOnMajorVersion { get; set; }

    /// <summary>
    /// Awaited before the version answer - a test gates this to hold a check sweep open and
    /// prove the run waits for the verdicts (#285).
    /// </summary>
    public Func<Task>? BeforeMajorVersion { get; set; }

    public async Task<int?> GetProductMajorVersionAsync(ServerConnection server, CancellationToken ct = default)
    {
        if (BeforeMajorVersion != null) await BeforeMajorVersion();
        if (ThrowOnMajorVersion != null) throw ThrowOnMajorVersion;
        return MajorVersionByServer.TryGetValue(server.ServerName, out var v) ? v : ProductMajorVersion;
    }

    /// <summary>What GetDatabaseOverviewAsync answers (#205).</summary>
    public DatabaseOverview? DatabaseOverview { get; set; } = new(150, "FULL", "sa");

    public Exception? OverviewThrows { get; set; }

    public Task<DatabaseOverview?> GetDatabaseOverviewAsync(
        ServerConnection server, string database, CancellationToken ct = default)
    {
        if (OverviewThrows != null) throw OverviewThrows;
        return Task.FromResult(DatabaseOverview);
    }

    /// <summary>What FindOrphanedUsersAsync answers (#205).</summary>
    public List<OrphanedUser> OrphanedUsers { get; set; } = [];

    public Exception? OrphanScanThrows { get; set; }

    public Task<List<OrphanedUser>> FindOrphanedUsersAsync(
        ServerConnection server, string database, CancellationToken ct = default)
    {
        if (OrphanScanThrows != null) throw OrphanScanThrows;
        return Task.FromResult(OrphanedUsers.ToList());
    }

    public Task<Dictionary<string, long>> GetVolumeFreeSpaceAsync(
        ServerConnection server, CancellationToken ct = default)
    {
        if (VolumeCheckThrows != null) throw VolumeCheckThrows;

        return Task.FromResult(VolumeFreeSpace);
    }

    /// <summary>How many times the default directories were asked for (#413).</summary>
    public int DefaultPathsAsked { get; private set; }

    /// <summary>Holds the call open, so a test can have a query in flight while it does something else.</summary>
    public TaskCompletionSource? HoldDefaultPaths { get; set; }

    public async Task<(string DataPath, string LogPath)> GetDefaultPathsAsync(
        ServerConnection server, CancellationToken ct = default)
    {
        DefaultPathsAsked++;
        if (HoldDefaultPaths != null) await HoldDefaultPaths.Task.WaitAsync(ct);
        return (@"D:\Data", @"D:\Logs");
    }

    public Task<DatabaseRecoveryState> GetDatabaseRecoveryStateAsync(
        ServerConnection server, string databaseName, CancellationToken ct = default)
        => Task.FromResult(RecoveryState);

    public Task ExecuteRecoveryActionAsync(ServerConnection server, string sql, CancellationToken ct = default)
        => Task.CompletedTask;

    /// <summary>The logical files the fake backup contains, with their sizes (#32).</summary>
    public List<FileMoveOption> FileList { get; set; } = [];

    public Task<List<FileMoveOption>> RestoreFileListOnlyAsync(
        ServerConnection server, IReadOnlyList<string> blobUrls, CancellationToken ct = default)
        => Task.FromResult(FileList);

    /// <summary>What the fake instance reads out of a backup header, or null for nothing.</summary>
    public BackupFileInfo? Header { get; set; }

    /// <summary>
    /// A header per request, when one answer for everything will not do.
    ///
    /// An audit reads a whole chain, and a fake that hands the same Full header back for a log
    /// reports a mismatch that is an artefact of the fake rather than of the code under test.
    /// </summary>
    public Func<IReadOnlyList<string>, BackupFileInfo?>? HeaderForUrls { get; set; }

    /// <summary>Every set of URLs a header read was asked about, in order.</summary>
    public List<IReadOnlyList<string>> HeaderReads { get; } = [];

    /// <summary>
    /// Makes one file unreadable - a container legitimately holds things that are not backups, and
    /// one of those must not stop the rest (#130).
    /// </summary>
    public string? HeaderThrowsForUrlContaining { get; set; }

    public Task<BackupFileInfo?> RestoreHeaderOnlyMultiAsync(
        ServerConnection server, IReadOnlyList<string> blobUrls, CancellationToken ct = default)
    {
        HeaderReads.Add(blobUrls);

        if (HeaderThrowsForUrlContaining != null &&
            blobUrls.Any(u => u.Contains(HeaderThrowsForUrlContaining, StringComparison.Ordinal)))
            throw new InvalidOperationException("fake: not a valid backup");

        // A fresh copy each time: the identifier writes onto the FILE from what it reads, and a
        // shared instance would let one file's result be mutated by the next.
        return Task.FromResult(Header == null ? null : Clone(Header));
    }

    /// <summary>Every batch of header requests, so a test can prove they went over ONE connection.</summary>
    public List<IReadOnlyList<IReadOnlyList<string>>> HeaderBatches { get; } = [];

    public Task<List<BackupFileInfo?>> RestoreHeaderOnlyBatchAsync(
        ServerConnection server,
        IReadOnlyList<IReadOnlyList<string>> requests,
        IProgress<int>? progress = null,
        Action<string>? timing = null,
        CancellationToken ct = default)
    {
        HeaderBatches.Add(requests);

        var results = new List<BackupFileInfo?>();
        foreach (var urls in requests)
        {
            HeaderReads.Add(urls);

            var unreadable = HeaderThrowsForUrlContaining != null &&
                urls.Any(u => u.Contains(HeaderThrowsForUrlContaining, StringComparison.Ordinal));

            var answer = HeaderForUrls?.Invoke(urls) ?? Header;
            results.Add(unreadable || answer == null ? null : Clone(answer));
            progress?.Report(results.Count);
        }

        timing?.Invoke($"fake: {requests.Count} statement(s)");
        return Task.FromResult(results);
    }

    private static BackupFileInfo Clone(BackupFileInfo source) => new()
    {
        DatabaseName = source.DatabaseName,
        Type = source.Type,
        BackupTypeCode = source.BackupTypeCode,
        BackupStartDate = source.BackupStartDate,
        BackupFinishDate = source.BackupFinishDate,
        FirstLsn = source.FirstLsn,
        LastLsn = source.LastLsn,
        CheckpointLsn = source.CheckpointLsn,
        DatabaseBackupLsn = source.DatabaseBackupLsn,
        SoftwareVersionMajor = source.SoftwareVersionMajor,
        TdeThumbprint = source.TdeThumbprint,
        EncryptorThumbprint = source.EncryptorThumbprint
    };

    /// <summary>Called with the token the viewmodel supplied, so a test can see it was cancellable.</summary>
    public Action<CancellationToken>? OnVerify { get; set; }

    /// <summary>Every token handed to a verify call, so a test can prove one was passed at all.</summary>
    public List<CancellationToken> VerifyTokens { get; } = [];

    /// <summary>The MOVE clauses the last verification was given (#129).</summary>
    public List<FileMoveOption> VerifiedWithMoves { get; private set; } = [];

    /// <summary>Whether each verification said WITH CHECKSUM - pins WHOSE setting was used (#293).</summary>
    public List<bool> VerifiedWithChecksum { get; } = [];

    public Task<VerifyOnlyResult> RestoreVerifyOnlyAsync(
        ServerConnection server, IReadOnlyList<string> blobUrls, bool withChecksum = false,
        IReadOnlyList<FileMoveOption>? fileMoves = null, CancellationToken ct = default)
    {
        VerifiedUrls.AddRange(blobUrls);
        VerifiedWithMoves = fileMoves?.ToList() ?? [];
        VerifiedWithChecksum.Add(withChecksum);
        VerifyTokens.Add(ct);
        OnVerify?.Invoke(ct);

        // The real service translates a cancelled command into this; the fake has to as well, or a
        // test would pass against a viewmodel that ignores the token entirely.
        ct.ThrowIfCancellationRequested();

        return Task.FromResult(VerifyResult);
    }

    public Task ExecuteNonQueryAsync(
        ServerConnection server, string sql, Action<string>? messageCallback = null, CancellationToken ct = default)
    {
        ExecutedAgainst.Add(server);
        ExecutedScripts.Add(sql);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Which execute call fails, 1-based, or null for none.
    ///
    /// A copy between servers runs two of them, and the interesting cases are the ones where the
    /// FIRST succeeds and the second does not - so a test has to be able to say which (#105).
    /// </summary>
    public int? FailOnExecuteNumber { get; set; }

    /// <summary>
    /// Runs inside each execute call, 1-based - lets a test change screen state MID-RUN, which
    /// is how the edit-during-copy defect is reproduced deterministically (#280).
    /// </summary>
    public Action<int>? OnExecute { get; set; }

    public Task ExecuteWithProgressAsync(
        ServerConnection server, string sql, Action<string>? messageCallback = null, CancellationToken ct = default)
    {
        // Same guard the VERIFYONLY fake carries, same reason: without it a test passes
        // against a caller that ignores the token entirely (#296).
        ct.ThrowIfCancellationRequested();
        ExecutedAgainst.Add(server);
        ExecutedScripts.Add(sql);
        OnExecute?.Invoke(ExecutedScripts.Count);
        messageCallback?.Invoke("100 percent processed.");

        if (FailOnExecuteNumber == ExecutedScripts.Count)
            throw new InvalidOperationException($"fake failure on execute {ExecutedScripts.Count}");

        if (ExecuteThrows != null) throw ExecuteThrows;
        return Task.CompletedTask;
    }

    /// <summary>
    /// Lets a test hold a credential check open, or fail one. Without it the check completes
    /// synchronously, so the sequencing between two overlapping checks cannot be reached.
    /// </summary>
    public Func<string, CancellationToken, Task<BlobCredentialStatus>>? OnCredentialCheck { get; set; }

    // ── shared backup location (#149) ───────────────────────────────────────────

    /// <summary>What the fake source instance says it backed up.</summary>
    public List<BackupHistoryEntry> BackupHistory { get; set; } = [];

    /// <summary>Every path the target was asked about, so a test can prove WHICH names were checked.</summary>
    public List<string> CheckedPaths { get; } = [];

    /// <summary>Paths the fake target refuses, and why. Anything not listed is readable.</summary>
    public Dictionary<string, BackupFileProblem> UnreadablePaths { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Marks per database name (#243).</summary>
    public Dictionary<string, List<LogMark>> LogMarksByDatabase { get; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Which instances were asked for marks, in order - the catalogue choice IS the bug surface (#268).</summary>
    public List<string> LogMarkServersAsked { get; } = [];

    public Task<List<LogMark>> GetLogMarksAsync(
        ServerConnection server, string database, CancellationToken ct = default)
    {
        LogMarkServersAsked.Add(server.ServerName);
        return Task.FromResult(
            LogMarksByDatabase.TryGetValue(database, out var marks) ? marks.ToList() : []);
    }

    /// <summary>Percent values the polling execute reports before completing (#user CHECKDB feedback).</summary>
    public List<double> PollPercents { get; set; } = [50];

    public Exception? PollingExecuteThrows { get; set; }

    /// <summary>
    /// Runs once the statement is notionally in flight, before it throws (#427). It is how a test
    /// reproduces the real sequence - the user presses Stop while the command is running, and what
    /// comes back is the driver's own exception rather than an OperationCanceledException. Nothing
    /// here can construct a SqlException, which has no public constructor, so the test supplies
    /// the cancel through this and the exception through PollingExecuteThrows.
    /// </summary>
    public Action? DuringPollingExecute { get; set; }

    public List<string> PolledScripts { get; } = [];

    public Task ExecuteWithPercentPollingAsync(
        ServerConnection server, string sql, IProgress<double>? percent = null,
        CancellationToken ct = default)
    {
        PolledScripts.Add(sql);
        ct.ThrowIfCancellationRequested();

        DuringPollingExecute?.Invoke();
        if (PollingExecuteThrows != null) throw PollingExecuteThrows;

        if (percent != null)
            foreach (var p in PollPercents)
                ((IProgress<double>)percent).Report(p);

        return Task.CompletedTask;
    }

    /// <summary>Exposure rows per server name (#239); a server absent from the map throws.</summary>
    public Dictionary<string, List<ExposureRow>> ExposureByServer { get; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>When set, the sweep waits here per server - and honours a cancellation that
    /// arrived while it waited, as the real query does (#287).</summary>
    public TaskCompletionSource<bool>? BeforeExposureReturns { get; set; }

    public async Task<List<ExposureRow>> GetBackupExposureAsync(
        ServerConnection server, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (BeforeExposureReturns != null)
        {
            await BeforeExposureReturns.Task;
            ct.ThrowIfCancellationRequested();
        }

        if (!ExposureByServer.TryGetValue(server.ServerName, out var rows))
            throw new InvalidOperationException($"fake: {server.ServerName} is not answering");

        // Fresh instances per call, as the real query produces - two connections to the same
        // instance must not share row objects, or the sweep cannot tell whose answer is whose.
        return rows.Select(r => new ExposureRow
        {
            ServerName = r.ServerName,
            DatabaseName = r.DatabaseName,
            RecoveryModel = r.RecoveryModel,
            StateDescription = r.StateDescription,
            IsUnreachable = r.IsUnreachable,
            LastFull = r.LastFull,
            LastDifferential = r.LastDifferential,
            LastLog = r.LastLog,
            Level = r.Level,
            Verdict = r.Verdict
        }).ToList();
    }

    /// <summary>What each ad-hoc file's headers say, keyed by path (#203).</summary>
    public Dictionary<string, List<BackupHistoryEntry>> FileHeaders { get; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Every path whose headers were read, in order.</summary>
    public List<string> HeaderReadPaths { get; } = [];

    public Task<List<BackupHistoryEntry>> ReadBackupFileHeadersAsync(
        ServerConnection server, string path, CancellationToken ct = default)
    {
        HeaderReadPaths.Add(path);

        if (!FileHeaders.TryGetValue(path, out var entries))
            throw new InvalidOperationException(
                $"Cannot open backup device '{path}'. Operating system error 2(The system cannot find the file specified.).");

        return Task.FromResult(entries.ToList());
    }

    public Task<List<BackupHistoryEntry>> ReadBackupHistoryAsync(
        ServerConnection server, string? databaseName = null, CancellationToken ct = default)
        => Task.FromResult(databaseName == null
            ? BackupHistory
            : BackupHistory.Where(h => h.DatabaseName == databaseName).ToList());

    /// <summary>Set to make the target unreachable rather than merely unhelpful.</summary>
    public Exception? ThrowOnCheck { get; set; }

    public Task<BackupFileCheck> CheckBackupFileAsync(
        ServerConnection server, string path, CancellationToken ct = default)
    {
        if (ThrowOnCheck != null) throw ThrowOnCheck;

        CheckedPaths.Add(path);

        return Task.FromResult(UnreadablePaths.TryGetValue(path, out var problem)
            ? new BackupFileCheck(path, problem, $"fake failure: {problem}")
            : BackupFileCheck.Ok(path));
    }

    public async Task<List<BackupFileCheck>> CheckBackupFilesAsync(
        ServerConnection server, IEnumerable<string> paths, CancellationToken ct = default)
    {
        // Observed for the same reason the execute fake observes its token (#283): a pressed
        // Stop reaches this check mid-copy, and a fake that ignores it lets a viewmodel
        // misreport the cancellation without any test noticing.
        ct.ThrowIfCancellationRequested();
        var results = new List<BackupFileCheck>();
        foreach (var path in paths)
        {
            var check = await CheckBackupFileAsync(server, path, ct);
            results.Add(check);
            if (!check.CanBeRestored) break;
        }
        return results;
    }

    public Task<BlobCredentialStatus> CredentialExistsAsync(
        ServerConnection server, string credentialName, CancellationToken ct = default)
        => OnCredentialCheck?.Invoke(credentialName, ct) ?? Task.FromResult(Credential);

    /// <summary>Every identity a credential write was asked for, in order (#147).</summary>
    public List<BlobCredentialIdentity> CredentialIdentitiesWritten { get; } = [];

    /// <summary>What the fake credential write reports back.</summary>
    public CredentialChange CredentialWriteResult { get; set; } = CredentialChange.None;

    /// <summary>The SAS token each write was handed, so a test can prove none was sent.</summary>
    public List<string> CredentialSecretsWritten { get; } = [];

    /// <summary>Set to make the write fail the way a real server can (#147).</summary>
    public Exception? CredentialWriteThrows { get; set; }

    public Task<CredentialChange> EnsureCredentialExistsAsync(
        ServerConnection server, string credentialName, string storageAccountUrl, string sasToken,
        BlobCredentialIdentity identity = BlobCredentialIdentity.SharedAccessSignature,
        CancellationToken ct = default)
    {
        if (CredentialWriteThrows != null) throw CredentialWriteThrows;

        CredentialWrites.Add(credentialName);
        CredentialIdentitiesWritten.Add(identity);
        CredentialSecretsWritten.Add(sasToken);
        return Task.FromResult(CredentialWriteResult);
    }

    /// <summary>What the fake instance says about managed identity. Supported by default.</summary>
    public ManagedIdentitySupport ManagedIdentity { get; set; } = new(true, 16, 3);

    /// <summary>Set to make ASKING fail, which is not the same as the answer being no.</summary>
    public Exception? ManagedIdentityCheckThrows { get; set; }

    public Task<ManagedIdentitySupport> SupportsManagedIdentityCredentialAsync(
        ServerConnection server, CancellationToken ct = default)
    {
        if (ManagedIdentityCheckThrows != null) throw ManagedIdentityCheckThrows;

        return Task.FromResult(ManagedIdentity);
    }
}

/// <summary>
/// What a person does after a load: pick a database, then a restore point.
///
/// The app deliberately chooses neither any more - preselecting the first database and the latest
/// point meant it had silently decided what to restore, and everything downstream described a
/// restore nobody had asked for. Tests that need a chain therefore have to make the choices a user
/// would, which is also a fair description of what they were always relying on.
/// </summary>
internal static class RestoreSetup
{
    public static void ChooseADatabaseAndAPoint(RestoreViewModel vm)
    {
        vm.Inventory.SelectedDatabaseName ??= vm.Inventory.DiscoveredDatabases.FirstOrDefault();
        vm.Timeline.SelectedPoint ??= vm.Timeline.Points.LastOrDefault();
    }
}

/// <summary>
/// A MainViewModel past the mode cards - where a test about screens wants to start (#369).
///
/// The cards are the landing screen on EVERY launch, not just the first, and navigation is now
/// refused behind them: Ctrl+1..9 stay bound on the window, and a keystroke used to land on a
/// screen with the sidebar collapsed to zero width, no mode chosen and no way back.
///
/// So a test that constructs a MainViewModel and navigates straight away is staging a state the
/// app does not have. Choosing a mode is what a first-run user does, and it is the one route past
/// the cards that works whether or not a mode was saved.
/// </summary>
public static class Launched
{
    public static MainViewModel App(AppMode mode = AppMode.Pro) =>
        App(new FakeCredentialStore(), mode);

    public static MainViewModel App(FakeCredentialStore store, AppMode mode = AppMode.Pro)
    {
        var main = new MainViewModel(store);
        main.ModeSelection.ChooseCommand.Execute(
            main.ModeSelection.Cards.Single(c => c.Mode == mode));
        return main;
    }
}
