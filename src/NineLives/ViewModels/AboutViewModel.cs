using CommunityToolkit.Mvvm.ComponentModel;
using Blackcat.NineLives.Models;
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

/// <summary>
/// Credits. Nothing here changes anything.
///
/// It used to carry the theme picker and the log folder, because it was the only page that was not
/// a workflow step - so the one screen with no settings on it became the settings screen. They are
/// on Settings now (#117 item 2).
/// </summary>
public partial class AboutViewModel : ViewModelBase
{
    /// <summary>
    /// Takes an optional store purely so the existing construction sites do not all have to
    /// change. Nothing is read from it.
    /// </summary>
    public AboutViewModel(ICredentialStore? credentialStore = null) { }

    public string AppName => "Nine Lives";
    public string Version => Services.AppVersion.Display;
    public string Year => "2026";
    public string Author => "Jake Morgan";
    public string Company => "Blackcat Data Solutions Ltd";
    public string Website => "https://blackcat.wales";
    public string Description => "Every database deserves nine lives. A production-ready utility for restoring SQL Server databases from Azure Blob Storage or S3-compatible object storage backups, with full support for point-in-time recovery using Full, Differential, and Transaction Log backup chains.";

}
