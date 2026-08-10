using Blackcat.NineLives.Models;
using Blackcat.NineLives.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Blackcat.NineLives.ViewModels;

/// <summary>
/// One notification endpoint row in Settings (#242): edits its model in place and asks the owner
/// to persist after each meaningful change - the same read-change-write discipline the rest of
/// the settings screen keeps.
///
/// The URL is the exception (#317): it is a secret - anyone holding it can post as the
/// integration - so it gets the SAS-token treatment. Typing goes into an input that commits
/// only on an explicit Save; once saved it lives in Windows Credential Manager, the model's
/// file copy is blanked, and the row shows an obfuscated placeholder. Replacing it means
/// saving a new one - the old one is never displayed again.
/// </summary>
public partial class WebhookEndpointViewModel : ObservableObject
{
    private readonly Action _save;
    private readonly Func<WebhookEndpointViewModel, Task> _test;
    private readonly ICredentialStore _store;

    public WebhookEndpointViewModel(
        WebhookEndpoint model, ICredentialStore store,
        Action save, Func<WebhookEndpointViewModel, Task> test)
    {
        Model = model;
        _store = store;
        _save = save;
        _test = test;

        _name = model.Name;
        _format = model.Format;
        _notifyStarted = model.NotifyStarted;
        _notifyFinished = model.NotifyFinished;
        _notifyProblems = model.NotifyProblems;
        _hasStoredUrl = WebhookTransport.HasUrl(model, store);
    }

    public WebhookEndpoint Model { get; }

    public IReadOnlyList<WebhookFormat> Formats { get; } =
        [WebhookFormat.Teams, WebhookFormat.Slack, WebhookFormat.Generic];

    [ObservableProperty]
    private string _name;

    partial void OnNameChanged(string value) { Model.Name = value; _save(); }

    // ── the URL, as a secret (#317) ─────────────────────────────────────────────

    /// <summary>What is being typed. Never pre-filled: a saved URL is not for reading back.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveUrlCommand))]
    private string _urlInput = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UrlDisplay))]
    private bool _hasStoredUrl;

    /// <summary>The masked status line - proof a URL exists without showing a byte of it.</summary>
    public string UrlDisplay => HasStoredUrl
        ? "●●●●●●●●●●  saved"
        : "No URL saved yet";

    public bool CanSaveUrl => !string.IsNullOrWhiteSpace(UrlInput);

    /// <summary>
    /// The explicit commit (#317): into the vault, out of the file, off the screen. Also the
    /// migration path for URLs that predate the vault - saving a replacement moves the
    /// endpoint onto the new arrangement.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanSaveUrl))]
    private void SaveUrl()
    {
        WebhookTransport.SaveUrl(Model, _store, UrlInput);
        UrlInput = string.Empty;
        HasStoredUrl = true;
        _save();
    }

    [ObservableProperty]
    private WebhookFormat _format;

    partial void OnFormatChanged(WebhookFormat value) { Model.Format = value; _save(); }

    [ObservableProperty]
    private bool _notifyStarted;

    partial void OnNotifyStartedChanged(bool value) { Model.NotifyStarted = value; _save(); }

    [ObservableProperty]
    private bool _notifyFinished;

    partial void OnNotifyFinishedChanged(bool value) { Model.NotifyFinished = value; _save(); }

    [ObservableProperty]
    private bool _notifyProblems;

    partial void OnNotifyProblemsChanged(bool value) { Model.NotifyProblems = value; _save(); }

    [RelayCommand]
    private Task TestAsync() => _test(this);
}
