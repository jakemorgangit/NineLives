using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Blackcat.NineLives.Models;
using Blackcat.NineLives.Services;

namespace Blackcat.NineLives.ViewModels;

public partial class BlobBrowserViewModel : ViewModelBase
{
    private readonly IBlobStorageService _blobService;
    private readonly ISqlServerService _sqlService;
    private readonly ICredentialStore _credentialStore;
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

    public BlobBrowserViewModel(
        IBlobStorageService blobService,
        ISqlServerService sqlService,
        ICredentialStore credentialStore)
    {
        _blobService = blobService;
        _sqlService = sqlService;
        _credentialStore = credentialStore;
        RefreshContainers();
    }

    /// <summary>
    /// Where to look (#197).
    ///
    /// This screen could only ever see a container, while backing up and restoring have both taken
    /// either medium since #165 - so the one screen whose whole purpose is LOOKING was the one that
    /// could not look at half of what the app writes.
    /// </summary>
    [ObservableProperty]
    private BackupMedium _selectedMedium = BackupMedium.AzureBlob;

    public bool MediumIsBlob => SelectedMedium == BackupMedium.AzureBlob;
    public bool MediumIsSharedPath => SelectedMedium == BackupMedium.SharedPath;

    partial void OnSelectedMediumChanged(BackupMedium value)
    {
        OnPropertyChanged(nameof(MediumIsBlob));
        OnPropertyChanged(nameof(MediumIsSharedPath));

        // What is on screen came from the other source and no longer belongs to what is selected
        // above it. Leaving it there would put a container's files under a server's name.
        Clear();
    }

    /// <summary>
    /// The instances whose backup history can be read.
    ///
    /// A share is NOT browsed by walking a directory: a folder of .bak files says nothing about
    /// which database each belongs to, what type it is, or which full a differential was taken
    /// against - inferring that from filenames is the whole reason #130 exists. The instance that
    /// TOOK the backups recorded all of it, so that is what gets read.
    /// </summary>
    [ObservableProperty]
    private ObservableCollection<ServerConnection> _servers = [];

    [ObservableProperty]
    private ServerConnection? _sourceServer;

    partial void OnSourceServerChanged(ServerConnection? value) => Clear();

    public void RefreshContainers()
    {
        var previous = SelectedContainer?.Name;
        var previousServer = SourceServer?.Id;
        var config = _credentialStore.LoadConfig();
        Containers = new ObservableCollection<BlobContainerConfig>(config.BlobContainers);

        Servers = new ObservableCollection<ServerConnection>(config.Servers);
        if (previousServer != null)
            SourceServer = Servers.FirstOrDefault(s => s.Id == previousServer);
        if (SourceServer == null && Servers.Count > 0)
            SourceServer = Servers[0];
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

    partial void OnSelectedContainerChanged(BlobContainerConfig? value) => Clear();

    /// <summary>
    /// Empties the screen. Called whenever the SOURCE changes, whichever part of it changed.
    ///
    /// One method rather than three copies: what is listed belongs to the thing named above it, and
    /// a screen that keeps one source's files under another's name is worse than an empty one.
    /// </summary>
    private void Clear()
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
        if (MediumIsBlob && SelectedContainer == null)
        {
            SetError("Please select a container first.");
            return;
        }

        if (MediumIsSharedPath && SourceServer == null)
        {
            SetError("Please select a server first.");
            return;
        }

        var ct = _loadCancellation.Begin();
        IsBusy = true;
        CanCancelLoad = true;
        ClearStatus();
        try
        {
            if (MediumIsSharedPath)
            {
                await LoadFromHistoryAsync(ct);
                return;
            }

            _allFiles = await _blobService.ListBackupFilesAsync(SelectedContainer!, ct);
            _allSets = _blobService.GroupIntoBackupSets(
                _allFiles, SelectedContainer?.BackupServerTimeZoneId);
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
    /// Reads what an instance recorded backing up (#197).
    ///
    /// The same two calls the restore screen makes, because it is the same question - what backups
    /// exist and how do they group into sets. No path mapping is applied: nothing is being restored
    /// here, so the paths worth showing are the ones the source actually wrote.
    /// </summary>
    private async Task LoadFromHistoryAsync(CancellationToken ct)
    {
        var source = SourceServer!;
        var history = await _sqlService.ReadBackupHistoryAsync(source, null, ct);
        var sets = BackupHistoryInventory.ToSets(history, BackupPathMapping.None);

        _allSets = sets;
        _allFiles = sets.SelectMany(s => s.Files).ToList();
        HasFiles = _allFiles.Count > 0;

        // Built from the sets rather than from filenames: msdb recorded each backup's database and
        // server, so there is nothing here to infer.
        DiscoveredServers = new ObservableCollection<string>(
            sets.Select(s => s.ServerDisplay)
                .Where(n => !string.IsNullOrEmpty(n))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)!);

        DiscoveredDatabases = new ObservableCollection<string>(
            sets.Select(s => s.DatabaseName)
                .Where(n => !string.IsNullOrEmpty(n))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)!);

        if (DiscoveredServers.Count > 0)
            SelectedServer = DiscoveredServers[0];
        else if (DiscoveredDatabases.Count > 0)
            SelectedDatabase = DiscoveredDatabases[0];

        ApplyFilter();
        UpdateFilteredSummary();

        SetStatus(sets.Count == 0
            ? $"{source.ServerName} has no backup history."
            : $"Read {_allFiles.Count} file(s) in {sets.Count} backup set(s) from "
              + $"{source.ServerName} across {DiscoveredDatabases.Count} database(s).");
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

            // Sets whose filename carried no timestamp fall back to the blob's LastModified, which
            // is UTC rather than the backup server's clock - so the range they bound is only as
            // good as that skew. Say so rather than presenting one exact-looking window.
            var approximate = Summary.ApproximateSets > 0
                ? $"  |  {Summary.ApproximateSets} approximate"
                : string.Empty;

            DbSummaryText = $"{Summary.FullBackups} Full, {Summary.DiffBackups} Diff, {Summary.LogBackups} Log sets  |  {earliest:yyyy-MM-dd} to {latest:yyyy-MM-dd}{approximate}";
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

    // The ?? SelectedFile is the one real difference between the two screens and is kept: here the
    // command is also reachable from a toolbar button with no row passed, where falling back to the
    // selection is what makes it work at all.
    [RelayCommand]
    private void CopyPathHttps(BackupFileInfo? file)
        => CopyBlobHttpsPath(file ?? SelectedFile, SelectedContainer);

    [RelayCommand]
    private void CopyPathContainer(BackupFileInfo? file)
        => CopyBlobContainerPath(file ?? SelectedFile, SelectedContainer);

    /// <summary>
    /// The path a file on disk actually has (#197).
    ///
    /// The two blob copies mean nothing here - there is no URL and no container - and offering them
    /// against a file read from msdb would put an empty string on the clipboard, which is worse
    /// than offering nothing.
    /// </summary>
    [RelayCommand]
    private void CopyLocalPath(BackupFileInfo? file)
    {
        var target = file ?? SelectedFile;
        if (target?.IsOnDisk != true) return;

        Clipboard.SetText(target.RestoreDevice);
        SetStatus("Path copied to clipboard.");
    }
}
