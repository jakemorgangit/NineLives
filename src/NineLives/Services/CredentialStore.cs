using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Blackcat.NineLives.Models;

namespace Blackcat.NineLives.Services;

/// <summary>
/// Manages credentials via Windows Credential Manager and persists
/// non-secret configuration (server names, container URLs) to a local JSON file.
/// </summary>
public class CredentialStore
{
    private const string AppPrefix = "NineLives";
    private static readonly string DefaultConfigDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "NineLives");

    private readonly string _configDir;
    private readonly string _configPath;

    public CredentialStore() : this(DefaultConfigDir) { }

    /// <summary>Lets the tests point config persistence at a temp directory instead of the real profile.</summary>
    internal CredentialStore(string configDir)
    {
        _configDir = configDir;
        _configPath = Path.Combine(configDir, "config.json");
    }

    #region Windows Credential Manager P/Invoke

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CredWriteW(ref CREDENTIAL credential, uint flags);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CredReadW(string target, uint type, uint flags, out IntPtr credential);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CredDeleteW(string target, uint type, uint flags);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr credential);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CredEnumerateW(string? filter, uint flags, out int count, out IntPtr credentials);

    private const uint CRED_TYPE_GENERIC = 1;
    private const uint CRED_PERSIST_LOCAL_MACHINE = 2;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CREDENTIAL
    {
        public uint Flags;
        public uint Type;
        public string TargetName;
        public string Comment;
        public long LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public string TargetAlias;
        public string UserName;
    }

    #endregion

    public void SaveSecret(string key, string username, string secret)
    {
        var secretBytes = Encoding.Unicode.GetBytes(secret);
        var blob = Marshal.AllocHGlobal(secretBytes.Length);
        try
        {
            Marshal.Copy(secretBytes, 0, blob, secretBytes.Length);
            var cred = new CREDENTIAL
            {
                Type = CRED_TYPE_GENERIC,
                TargetName = key,
                UserName = username,
                CredentialBlob = blob,
                CredentialBlobSize = (uint)secretBytes.Length,
                Persist = CRED_PERSIST_LOCAL_MACHINE,
                Comment = $"{AppPrefix} credential"
            };

            if (!CredWriteW(ref cred, 0))
                throw new InvalidOperationException(
                    $"Failed to write credential: {Marshal.GetLastWin32Error()}");
        }
        finally
        {
            Marshal.FreeHGlobal(blob);
        }
    }

    public (string? username, string? secret) ReadSecret(string key)
    {
        if (!CredReadW(key, CRED_TYPE_GENERIC, 0, out var credPtr))
            return (null, null);

        try
        {
            var cred = Marshal.PtrToStructure<CREDENTIAL>(credPtr);
            var secret = cred.CredentialBlobSize > 0
                ? Marshal.PtrToStringUni(cred.CredentialBlob, (int)cred.CredentialBlobSize / 2)
                : null;
            return (cred.UserName, secret);
        }
        finally
        {
            CredFree(credPtr);
        }
    }

    public bool DeleteSecret(string key)
        => CredDeleteW(key, CRED_TYPE_GENERIC, 0);

    public List<string> ListCredentialKeys(string prefix)
    {
        var keys = new List<string>();
        var filter = $"{prefix}*";

        if (!CredEnumerateW(filter, 0, out int count, out IntPtr pCredentials))
            return keys;

        try
        {
            for (int i = 0; i < count; i++)
            {
                var credPtr = Marshal.ReadIntPtr(pCredentials, i * IntPtr.Size);
                var cred = Marshal.PtrToStructure<CREDENTIAL>(credPtr);
                keys.Add(cred.TargetName);
            }
        }
        finally
        {
            CredFree(pCredentials);
        }

        return keys;
    }

    #region SAS Token Helpers

    public void SaveSasToken(BlobContainerConfig config, string sasToken)
    {
        var accountName = config.StorageAccountName ?? "azure";
        SaveSecret(config.CredentialKey, accountName, sasToken);
        config.CacheSasToken(sasToken);
    }

    public string? GetSasToken(BlobContainerConfig config)
    {
        var (_, secret) = ReadSecret(config.CredentialKey);
        if (secret != null)
            config.CacheSasToken(secret);
        return secret;
    }

    public bool IsSasTokenExpired(BlobContainerConfig config)
    {
        var sasToken = GetSasToken(config);
        if (sasToken == null) return true;
        return config.ReadSasExpiry(sasToken).IsExpired;
    }

    public DateTime? GetSasTokenExpiry(BlobContainerConfig config)
        => ReadSasTokenExpiry(config).ExpiresAt;

    /// <summary>Full expiry state, so callers can tell "no expiry" from "unreadable" (#21).</summary>
    public SasExpiryInfo ReadSasTokenExpiry(BlobContainerConfig config)
    {
        var sasToken = GetSasToken(config);
        return sasToken != null ? config.ReadSasExpiry(sasToken) : SasExpiryInfo.None;
    }

    #endregion

    #region SQL Credential Helpers

    public void SaveSqlPassword(ServerConnection connection, string password)
    {
        SaveSecret(connection.CredentialKey, connection.Username ?? "sa", password);
    }

    public string? GetSqlPassword(ServerConnection connection)
    {
        var (_, secret) = ReadSecret(connection.CredentialKey);
        return secret;
    }

    #endregion

    #region Config Persistence (non-secret data)

    /// <summary>
    /// Reads config.json. Never throws, so startup cannot be bricked by a bad file - but a file
    /// that exists and could not be read comes back carrying <see cref="AppConfig.LoadError"/>,
    /// and <see cref="SaveConfig"/> refuses to overwrite anything in that state.
    ///
    /// The distinction matters. "No file" means a fresh install, where empty defaults are exactly
    /// right. "File is there but I could not read it" used to produce the same empty defaults,
    /// which is how a transient lock - antivirus, a backup agent, a sync client mid-upload -
    /// turned into permanent data loss: the UI showed no containers, the user re-added one, and
    /// the save wrote that single entry over everything they had.
    /// </summary>
    public AppConfig LoadConfig()
    {
        if (!File.Exists(_configPath))
            return new AppConfig();

        try
        {
            var json = File.ReadAllText(_configPath);
            var config = JsonSerializer.Deserialize<AppConfig>(json);
            if (config != null)
                return config;

            return new AppConfig
            {
                LoadError = $"{_configPath} does not contain valid configuration."
            };
        }
        catch (Exception ex)
        {
            return new AppConfig
            {
                LoadError = $"{_configPath} could not be read: {ex.Message}"
            };
        }
    }

    /// <summary>
    /// Writes config.json, or throws. Callers must report the failure rather than telling the
    /// user their work was saved - the old version swallowed everything and the UI said
    /// "saved successfully" regardless.
    /// </summary>
    /// <exception cref="ConfigSaveRefusedException">
    /// The config being saved came from a failed load, so writing it would destroy whatever is
    /// still on disk.
    /// </exception>
    public void SaveConfig(AppConfig config)
    {
        if (config.LoadError != null)
            throw new ConfigSaveRefusedException(config.LoadError);

        Directory.CreateDirectory(_configDir);
        var json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });

        // Write beside the real file and swap it in, so a crash or a full disk part-way through
        // cannot leave a truncated config.json where a complete one used to be. File.Replace is
        // atomic and keeps the previous contents as .bak, which is a free undo for the case
        // where something else has gone wrong.
        var tempPath = _configPath + ".tmp";
        File.WriteAllText(tempPath, json);

        if (File.Exists(_configPath))
        {
            try
            {
                File.Replace(tempPath, _configPath, _configPath + ".bak", ignoreMetadataErrors: true);
                return;
            }
            catch (PlatformNotSupportedException)
            {
                // Some filesystems (a few network shares) cannot do the atomic swap. Fall through
                // to the plain move, which is still better than writing over the file in place.
            }
            File.Delete(_configPath);
        }

        File.Move(tempPath, _configPath);
    }

    #endregion
}

