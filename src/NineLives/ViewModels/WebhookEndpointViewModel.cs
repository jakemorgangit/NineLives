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

    /// <summary>
    /// The last delivery attempt, on the row (#292). Failures land only in the operation log
    /// by design, so without this a webhook broken for weeks looked identical to one that
    /// works.
    /// </summary>
    public string LastDeliveryDisplay => Model.LastDeliveryAt is not { } at
        ? "No deliveries yet"
        : Model.LastDeliveryOutcome == "delivered"
            ? $"Last delivery {at:yyyy-MM-dd HH:mm}: delivered"
            : $"Last delivery {at:yyyy-MM-dd HH:mm}: FAILED - {Model.LastDeliveryOutcome}";

    /// <summary>A delivery just happened on this screen - the row updates without a reload.</summary>
    public void NoteDelivery(string? error)
    {
        Model.LastDeliveryAt = DateTime.Now;
        Model.LastDeliveryOutcome = error ?? "delivered";
        OnPropertyChanged(nameof(LastDeliveryDisplay));
    }

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

    /// <summary>Why the last URL save was refused - empty when it was not (#292).</summary>
    [ObservableProperty]
    private string _urlFeedback = string.Empty;

    /// <summary>
    /// The explicit commit (#317): into the vault, out of the file, off the screen. Also the
    /// migration path for URLs that predate the vault - saving a replacement moves the
    /// endpoint onto the new arrangement.
    ///
    /// Validated first (#292): a typo used to be accepted, saved, and then fail on every
    /// run - with the failures landing only in the operation log.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanSaveUrl))]
    private void SaveUrl()
    {
        var candidate = UrlInput.Trim();
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps)
        {
            UrlFeedback = "Not saved: a webhook URL is an absolute https:// address.";
            return;
        }

        UrlFeedback = string.Empty;
        WebhookTransport.SaveUrl(Model, _store, candidate);
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
