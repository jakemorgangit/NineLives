using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Blackcat.NineLives.Models;
using Blackcat.NineLives.Services;

namespace Blackcat.NineLives.ViewModels;

public partial class BlobBrowserViewModel : ViewModelBase
{
    private readonly BlobStorageService _blobService;
    private readonly CredentialStore _credentialStore;
    private readonly OperationCancellation _loadCancellation = new();

    /// <summary>True while a listing is running and has not been asked to stop (#25).</summary>
    [ObservableProperty]
    private bool _canCancelLoad;

    private List<BackupFileInfo> _allFiles = [];
    private List<BackupSet> _allSets = [];

    [ObservableProperty]
    private ObservableCollection<BlobContainerConfig> _containers = [];

    [ObservableProperty]
    private BlobContainerConfig? _selectedContainer;

    [ObservableProperty]
    private ObservableCollection<BackupFileInfo> _filteredFiles = [];

    [ObservableProperty]
    private BackupFileInfo? _selectedFile;

    [ObservableProperty]
    private string _filterText = string.Empty;

    [ObservableProperty]
    private bool _showFull = true;

    [ObservableProperty]
    private bool _showDiff = true;

    [ObservableProperty]
    private bool _showLog = true;

    [ObservableProperty]
    private bool _showUnknown = true;

    [ObservableProperty]
    private ContainerSummary? _summary;

    [ObservableProperty]
    private bool _hasFiles;

    [ObservableProperty]
    private ObservableCollection<string> _discoveredServers = [];

    [ObservableProperty]
    private string? _selectedServer;

    [ObservableProperty]
    private ObservableCollection<string> _discoveredDatabases = [];

    [ObservableProperty]
    private string? _selectedDatabase;

    [ObservableProperty]
    private string _dbSummaryText = string.Empty;

    public BlobBrowserViewModel(BlobStorageService blobService, CredentialStore credentialStore)
    {
        _blobService = blobService;
        _credentialStore = credentialStore;
        RefreshContainers();
    }

    public void RefreshContainers()
    {
        var previous = SelectedContainer?.Name;
        var config = _credentialStore.LoadConfig();
        Containers = new ObservableCollection<BlobContainerConfig>(config.BlobContainers);
        foreach (var c in Containers)
        {
            var sas = _credentialStore.GetSasToken(c);
            if (sas != null) c.CacheSasToken(sas);
        }

        if (previous != null)
            SelectedContainer = Containers.FirstOrDefault(c => c.Name == previous);
        if (SelectedContainer == null && Containers.Count > 0)
            SelectedContainer = Containers[0];
    }

    partial void OnSelectedContainerChanged(BlobContainerConfig? value)
    {
        _allFiles.Clear();
        _allSets.Clear();
        FilteredFiles.Clear();
        Summary = null;
        HasFiles = false;
        DiscoveredServers.Clear();
        SelectedServer = null;
        DiscoveredDatabases.Clear();
        SelectedDatabase = null;
        DbSummaryText = string.Empty;
    }

    partial void OnFilterTextChanged(string value) => ApplyFilter();
    partial void OnShowFullChanged(bool value) => ApplyFilter();
    partial void OnShowDiffChanged(bool value) => ApplyFilter();
    partial void OnShowLogChanged(bool value) => ApplyFilter();
    partial void OnShowUnknownChanged(bool value) => ApplyFilter();

    partial void OnSelectedServerChanged(string? value)
    {
        RefreshDatabasesForServer();
        ApplyFilter();
        UpdateFilteredSummary();
    }

    partial void OnSelectedDatabaseChanged(string? value)
    {
        ApplyFilter();
        UpdateFilteredSummary();
    }

    [RelayCommand]
    private void Refresh()
    {
        RefreshContainers();
    }

    [RelayCommand]
    private async Task LoadBackupsAsync()
    {
        if (SelectedContainer == null)
        {
            SetError("Please select a container first.");
            return;
        }

        var ct = _loadCancellation.Begin();
        IsBusy = true;
        CanCancelLoad = true;
        ClearStatus();
        try
        {
            _allFiles = await _blobService.ListBackupFilesAsync(SelectedContainer, ct);
            _allSets = _blobService.GroupIntoBackupSets(_allFiles);
            HasFiles = _allFiles.Count > 0;

            var servers = _blobService.GetDiscoveredServers(_allFiles);
            DiscoveredServers = new ObservableCollection<string>(servers);

            var dbs = _blobService.GetDiscoveredDatabases(_allFiles);
            DiscoveredDatabases = new ObservableCollection<string>(dbs);

            if (DiscoveredServers.Count > 0)
                SelectedServer = DiscoveredServers[0];
            else if (DiscoveredDatabases.Count > 0)
                SelectedDatabase = DiscoveredDatabases[0];

            ApplyFilter();
            UpdateFilteredSummary();
            SetStatus($"Loaded {_allFiles.Count} files in {_allSets.Count} backup set(s) across {dbs.Count} database(s).");
        }
        catch (OperationCanceledException)
        {
            // Listing is read-only, so there is nothing to undo or explain - just say it stopped.
            SetStatus("Loading cancelled.");
            HasFiles = false;
        }
        catch (Exception ex)
        {
            SetError($"Failed to load backups: {ex.Message}");
            HasFiles = false;
        }
        finally
        {
            _loadCancellation.End();
            IsBusy = false;
            CanCancelLoad = false;
        }
    }