/// <summary>
/// Thrown when a save is refused because the configuration in hand was never successfully loaded.
/// Writing it would replace real saved servers and containers with an empty list.
/// </summary>
public class ConfigSaveRefusedException(string loadError)
    : InvalidOperationException(
        $"Configuration was not saved, because it could not be read in the first place and " +
        $"saving now would overwrite the copy on disk. {loadError}")
{
    public string LoadError { get; } = loadError;
}

public class AppConfig
{
    public List<BlobContainerConfig> BlobContainers { get; set; } = [];
    public List<ServerConnection> Servers { get; set; } = [];
    public string? LastSelectedContainer { get; set; }
    public string? LastSelectedServer { get; set; }

    /// <summary>Check GitHub for a newer release at startup. Turn off for offline installs.</summary>
    public bool CheckForUpdates { get; set; } = true;

    /// <summary>Last release tag the user was told about, so the banner does not nag.</summary>
    public string? LastNotifiedReleaseTag { get; set; }

    /// <summary>
    /// Set when config.json existed but could not be read or parsed. Not persisted - it describes
    /// this load attempt, not the configuration. While it is set, this object holds empty defaults
    /// that do NOT reflect what is on disk, so <see cref="CredentialStore.SaveConfig"/> will
    /// refuse to write it.
    /// </summary>
    [JsonIgnore]
    public string? LoadError { get; set; }
}
