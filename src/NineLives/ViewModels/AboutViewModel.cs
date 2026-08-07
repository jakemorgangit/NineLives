using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Blackcat.NineLives.Services;

namespace Blackcat.NineLives.ViewModels;

/// <summary>A theme and the name shown for it.</summary>
/// <param name="Value">The theme itself.</param>
/// <param name="Name">What the picker calls it.</param>
public sealed record ThemeOption(AppTheme Value, string Name)
{
    /// <summary>
    /// The combo box's own control template renders the selected item through a plain
    /// ContentPresenter, which ignores DisplayMemberPath and falls back to ToString - so without
    /// this the picker reads "ThemeOption { Value = Dark, Name = Dark }".
    /// </summary>
    public override string ToString() => Name;
}

public partial class AboutViewModel : ViewModelBase
{
    private readonly ICredentialStore? _credentialStore;

    public AboutViewModel() : this(null) { }

    /// <summary>
    /// Takes the store so the theme choice can be remembered. Optional, because About is otherwise
    /// a static page and a test that only wants to render it should not need a store.
    /// </summary>
    public AboutViewModel(ICredentialStore? credentialStore)
    {
        _credentialStore = credentialStore;
        _selectedTheme = ThemeManager.Current;
    }

    public string AppName => "Nine Lives";
    public string Version => Services.AppVersion.Display;
    public string Year => "2026";
    public string Author => "Jake Morgan";
    public string Company => "Blackcat Data Solutions Ltd";
    public string Website => "https://blackcat.wales";
    public string Description => "Every database deserves nine lives. A production-ready utility for restoring SQL Server databases from Azure Blob Storage backups with full support for point-in-time recovery using Full, Differential, and Transaction Log backup chains.";

    /// <summary>Shown so the path can be read even if opening the folder fails.</summary>
    public string LogFolder => App.Log.Directory;

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
        if (!ThemeManager.Apply(value))
        {
            SetError("The theme could not be applied.");
            return;
        }

        ClearStatus();

        if (_credentialStore == null) return;

        try
        {
            var config = _credentialStore.LoadConfig();
            config.Theme = value;
            _credentialStore.SaveConfig(config);
        }
        catch (Exception ex)
        {
            SetError($"The theme was applied, but could not be saved for next time: {ex.Message}");
        }
    }

    public static string ThemeName(AppTheme theme) => ThemeManager.DisplayName(theme);

    /// <summary>
    /// Opens the log folder in Explorer. The logs are what someone attaches to a bug report, so
    /// there needs to be a way to find them that is not "know where LocalAppData is" (#40).
    /// </summary>
    [RelayCommand]
    private void OpenLogFolder()
    {
        try
        {
            Directory.CreateDirectory(App.Log.Directory);
            Process.Start(new ProcessStartInfo(App.Log.Directory) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            SetError($"Could not open the log folder: {ex.Message}. It is at {App.Log.Directory}");
        }
    }
}