    /// <summary>
    /// Stops an in-progress listing. A container with a large number of blobs is enumerated a page
    /// at a time with no natural end, and until now there was no way out of it (#25).
    /// </summary>
    [RelayCommand]
    private void CancelLoad()
    {
        _loadCancellation.Cancel();
        CanCancelLoad = false;
        SetStatus("Cancelling...");
    }

    private void RefreshDatabasesForServer()
    {
        var files = _allFiles.AsEnumerable();
        if (!string.IsNullOrEmpty(SelectedServer))
            files = files.Where(f => MatchesServer(f, SelectedServer));

        var dbs = files
            .Where(f => !string.IsNullOrEmpty(f.InferredDatabaseName))
            .Select(f => f.InferredDatabaseName!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();

        DiscoveredDatabases = new ObservableCollection<string>(dbs);
        if (DiscoveredDatabases.Count > 0)
            SelectedDatabase = DiscoveredDatabases[0];
        else
            SelectedDatabase = null;
    }

    private void ApplyFilter()
    {
        var filtered = _allFiles.AsEnumerable();

        if (!string.IsNullOrEmpty(SelectedServer))
            filtered = filtered.Where(f => MatchesServer(f, SelectedServer));

        if (!string.IsNullOrEmpty(SelectedDatabase))
            filtered = filtered.Where(f =>
                string.Equals(f.InferredDatabaseName, SelectedDatabase, StringComparison.OrdinalIgnoreCase));

        if (!ShowFull) filtered = filtered.Where(f => f.Type != BackupType.Full);
        if (!ShowDiff) filtered = filtered.Where(f => f.Type != BackupType.Differential);
        if (!ShowLog) filtered = filtered.Where(f => f.Type != BackupType.TransactionLog);
        if (!ShowUnknown) filtered = filtered.Where(f => f.Type != BackupType.Unknown);

        if (!string.IsNullOrWhiteSpace(FilterText))
        {
            filtered = filtered.Where(f =>
                f.BlobName.Contains(FilterText, StringComparison.OrdinalIgnoreCase) ||
                (f.InferredDatabaseName?.Contains(FilterText, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (f.InferredServerName?.Contains(FilterText, StringComparison.OrdinalIgnoreCase) ?? false));
        }

        FilteredFiles = new ObservableCollection<BackupFileInfo>(filtered);
    }

    private void UpdateFilteredSummary()
    {
        var sets = _allSets.AsEnumerable();

        if (!string.IsNullOrEmpty(SelectedServer))
            sets = sets.Where(s => MatchesServerSet(s, SelectedServer));

        if (!string.IsNullOrEmpty(SelectedDatabase))
            sets = sets.Where(s =>
                string.Equals(s.DatabaseName, SelectedDatabase, StringComparison.OrdinalIgnoreCase));

        var filtered = sets.ToList();
        Summary = _blobService.GetSetBasedSummary(filtered);

        if (filtered.Count > 0)
        {
            var earliest = filtered.Min(s => s.Timestamp);
            var latest = filtered.Max(s => s.Timestamp);
            DbSummaryText = $"{Summary.FullBackups} Full, {Summary.DiffBackups} Diff, {Summary.LogBackups} Log sets  |  {earliest:yyyy-MM-dd} to {latest:yyyy-MM-dd}";
        }
        else
        {
            DbSummaryText = string.Empty;
        }
    }

    // Both delegate to the shared identity comparison. MatchesServerSet previously had a
    // parts.Length == 2 branch that was byte-identical to its fallback and never compared the
    // instance, so set-based summary counts silently included other instances of the same host.
    private static bool MatchesServer(BackupFileInfo file, string serverFilter)
        => file.MatchesServer(serverFilter);

    private static bool MatchesServerSet(BackupSet set, string serverFilter)
        => set.MatchesServer(serverFilter);

    [RelayCommand]
    private void CopyPathHttps(BackupFileInfo? file)
    {
        var target = file ?? SelectedFile;
        if (target == null || SelectedContainer == null) return;
        TryCopyToClipboard(
            BlobStorageService.BuildBlobUrl(SelectedContainer, target.BlobName),
            "HTTPS path copied to clipboard (no SAS token).");
    }

    [RelayCommand]
    private void CopyPathContainer(BackupFileInfo? file)
    {
        var target = file ?? SelectedFile;
        if (target == null || SelectedContainer == null) return;
        var containerName = SelectedContainer.ContainerName ?? "container";
        TryCopyToClipboard($"{containerName}/{target.BlobName}", "Container path copied to clipboard.");
    }
}
