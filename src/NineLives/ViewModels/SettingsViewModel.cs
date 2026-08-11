using System.Diagnostics;
using Blackcat.NineLives.Models;
using System.IO;
using System.Windows;
using Blackcat.NineLives.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Blackcat.NineLives.ViewModels;

/// <summary>
/// The application's own settings, in one place (#117 item 2).
///
/// They were scattered or unreachable: the theme picker went into About because About was the only
/// page that was not a workflow step, the update check existed only in config.json, and log
/// retention was a constant in the source. About is a credits page again.
///
/// Everything here is global. The per-container backup server time zone (#102) is NOT here even
/// though the issue listed it, because it is a property OF a container - one machine can hold
/// backups from several servers on different clocks - so it stays on the container editor where
/// the container it describes is.
/// </summary>
public partial class SettingsViewModel : ViewModelBase
{
    private readonly ICredentialStore _credentialStore;
    private readonly OperationLog _log;

    // ── notifications (#242) ────────────────────────────────────────────────────

    /// <summary>The rows on screen. Each edits its model object; Save persists the lot.</summary>
    public System.Collections.ObjectModel.ObservableCollection<WebhookEndpointViewModel> Webhooks { get; } = [];

    /// <summary>What the last webhook test said, per the whole card - "sent" or the error.</summary>
    [ObservableProperty]
    private string _webhookTestResult = string.Empty;

    private void LoadWebhooks(AppConfig config)
    {
        Webhooks.Clear();
        foreach (var endpoint in config.Webhooks)
            Webhooks.Add(new WebhookEndpointViewModel(
                endpoint, _credentialStore, SaveWebhooks, TestWebhookAsync));

        var proxy = config.WebhookProxy;
        ProxyMode = proxy?.Mode ?? WebhookProxyMode.SystemDefault;
        ProxyUrl = proxy?.Url ?? string.Empty;
        ProxyUsername = proxy?.Username ?? string.Empty;
    }

    // ── how deliveries reach the network (#316) ────────────────────────────────

