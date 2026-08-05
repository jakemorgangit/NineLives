using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Text.Json.Serialization;
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

/// <summary>
/// A saved blob container. Implements INotifyPropertyChanged so the derived TagChips member
/// notifies when Tags changes - see the note on Tags.
/// </summary>
public class BlobContainerConfig : INotifyPropertyChanged
{
    /// <summary>
    /// Stable identity for the stored SAS token, assigned once and never changed.
    ///
    /// The credential key used to derive from Name, so renaming a container pointed it at a key
    /// that did not exist and the working token was stranded under the old one with no way to get
    /// it back (#8).
    ///
    /// Null on entries written before this existed. It is deliberately NOT defaulted to a new Guid:
    /// an absent value in JSON leaves the property at its initialiser, so defaulting would hand
    /// every legacy entry a fresh id on load and lose its secret immediately. ConfigMigrator
    /// assigns ids and moves the secrets; NewId() is what callers creating a container use.
    /// </summary>
    public string? Id { get; set; }

    public static string NewId() => Guid.NewGuid().ToString("n");

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

    private ObservableCollection<string> _tags = [];

    /// <summary>
    /// Free-text labels shown as coloured pills. Absent from older config files, which
    /// deserialise to an empty collection - no migration needed.
    ///
    /// Editing in place or replacing wholesale both work: the setter re-subscribes, and the
    /// collection's own changes are forwarded to the derived TagChips so the pill list refreshes
    /// on save rather than when the row is next rebuilt.
    /// </summary>
    public ObservableCollection<string> Tags
    {
        get => _tags;
        set
        {
            if (ReferenceEquals(_tags, value)) return;
            _tags.CollectionChanged -= OnTagsCollectionChanged;
            _tags = value ?? [];
            _tags.CollectionChanged += OnTagsCollectionChanged;
            RaiseTagMembersChanged();
        }
    }

    public BlobContainerConfig()
    {
        _tags.CollectionChanged += OnTagsCollectionChanged;
    }

    /// <summary>
    /// Tags as chips, matching the server list. Containers have no automatic tags yet, but going
    /// through the same shape keeps one rendering path.
    /// </summary>
    [JsonIgnore]
    public IEnumerable<TagChip> TagChips => Tags.Select(TagChip.Manual);

    private void OnTagsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        => RaiseTagMembersChanged();

    private void RaiseTagMembersChanged()
    {
        OnPropertyChanged(nameof(Tags));
        OnPropertyChanged(nameof(TagChips));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged(string propertyName)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    /// <summary>
    /// Key used to look up the SAS token in Windows Credential Manager. Keyed on the immutable
    /// <see cref="Id"/> so renaming the container does not strand its token; falls back to the
    /// old name-derived key only for entries that have not been migrated yet.
    /// </summary>
    [JsonIgnore]
    public string CredentialKey =>
        string.IsNullOrEmpty(Id) ? LegacyCredentialKey : $"NineLives:Blob:{Id}";

    /// <summary>The pre-#8 name-derived key. Only ConfigMigrator should need this.</summary>
    [JsonIgnore]
    public string LegacyCredentialKey => $"NineLives:Blob:{Name}";

    /// <summary>
    /// A SAS token held in memory for this object only, never written anywhere. When set, the
    /// blob service uses it in place of the stored one.
    ///
    /// This exists for Test Connection (#12). Testing a newly typed token used to persist it to
    /// Credential Manager first - so pasting a typo'd or expired token, testing it, watching it
    /// fail and clicking Cancel destroyed the working token that was there before, with no way
    /// to get it back because the form never displays stored tokens.
    /// </summary>
    [JsonIgnore]
    public string? UnsavedSasToken { get; set; }

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
