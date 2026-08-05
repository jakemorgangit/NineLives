using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
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

    public bool IsExpired => ReadSasExpiry().IsExpired;

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

    public DateTime? GetSasExpiry(string? sasToken = null) => ReadSasExpiry(sasToken).ExpiresAt;

    /// <summary>
    /// Reads the SAS <c>se=</c> expiry, distinguishing "there isn't one" from "there is one and it
    /// is not readable" (#21).
    ///
    /// That distinction is the point. The old version parsed with the ambient culture and returned
    /// null on any failure, and every caller reads null as "not expired" - so on a non-invariant
    /// locale an expired token could be presented as perfectly fine, and the user found out when
    /// the restore failed. An se= that will not parse now counts as expired, because the honest
    /// answer is "this token cannot be trusted to be valid".
    ///
    /// A token with no se= at all is a different case and stays "unknown, not expired": a SAS
    /// built on a stored access policy legitimately has no expiry of its own.
    /// </summary>
    public SasExpiryInfo ReadSasExpiry(string? sasToken = null)
    {
        var token = sasToken ?? _cachedSasTokenValue;
        if (string.IsNullOrEmpty(token))
            return SasExpiryInfo.None;

        string? se;
        try
        {
            var query = token.StartsWith("?") ? token : "?" + token;
            se = HttpUtility.ParseQueryString(query)["se"];
        }
        catch
        {
            return SasExpiryInfo.Unreadable;
        }

        // No se= at all is "this token does not state an expiry". An se= that is present but empty
        // is a different thing: it claims one and supplies nothing, which is unreadable, not absent.
        if (se == null)
            return SasExpiryInfo.None;

        if (string.IsNullOrWhiteSpace(se))
            return SasExpiryInfo.Unreadable;

        // Invariant, and treating the value as UTC whether or not it carries a zone. Azure writes
        // ISO-8601 here; the ambient culture has no business interpreting it.
        var parsed = DateTime.TryParse(
            se,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
            out var expiry);

        return parsed ? new SasExpiryInfo(expiry, false) : SasExpiryInfo.Unreadable;
    }

    public void CacheSasToken(string sasToken)
    {
        _cachedSasTokenValue = sasToken;
    }

    public override string ToString() => DisplayText;
}

/// <summary>
/// What a SAS token says about its own expiry. Three states rather than a nullable DateTime,
/// because "no expiry stated" and "expiry stated but unreadable" mean opposite things and both
/// used to come back as null (#21).
/// </summary>
/// <param name="ExpiresAt">When the token expires, in UTC. Null if it does not say.</param>
/// <param name="CouldNotParse">The token carries an se= value that could not be read.</param>
public readonly record struct SasExpiryInfo(DateTime? ExpiresAt, bool CouldNotParse)
{
    /// <summary>No se= parameter. Legitimate for a SAS built on a stored access policy.</summary>
    public static SasExpiryInfo None => new(null, false);

    /// <summary>There is an se=, and it is not readable. Treated as expired.</summary>
    public static SasExpiryInfo Unreadable => new(null, true);

    /// <summary>
    /// Unreadable counts as expired. The alternative is presenting a token we cannot vouch for as
    /// valid, and finding out mid-restore.
    /// </summary>
    public bool IsExpired =>
        CouldNotParse || (ExpiresAt.HasValue && ExpiresAt.Value < DateTime.UtcNow);
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
