using System.Collections.ObjectModel;
using System.Text.Json.Serialization;

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
    /// deserialise to an empty collection - no migration needed.
    ///
    /// ObservableCollection, and callers MUST mutate it in place rather than assigning a new
    /// one: these models are plain serialised objects with no PropertyChanged, so a reassignment
    /// is invisible to a bound ItemsControl and the pills only appear after navigating away and
    /// back. Mutating in place raises CollectionChanged, which the binding does see.
    /// </summary>
    public ObservableCollection<string> Tags { get; set; } = [];

    /// <summary>
    /// Product name detected on the last successful connection, e.g. "SQL Server 2022". Shown as
    /// an automatic tag. Persisted so it survives a restart and is available before reconnecting.
    /// </summary>
    public string? DetectedVersion { get; set; }

    /// <summary>
    /// Tags derived from observed facts rather than typed by the user. Rendered distinctly so a
    /// derived fact is never mistaken for a human assertion.
    /// </summary>
    [JsonIgnore]
    public IEnumerable<string> AutoTags =>
        string.IsNullOrWhiteSpace(DetectedVersion) ? [] : [DetectedVersion];

    [JsonIgnore]
    public bool HasAutoTags => !string.IsNullOrWhiteSpace(DetectedVersion);

    /// <summary>True when any tag marks this as a production-like environment.</summary>
    [JsonIgnore]
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
