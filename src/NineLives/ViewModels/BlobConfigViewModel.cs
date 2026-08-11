using System.Collections.ObjectModel;
using System.Text;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Blackcat.NineLives.Models;
using Blackcat.NineLives.Services;

namespace Blackcat.NineLives.ViewModels;

/// <summary>
/// The Blob Storage form's provider choice (#51). UI state only - what persists is the
/// container URL, whose scheme is the provider truth everywhere else in the app.
/// </summary>
public enum StorageProviderChoice
{
    Azure,
    S3
}

public partial class BlobConfigViewModel : ViewModelBase
{
    private readonly ICredentialStore _credentialStore;
    private readonly IBlobStorageService _blobService;
    private readonly OperationCancellation _testCancellation = new();

    /// <summary>True while a connection test is running and has not been asked to stop (#25).</summary>
    [ObservableProperty]
    private bool _canCancelTest;

    [ObservableProperty]
    private ObservableCollection<BlobContainerConfig> _containers = [];

    [ObservableProperty]
    private BlobContainerConfig? _selectedContainer;

    [ObservableProperty]
    private string _editName = string.Empty;

    /// <summary>Comma-separated tag list as typed by the user; parsed on save.</summary>
    [ObservableProperty]
    private string _editTags = string.Empty;

    private string _originalTags = string.Empty;

    [ObservableProperty]
    private string _editContainerUrl = string.Empty;

    /// <summary>
    /// How this container signs in (#29). Entra holds no secret of ours, which is the point:
    /// many organisations now prohibit long-lived SAS tokens outright.
    /// </summary>
    [ObservableProperty]
    private BlobAuthMode _editAuthMode = BlobAuthMode.SasToken;

    public bool IsSasAuth => EditAuthMode.NeedsSasToken();

    public bool IsEntraAuth => EditAuthMode.IsEntra();

    [ObservableProperty]
    private string _editSasToken = string.Empty;

