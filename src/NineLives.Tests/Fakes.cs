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

    public Task<bool> VerifyConnectionAsync(BlobContainerConfig config, CancellationToken ct = default)
    {
        LastConfig = config;
        return Task.FromResult(true);
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

    public Task<List<BackupFileInfo>> ListBackupFilesAsync(
        BlobContainerConfig config, BlobListingScope? scope, IProgress<int>? progress, CancellationToken ct = default)
    {
        ListCalls++;
        LastScope = scope;
        ct.ThrowIfCancellationRequested();
        if (ListThrows != null) throw ListThrows;

        progress?.Report(Files.Count);
        return Task.FromResult(Files.ToList());
    }

    public ContainerSummary GetContainerSummary(List<BackupFileInfo> files) => _real.GetContainerSummary(files);
    public ContainerSummary GetSetBasedSummary(List<BackupSet> sets) => _real.GetSetBasedSummary(sets);
    public List<BackupSet> GroupIntoBackupSets(List<BackupFileInfo> files, string? backupServerTimeZoneId = null)
        => _real.GroupIntoBackupSets(files, backupServerTimeZoneId);
    public List<string> GetDiscoveredDatabases(List<BackupFileInfo> files) => _real.GetDiscoveredDatabases(files);
    public List<string> GetDiscoveredServers(List<BackupFileInfo> files) => _real.GetDiscoveredServers(files);
}

/// <summary>An in-memory restore history, so the tests never touch the real one.</summary>
public sealed class FakeRestoreHistoryStore : IRestoreHistoryStore
{
    public List<RestoreHistoryEntry> Entries { get; } = [];

    public string FilePath => "(in memory)";

    public List<RestoreHistoryEntry> Load() => Entries.ToList();

    public void Append(RestoreHistoryEntry entry) => Entries.Insert(0, entry);

    public void Clear() => Entries.Clear();
}

/// <summary>
/// A SQL Server that records what it was asked to do. Nothing opens a connection.
/// </summary>
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

    public Task<bool> TestConnectionAsync(ServerConnection server, CancellationToken ct = default)
        => Task.FromResult(true);

    public Task<bool?> WouldConnectWithCertificateValidationAsync(ServerConnection server, CancellationToken ct = default)
        => Task.FromResult<bool?>(true);

    public Task<string> GetServerVersionAsync(ServerConnection server, CancellationToken ct = default)
        => Task.FromResult("Microsoft SQL Server 2022");

    /// <summary>What the fake instance says it holds.</summary>
    public List<string> DatabaseList { get; set; } = [];

    public Task<List<string>> GetDatabaseListAsync(ServerConnection server, CancellationToken ct = default)
        => Task.FromResult(DatabaseList);

    public Task<(string DataPath, string LogPath)> GetDefaultPathsAsync(ServerConnection server, CancellationToken ct = default)
        => Task.FromResult((@"D:\Data", @"D:\Logs"));

    public Task<DatabaseRecoveryState> GetDatabaseRecoveryStateAsync(
        ServerConnection server, string databaseName, CancellationToken ct = default)
        => Task.FromResult(RecoveryState);

    public Task ExecuteRecoveryActionAsync(ServerConnection server, string sql, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task<List<FileMoveOption>> RestoreFileListOnlyAsync(
        ServerConnection server, IReadOnlyList<string> blobUrls, CancellationToken ct = default)
        => Task.FromResult(new List<FileMoveOption>());

    public Task<BackupFileInfo?> RestoreHeaderOnlyMultiAsync(
        ServerConnection server, IReadOnlyList<string> blobUrls, CancellationToken ct = default)
        => Task.FromResult<BackupFileInfo?>(null);

    /// <summary>Called with the token the viewmodel supplied, so a test can see it was cancellable.</summary>
    public Action<CancellationToken>? OnVerify { get; set; }

    /// <summary>Every token handed to a verify call, so a test can prove one was passed at all.</summary>
    public List<CancellationToken> VerifyTokens { get; } = [];

    /// <summary>The MOVE clauses the last verification was given (#129).</summary>
    public List<FileMoveOption> VerifiedWithMoves { get; private set; } = [];

    public Task<VerifyOnlyResult> RestoreVerifyOnlyAsync(
        ServerConnection server, IReadOnlyList<string> blobUrls, bool withChecksum = false,
        IReadOnlyList<FileMoveOption>? fileMoves = null, CancellationToken ct = default)
    {
        VerifiedUrls.AddRange(blobUrls);
        VerifiedWithMoves = fileMoves?.ToList() ?? [];
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

    public Task ExecuteWithProgressAsync(
        ServerConnection server, string sql, Action<string>? messageCallback = null, CancellationToken ct = default)
    {
        ExecutedAgainst.Add(server);
        ExecutedScripts.Add(sql);
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

    public Task<CredentialChange> EnsureCredentialExistsAsync(
        ServerConnection server, string credentialName, string storageAccountUrl, string sasToken,
        CancellationToken ct = default)
    {
        CredentialWrites.Add(credentialName);
        return Task.FromResult(CredentialChange.None);
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
