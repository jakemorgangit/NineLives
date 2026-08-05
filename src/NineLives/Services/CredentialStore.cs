using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Blackcat.NineLives.Models;

namespace Blackcat.NineLives.Services;

/// <summary>
/// Manages credentials via Windows Credential Manager and persists
/// non-secret configuration (server names, container URLs) to a local JSON file.
/// </summary>
public class CredentialStore
{
    private const string AppPrefix = "NineLives";
    private static readonly string ConfigDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "NineLives");
    private static readonly string ConfigPath = Path.Combine(ConfigDir, "config.json");

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
        var expiry = config.GetSasExpiry(sasToken);
        return expiry.HasValue && expiry.Value < DateTime.UtcNow;
    }

    public DateTime? GetSasTokenExpiry(BlobContainerConfig config)
    {
        var sasToken = GetSasToken(config);
        return sasToken != null ? config.GetSasExpiry(sasToken) : null;
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

    public AppConfig LoadConfig()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                var json = File.ReadAllText(ConfigPath);
                return JsonSerializer.Deserialize<AppConfig>(json) ?? new AppConfig();
            }
        }
        catch
        {
            // Config corrupted, return defaults
        }
        return new AppConfig();
    }

    public void SaveConfig(AppConfig config)
    {
        try
        {
            Directory.CreateDirectory(ConfigDir);
            var json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(ConfigPath, json);
        }
        catch
        {
            // Silently fail config save
        }
    }

    #endregion
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
}