    public IReadOnlyList<WebhookProxyMode> ProxyModes { get; } =
        [WebhookProxyMode.SystemDefault, WebhookProxyMode.Direct, WebhookProxyMode.Custom];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsProxyDetails))]
    private WebhookProxyMode _proxyMode = WebhookProxyMode.SystemDefault;

    public bool ShowsProxyDetails => ProxyMode == WebhookProxyMode.Custom;

    [ObservableProperty]
    private string _proxyUrl = string.Empty;

    [ObservableProperty]
    private string _proxyUsername = string.Empty;

    /// <summary>Typed, saved, cleared - the password is never read back out of the vault.</summary>
    [ObservableProperty]
    private string _proxyPasswordInput = string.Empty;

    /// <summary>
    /// Commits the delivery route (#316). The password goes to Windows Credential Manager and
    /// the input clears; the mode, URL and username go to the config file - they are
    /// addressing, not secrets.
    /// </summary>
    [RelayCommand]
    private void SaveProxy()
    {
        Save(config =>
        {
            config.WebhookProxy = new WebhookProxySettings
            {
                Mode = ProxyMode,
                Url = string.IsNullOrWhiteSpace(ProxyUrl) ? null : ProxyUrl.Trim(),
                Username = string.IsNullOrWhiteSpace(ProxyUsername) ? null : ProxyUsername.Trim()
            };
        }, "The delivery route could not be saved");

        if (!string.IsNullOrWhiteSpace(ProxyPasswordInput))
        {
            _credentialStore.SaveSecret(
                WebhookTransport.ProxyCredentialKey, ProxyUsername, ProxyPasswordInput);
            ProxyPasswordInput = string.Empty;
        }

        WebhookTestResult = "Delivery route saved. The next send - test or real - uses it.";
    }

    [RelayCommand]
    private void AddWebhook()
    {
        var endpoint = new WebhookEndpoint { Name = $"Endpoint {Webhooks.Count + 1}" };
        Webhooks.Add(new WebhookEndpointViewModel(
            endpoint, _credentialStore, SaveWebhooks, TestWebhookAsync));
        SaveWebhooks();
    }

    [RelayCommand]
    private void RemoveWebhook(WebhookEndpointViewModel? row)
    {
        if (row == null) return;

        // A webhook URL is a secret this app never displays back - most carry their own token
        // in the path - so removing one destroys the only copy it holds, on a single click, with
        // nothing to undo with (#370). The same question the container and server lists ask.
        var confirm = MessageBox.Show(
            $"Remove the webhook \"{row.Name}\"?\n\n" +
            "Its URL is stored in Windows Credential Manager and never displayed, so if this is " +
            "the only copy you will need the original to add it back.\n\n" +
            "Runs will stop being announced to it. Nothing else changes.",
            "Nine Lives", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);

        if (confirm != MessageBoxResult.Yes) return;

        Webhooks.Remove(row);
        SaveWebhooks();
    }

    /// <summary>The whole list, rewritten from what is on screen - additive edits, no merging.</summary>
    private void SaveWebhooks() =>
        Save(config =>
        {
            config.Webhooks = Webhooks.Select(w => w.Model).ToList();
        }, "The notification settings could not be saved");

    private async Task TestWebhookAsync(WebhookEndpointViewModel row)
    {
        WebhookTestResult = $"Sending a test to {row.Name}...";

        // Same transport and same hydration as a real delivery (#316, #317): a test that
        // bypasses the proxy or reads a different URL proves nothing about the run.
        var url = WebhookTransport.ResolveUrl(row.Model, _credentialStore);
        if (url == null)
        {
            WebhookTestResult = $"{row.Name}: no URL saved yet.";
            return;
        }

        var hydrated = WebhookTransport.HydrateUsable([row.Model], _credentialStore).Single();
        using var handler = WebhookTransport.BuildHandler(
            _credentialStore.LoadConfig().WebhookProxy, _credentialStore);
        var error = await new WebhookNotifier(handler).SendTestAsync(hydrated);
        WebhookTransport.RecordDelivery(_credentialStore, row.Model.Id, error);
        row.NoteDelivery(error);
        WebhookTestResult = DescribeTestOutcome(row.Name, error, row.Model);
    }

    /// <summary>
    /// The sentence the test button produces (#292). The natural way to pause an endpoint is
    /// unticking all three moments - there is no Enabled flag - and a plain "test sent" then
    /// promised a channel that would stay silent forever.
    /// </summary>
    internal static string DescribeTestOutcome(string name, string? error, WebhookEndpoint model)
    {
        if (error != null) return $"{name}: {error}";

        var listens = model.NotifyStarted || model.NotifyFinished || model.NotifyProblems;
        return listens
            ? $"Test sent to {name} - check the channel."
            : $"Test sent to {name} - but it is subscribed to nothing (Started, Finished and " +
              "Problems are all off), so it will never fire during a run.";
    }

    // ── moving machines (#213) ──────────────────────────────────────────────────

    /// <summary>
    /// Writes the configuration to a file that holds shapes rather than secrets - SAS tokens and
    /// SQL passwords live in Windows Credential Manager and stay there, and a SAS pasted into a
    /// container URL is stripped on the way out.
    /// </summary>
    [RelayCommand]
    private void ExportConfig()
    {
        try
        {
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Export configuration (no secrets)",
                FileName = $"NineLives-config-{DateTime.Now:yyyyMMdd}.json",
                Filter = "JSON configuration|*.json",
                DefaultExt = ".json"
            };

            if (dialog.ShowDialog() != true) return;

            var config = _credentialStore.LoadConfig();
            System.IO.File.WriteAllText(dialog.FileName, ConfigPortability.Export(config));

            SetStatus($"Exported to {dialog.FileName}. The file holds no secrets - credentials " +
                      "are re-entered on the importing machine.");
            _log.Info($"[config] exported to {dialog.FileName}");
        }
        catch (Exception ex)
        {
            SetError($"Export failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Folds an exported file in: adds what is new, updates what matches, never deletes -
    /// an import is additive or it is a trap.
    /// </summary>
    [RelayCommand]
    private void ImportConfig()
    {
        try
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Import configuration",
                Filter = "JSON configuration|*.json|All files|*.*"
            };

            if (dialog.ShowDialog() != true) return;

            var imported = ConfigPortability.Read(System.IO.File.ReadAllText(dialog.FileName));
            if (imported == null)
            {
                SetError("That file is not a Nine Lives configuration export - or it came " +
                         "from a newer version of the app than this one.");
                return;
            }

            var summary = ImportFrom(imported);
            if (summary == null) return;

            SetStatus(summary.Describe() + " Imported entries appear on the servers and " +
                      "containers screens on their next visit.");
            _log.Info($"[config] imported from {dialog.FileName}: {summary.Describe()}");
        }
        catch (Exception ex)
        {
            SetError($"Import failed: {ex.Message}");
        }
    }

    /// <summary>
    /// The import minus the file dialog, so the merge-and-reload contract is testable (#277).
    /// </summary>
    internal ImportSummary? ImportFrom(ExportedConfig imported)
    {
        var config = _credentialStore.LoadConfig();
        if (config.LoadError != null)
        {
            SetError($"The local configuration could not be read, so nothing was imported: {config.LoadError}");
            return null;
        }

        var summary = ConfigPortability.Merge(config, imported);
        _credentialStore.SaveConfig(config);

        // The on-screen webhook list catches up NOW, not on restart: the next edit to any row
        // rewrites the whole webhook list from screen state, and a stale list would erase
        // exactly what was just imported (#277).
        LoadWebhooks(config);
        return summary;
    }

    /// <summary>True while the constructor is filling the properties from config.</summary>
    private readonly bool _loading;

    /// <summary>Set while putting the retention box back, so the revert does not
    /// re-enter its own handler and ask again (#370).</summary>
    private bool _revertingRetention;

    public SettingsViewModel(ICredentialStore credentialStore, OperationLog? log = null)
    {
        _credentialStore = credentialStore;
        _log = log ?? App.Log;

        _loading = true;
        try
        {
            LoadWebhooks(_credentialStore.LoadConfig());
            var config = _credentialStore.LoadConfig();
            _selectedTheme = ThemeManager.Current;
            _currentMode = config.Mode ?? AppMode.Pro;
            _checkForUpdates = config.CheckForUpdates;
            _logRetentionDays = config.LogRetentionDays;
        }
        catch
        {
            // A config that will not load is reported where it is loaded for real. Here it just
            // means the screen shows defaults rather than failing to open at all.
            _selectedTheme = ThemeManager.Current;
            _checkForUpdates = true;
            _logRetentionDays = OperationLog.DefaultRetentionDays;
        }
        finally
        {
            _loading = false;
        }
    }

    // ── how much of the app to show (#176) ──────────────────────────────────────

    /// <summary>
    /// The mode in force. Kept in step by the shell, which owns it.
    ///
    /// Changing it is NOT done here: it reopens the cards, which is the screen that says what each
    /// mode actually turns on. A drop-down of three names could only ever say what they were
    /// called (#191).
    /// </summary>
    [ObservableProperty]
    private AppMode _currentMode = AppMode.Pro;

    public string CurrentModeTitle => AppModeCapabilities.Title(CurrentMode);
    public string CurrentModeTagline => AppModeCapabilities.Tagline(CurrentMode);

    partial void OnCurrentModeChanged(AppMode value)
    {
        OnPropertyChanged(nameof(CurrentModeTitle));
        OnPropertyChanged(nameof(CurrentModeTagline));
    }

    /// <summary>Raised so the shell can put the cards back up.</summary>
    public event Action? ShowModeCardsRequested;

    [RelayCommand]
    private void ShowModeCards() => ShowModeCardsRequested?.Invoke();

    // ── appearance ──────────────────────────────────────────────────────────────

    /// <summary>One entry per theme, named for the picker.</summary>
    public IReadOnlyList<ThemeOption> Themes { get; } =
        ThemeManager.All.Select(t => new ThemeOption(t, ThemeManager.DisplayName(t))).ToList();

    [ObservableProperty]
    private AppTheme _selectedTheme;

    /// <summary>
    /// Applies the theme immediately and remembers it.
    ///
    /// Applying first: a theme that will not load is worth knowing about right away, and the
    /// switch is instant because every colour is a DynamicResource. Saving is best-effort - a
    /// config that refuses to write should not undo a change the user can already see.
    /// </summary>
    partial void OnSelectedThemeChanged(AppTheme value)
    {
        if (_loading) return;

        if (!ThemeManager.Apply(value))
        {
            SetError("The theme could not be applied.");
            return;
        }

        Save(config => config.Theme = value, "The theme was applied, but could not be saved for next time");
    }

    // ── updates ─────────────────────────────────────────────────────────────────

    [ObservableProperty]
    private bool _checkForUpdates;

    partial void OnCheckForUpdatesChanged(bool value)
    {
        if (_loading) return;
        Save(config => config.CheckForUpdates = value, "The update setting could not be saved");
    }

    // ── logs ────────────────────────────────────────────────────────────────────

    /// <summary>Shown so the path can be read even if opening the folder fails.</summary>
    public string LogFolder => _log.Directory;

    [ObservableProperty]
    private int _logRetentionDays;

    /// <summary>
    /// Applied to the live log as well as saved, so a shortened retention takes effect now rather
    /// than at next startup - somebody reducing it on a shared machine means it, and telling them
    /// to restart the app for that is the kind of thing that gets skipped.
    /// </summary>
    partial void OnLogRetentionDaysChanged(int value)
    {
        if (_loading || _revertingRetention) return;

        // Asked before anything is deleted, and only when something would be (#370).
        //
        // This screen's own words are that a restore's record lives in these files and a change
        // ticket may need the evidence later - and then it deleted them on the way past, with
        // no question and nothing to undo with. The count is what makes the question worth
        // asking: shortening retention on a machine that has nothing old enough to lose is not
        // a decision, and should not be interrupted like one.
        var doomed = _log.CountPrunable(value);
        if (doomed > 0)
        {
            var confirm = MessageBox.Show(
                $"Keeping logs for {value} day(s) deletes {doomed} log file(s) now.\n\n" +
                "A restore's own record lives in these files. If a change ticket or an incident " +
                "review might need the evidence, copy them out first - Open log folder is on " +
                "this screen.\n\nDelete them?",
                "Nine Lives", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);

            if (confirm != MessageBoxResult.Yes)
            {
                // Put the box back to what is actually in force, without re-entering this.
                _revertingRetention = true;
                LogRetentionDays = _log.RetentionDays;
                _revertingRetention = false;
                return;
            }
        }

        _log.RetentionDays = value;
        _log.Prune();

        // The clamped value, not the typed one: the box should show what is actually in force.
        if (_log.RetentionDays != value)
        {
            LogRetentionDays = _log.RetentionDays;
            return;
        }

        Save(config => config.LogRetentionDays = value, "The log retention could not be saved");
    }

    /// <summary>
    /// Today's log, inside the app that wrote it (#214) - the support conversation is "what does
    /// the log say about X?", not "navigate to this folder".
    /// </summary>
    [RelayCommand]
    private void ViewLog()
    {
        var window = new Views.LogViewerWindow(new LogViewerViewModel(_log))
        {
            Owner = System.Windows.Application.Current?.MainWindow
        };
        window.Show();
    }

    /// <summary>
    /// Opens the log folder in Explorer. The logs are what someone attaches to a bug report, so
    /// there needs to be a way to find them that is not "know where LocalAppData is" (#40).
    /// </summary>
    [RelayCommand]
    private void OpenLogFolder()
    {
        try
        {
            Directory.CreateDirectory(_log.Directory);
            Process.Start(new ProcessStartInfo(_log.Directory) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            SetError($"Could not open the log folder: {ex.Message}. It is at {_log.Directory}");
        }
    }

    /// <summary>
    /// Read, change, write - one setting at a time, on the config as it is on disk right now.
    ///
    /// Never holds a loaded config across edits. Two screens both writing settings from their own
    /// stale copy is how one of them silently reverts the other's, and the container and server
    /// lists live in the same file.
    /// </summary>
    private void Save(Action<AppConfig> change, string failureLead)
    {
        try
        {
            var config = _credentialStore.LoadConfig();
            change(config);
            _credentialStore.SaveConfig(config);
            ClearStatus();
        }
        catch (Exception ex)
        {
            SetError($"{failureLead}: {ex.Message}");
        }
    }
}
