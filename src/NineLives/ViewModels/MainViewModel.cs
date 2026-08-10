using System.Diagnostics;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Blackcat.NineLives.Models;
using Blackcat.NineLives.Services;

namespace Blackcat.NineLives.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    private readonly ICredentialStore _credentialStore;
    private readonly IBlobStorageService _blobService;
    private readonly ISqlServerService _sqlService;
    private readonly BackupChainBuilder _chainBuilder;
    private readonly RestoreScriptGenerator _scriptGenerator;
    private readonly IRestoreHistoryStore _historyStore;

    [ObservableProperty]
    private ViewModelBase? _currentView;

    [ObservableProperty]
    // The sidebar's selection is bound to this, so it also decides which button is highlighted at
    // startup - it used to be a hardcoded IsChecked="True" on the first one (#117 item 5).
    private string _currentViewName = Nav.BlobStorage;

    /// <summary>How much of the app is on screen (#176).</summary>
    [ObservableProperty]
    private AppMode _mode = AppMode.Pro;

    /// <summary>True while the mode cards are up, which is once, on first run.</summary>
    [ObservableProperty]
    private bool _isChoosingMode;

    /// <summary>
    /// How much room the sidebar's COLUMN takes.
    ///
    /// Collapsing the sidebar itself is not enough: a ColumnDefinition keeps its width whatever
    /// happens to the element inside it, so hiding the sidebar left 220px of nothing on the left
    /// and pushed the mode cards - which centre inside the remaining column - visibly off to the
    /// right of the window (#191).
    /// </summary>
    public GridLength SidebarWidth => IsChoosingMode ? new GridLength(0) : new GridLength(220);

    partial void OnIsChoosingModeChanged(bool value) => OnPropertyChanged(nameof(SidebarWidth));

    // Each of these is asked by the navigation and by the screens themselves, so they live here
    // rather than being recomputed from Mode at every binding.
    public bool ShowBackup => AppModeCapabilities.CanBackUp(Mode);
    public bool ShowCopyDatabase => AppModeCapabilities.CanCopyBetweenServers(Mode);
    public bool ShowBrowseBackups => AppModeCapabilities.CanBrowseBackups(Mode);

    partial void OnModeChanged(AppMode value)
    {
        OnPropertyChanged(nameof(ShowBackup));
        OnPropertyChanged(nameof(ShowCopyDatabase));
        OnPropertyChanged(nameof(ShowBrowseBackups));

        Restore.Mode = value;
        Backup.Mode = value;
        Settings.CurrentMode = value;

        // A screen that has just been hidden must not stay on display underneath a sidebar that no
        // longer offers it - which is what changing mode from Settings would otherwise do.
        if (!IsViewAvailable(CurrentViewName)) NavigateTo(Nav.Restore);
    }

    private void OnModeChosen(AppMode mode)
    {
        Mode = mode;
        IsChoosingMode = false;

        // Restore rather than back to Settings, deliberately: the point of changing the mode is the
        // shape of the app, and landing on the settings page you came from shows none of it.
        NavigateTo(Nav.Restore);
    }

    /// <summary>
    /// Puts the cards back up from Settings (#191).
    ///
    /// They used to be shown once and then reduced to a drop-down of three names, which is the part
    /// of them worth the least - what each mode turns ON is written on the cards.
    /// </summary>
    private void ShowModeCards() => ShowModeCards(Nav.Settings);

    private void ShowModeCards(string returnTo)
    {
        _modeCardsReturnTo = returnTo;
        ModeSelection.ShowCurrent(Mode);
        IsChoosingMode = true;
        CurrentView = ModeSelection;
    }

    /// <summary>
    /// Where "Keep what I have" goes back to.
    ///
    /// Depends on why the cards are up: from Settings, back to Settings; from a launch, on into the
    /// app. Sending somebody who has just started the app to the settings page would be a stranger
    /// place to land than the one they were heading for.
    /// </summary>
    private string _modeCardsReturnTo = Nav.Restore;

    private void OnModeCardsCancelled()
    {
        IsChoosingMode = false;
        NavigateTo(_modeCardsReturnTo);
    }

    /// <summary>
    /// Where carrying on from the launch cards lands (#211): the last session's screen when it is
    /// still a screen this mode offers, Restore otherwise. Restore rather than the recorded
    /// screen's nearest cousin, because a guess about intent is worse than the app's centre.
    /// </summary>
    internal string LandingScreen(string? lastScreen) =>
        lastScreen != null && Nav.Views.Contains(lastScreen) && IsViewAvailable(lastScreen)
            ? lastScreen
            : Nav.Restore;

    /// <summary>
    /// Files everything the next launch wants back: where the window was, and which screen was in
    /// use (#211). Called once, at close - geometry changes are not worth a disk write each.
    ///
    /// Silent on failure, deliberately: the app is shutting down, and there is nobody left to act
    /// on an error about remembering window positions. The store itself refuses to save over a
    /// config that failed to load (#7), and that refusal is respected here too.
    /// </summary>
    public void SaveShutdownState(WindowGeometry geometry)
    {
        try
        {
            var config = _credentialStore.LoadConfig();
            if (config.LoadError != null) return;

            config.Window = geometry;
            // Closing on the launch mode-cards leaves the remembered screen alone (#290): the
            // cards are up at every start, so writing null here wiped #211's memory whenever
            // the app was closed without clicking through - and the next launch forgot where
            // its owner worked. No choice made means nothing to record, not "record nothing".
            if (!IsChoosingMode) config.LastScreen = CurrentViewName;
            _credentialStore.SaveConfig(config);
        }
        catch
        {
            // Shutdown. Nothing useful can be done with a failure here.
        }
    }

    /// <summary>The geometry the last session saved, for the window to apply before first paint.</summary>
    public WindowGeometry? SavedGeometry { get; private set; }

    /// <summary>
    /// The dashboard answers "how exposed am I?" - arriving at an empty screen that needs a
    /// button press first is the screen failing its own question. First visit sweeps
    /// automatically; after that Refresh is deliberate, because a sweep touches every server.
    /// </summary>
    private ExposureViewModel SweepExposureOnFirstVisit()
    {
        if (Exposure.Rows.Count == 0 && !Exposure.IsSweeping)
            _ = Exposure.RefreshCommand.ExecuteAsync(null);

        return Exposure;
    }

    /// <summary>Whether a sidebar entry is offered in the current mode.</summary>
    public bool IsViewAvailable(string viewName) => viewName switch
    {
        Nav.Backup => ShowBackup,
        Nav.CopyDatabase => ShowCopyDatabase,
        Nav.BrowseBackups => ShowBrowseBackups,
        // The same tier as browsing: read-only insight over the estate's backups.
        Nav.Exposure => ShowBrowseBackups,
        _ => true
    };

    [ObservableProperty]
    private string _globalStatus = "Ready";

    [ObservableProperty]
    private bool _isConnectedToSql;

    [ObservableProperty]
    private string _connectedServerName = "Not connected";

    [ObservableProperty]
    private bool _updateAvailable;

    [ObservableProperty]
    private string _updateMessage = string.Empty;

    private string _updateUrl = UpdateChecker.ReleasesPage;
    private string? _updateTag;

    /// <summary>
    /// Shown in the status bar. Read from the assembly so it cannot go stale.
    /// Not named AppVersion - that would shadow the class of the same name inside this type.
    /// </summary>
    public string VersionText => Services.AppVersion.Display;

    public BlobConfigViewModel BlobConfig { get; }
    public ServerManagerViewModel ServerManager { get; }
    public BlobBrowserViewModel BlobBrowser { get; }
    public ModeSelectionViewModel ModeSelection { get; }
    public BackupViewModel Backup { get; }
    public CopyDatabaseViewModel CopyDatabase { get; }
    public RestoreViewModel Restore { get; }
    public HistoryViewModel History { get; }
    public ExposureViewModel Exposure { get; }
    public SettingsViewModel Settings { get; }
    public AboutViewModel About { get; }

    /// <summary>
    /// The app's one composition point: builds the real services against the real credential
    /// store. Deliberately not a DI container - three services and one place that wires them does
    /// not need one (#41).
    /// </summary>
    public MainViewModel() : this(new CredentialStore()) { }

    /// <summary>
    /// Takes the store so a test can point the whole object graph at a temp directory instead of
    /// the user's actual profile. Constructing this type used to migrate and read the real
    /// %LOCALAPPDATA% config as a side effect of a XAML load test.
    /// </summary>
    public MainViewModel(ICredentialStore credentialStore)
    {
        _credentialStore = credentialStore;

        // Before the child viewmodels build any views: applying a palette afterwards works, since
        // every colour is a DynamicResource, but doing it first means the first frame drawn is
        // already the right colour rather than a flash of dark on the way to light.
        ApplySavedTheme();

        // Before anything reads the config: move secrets from name-derived keys onto stable ids
        // (#8). Must happen ahead of the child viewmodels, which load containers and servers in
        // their constructors and would otherwise look up keys the migration is about to change.
        MigrateSecretKeys();

        _blobService = new BlobStorageService(_credentialStore);
        _sqlService = new SqlServerService(_credentialStore);
        _chainBuilder = new BackupChainBuilder();
        _scriptGenerator = new RestoreScriptGenerator();

        BlobConfig = new BlobConfigViewModel(_credentialStore, _blobService);
        ServerManager = new ServerManagerViewModel(_credentialStore, _sqlService);
        BlobBrowser = new BlobBrowserViewModel(_blobService, _sqlService, _credentialStore);
        // One store, shared: the Restore screen writes to it and the History screen reads it back,
        // and two instances pointed at the same file would be a way to lose an entry.
        _historyStore = new RestoreHistoryStore();

        // One notifier for every screen that runs things (#242) - reads the endpoint list fresh
        // per event, so a webhook added in Settings works without a restart.
        var notifier = new WebhookRunNotifier(_credentialStore, App.Log);

        Restore = new RestoreViewModel(
            _blobService, _sqlService, _chainBuilder, _scriptGenerator, _credentialStore,
            log: null, history: _historyStore, notifier: notifier);
        ModeSelection = new ModeSelectionViewModel(_credentialStore);
        ModeSelection.Chosen += OnModeChosen;
        ModeSelection.Cancelled += OnModeCardsCancelled;

        Backup = new BackupViewModel(_credentialStore, _sqlService, notifier: notifier);
        CopyDatabase = new CopyDatabaseViewModel(_credentialStore, _sqlService, notifier: notifier);
        History = new HistoryViewModel(_historyStore);
        Exposure = new ExposureViewModel(_credentialStore, _sqlService, _historyStore);
        Settings = new SettingsViewModel(_credentialStore);
        About = new AboutViewModel();

        // After the config is loaded, not at startup in App: pruning first and reading the setting
        // afterwards would delete files the user had asked to keep, using the default of 30 to
        // decide (#117 item 2).
        App.Log.RetentionDays = Settings.LogRetentionDays;
        App.Log.Prune();

        ServerManager.ConnectionChanged += OnSqlConnectionChanged;

        // From looking to restoring in one move (#202). Navigation first, so the load's progress
        // happens on the screen the user is now watching rather than behind the one they left.
        BlobBrowser.RestoreRequested += handoff =>
        {
            NavigateTo(Nav.Restore);
            _ = Restore.AcceptHandoffAsync(handoff);
        };

        // The dashboard's rows get the same one-click path (#202's pattern): a red row's
        // distance to being acted on is one click, or the screen is guilt rather than a to-do.
        Exposure.RestoreRequested += handoff =>
        {
            NavigateTo(Nav.Restore);
            _ = Restore.AcceptHandoffAsync(handoff);
        };

        // Changing the mode from Settings has to move the app immediately - a sidebar that still
        // offers a screen the new mode hides, or a screen left on display underneath one that no
        // longer lists it, is worse than not offering the setting (#176).
        Settings.ShowModeCardsRequested += ShowModeCards;

        // One place that answers "is it doing something?". Every screen already tracks its own
        // work; without this the answer depended on which part of a long page was scrolled into
        // view, and the Restore screen's own status line is below the fold most of the time (#128).
        WatchForBusy(BlobConfig, ServerManager, BlobBrowser, Backup, Restore, CopyDatabase);

        // How much of the app to show (#176).
        //
        // An unreadable or unrecognised value lands on Pro rather than Basic: hiding features from
        // somebody whose config got mangled is a worse failure than showing too many, because the
        // second is untidy and the first looks like the app has lost a capability.
        var config = _credentialStore.LoadConfig();
        var savedMode = config.Mode;
        Mode = savedMode ?? AppMode.Pro;

        // The window applies this before first paint (#211) - read here so the window never
        // touches the store itself.
        SavedGeometry = config.Window;

        if (savedMode == null)
        {
            // First run. Nothing else is shown until the question is answered - a sidebar behind a
            // choice about what the sidebar contains would be answering it twice - and there is no
            // way past it, because leaving would mean an app that has not been told how much of
            // itself to show.
            IsChoosingMode = true;
            CurrentView = ModeSelection;
        }
        else
        {
            // The cards are the landing screen, not a first-run gate (#200).
            //
            // It reads better than opening on the container list: the first thing on screen says
            // what shape the app is in and offers to change it, rather than the app simply being
            // that shape with no indication why. It is only a question the first time - after that
            // the current mode is marked and "Keep what I have" carries straight on.
            //
            // Straight on to where the last session WAS (#211), or to Restore - the same place
            // choosing a mode lands (#209) - when nothing is recorded or the mode no longer
            // offers it. Carrying on means carrying on.
            ShowModeCards(LandingScreen(config.LastScreen));
        }

        // Fire and forget - startup must not wait on the network.
        _ = CheckForUpdatesAsync();
    }

    /// <summary>What the app is currently doing, or empty when it is idle.</summary>
    [ObservableProperty]
    private string _busyText = string.Empty;

    [ObservableProperty]
    private bool _isBusy;

    private readonly List<ViewModelBase> _watched = [];

    private void WatchForBusy(params ViewModelBase[] children)
    {
        foreach (var child in children)
        {
            _watched.Add(child);
            child.PropertyChanged += (_, _) => RefreshBusy();
        }
    }

    /// <summary>
    /// The Restore screen names what it is doing; the others only say they are busy, and what that
    /// means is obvious from the screen. Restore wins when more than one is going, because it is
    /// the one that can be running a RESTORE.
    /// </summary>
    private void RefreshBusy()
    {
        var text =
            Restore.IsBusyWithAnything ? Restore.BusyDescription
            : BlobConfig.IsBusy ? "Testing the container connection..."
            : BlobBrowser.IsBusy ? "Listing the container..."
            : ServerManager.IsBusy ? "Connecting to SQL Server..."
            : string.Empty;

        if (text == BusyText) return;

        BusyText = text;
        IsBusy = text.Length > 0;
    }

    /// <summary>
    /// Puts the remembered colour scheme back. Never fatal: a theme that will not load leaves the
    /// default in place, which is a cosmetic problem rather than one worth refusing to start over.
    /// </summary>
    private void ApplySavedTheme()
    {
        try
        {
            ThemeManager.Apply(_credentialStore.LoadConfig().Theme);
        }
        catch
        {
            // Dark it is.
        }
    }

    /// <summary>
    /// One-off relocation of stored secrets onto stable ids. A no-op on every launch after the
    /// first, and never fatal - if it cannot complete, the old keys are untouched and everything
    /// still works, so the app starts normally and tries again next time.
    /// </summary>
    private void MigrateSecretKeys()
    {
        try
        {
            var config = _credentialStore.LoadConfig();
            var result = new ConfigMigrator(_credentialStore).Migrate(config);

            if (result.Error != null)
                GlobalStatus = $"Stored credentials could not be updated: {result.Error}";
            else if (result.DidWork)
                GlobalStatus = "Ready";
        }
        catch
        {
            // Startup must not depend on this.
        }
    }

    private async Task CheckForUpdatesAsync()
    {
        try
        {
            var config = _credentialStore.LoadConfig();
            if (!config.CheckForUpdates) return;

            var latest = await new UpdateChecker().FetchLatestAsync();
            if (!UpdateChecker.ShouldNotify(
                    AppVersion.Current, latest, config.LastNotifiedReleaseTag, config.CheckForUpdates))
                return;

            _updateUrl = latest!.Url;
            _updateTag = latest.Tag;
            UpdateMessage = $"Nine Lives {latest.Tag} is available. You have {AppVersion.Display}.";
            UpdateAvailable = true;
        }
        catch
        {
            // Never surface an update-check failure. The app works fine without it.
        }
    }

    /// <summary>Opens the releases page. The app downloads nothing itself.</summary>
    [RelayCommand]
    private void OpenReleasesPage()
    {
        try
        {
            Process.Start(new ProcessStartInfo(_updateUrl) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            GlobalStatus = $"Could not open browser: {ex.Message}";
        }

        DismissUpdate();
    }

    /// <summary>Hides the banner and remembers the tag so it is not shown again.</summary>
    [RelayCommand]
    private void DismissUpdate()
    {
        UpdateAvailable = false;

        if (string.IsNullOrWhiteSpace(_updateTag)) return;

        try
        {
            var config = _credentialStore.LoadConfig();
            config.LastNotifiedReleaseTag = _updateTag;
            _credentialStore.SaveConfig(config);
        }
        catch
        {
            // Worst case the banner shows again next launch.
        }
    }

    private void OnSqlConnectionChanged(object? sender, ServerConnectionChangedEventArgs e)
    {
        IsConnectedToSql = e.IsConnected;
        ConnectedServerName = e.IsConnected ? e.ServerName : "Not connected";
        Restore.IsConnectedToServer = e.IsConnected;
        Restore.ConnectedServerName = e.ServerName;
        // Setting ConnectedServer re-checks the credential itself (#115 seam 5).
        Restore.ConnectedServer = e.ConnectedServer;
        GlobalStatus = e.IsConnected ? $"Connected to {e.ServerName}" : "Ready";
    }

    [RelayCommand]
    private void DisconnectSql()
    {
        ServerManager.DisconnectCommand.Execute(null);
    }

    // ── keyboard (#117 item 5) ──────────────────────────────────────────────────
    //
    // The shortcuts are declared on the window, but what they DO is decided here, because a key
    // that reaches the whole app has to mean something sensible on every screen. F5 bound straight
    // to the Restore screen's loader would run a blob listing while somebody was reading About -
    // no visible effect, real network traffic, and a status line changing on a page that has none.
    //
    // Each of these routes to the current screen and does nothing where it has no meaning, rather
    // than being disabled globally because one screen cannot use it.

    /// <summary>F5: reload whatever the current screen lists.</summary>
    [RelayCommand]
    private void Reload()
    {
        switch (CurrentViewName)
        {
            case Nav.Restore:
                if (Restore.LoadBackupsCommand.CanExecute(null)) Restore.LoadBackupsCommand.Execute(null);
                break;
            case Nav.BrowseBackups:
                BlobBrowser.RefreshContainers();
                break;
            case Nav.History:
                History.Refresh();
                break;
        }
    }


    /// <summary>
    /// Esc: stop whatever is running.
    ///
    /// Not gated on the current screen. A restore or a verify keeps running while somebody
    /// navigates away, and the whole point of a stop key is that it works when you reach for it.
    /// Ordered by cost of letting it continue: an execute is writing to a database, a query is
    /// only reading, a load is only listing.
    /// </summary>
    [RelayCommand]
    private void CancelCurrent()
    {
        if (Restore.Execution.CancelCommand.CanExecute(null)) { Restore.Execution.CancelCommand.Execute(null); return; }
        if (Restore.CancelQueryCommand.CanExecute(null)) { Restore.CancelQueryCommand.Execute(null); return; }
        if (Restore.CancelLoadCommand.CanExecute(null)) Restore.CancelLoadCommand.Execute(null);
    }

    /// <summary>
    /// The sidebar's view names. Constants rather than literals scattered across the switch,
    /// because an unrecognised name here silently lands on Blob Storage rather than failing -
    /// so a typo looks like a button that goes to the wrong place (#42).
    ///
    /// The XAML still passes them as strings; <see cref="Views"/> is what a test can check the
    /// switch against.
    /// </summary>
    public static class Nav
    {
        public const string BlobStorage = "Blob Storage";
        public const string SqlServers = "SQL Servers";
        public const string BrowseBackups = "Browse Backups";
        public const string Exposure = "Exposure";
        public const string Backup = "Back Up";
        public const string Restore = "Restore";
        public const string CopyDatabase = "Copy Database";
        public const string History = "History";
        public const string Settings = "Settings";
        public const string About = "About";

        public static IReadOnlyList<string> Views =>
            [BlobStorage, SqlServers, BrowseBackups, Exposure, Backup, Restore, CopyDatabase, History, Settings, About];
    }

    [RelayCommand]
    private void NavigateTo(string viewName)
    {
        CurrentViewName = viewName;
        CurrentView = viewName switch
        {
            Nav.BlobStorage => BlobConfig,
            Nav.SqlServers => ServerManager,
            Nav.BrowseBackups => BlobBrowser,
            Nav.Exposure => SweepExposureOnFirstVisit(),
            Nav.Backup => Backup,
            Nav.Restore => Restore,
            Nav.CopyDatabase => CopyDatabase,
            Nav.History => History,
            Nav.Settings => Settings,
            Nav.About => About,
            _ => BlobConfig
        };

        // Refresh container lists when navigating to views that depend on them
        if (viewName is Nav.BrowseBackups)
            BlobBrowser.RefreshContainers();
        else if (viewName is Nav.Backup)
            // Servers AND containers: a backup needs one of each, and either can have been added
            // on another screen since the last visit.
            Backup.Refresh();
        else if (viewName is Nav.CopyDatabase)
            // Two servers and a container here, for the same reason.
            CopyDatabase.Refresh();
        else if (viewName is Nav.Restore)
            // Containers AND servers: either can be the backup source now, and one added on
            // another screen since the last visit has to be selectable here (#165).
            Restore.RefreshContainers();
        else if (viewName is Nav.History)
            // Re-read on every visit: a restore run since the last look must be here, and the
            // history is written by the Restore screen rather than by this one.
            History.Refresh();
        else if (viewName is Nav.SqlServers)
            // Catches up with servers the import or the CLI's add-server wrote since the last
            // visit (#276) - without this, the screen's next save wrote its stale list over them.
            ServerManager.RefreshFromConfig();
        else if (viewName is Nav.BlobStorage)
            // Same for containers, same reason (#276).
            BlobConfig.RefreshFromConfig();
    }
}

public class ServerConnectionChangedEventArgs : EventArgs
{
    public bool IsConnected { get; init; }
    public string ServerName { get; init; } = string.Empty;
    public ServerConnection? ConnectedServer { get; init; }
}