    /// <summary>
    /// True when the URL on the form is an s3:// endpoint (#51). What is SAVED never carries a
    /// provider field - the URL's scheme is the answer - so this is the truth the form's
    /// provider choice below has to agree with by the time Save runs.
    /// </summary>
    public bool IsS3Url =>
        EditContainerUrl.TrimStart().StartsWith("s3://", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Which provider the form is collecting for. UI state, not a persisted field: the saved
    /// truth stays the URL's own scheme. This exists because the scheme alone made S3
    /// invisible on an empty Add Container form - every section defaulted to its Azure shape
    /// and nothing said a bucket was even possible until an s3:// URL had been typed. The
    /// choice shows the right sections immediately; typing a URL with a scheme snaps the
    /// choice to match it (the URL wins - it is what will be saved); a mismatch left standing
    /// is refused at Save with the fix named.
    ///
    /// An enum bound through EnumToBool, exactly like the authentication radios above it: that
    /// converter answers an uncheck with Binding.DoNothing, which is what keeps the radio
    /// group's own unchecking from writing junk back through the binding.
    /// </summary>
    [ObservableProperty]
    private StorageProviderChoice _editProvider = StorageProviderChoice.Azure;

    /// <summary>The provider choice as the yes/no every section gate and code branch asks.</summary>
    public bool EditIsS3 => EditProvider == StorageProviderChoice.S3;

    /// <summary>The two halves the form collects for an s3:// container. Combined on save into
    /// the one AccessKeyId:SecretKey string the engine's credential and the vault slot both
    /// want - the halves are never stored separately.</summary>
    [ObservableProperty]
    private string _editS3KeyId = string.Empty;

    [ObservableProperty]
    private string _editS3SecretKey = string.Empty;

    /// <summary>
    /// The bucket's region, when the provider needs one said (#51). Addressing, not a secret -
    /// it rides in the config next to the URL.
    /// </summary>
    [ObservableProperty]
    private string _editS3Region = string.Empty;

    private string _originalS3Region = string.Empty;

    /// <summary>
    /// The pair as the one string everything downstream understands, or empty when the form
    /// holds no complete pair. Trimmed halves: a pasted key id arrives with the clipboard's
    /// whitespace often enough, and neither half legitimately contains any.
    /// </summary>
    private string ComposedS3Pair =>
        string.IsNullOrWhiteSpace(EditS3KeyId) || string.IsNullOrWhiteSpace(EditS3SecretKey)
            ? string.Empty
            : $"{EditS3KeyId.Trim()}:{EditS3SecretKey.Trim()}";

    [ObservableProperty]
    private string _editPathPattern = BlobContainerConfig.DefaultPathPattern;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsMixedSourceType))]
    [NotifyPropertyChangedFor(nameof(IsAgPathSectionVisible))]
    private BackupSourceType _editBackupSourceType = BackupSourceType.Standalone;

    [ObservableProperty]
    private string? _editAgPathPattern;

    /// <summary>
    /// The backup server's time zone, or null for "not known" (#102). Selected from
    /// <see cref="TimeZones"/>.
    /// </summary>
    [ObservableProperty]
    private TimeZoneOption? _editBackupServerTimeZone;

    private string? _originalTimeZoneId;

    /// <summary>
    /// Every zone this machine knows, with an explicit "not known" first. Read once - the list
    /// does not change while the app is running, and enumerating it is not free.
    /// </summary>
    public static IReadOnlyList<TimeZoneOption> TimeZones { get; } = BuildTimeZones();

    private static IReadOnlyList<TimeZoneOption> BuildTimeZones()
    {
        var options = new List<TimeZoneOption> { TimeZoneOption.Unknown };
        try
        {
            options.AddRange(TimeZoneInfo.GetSystemTimeZones()
                .Select(z => new TimeZoneOption(z.Id, z.DisplayName)));
        }
        catch
        {
            // A machine with an unreadable zone database still gets "not known", which is the
            // behaviour the app had before this existed.
        }
        return options;
    }

    [ObservableProperty]
    private ObservableCollection<PathElement> _activePathElements = [];

    [ObservableProperty]
    private ObservableCollection<PathElement> _availablePathElements = [];

    [ObservableProperty]
    private ObservableCollection<PathElement> _agActivePathElements = [];

    [ObservableProperty]
    private ObservableCollection<PathElement> _agAvailablePathElements = [];

    [ObservableProperty]
    private bool _isEditing;

    [ObservableProperty]
    private bool _isNew;

    [ObservableProperty]
    private bool _hasUnsavedChanges;

    /// <summary>When true, a SAS token is stored for this container; it is never shown, only replaced.</summary>
    [ObservableProperty]
    private bool _hasStoredSasToken;

    /// <summary>When true (and no stored token), the SAS token text is visible in the edit box.</summary>
    [ObservableProperty]
    private bool _showSasToken;

    [ObservableProperty]
    private string _testResult = string.Empty;

    [ObservableProperty]
    private bool _testSuccess;

    [ObservableProperty]
    private string? _sasExpiryText;

    [ObservableProperty]
    private bool _isSasExpired;

    [ObservableProperty]
    private ContainerSummary? _containerSummary;

    /// <summary>For binding BackupSourceType options in the UI.</summary>
    public static BackupSourceType[] BackupSourceTypeOptions { get; } =
        Enum.GetValues<BackupSourceType>();

    public bool IsMixedSourceType => EditBackupSourceType == BackupSourceType.Mixed;

    /// <summary>True when AG path structure section should be shown (AG or Mixed).</summary>
    public bool IsAgPathSectionVisible => EditBackupSourceType == BackupSourceType.AvailabilityGroup
        || EditBackupSourceType == BackupSourceType.Mixed;

    private const string StoredSasSentinel = "***STORED***";
    private string _originalName = "";
    private string _originalUrl = "";
    private string _originalSas = "";
    private BlobAuthMode _originalAuthMode = BlobAuthMode.SasToken;
    private string _originalPattern = "";
    private BackupSourceType _originalBackupSourceType = BackupSourceType.Standalone;
    private string? _originalAgPathPattern;

    public BlobConfigViewModel(ICredentialStore credentialStore, IBlobStorageService blobService)
    {
        _credentialStore = credentialStore;
        _blobService = blobService;
        LoadContainers();
    }

    private void LoadContainers()
    {
        var config = _credentialStore.LoadConfig();
        Containers = new ObservableCollection<BlobContainerConfig>(config.BlobContainers);

        foreach (var container in Containers)
        {
            var sas = _credentialStore.GetSasToken(container);
            if (sas != null) container.CacheSasToken(sas);
        }
    }

    /// <summary>
    /// Containers deleted on THIS screen since the last successful save (#276) - the merge in
    /// SaveContainers would otherwise resurrect them from the fresh config.
    /// </summary>
    private readonly HashSet<string> _pendingDeletes = [];

    /// <summary>
    /// Catches the list up with entries added elsewhere - Settings' import, the CLI's
    /// add-container - since this screen loaded (#276). Called on navigation, like every other
    /// screen's refresh; skipped mid-edit so the open editor's selection survives.
    /// </summary>
    public void RefreshFromConfig()
    {
        if (IsEditing || IsBusy) return;

        var config = _credentialStore.LoadConfig();
        var onDisk = config.BlobContainers.Select(c => c.Id).ToHashSet();
        if (onDisk.SetEquals(Containers.Select(c => c.Id))) return;

        var selectedId = SelectedContainer?.Id;
        LoadContainers();
        SelectedContainer = Containers.FirstOrDefault(c => c.Id == selectedId)
                            ?? Containers.FirstOrDefault();
    }

    /// <summary>
    /// Persists the container list. Returns false and sets the error when the write failed, so
    /// callers do not go on to report success - the config save used to swallow everything and
    /// the UI said "saved successfully" whether or not anything reached the disk.
    /// </summary>
    private bool SaveContainers()
    {
        try
        {
            var config = _credentialStore.LoadConfig();

            // Merge, never replace (#276). Assigning [.. Containers] deleted anything the
            // import or the CLI's add-container had written since this screen loaded. Known
            // entries come from this screen, edits included; unseen entries are kept; only
            // the ledger's local deletions are dropped.
            var mine = Containers.Where(c => c.Id != null).ToDictionary(c => c.Id!);
            var merged = new List<BlobContainerConfig>();
            foreach (var existing in config.BlobContainers)
            {
                if (existing.Id != null && _pendingDeletes.Contains(existing.Id)) continue;
                merged.Add(existing.Id != null && mine.TryGetValue(existing.Id, out var ours)
                    ? ours
                    : existing);
            }

            foreach (var container in Containers)
                if (!merged.Any(m => ReferenceEquals(m, container)))
                    merged.Add(container);

            config.BlobContainers = merged;
            _credentialStore.SaveConfig(config);
            _pendingDeletes.Clear();
            return true;
        }
        catch (Exception ex)
        {
            SetError($"Could not save configuration: {ex.Message}");
            return false;
        }
    }

    partial void OnSelectedContainerChanged(BlobContainerConfig? value)
    {
        if (value == null) return;
        UpdateSasExpiryStatus(value);
    }

    partial void OnEditNameChanged(string value) => CheckForUnsavedChanges();
    partial void OnEditContainerUrlChanged(string value)
    {
        OnPropertyChanged(nameof(IsS3Url));

        // A typed scheme decides: the URL is what gets saved, so the provider choice follows
        // it rather than arguing with it. A URL with no scheme yet moves nothing, so the
        // choice made on an empty form survives the typing that comes after it.
        if (IsS3Url) EditProvider = StorageProviderChoice.S3;
        else if (value.TrimStart().StartsWith("http", StringComparison.OrdinalIgnoreCase))
            EditProvider = StorageProviderChoice.Azure;

        CheckForUnsavedChanges();
    }

    partial void OnEditProviderChanged(StorageProviderChoice value)
    {
        OnPropertyChanged(nameof(EditIsS3));

        // An s3:// endpoint has exactly one way in - the key pair - so the Entra choices
        // vanish from the form and the mode snaps back rather than silently riding along on
        // a container that can never use it (#51).
        if (value == StorageProviderChoice.S3 && EditAuthMode != BlobAuthMode.SasToken)
            EditAuthMode = BlobAuthMode.SasToken;

        if (IsEditing) UpdateSasExpiryStatusForForm();
        CheckForUnsavedChanges();
    }
    partial void OnEditSasTokenChanged(string value) => CheckForUnsavedChanges();
    partial void OnEditS3KeyIdChanged(string value) => CheckForUnsavedChanges();
    partial void OnEditS3SecretKeyChanged(string value) => CheckForUnsavedChanges();
    partial void OnEditS3RegionChanged(string value) => CheckForUnsavedChanges();
    partial void OnEditAuthModeChanged(BlobAuthMode value)
    {
        OnPropertyChanged(nameof(IsSasAuth));
        OnPropertyChanged(nameof(IsEntraAuth));
        if (SelectedContainer != null) UpdateSasExpiryStatus(SelectedContainer);
        CheckForUnsavedChanges();
    }
    partial void OnEditPathPatternChanged(string value) => CheckForUnsavedChanges();
    partial void OnEditBackupSourceTypeChanged(BackupSourceType value)
    {
        if (IsAgPathSectionVisible && AgActivePathElements.Count == 0)
            SyncAgPathElementsFromPattern();
        CheckForUnsavedChanges();
    }
    partial void OnEditAgPathPatternChanged(string? value) => CheckForUnsavedChanges();
    partial void OnEditBackupServerTimeZoneChanged(TimeZoneOption? value) => CheckForUnsavedChanges();

    // Tags are saved like every other field, so editing only the tags has to count as an unsaved
    // change - otherwise the guard that protects unsaved edits lets them be discarded silently.
    partial void OnEditTagsChanged(string value) => CheckForUnsavedChanges();

    private void CheckForUnsavedChanges()
    {
        if (!IsEditing) return;

        // Whichever credential the form is collecting - the SAS box or the key-pair halves -
        // counts the same way: against the stored sentinel, anything entered is a change.
        var enteredSecret = EditIsS3 ? ComposedS3Pair : EditSasToken;
        var sasChanged = _originalSas == StoredSasSentinel
            ? !string.IsNullOrEmpty(enteredSecret)
            : enteredSecret != _originalSas;
        HasUnsavedChanges =
            EditName != _originalName ||
            EditContainerUrl != _originalUrl ||
            EditAuthMode != _originalAuthMode ||
            sasChanged ||
            EditS3Region != _originalS3Region ||
            EditPathPattern != _originalPattern ||
            EditBackupSourceType != _originalBackupSourceType ||
            EditAgPathPattern != _originalAgPathPattern ||
            EditTags != _originalTags ||
            EditBackupServerTimeZone?.Id != _originalTimeZoneId;
    }

    /// <summary>
    /// Captures a container's persisted fields and returns the action that puts them back. Used to
    /// undo an in-place edit when the config write is refused.
    /// </summary>
    private static Action Snapshot(BlobContainerConfig container)
    {
        var name = container.Name;
        var url = container.ContainerUrl;
        var authMode = container.AuthMode;
        var s3Region = container.S3Region;
        var pattern = container.PathPattern;
        var sourceType = container.BackupSourceType;
        var agPattern = container.AgPathPattern;
        var timeZoneId = container.BackupServerTimeZoneId;
        var tags = container.Tags.ToList();

        return () =>
        {
            container.Name = name;
            container.ContainerUrl = url;
            container.AuthMode = authMode;
            container.S3Region = s3Region;
            container.PathPattern = pattern;
            container.BackupSourceType = sourceType;
            container.AgPathPattern = agPattern;
            container.BackupServerTimeZoneId = timeZoneId;
            ReplaceTags(container.Tags, tags);
        };
    }

    private void StoreOriginalValues()
    {
        _originalName = EditName;
        _originalUrl = EditContainerUrl;
        _originalAuthMode = EditAuthMode;
        _originalSas = HasStoredSasToken ? StoredSasSentinel : EditSasToken;
        _originalS3Region = EditS3Region;
        _originalPattern = EditPathPattern;
        _originalBackupSourceType = EditBackupSourceType;
        _originalAgPathPattern = EditAgPathPattern;
        _originalTags = EditTags;
        _originalTimeZoneId = EditBackupServerTimeZone?.Id;
        HasUnsavedChanges = false;
    }

    /// <summary>Re-reads the expiry line from what the form currently says, for the edits that
    /// change what kind of credential is even in play.</summary>
    private void UpdateSasExpiryStatusForForm()
    {
        if (EditIsS3)
        {
            SasExpiryText = string.Empty;
            IsSasExpired = false;
        }
        else if (SelectedContainer != null)
        {
            UpdateSasExpiryStatus(SelectedContainer);
        }
    }

    private void UpdateSasExpiryStatus(BlobContainerConfig container)
    {
        // A key pair has no se= to read: it does not expire on a schedule the app can see, and
        // an expiry line against it would send someone hunting for a token that does not exist
        // (#51). Same reasoning as Entra below.
        if ((IsEditing && EditIsS3) || (!IsEditing && container.IsS3))
        {
            SasExpiryText = string.Empty;
            IsSasExpired = false;
            return;
        }

        // Entra has no token of ours to expire. Reporting "expired" against a container that never
        // had a SAS would send someone hunting for a token that is not the problem.
        if (EditAuthMode.IsEntra() || (!IsEditing && container.AuthMode.IsEntra()))
        {
            SasExpiryText = string.Empty;
            IsSasExpired = false;
            return;
        }

        var expiry = _credentialStore.ReadSasTokenExpiry(container);

        if (expiry.CouldNotParse)
        {
            // The token states an expiry we cannot read, so we cannot say it is still valid.
            // Showing this as "unknown" alongside everything else that is fine would be a lie of
            // omission - the restore is the wrong place to discover it (#21).
            SasExpiryText = "SAS token expiry could not be read - treat this token as expired and replace it";
            IsSasExpired = true;
        }
        else if (expiry.ExpiresAt is { } expiresAt)
        {
            IsSasExpired = expiresAt < DateTime.UtcNow;
            var remaining = expiresAt - DateTime.UtcNow;
            SasExpiryText = IsSasExpired
                ? $"SAS token expired {-remaining.TotalHours:F0}h ago"
                : $"SAS token expires in {remaining.TotalHours:F0}h ({expiresAt:yyyy-MM-dd HH:mm} UTC)";
        }
        else
        {
            // No se= at all, which is legitimate for a SAS built on a stored access policy.
            SasExpiryText = "SAS token states no expiry";
            IsSasExpired = false;
        }
    }

    private void SyncPathElementsFromPattern()
    {
        var active = PathElement.ParsePattern(EditPathPattern);
        ActivePathElements = new ObservableCollection<PathElement>(active);
        RefreshAvailableElements();
    }

    private void SyncPatternFromElements()
    {
        EditPathPattern = PathElement.BuildPattern(ActivePathElements);
    }

    private void RefreshAvailableElements()
    {
        var activeTokens = ActivePathElements.Select(e => e.Token).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var available = PathElement.AllElements.Where(e => !activeTokens.Contains(e.Token)).ToList();
        AvailablePathElements = new ObservableCollection<PathElement>(available);
    }

    [RelayCommand]
    private void AddPathElement(PathElement? element)
    {
        if (element == null) return;
        var insertIdx = ActivePathElements.Count;
        var fileNameIdx = -1;
        for (int i = 0; i < ActivePathElements.Count; i++)
        {
            if (ActivePathElements[i].Token.Equals("FileName", StringComparison.OrdinalIgnoreCase))
            {
                fileNameIdx = i;
                break;
            }
        }
        if (fileNameIdx >= 0) insertIdx = fileNameIdx;
        ActivePathElements.Insert(insertIdx, element);
        RefreshAvailableElements();
        SyncPatternFromElements();
    }

    [RelayCommand]
    private void RemovePathElement(PathElement? element)
    {
        if (element == null || element.Token.Equals("FileName", StringComparison.OrdinalIgnoreCase)) return;
        ActivePathElements.Remove(element);
        RefreshAvailableElements();
        SyncPatternFromElements();
    }

    public void MovePathElement(int fromIndex, int toIndex)
    {
        if (fromIndex < 0 || fromIndex >= ActivePathElements.Count) return;
        if (toIndex < 0 || toIndex >= ActivePathElements.Count) return;
        if (fromIndex == toIndex) return;
        ActivePathElements.Move(fromIndex, toIndex);
        SyncPatternFromElements();
    }

    private void SyncAgPathElementsFromPattern()
    {
        var pattern = string.IsNullOrWhiteSpace(EditAgPathPattern)
            ? BlobContainerConfig.DefaultPathPattern
            : EditAgPathPattern;
        var active = PathElement.ParsePattern(pattern);
        AgActivePathElements = new ObservableCollection<PathElement>(active);
        RefreshAgAvailableElements();
    }

    private void SyncAgPatternFromElements()
    {
        EditAgPathPattern = PathElement.BuildPattern(AgActivePathElements);
    }

    private void RefreshAgAvailableElements()
    {
        var activeTokens = AgActivePathElements.Select(e => e.Token).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var available = PathElement.AllElements.Where(e => !activeTokens.Contains(e.Token)).ToList();
        AgAvailablePathElements = new ObservableCollection<PathElement>(available);
    }

    [RelayCommand]
    private void AddAgPathElement(PathElement? element)
    {
        if (element == null) return;
        var insertIdx = AgActivePathElements.Count;
        var fileNameIdx = -1;
        for (int i = 0; i < AgActivePathElements.Count; i++)
        {
            if (AgActivePathElements[i].Token.Equals("FileName", StringComparison.OrdinalIgnoreCase))
            {
                fileNameIdx = i;
                break;
            }
        }
        if (fileNameIdx >= 0) insertIdx = fileNameIdx;
        AgActivePathElements.Insert(insertIdx, element);
        RefreshAgAvailableElements();
        SyncAgPatternFromElements();
    }

    [RelayCommand]
    private void RemoveAgPathElement(PathElement? element)
    {
        if (element == null || element.Token.Equals("FileName", StringComparison.OrdinalIgnoreCase)) return;
        AgActivePathElements.Remove(element);
        RefreshAgAvailableElements();
        SyncAgPatternFromElements();
    }

    public void MoveAgPathElement(int fromIndex, int toIndex)
    {
        if (fromIndex < 0 || fromIndex >= AgActivePathElements.Count) return;
        if (toIndex < 0 || toIndex >= AgActivePathElements.Count) return;
        if (fromIndex == toIndex) return;
        AgActivePathElements.Move(fromIndex, toIndex);
        SyncAgPatternFromElements();
    }

    [RelayCommand]
    private void ToggleShowSasToken()
    {
        ShowSasToken = !ShowSasToken;
    }

    [RelayCommand]
    private void AddNew()
    {
        EditName = string.Empty;
        EditTags = string.Empty;
        EditContainerUrl = string.Empty;
        EditProvider = StorageProviderChoice.Azure;
        EditAuthMode = BlobAuthMode.SasToken;
        EditSasToken = string.Empty;
        EditS3KeyId = string.Empty;
        EditS3SecretKey = string.Empty;
        EditS3Region = string.Empty;
        EditPathPattern = BlobContainerConfig.DefaultPathPattern;
        EditBackupSourceType = BackupSourceType.Standalone;
        EditAgPathPattern = null;
        EditBackupServerTimeZone = TimeZoneOption.Unknown;
        SyncPathElementsFromPattern();
        SyncAgPathElementsFromPattern();
        HasStoredSasToken = false;
        ShowSasToken = true;
        IsNew = true;
        IsEditing = true;
        TestResult = string.Empty;
        ContainerSummary = null;
        StoreOriginalValues();
    }

    [RelayCommand]
    private void Edit()
    {
        if (SelectedContainer == null) return;
        EditName = SelectedContainer.Name;
        EditTags = TagPalette.FormatTags(SelectedContainer.Tags);
        EditContainerUrl = SelectedContainer.ContainerUrl;
        EditProvider = SelectedContainer.IsS3 ? StorageProviderChoice.S3 : StorageProviderChoice.Azure;
        EditAuthMode = SelectedContainer.AuthMode;
        var storedToken = _credentialStore.GetSasToken(SelectedContainer);
        HasStoredSasToken = !string.IsNullOrEmpty(storedToken);
        EditSasToken = string.Empty; // Never show stored token; user can only replace it
        EditS3KeyId = string.Empty; // The pair is stored, never displayed - same rule as the SAS
        EditS3SecretKey = string.Empty;
        EditS3Region = SelectedContainer.S3Region ?? string.Empty;
        EditPathPattern = SelectedContainer.PathPattern;
        EditBackupSourceType = SelectedContainer.BackupSourceType;
        EditBackupServerTimeZone = TimeZoneOption.For(SelectedContainer.BackupServerTimeZoneId, TimeZones);
        EditAgPathPattern = SelectedContainer.AgPathPattern;
        SyncPathElementsFromPattern();
        SyncAgPathElementsFromPattern();
        IsNew = false;
        IsEditing = true;
        TestResult = string.Empty;
        ContainerSummary = null;
        StoreOriginalValues();
    }

    [RelayCommand]
    private void CancelEdit()
    {
        IsEditing = false;
        HasUnsavedChanges = false;
        ClearStatus();
    }

    [RelayCommand]
    private void Save()
    {
        if (string.IsNullOrWhiteSpace(EditName) || string.IsNullOrWhiteSpace(EditContainerUrl))
        {
            SetError("Name and Container URL are required.");
            return;
        }

        bool haveTokenToSave;
        if (EditIsS3)
        {
            // The URL and the pair are both shape-checked here, at entry - a malformed one
            // refused now is an authentication failure that never happens later (#51). The
            // saved truth is the URL's scheme, so the provider choice and the URL must agree
            // before anything persists.
            if (!IsS3Url)
            {
                SetError("S3-compatible storage is selected, but the URL is not an s3:// one. "
                    + "An S3 container URL is s3://endpoint[:port]/bucket.");
                return;
            }
            if (S3Url.TryParse(EditContainerUrl.TrimEnd('/')) == null)
            {
                SetError("An s3:// container URL is s3://endpoint[:port]/bucket - the endpoint "
                    + "is the provider's host name, and the first path segment is the bucket.");
                return;
            }

            var hasEither = !string.IsNullOrWhiteSpace(EditS3KeyId)
                || !string.IsNullOrWhiteSpace(EditS3SecretKey);
            haveTokenToSave = ComposedS3Pair.Length > 0;
            if (hasEither && !haveTokenToSave)
            {
                SetError("Both halves of the key pair are needed - the access key id and the "
                    + "secret key.");
                return;
            }
            if (haveTokenToSave && S3Credentials.Validate(ComposedS3Pair) is { } why)
            {
                SetError(why);
                return;
            }
            if (IsNew && !haveTokenToSave)
            {
                SetError("The access key pair is required.");
                return;
            }
        }
        else
        {
            // The mirror of the check above: an s3:// URL with Azure selected is a mismatch
            // someone should resolve, not a guess the save should make.
            if (IsS3Url)
            {
                SetError("This URL is s3:// - S3-compatible storage. Select that above, or "
                    + "paste an https:// Azure container URL.");
                return;
            }
            haveTokenToSave = IsSasAuth && !string.IsNullOrWhiteSpace(EditSasToken);
            if (IsNew && IsSasAuth && !haveTokenToSave)
            {
                SetError("SAS Token is required.");
                return;
            }
        }

        // Addressing, not a secret - and null clears a stale region when the URL stops being s3.
        var s3Region = EditIsS3 && !string.IsNullOrWhiteSpace(EditS3Region)
            ? EditS3Region.Trim()
            : null;

        BlobContainerConfig container;

        // Undoes the edit if the save is refused. Null for a new container, which is removed from
        // the list instead.
        Action? restore = null;

        if (IsNew)
        {
            if (Containers.Any(c => c.Name.Equals(EditName, StringComparison.OrdinalIgnoreCase)))
            {
                SetError("A container with this name already exists.");
                return;
            }
            var agPattern = IsAgPathSectionVisible ? PathElement.BuildPattern(AgActivePathElements) : null;
            container = new BlobContainerConfig
            {
                // Assigned here rather than defaulted on the model - see the note on Id.
                Id = BlobContainerConfig.NewId(),
                Name = EditName,
                ContainerUrl = EditContainerUrl.TrimEnd('/'),
                AuthMode = EditAuthMode,
                S3Region = s3Region,
                PathPattern = EditPathPattern,
                BackupSourceType = EditBackupSourceType,
                AgPathPattern = string.IsNullOrWhiteSpace(agPattern) ? null : agPattern.Trim(),
                BackupServerTimeZoneId = EditBackupServerTimeZone?.Id,
                Tags = [.. TagPalette.ParseTags(EditTags)]
            };
            Containers.Add(container);
        }
        else
        {
            container = SelectedContainer!;

            // Snapshot before mutating, so a refused save can put the model back. Without this an
            // edit that failed to persist left the in-memory container showing values that are not
            // on disk - and the next save writes them without the user knowing they were pending.
            restore = Snapshot(container);

            container.Name = EditName;
            // Mutate in place - see ReplaceTags.
            ReplaceTags(container.Tags, TagPalette.ParseTags(EditTags));
            container.ContainerUrl = EditContainerUrl.TrimEnd('/');
            container.AuthMode = EditAuthMode;
            container.S3Region = s3Region;
            container.PathPattern = EditPathPattern;
            container.BackupSourceType = EditBackupSourceType;
            container.BackupServerTimeZoneId = EditBackupServerTimeZone?.Id;
            var agPattern = IsAgPathSectionVisible ? PathElement.BuildPattern(AgActivePathElements) : null;
            container.AgPathPattern = string.IsNullOrWhiteSpace(agPattern) ? null : agPattern.Trim();
        }

        // Config FIRST, secret second.
        //
        // The other order wrote a durable secret and then discovered the config could not be
        // saved. Change a name and a token together, have config.json briefly locked, and the
        // Credential Manager ends up holding the NEW token under a key the OLD config points at -
        // or, for a rename, under a name nothing references. The form never shows a stored secret,
        // so nothing on screen reveals the mismatch. Delete already follows this rule.
        if (!SaveContainers())
        {
            // Nothing reached the disk, so do not leave the list showing a container that is not
            // really there. Stay in the edit form so the save can be retried once whatever was
            // holding the file has let go.
            if (IsNew) Containers.Remove(container);
            else restore?.Invoke();
            return;
        }

        // When editing and leaving SAS field empty, existing token is kept (never re-read or shown)
        if (haveTokenToSave)
        {
            try
            {
                // For an s3:// container the two halves leave the form as the one string the
                // engine's CREATE CREDENTIAL wants - the same slot the SAS occupies (#51).
                _credentialStore.SaveSasToken(container, EditIsS3 ? ComposedS3Pair : EditSasToken);
            }
            catch (Exception ex)
            {
                // The config is saved and consistent; only the token is missing. Say exactly that,
                // because "save failed" would send the user looking for the wrong problem.
                SetError($"The container was saved, but the SAS token could not be stored: {ex.Message}");
                return;
            }
        }
        else if (IsEntraAuth)
        {
            // Switching to Entra leaves no reason to keep the SAS token, and an organisation that
            // has banned long-lived SAS has banned it wherever it is sitting - including in this
            // machine's Credential Manager. After the config write, for the same reason the save
            // itself is ordered that way.
            _credentialStore.DeleteSecret(container.CredentialKey);
            HasStoredSasToken = false;
        }

        SelectedContainer = container;
        IsEditing = false;
        HasUnsavedChanges = false;
        UpdateSasExpiryStatus(container);
        SetStatus("Container saved successfully.");
    }

    [RelayCommand]
    private void Delete()
    {
        if (SelectedContainer == null) return;

        // Deleting takes the SAS token out of Credential Manager, and the token is never displayed
        // anywhere in the app - so if it was the only copy, it is gone for good. That deserves a
        // question rather than a single click (#42).
        var confirm = MessageBox.Show(
            $"Remove the container \"{SelectedContainer.Name}\"?\n\n" +
            "Its stored credential - the SAS token or access key pair - will be deleted from " +
            "Windows Credential Manager. The app never displays stored credentials, so if this " +
            "is the only copy you will need to obtain a new one.\n\n" +
            "Nothing in the container itself is affected - no backups are deleted.",
            "Nine Lives", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);

        if (confirm != MessageBoxResult.Yes) return;

        // Remove from the config first and only destroy the secret once that write has actually
        // landed. The other order threw the SAS token away and then, if the save failed, left the
        // container in config.json pointing at a credential that no longer exists.
        var removed = SelectedContainer;
        Containers.Remove(removed);
        if (removed.Id != null) _pendingDeletes.Add(removed.Id);
        if (!SaveContainers())
        {
            Containers.Add(removed);
            if (removed.Id != null) _pendingDeletes.Remove(removed.Id);
            SelectedContainer = removed;
            return;
        }

        _credentialStore.DeleteSecret(removed.CredentialKey);
        SelectedContainer = Containers.FirstOrDefault();
        SetStatus("Container removed.");
    }

    [RelayCommand]
    private void RefreshToken()
    {
        if (SelectedContainer == null) return;
        EditName = SelectedContainer.Name;
        EditContainerUrl = SelectedContainer.ContainerUrl;
        EditProvider = SelectedContainer.IsS3 ? StorageProviderChoice.S3 : StorageProviderChoice.Azure;
        HasStoredSasToken = true; // Still have a token; user will replace it
        EditSasToken = string.Empty;
        EditS3KeyId = string.Empty;
        EditS3SecretKey = string.Empty;
        EditS3Region = SelectedContainer.S3Region ?? string.Empty;
        EditPathPattern = SelectedContainer.PathPattern;
        EditBackupServerTimeZone = TimeZoneOption.For(SelectedContainer.BackupServerTimeZoneId, TimeZones);
        EditBackupSourceType = SelectedContainer.BackupSourceType;
        EditAgPathPattern = SelectedContainer.AgPathPattern;

        // Tags too. Save writes whatever is in this box over the container's tags, so leaving it
        // empty here deleted every tag on any container whose token was refreshed - silently, and
        // with no undo. Refresh Token is a separate button from Edit, so replacing an expired
        // token was the natural way to hit it.
        EditTags = TagPalette.FormatTags(SelectedContainer.Tags);

        SyncPathElementsFromPattern();
        SyncAgPathElementsFromPattern();
        IsNew = false;
        IsEditing = true;
        StoreOriginalValues();
        SetStatus("Enter the new SAS token below to replace the existing one.");
    }

    [RelayCommand]
    private async Task TestConnectionAsync()
    {
        BlobContainerConfig? config;
        if (IsEditing)
        {
            if (EditIsS3)
            {
                // Built from the form, so what is tested is exactly what is on screen - the
                // URL and region included, which matters when the region is the thing being
                // fixed. The pair comes from the form when entered, else the stored one; in
                // memory only either way, same rule as the SAS below (#12).
                if (S3Url.TryParse(EditContainerUrl.TrimEnd('/')) == null)
                {
                    TestSuccess = false;
                    TestResult = "An s3:// container URL is s3://endpoint[:port]/bucket - "
                        + "fix the URL and test again.";
                    return;
                }

                var pair = ComposedS3Pair.Length > 0
                    ? ComposedS3Pair
                    : SelectedContainer != null ? _credentialStore.GetSasToken(SelectedContainer) : null;

                if (ComposedS3Pair.Length > 0 && S3Credentials.Validate(ComposedS3Pair) is { } why)
                {
                    TestSuccess = false;
                    TestResult = why;
                    return;
                }

                config = new BlobContainerConfig
                {
                    Name = EditName,
                    ContainerUrl = EditContainerUrl,
                    UnsavedSasToken = string.IsNullOrEmpty(pair) ? null : pair,
                    S3Region = string.IsNullOrWhiteSpace(EditS3Region) ? null : EditS3Region.Trim()
                };
            }
            else if (IsEntraAuth)
            {
                // Nothing stored to fall back on, and nothing needed - the token comes from the
                // signed-in account. Built from the form, so what is tested is the URL on screen.
                //
                // Carrying the mode is the whole point: without it this fell through to the SAS
                // branch below and refused with "No SAS token found" for a container that is never
                // going to have one (#29).
                config = new BlobContainerConfig
                {
                    Name = EditName,
                    ContainerUrl = EditContainerUrl,
                    AuthMode = EditAuthMode
                };
            }
            else if (HasStoredSasToken && string.IsNullOrWhiteSpace(EditSasToken))
                config = SelectedContainer; // Use stored token for test
            else
            {
                // In memory only. This used to call SaveSasToken, which is a durable write to
                // Credential Manager - so pasting a typo'd or expired token, testing it, and
                // clicking Cancel destroyed the working token that was there before, with no way
                // to get it back because the form never displays stored tokens (#12).
                config = new BlobContainerConfig
                {
                    Name = EditName,
                    ContainerUrl = EditContainerUrl,
                    UnsavedSasToken = string.IsNullOrWhiteSpace(EditSasToken) ? null : EditSasToken
                };
            }
        }
        else
            config = SelectedContainer;

        if (config == null) return;

        // Test Connection sounds quick, but it enumerates the whole container to build the summary
        // - 4000+ blobs on a real one - so it needs the same escape as the other listings (#25).
        var ct = _testCancellation.Begin();
        IsBusy = true;
        CanCancelTest = true;
        TestResult = string.Empty;
        try
        {
            await _blobService.VerifyConnectionAsync(config, ct);
            var files = await _blobService.ListBackupFilesAsync(config, ct);
            var summary = _blobService.GetContainerSummary(files);
            ContainerSummary = summary;
            TestSuccess = true;
            TestResult = $"Connected! {summary.TotalFiles} files found ({summary.TotalSizeDisplay})";
        }
        catch (OperationCanceledException)
        {
            TestSuccess = false;
            TestResult = "Cancelled.";
            ContainerSummary = null;
        }
        catch (Exception ex)
        {
            TestSuccess = false;
            ContainerSummary = null;

            // Azure's own message is accurate and useless in equal measure - a permission failure
            // arrives with a request id, the original XML and eleven headers, and says nothing
            // about what to change. Same instinct as the recovery guidance after a failed restore
            // (#14): the moment it fails is the moment to say what to do about it.
            TestResult = await ExplainFailureAsync(ex, config, ct);
        }
        finally
        {
            _testCancellation.End();
            IsBusy = false;
            CanCancelTest = false;
        }
    }

    /// <summary>
    /// The failure, then who was refused, then what to do about it.
    ///
    /// The identity comes first among the details because it is the fact that settles the most
    /// common confusion: a permission error against an account that demonstrably has access
    /// usually means a different account was used than the one being thought about.
    /// </summary>
    private async Task<string> ExplainFailureAsync(
        Exception ex, BlobContainerConfig config, CancellationToken ct)
    {
        var message = new StringBuilder($"Connection failed: {FirstLine(ex.Message)}");

        if (config.AuthMode.IsEntra())
        {
            string? identity = null;
            try
            {
                identity = await _blobService.DescribeSignedInIdentityAsync(config, ct);
            }
            catch
            {
                // Explaining a failure must not produce one.
            }

            if (identity != null)
                message.Append($"{Environment.NewLine}{Environment.NewLine}Signed in as: {identity}");
        }

        var guidance = BlobFailureExplainer.Explain(ex, config);
        if (guidance != null)
            message.Append($"{Environment.NewLine}{Environment.NewLine}{guidance}");

        return message.ToString();
    }

    /// <summary>
    /// Azure appends the request id, the timestamp, the original XML and every response header to
    /// its exception message. The first line is the part a human reads; the rest buries it.
    /// </summary>
    private static string FirstLine(string message)
    {
        var end = message.IndexOfAny(['\r', '\n']);
        return end < 0 ? message : message[..end].TrimEnd();
    }

    /// <summary>Stops an in-progress connection test.</summary>
    [RelayCommand]
    private void CancelTest()
    {
        _testCancellation.Cancel();
        CanCancelTest = false;
        TestResult = "Cancelling...";
    }
}
