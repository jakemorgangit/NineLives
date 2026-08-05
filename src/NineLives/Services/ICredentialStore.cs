using Blackcat.NineLives.Models;

namespace Blackcat.NineLives.Services;

/// <summary>
/// Stored configuration and secrets. Implemented by <see cref="CredentialStore"/> against the
/// Windows Credential Manager and a JSON file under %LOCALAPPDATA%.
///
/// This exists so a ViewModel can be constructed in a test without reaching the real machine.
/// Every method here touches either the credential vault or the user's config file, which is a
/// side effect no test should have (#41).
/// </summary>
public interface ICredentialStore
{
    void SaveSecret(string key, string username, string secret);
    (string? username, string? secret) ReadSecret(string key);
    bool DeleteSecret(string key);
    List<string> ListCredentialKeys(string prefix);

    void SaveSasToken(BlobContainerConfig config, string sasToken);
    string? GetSasToken(BlobContainerConfig config);
    bool IsSasTokenExpired(BlobContainerConfig config);
    DateTime? GetSasTokenExpiry(BlobContainerConfig config);
    SasExpiryInfo ReadSasTokenExpiry(BlobContainerConfig config);

    void SaveSqlPassword(ServerConnection connection, string password);
    string? GetSqlPassword(ServerConnection connection);

    AppConfig LoadConfig();
    void SaveConfig(AppConfig config);
}
