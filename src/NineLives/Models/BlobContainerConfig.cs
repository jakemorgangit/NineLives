using System.Collections.ObjectModel;
using System.Web;

namespace Blackcat.NineLives.Models;

/// <summary>
/// Indicates what type of backups are stored in the container:
/// Standalone = path-based structure only; AG = Ola default AG naming (flat filenames);
/// Mixed = both, with separate path patterns for each.
/// </summary>
public enum BackupSourceType
{
    Standalone,
    AvailabilityGroup,
    Mixed
}

public class BlobContainerConfig
{
    public string Name { get; set; } = string.Empty;
    public string ContainerUrl { get; set; } = string.Empty;

    /// <summary>
    /// Whether backups are from standalone instances, AG (Ola default naming), or both.
    /// </summary>
    public BackupSourceType BackupSourceType { get; set; } = BackupSourceType.Standalone;

    /// <summary>
    /// Pattern describing the blob path structure (standalone, or when Mixed this is the standalone pattern).
    /// Supported tokens: {BackupType}, {ServerName}, {InstanceName}, {DatabaseName}, {FileName}
    /// Default: {BackupType}/{ServerName}/{DatabaseName}/{FileName}
    /// </summary>
    public string PathPattern { get; set; } = "{BackupType}/{ServerName}/{DatabaseName}/{FileName}";

    /// <summary>
    /// When BackupSourceType is Mixed or AvailabilityGroup, optional path pattern for AG backups.
    /// If null/empty, AG backups are assumed to use Ola default flat naming and are parsed from the filename.
    /// </summary>
    public string? AgPathPattern { get; set; }

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
    /// Key used to look up SAS token in Windows Credential Manager.
    /// </summary>
    public string CredentialKey => $"NineLives:Blob:{Name}";

    public bool IsExpired => GetSasExpiry() is DateTime expiry && expiry < DateTime.UtcNow;

    public DateTime? SasExpiry => GetSasExpiry();

    public string? StorageAccountName
    {
        get
        {
            if (!Uri.TryCreate(ContainerUrl, UriKind.Absolute, out var uri))
                return null;
            var host = uri.Host;
            var dotIndex = host.IndexOf('.');
            return dotIndex > 0 ? host[..dotIndex] : host;
        }
    }

    public string? ContainerName
    {
        get
        {
            if (!Uri.TryCreate(ContainerUrl, UriKind.Absolute, out var uri))
                return null;
            return uri.AbsolutePath.Trim('/');
        }
    }

    public string DisplayText
    {
        get
        {
            var status = IsExpired ? " [EXPIRED]" : "";
            return $"{Name}{status}";
        }
    }

    private string? _cachedSasTokenValue;

    public DateTime? GetSasExpiry(string? sasToken = null)
    {
        var token = sasToken ?? _cachedSasTokenValue;
        if (string.IsNullOrEmpty(token))
            return null;

        try
        {
            var query = token.StartsWith("?") ? token : "?" + token;
            var parsed = HttpUtility.ParseQueryString(query);
            var se = parsed["se"];
            if (se != null && DateTime.TryParse(se, out var expiry))
                return expiry.ToUniversalTime();
        }
        catch
        {
            // Malformed token
        }

        return null;
    }

    public void CacheSasToken(string sasToken)
    {
        _cachedSasTokenValue = sasToken;
    }

    public override string ToString() => DisplayText;
}

/// <summary>
/// Represents a single token element in the blob path structure builder.
/// </summary>
public class PathElement
{
    public string Token { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string HexColor { get; set; } = "#4A90D9";

    public static readonly List<PathElement> AllElements =
    [
        new() { Token = "BackupType", DisplayName = "Backup Type", HexColor = "#4A90D9" },
        new() { Token = "ServerName", DisplayName = "Server Name", HexColor = "#F39C12" },
        new() { Token = "InstanceName", DisplayName = "Instance Name", HexColor = "#9B59B6" },
        new() { Token = "DatabaseName", DisplayName = "Database Name", HexColor = "#27AE60" },
        new() { Token = "FileName", DisplayName = "File Name", HexColor = "#8890A4" },
        // AG-specific: cluster name, AG name, or single segment (with $ or _)
        new() { Token = "ClusterName", DisplayName = "Cluster Name", HexColor = "#E67E22" },
        new() { Token = "AgName", DisplayName = "AG Name", HexColor = "#1ABC9C" },
        new() { Token = "ClusterName$AgName", DisplayName = "Cluster $ AG", HexColor = "#8E44AD" },
        new() { Token = "ClusterName_AgName", DisplayName = "Cluster _ AG", HexColor = "#3498DB" },
    ];

    public static PathElement? FromToken(string token)
        => AllElements.FirstOrDefault(e => e.Token.Equals(token, StringComparison.OrdinalIgnoreCase));

    public static List<PathElement> ParsePattern(string pattern)
    {
        var result = new List<PathElement>();
        var parts = pattern.Split('/', StringSplitOptions.RemoveEmptyEntries);
        foreach (var part in parts)
        {
            var trimmed = part.Trim().Trim('{', '}');
            var elem = FromToken(trimmed);
            if (elem != null) result.Add(elem);
        }
        return result;
    }

    public static string BuildPattern(IEnumerable<PathElement> elements)
        => string.Join("/", elements.Select(e => $"{{{e.Token}}}"));
}
