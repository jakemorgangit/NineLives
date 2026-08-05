using System.Windows;

namespace Blackcat.NineLives.Services;

public enum AppTheme
{
    Dark,
    Light,
    HighContrast
}

/// <summary>
/// Swaps the colour palette at runtime.
///
/// The application's merged dictionaries are [palette, controls, logo]. Only the palette changes;
/// the control styles and the logo are the same in every theme. Replacing the dictionary at index
/// 0 re-resolves every <c>DynamicResource</c> in every loaded window immediately, which is why
/// the styles and views reference brushes dynamically - a StaticResource is resolved once when the
/// element loads and would keep the old colour for the lifetime of the window.
///
/// Position, not Source Uri, identifies the palette: comparing pack Uris is fiddly, and the merge
/// order is declared in one place (App.xaml) right next to a comment saying so.
/// </summary>
public static class ThemeManager
{
    public const int PaletteIndex = 0;

    /// <summary>The theme currently applied. Dark until something says otherwise.</summary>
    public static AppTheme Current { get; private set; } = AppTheme.Dark;

    /// <summary>
    /// An absolute pack Uri, not a relative path.
    ///
    /// A relative Uri resolves against the ENTRY assembly, which is this app when it runs and the
    /// test host when the tests run - so the tests could not load a palette at all. Naming the
    /// assembly works in both.
    /// </summary>
    public static string SourceFor(AppTheme theme) => theme switch
    {
        AppTheme.Light => Pack("Palette.Light"),
        AppTheme.HighContrast => Pack("Palette.HighContrast"),
        _ => Pack("Palette.Dark")
    };

    private static string Pack(string name)
        => $"pack://application:,,,/NineLives;component/Themes/{name}.xaml";

    /// <summary>Loads a palette without applying it. Used by the tests to compare key sets.</summary>
    public static ResourceDictionary Load(AppTheme theme) => new()
    {
        Source = new Uri(SourceFor(theme), UriKind.Absolute)
    };

    /// <summary>
    /// Applies a theme to the running application.
    ///
    /// Never throws. A theme that will not load is a cosmetic problem, and taking the app down -
    /// or worse, failing mid-restore - over a colour scheme would not be a trade anyone would
    /// make. The previous palette simply stays in place.
    /// </summary>
    public static bool Apply(AppTheme theme, Application? application = null)
    {
        var app = application ?? Application.Current;
        if (app == null) return false;

        try
        {
            var merged = app.Resources.MergedDictionaries;
            if (merged.Count == 0) return false;

            merged[PaletteIndex] = Load(theme);
            Current = theme;
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Human-readable name, for the picker and the config file.</summary>
    public static string DisplayName(AppTheme theme) => theme switch
    {
        AppTheme.Light => "Light",
        AppTheme.HighContrast => "High contrast",
        _ => "Dark"
    };

    public static IReadOnlyList<AppTheme> All => [AppTheme.Dark, AppTheme.Light, AppTheme.HighContrast];
}
