namespace Blackcat.NineLives.Services;

/// <summary>
/// The chosen palette. Lives in Core rather than beside ThemeManager because it is a persisted
/// SETTING - the config stores and ports it - while applying it to actual windows is the
/// desktop app's business (#63). A headless front end round-trips the value without ever
/// looking at it.
/// </summary>
public enum AppTheme
{
    Dark,
    Light,
    HighContrast
}
