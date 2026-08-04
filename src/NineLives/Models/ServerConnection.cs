namespace Blackcat.NineLives.Models;

public enum AuthMode
{
    WindowsAuth,
    SqlAuth
}

public enum EncryptMode
{
    Yes,
    No,
    Strict
}

public class ServerConnection
{
    public string Name { get; set; } = string.Empty;
    public string ServerName { get; set; } = string.Empty;
    public AuthMode AuthMode { get; set; } = AuthMode.WindowsAuth;
    public string? Username { get; set; }
    public int ConnectionTimeoutSeconds { get; set; } = 15;
    public bool TrustServerCertificate { get; set; } = true;
    public EncryptMode Encrypt { get; set; } = EncryptMode.Yes;

    /// <summary>
    /// Key used to look up password in Windows Credential Manager.
    /// Only used when AuthMode is SqlAuth.
    /// </summary>
    public string CredentialKey => $"NineLives:SQL:{Name}";

    public string DisplayText => AuthMode == AuthMode.WindowsAuth
        ? $"{ServerName} (Windows Auth)"
        : $"{ServerName} ({Username})";
}
