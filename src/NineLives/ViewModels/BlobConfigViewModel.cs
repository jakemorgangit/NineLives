using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Blackcat.NineLives.Models;
using Blackcat.NineLives.Services;

namespace Blackcat.NineLives.ViewModels;

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

    [ObservableProperty]
    private string _editPathPattern = "{BackupType}/{ServerName}/{DatabaseName}/{FileName}";

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
    /// Persists the container list. Returns false and sets the error when the write failed, so
    /// callers do not go on to report success - the config save used to swallow everything and
    /// the UI said "saved successfully" whether or not anything reached the disk.
    /// </summary>
    private bool SaveContainers()
    {
        try
        {
            var config = _credentialStore.LoadConfig();
            config.BlobContainers = [.. Containers];
            _credentialStore.SaveConfig(config);
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
    partial void OnEditContainerUrlChanged(string value) => CheckForUnsavedChanges();
    partial void OnEditSasTokenChanged(string value) => CheckForUnsavedChanges();
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
        var sasChanged = _originalSas == StoredSasSentinel
            ? !string.IsNullOrEmpty(EditSasToken)
            : EditSasToken != _originalSas;
        HasUnsavedChanges =
            EditName != _originalName ||
            EditContainerUrl != _originalUrl ||
            EditAuthMode != _originalAuthMode ||
            sasChanged ||
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
        _originalPattern = EditPathPattern;
        _originalBackupSourceType = EditBackupSourceType;
        _originalAgPathPattern = EditAgPathPattern;
        _originalTags = EditTags;
        _originalTimeZoneId = EditBackupServerTimeZone?.Id;
        HasUnsavedChanges = false;
    }

    private void UpdateSasExpiryStatus(BlobContainerConfig container)
    {
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
            ? "{BackupType}/{ServerName}/{DatabaseName}/{FileName}"
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
        EditAuthMode = BlobAuthMode.SasToken;
        EditSasToken = string.Empty;
        EditPathPattern = "{BackupType}/{ServerName}/{DatabaseName}/{FileName}";
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
        EditAuthMode = SelectedContainer.AuthMode;
        var storedToken = _credentialStore.GetSasToken(SelectedContainer);
        HasStoredSasToken = !string.IsNullOrEmpty(storedToken);
        EditSasToken = string.Empty; // Never show stored token; user can only replace it
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

        bool haveTokenToSave = IsSasAuth && !string.IsNullOrWhiteSpace(EditSasToken);
        if (IsNew && IsSasAuth && !haveTokenToSave)
        {
            SetError("SAS Token is required.");
            return;
        }

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
                _credentialStore.SaveSasToken(container, EditSasToken);
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
            "Its stored SAS token will be deleted from Windows Credential Manager. The app never " +
            "displays stored tokens, so if this is the only copy you will need to obtain a new one.\n\n" +
            "Nothing in Azure is affected - no backups are deleted.",
            "Nine Lives", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);

        if (confirm != MessageBoxResult.Yes) return;

        // Remove from the config first and only destroy the secret once that write has actually
        // landed. The other order threw the SAS token away and then, if the save failed, left the
        // container in config.json pointing at a credential that no longer exists.
        var removed = SelectedContainer;
        Containers.Remove(removed);
        if (!SaveContainers())
        {
            Containers.Add(removed);
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
        HasStoredSasToken = true; // Still have a token; user will replace it
        EditSasToken = string.Empty;
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
            if (IsEntraAuth)
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
            TestResult = $"Connection failed: {ex.Message}";
            ContainerSummary = null;
        }
        finally
        {
            _testCancellation.End();
            IsBusy = false;
            CanCancelTest = false;
        }
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
