using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Blackcat.NineLives.Models;
using Blackcat.NineLives.Services;

namespace Blackcat.NineLives.ViewModels;

public partial class BlobConfigViewModel : ViewModelBase
{
    private readonly CredentialStore _credentialStore;
    private readonly BlobStorageService _blobService;

    [ObservableProperty]
    private ObservableCollection<BlobContainerConfig> _containers = [];

    [ObservableProperty]
    private BlobContainerConfig? _selectedContainer;

    [ObservableProperty]
    private string _editName = string.Empty;

    /// <summary>Comma-separated tag list as typed by the user; parsed on save.</summary>
    [ObservableProperty]
    private string _editTags = string.Empty;

    [ObservableProperty]
    private string _editContainerUrl = string.Empty;

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
    private string _originalPattern = "";
    private BackupSourceType _originalBackupSourceType = BackupSourceType.Standalone;
    private string? _originalAgPathPattern;

    public BlobConfigViewModel(CredentialStore credentialStore, BlobStorageService blobService)
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

    private void SaveContainers()
    {
        var config = _credentialStore.LoadConfig();
        config.BlobContainers = [.. Containers];
        _credentialStore.SaveConfig(config);
    }

    partial void OnSelectedContainerChanged(BlobContainerConfig? value)
    {
        if (value == null) return;
        UpdateSasExpiryStatus(value);
    }

    partial void OnEditNameChanged(string value) => CheckForUnsavedChanges();
    partial void OnEditContainerUrlChanged(string value) => CheckForUnsavedChanges();
    partial void OnEditSasTokenChanged(string value) => CheckForUnsavedChanges();
    partial void OnEditPathPatternChanged(string value) => CheckForUnsavedChanges();
    partial void OnEditBackupSourceTypeChanged(BackupSourceType value)
    {
        if (IsAgPathSectionVisible && AgActivePathElements.Count == 0)
            SyncAgPathElementsFromPattern();
        CheckForUnsavedChanges();
    }
    partial void OnEditAgPathPatternChanged(string? value) => CheckForUnsavedChanges();

    private void CheckForUnsavedChanges()
    {
        if (!IsEditing) return;
        var sasChanged = _originalSas == StoredSasSentinel
            ? !string.IsNullOrEmpty(EditSasToken)
            : EditSasToken != _originalSas;
        HasUnsavedChanges =
            EditName != _originalName ||
            EditContainerUrl != _originalUrl ||
            sasChanged ||
            EditPathPattern != _originalPattern ||
            EditBackupSourceType != _originalBackupSourceType ||
            EditAgPathPattern != _originalAgPathPattern;
    }

    private void StoreOriginalValues()
    {
        _originalName = EditName;
        _originalUrl = EditContainerUrl;
        _originalSas = HasStoredSasToken ? StoredSasSentinel : EditSasToken;
        _originalPattern = EditPathPattern;
        _originalBackupSourceType = EditBackupSourceType;
        _originalAgPathPattern = EditAgPathPattern;
        HasUnsavedChanges = false;
    }

    private void UpdateSasExpiryStatus(BlobContainerConfig container)
    {
        var expiry = _credentialStore.GetSasTokenExpiry(container);
        if (expiry.HasValue)
        {
            IsSasExpired = expiry.Value < DateTime.UtcNow;
            var remaining = expiry.Value - DateTime.UtcNow;
            SasExpiryText = IsSasExpired
                ? $"SAS token expired {-remaining.TotalHours:F0}h ago"
                : $"SAS token expires in {remaining.TotalHours:F0}h ({expiry.Value:yyyy-MM-dd HH:mm} UTC)";
        }
        else
        {
            SasExpiryText = "SAS token expiry unknown";
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
        EditSasToken = string.Empty;
        EditPathPattern = "{BackupType}/{ServerName}/{DatabaseName}/{FileName}";
        EditBackupSourceType = BackupSourceType.Standalone;
        EditAgPathPattern = null;
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
        var storedToken = _credentialStore.GetSasToken(SelectedContainer);
        HasStoredSasToken = !string.IsNullOrEmpty(storedToken);
        EditSasToken = string.Empty; // Never show stored token; user can only replace it
        EditPathPattern = SelectedContainer.PathPattern;
        EditBackupSourceType = SelectedContainer.BackupSourceType;
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

        bool haveTokenToSave = !string.IsNullOrWhiteSpace(EditSasToken);
        if (IsNew && !haveTokenToSave)
        {
            SetError("SAS Token is required.");
            return;
        }

        BlobContainerConfig container;
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
                Name = EditName,
                ContainerUrl = EditContainerUrl.TrimEnd('/'),
                PathPattern = EditPathPattern,
                BackupSourceType = EditBackupSourceType,
                AgPathPattern = string.IsNullOrWhiteSpace(agPattern) ? null : agPattern.Trim(),
                Tags = TagPalette.ParseTags(EditTags)
            };
            Containers.Add(container);
        }
        else
        {
            container = SelectedContainer!;
            container.Name = EditName;
            container.Tags = TagPalette.ParseTags(EditTags);
            container.ContainerUrl = EditContainerUrl.TrimEnd('/');
            container.PathPattern = EditPathPattern;
            container.BackupSourceType = EditBackupSourceType;
            var agPattern = IsAgPathSectionVisible ? PathElement.BuildPattern(AgActivePathElements) : null;
            container.AgPathPattern = string.IsNullOrWhiteSpace(agPattern) ? null : agPattern.Trim();
        }

        if (haveTokenToSave)
            _credentialStore.SaveSasToken(container, EditSasToken);
        // When editing and leaving SAS field empty, existing token is kept (never re-read or shown)
        SaveContainers();

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
        _credentialStore.DeleteSecret(SelectedContainer.CredentialKey);
        Containers.Remove(SelectedContainer);
        SaveContainers();
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
        EditBackupSourceType = SelectedContainer.BackupSourceType;
        EditAgPathPattern = SelectedContainer.AgPathPattern;
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
            if (HasStoredSasToken && string.IsNullOrWhiteSpace(EditSasToken))
                config = SelectedContainer; // Use stored token for test
            else
            {
                config = new BlobContainerConfig { Name = EditName, ContainerUrl = EditContainerUrl };
                if (!string.IsNullOrWhiteSpace(EditSasToken))
                    _credentialStore.SaveSasToken(config, EditSasToken);
            }
        }
        else
            config = SelectedContainer;

        if (config == null) return;

        IsBusy = true;
        TestResult = string.Empty;
        try
        {
            await _blobService.VerifyConnectionAsync(config);
            var files = await _blobService.ListBackupFilesAsync(config);
            var summary = _blobService.GetContainerSummary(files);
            ContainerSummary = summary;
            TestSuccess = true;
            TestResult = $"Connected! {summary.TotalFiles} files found ({summary.TotalSizeDisplay})";
        }
        catch (Exception ex)
        {
            TestSuccess = false;
            TestResult = $"Connection failed: {ex.Message}";
            ContainerSummary = null;
        }
        finally
        {
            IsBusy = false;
        }
    }
}
