using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Text.Json.Serialization;

using Blackcat.NineLives.Services;

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

/// <summary>
/// A saved SQL Server connection.
///
/// Implements INotifyPropertyChanged specifically so the DERIVED tag members notify. Tags were
/// made an ObservableCollection so in-place edits update the UI, but TagChips is computed from
/// Tags and AutoTags - a CollectionChanged on Tags says nothing about TagChips, so the pill list
/// went stale again and only refreshed when the row happened to be rebuilt. Raising
/// PropertyChanged for the computed members whenever their inputs change fixes the whole class
/// of problem rather than one instance of it.
/// </summary>
public class ServerConnection : INotifyPropertyChanged
{
    /// <summary>
    /// Stable identity for the stored password, assigned once and never changed. See the note on
    /// <see cref="BlobContainerConfig.Id"/> - same bug (#8), same reason it is not defaulted to a
    /// new Guid, same migration.
    /// </summary>
    public string? Id { get; set; }

    public static string NewId() => Guid.NewGuid().ToString("n");

    public string Name { get; set; } = string.Empty;
    public string ServerName { get; set; } = string.Empty;
    public AuthMode AuthMode { get; set; } = AuthMode.WindowsAuth;
    public string? Username { get; set; }
    public int ConnectionTimeoutSeconds { get; set; } = 15;
    public bool TrustServerCertificate { get; set; } = true;
    public EncryptMode Encrypt { get; set; } = EncryptMode.Yes;

    private ObservableCollection<string> _tags = [];

    /// <summary>
    /// Free-text labels shown as coloured pills. Absent from older config files, which
    /// deserialise to an empty collection - no migration needed.
    ///
    /// Editing in place or replacing wholesale both work now: the setter re-subscribes and the
    /// collection's own changes are forwarded on to the derived members.
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

    public ServerConnection()
    {
        _tags.CollectionChanged += OnTagsCollectionChanged;
    }

    private string? _detectedVersion;

    /// <summary>
    /// Product name detected on the last successful connection, e.g. "SQL Server 2022". Shown as
    /// an automatic tag. Persisted so it survives a restart and is available before reconnecting.
    /// </summary>
    public string? DetectedVersion
    {
        get => _detectedVersion;
        set
        {
            if (_detectedVersion == value) return;
            _detectedVersion = value;
            OnPropertyChanged(nameof(DetectedVersion));
            RaiseTagMembersChanged();
        }
    }

    private void OnTagsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        => RaiseTagMembersChanged();

    /// <summary>Everything computed from Tags or DetectedVersion, notified together.</summary>
    private void RaiseTagMembersChanged()
    {
        OnPropertyChanged(nameof(Tags));
        OnPropertyChanged(nameof(TagChips));
        OnPropertyChanged(nameof(AutoTags));
        OnPropertyChanged(nameof(HasAutoTags));
        OnPropertyChanged(nameof(IsProductionTagged));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged(string propertyName)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    /// <summary>
    /// Tags derived from observed facts rather than typed by the user. Rendered distinctly so a
    /// derived fact is never mistaken for a human assertion.
    /// </summary>
    [JsonIgnore]
    public IEnumerable<string> AutoTags =>
        string.IsNullOrWhiteSpace(DetectedVersion) ? [] : [DetectedVersion];

    [JsonIgnore]
    public bool HasAutoTags => !string.IsNullOrWhiteSpace(DetectedVersion);

    /// <summary>
    /// Manual and automatic tags as one sequence, so the UI can render them from a SINGLE
    /// wrapping panel. Two adjacent ItemsControls inside a WrapPanel cannot wrap - each is
    /// measured with infinite width - and their pills get clipped by the row instead.
    ///
    /// Each group is alphabetical, and manual tags come before automatic ones. Sorting here as
    /// well as in ParseTags means entries saved before this change display in order straight away,
    /// without waiting to be edited and re-saved. Automatic tags stay in their own group at the
    /// end so a derived fact is not shuffled in among the user's own labels.
    /// </summary>
    [JsonIgnore]
    public IEnumerable<TagChip> TagChips =>
        TagPalette.Sort(Tags).Select(TagChip.Manual)
            .Concat(TagPalette.Sort(AutoTags).Select(TagChip.Automatic));

    /// <summary>True when any tag marks this as a production-like environment.</summary>
    [JsonIgnore]
    public bool IsProductionTagged => Tags.Any(Services.TagPalette.IsProductionLike);

    /// <summary>
    /// Key used to look up password in Windows Credential Manager.
    /// Only used when AuthMode is SqlAuth.
    /// </summary>
    [JsonIgnore]
    public string CredentialKey =>
        string.IsNullOrEmpty(Id) ? LegacyCredentialKey : $"NineLives:SQL:{Id}";

    /// <summary>The pre-#8 name-derived key. Only ConfigMigrator should need this.</summary>
    [JsonIgnore]
    public string LegacyCredentialKey => $"NineLives:SQL:{Name}";

    /// <summary>
    /// A password held in memory for this object only, never written anywhere. When set, the
    /// connection string uses it in place of the stored one. Same purpose as
    /// <see cref="BlobContainerConfig.UnsavedSasToken"/> - see the note there (#12).
    /// </summary>
    [JsonIgnore]
    public string? UnsavedPassword { get; set; }

    public string DisplayText => AuthMode == AuthMode.WindowsAuth
        ? $"{ServerName} (Windows Auth)"
        : $"{ServerName} ({Username})";

    private bool _isConnectedServer;

    /// <summary>
    /// True for the one server currently connected. Not persisted - it describes this session.
    ///
    /// Connecting used to change the banner at the top of the window and nothing in the list, so
    /// with more than a screenful of servers there was no way to tell which one was live (#127).
    /// Kept on the model rather than compared in the view so the row can simply bind to it.
    /// </summary>
    [JsonIgnore]
    public bool IsConnectedServer
    {
        get => _isConnectedServer;
        set
        {
            if (_isConnectedServer == value) return;
            _isConnectedServer = value;
            OnPropertyChanged(nameof(IsConnectedServer));
        }
    }
}
