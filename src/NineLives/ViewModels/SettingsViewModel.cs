using System.Diagnostics;
using System.IO;
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

    /// <summary>True while the constructor is filling the properties from config.</summary>
    private readonly bool _loading;

    public SettingsViewModel(ICredentialStore credentialStore, OperationLog? log = null)
    {
        _credentialStore = credentialStore;
        _log = log ?? App.Log;

        _loading = true;
        try
        {
            var config = _credentialStore.LoadConfig();
            _selectedTheme = ThemeManager.Current;
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
        if (_loading) return;

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
