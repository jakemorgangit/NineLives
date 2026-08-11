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

    /// <summary>
    /// Where somebody stuck on a screen goes next (#370).
    ///
    /// The app had exactly one hyperlink in it - blackcat.wales - so the documentation, the
    /// issue tracker and the release notes were reachable only by already knowing where they
    /// are. A tool that generates T-SQL you are meant to read before running has a lot to
    /// explain, and all of it lived somewhere the app never mentioned.
    /// </summary>
    public string RepositoryUrl => "https://github.com/jakemorgangit/NineLives";
    public string DocumentationUrl => "https://github.com/jakemorgangit/NineLives#readme";
    public string IssuesUrl => "https://github.com/jakemorgangit/NineLives/issues";
    public string ReleasesUrl => "https://github.com/jakemorgangit/NineLives/releases";

    /// <summary>
    /// Both directions and every medium (#370). This described a restore-only, cloud-only tool
    /// in an app that has had a Back Up screen, a Copy Database screen and a shared-path medium
    /// for several releases - the one paragraph a stranger reads to find out what this is, and it
    /// was describing half of it.
    /// </summary>
    public string Description => "Every database deserves nine lives. Nine Lives finds the SQL Server backups in your storage - an Azure Blob container, an S3-compatible bucket, a path the server can see, or an instance's own backup history - works out the restore chains, and turns any moment you pick into a script you can read. It takes backups too, copies a database from one instance to another, rehearses a restore to prove it works, and receipts everything it runs.";

}
