using Blackcat.NineLives.Models;
using Blackcat.NineLives.Services;

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

    public Task<bool> VerifyConnectionAsync(BlobContainerConfig config, CancellationToken ct = default)
        => Task.FromResult(true);

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
    public List<BackupSet> GroupIntoBackupSets(List<BackupFileInfo> files) => _real.GroupIntoBackupSets(files);
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

    public bool CredentialExists { get; set; } = true;
    public bool CredentialIsSas { get; set; } = true;

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

    public Task<List<string>> GetDatabaseListAsync(ServerConnection server, CancellationToken ct = default)
        => Task.FromResult(new List<string>());

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

    public Task<VerifyOnlyResult> RestoreVerifyOnlyAsync(
        ServerConnection server, IReadOnlyList<string> blobUrls, bool withChecksum = false, CancellationToken ct = default)
    {
        VerifiedUrls.AddRange(blobUrls);
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

    public Task ExecuteRestoreWithProgressAsync(
        ServerConnection server, string sql, Action<string>? messageCallback = null, CancellationToken ct = default)
    {
        ExecutedAgainst.Add(server);
        ExecutedScripts.Add(sql);
        messageCallback?.Invoke("100 percent processed.");
        if (ExecuteThrows != null) throw ExecuteThrows;
        return Task.CompletedTask;
    }

    public Task<(bool Exists, bool IsSharedAccessSignature)> CredentialExistsAsync(
        ServerConnection server, string credentialName, CancellationToken ct = default)
        => Task.FromResult((CredentialExists, CredentialIsSas));

    public Task<CredentialChange> EnsureCredentialExistsAsync(
        ServerConnection server, string credentialName, string storageAccountUrl, string sasToken,
        CancellationToken ct = default)
        => Task.FromResult(CredentialChange.None);
}
