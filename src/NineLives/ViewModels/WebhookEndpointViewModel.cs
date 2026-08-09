using Blackcat.NineLives.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Blackcat.NineLives.ViewModels;

/// <summary>
/// One notification endpoint row in Settings (#242): edits its model in place and asks the owner
/// to persist after each meaningful change - the same read-change-write discipline the rest of
/// the settings screen keeps.
/// </summary>
public partial class WebhookEndpointViewModel : ObservableObject
{
    private readonly Action _save;
    private readonly Func<WebhookEndpointViewModel, Task> _test;

    public WebhookEndpointViewModel(
        WebhookEndpoint model, Action save, Func<WebhookEndpointViewModel, Task> test)
    {
        Model = model;
        _save = save;
        _test = test;

        _name = model.Name;
        _url = model.Url;
        _format = model.Format;
        _notifyStarted = model.NotifyStarted;
        _notifyFinished = model.NotifyFinished;
        _notifyProblems = model.NotifyProblems;
    }

    public WebhookEndpoint Model { get; }

    public IReadOnlyList<WebhookFormat> Formats { get; } =
        [WebhookFormat.Teams, WebhookFormat.Slack, WebhookFormat.Generic];

    [ObservableProperty]
    private string _name;

    partial void OnNameChanged(string value) { Model.Name = value; _save(); }

    [ObservableProperty]
    private string _url;

    partial void OnUrlChanged(string value) { Model.Url = value.Trim(); _save(); }

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
