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
    /// Free-text labels shown as coloured pills. Absent from older config files, which
    /// deserialise to an empty list - no migration needed.
    /// </summary>
    public List<string> Tags { get; set; } = [];

    /// <summary>True when any tag marks this as a production-like environment.</summary>
    public bool IsProductionTagged => Tags.Any(Services.TagPalette.IsProductionLike);

    /// <summary>
    /// Key used to look up password in Windows Credential Manager.
    /// Only used when AuthMode is SqlAuth.
    /// </summary>
    public string CredentialKey => $"NineLives:SQL:{Name}";

    public string DisplayText => AuthMode == AuthMode.WindowsAuth
        ? $"{ServerName} (Windows Auth)"
        : $"{ServerName} ({Username})";
}
