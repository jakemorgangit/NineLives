using System.Collections.ObjectModel;
using Blackcat.NineLives.Models;
using Blackcat.NineLives.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Blackcat.NineLives.ViewModels;

/// <summary>One card. What the mode is, who it is for, and what it turns on.</summary>
public sealed partial class ModeCard(AppMode mode) : ObservableObject
{
    public AppMode Mode { get; } = mode;

    /// <summary>
    /// Whether this is the mode currently in force.
    ///
    /// Only ever true when the cards are reopened from Settings, which is the case where it
    /// matters: the screen is being read to find out what the OTHER two contain, so the one you
    /// are already on has to be obvious or every reading of it starts by working that out.
    /// </summary>
    [ObservableProperty]
    private bool _isCurrent;

    public string Title { get; } = AppModeCapabilities.Title(mode);

    /// <summary>
    /// The button's text, built here rather than by a StringFormat on the binding.
    ///
    /// Button.Content is an object, so a StringFormat on it is quietly ignored - the button read
    /// "Basic" where it should have said what pressing it does.
    ///
    /// The same words on all three now that the titles name the work: "Use Pro" alongside a price
    /// -list heading was most of what made this screen feel like a checkout (#191).
    /// </summary>
    public string ChooseLabel { get; } = "Show me this view";
    public string Tagline { get; } = AppModeCapabilities.Tagline(mode);
    public string WhoFor { get; } = AppModeCapabilities.WhoFor(mode);

    public IReadOnlyList<string> Highlights { get; } = AppModeCapabilities.Highlights(mode);
    public string HighlightsLabel { get; } = AppModeCapabilities.HighlightsLabel(mode);

    /// <summary>
    /// Which shade the card takes.
    ///
    /// Deliberately not a traffic light, and deliberately not a gradient from cool to hot either -
    /// three unrelated colours, because a progression is exactly the thing this screen should not
    /// suggest. They are three views of one app, not three rungs.
    /// </summary>
    public string AccentBrushKey { get; } = mode switch
    {
        AppMode.Basic => "ModeBasicBrush",
        AppMode.Standard => "ModeStandardBrush",
        _ => "ModeProBrush"
    };
}

/// <summary>
/// The first thing somebody sees, once (#176).
///
/// Once, not every launch - the choice is remembered and changed from Settings. Being asked on
/// every start would be a worse problem than the clutter this exists to fix.
///
/// It is also not a wall: whichever card is chosen, nothing is deleted and nothing is unavailable
/// forever. That matters for the honesty of the screen - somebody who picks Basic and later needs
/// to take a backup has not lost anything, they change a setting.
/// </summary>
public partial class ModeSelectionViewModel : ViewModelBase
{
    private readonly ICredentialStore _store;

    public ModeSelectionViewModel(ICredentialStore store)
    {
        _store = store;
    }

    public ObservableCollection<ModeCard> Cards { get; } =
    [
        new(AppMode.Basic),
        new(AppMode.Standard),
        new(AppMode.Pro)
    ];

    /// <summary>Raised once a mode has been chosen and saved, so the shell can move on.</summary>
    public event Action<AppMode>? Chosen;

    /// <summary>Raised when the cards are closed without changing anything.</summary>
    public event Action? Cancelled;

    /// <summary>
    /// Whether there is anything to go back to.
    ///
    /// False on first run, because there is no mode yet and a way out would be a way to reach an
    /// app that has not been told how much of itself to show. True whenever the cards are reopened
    /// from Settings, where refusing to let somebody leave a screen they opened to READ would be
    /// the surest way to stop them opening it.
    /// </summary>
    [ObservableProperty]
    private bool _canCancel;

    /// <summary>
    /// Reopens the cards as a reference rather than as a first-run question (#191).
    ///
    /// A drop-down list of three names told you what the modes were CALLED. What is actually worth
    /// knowing - what each one turns on, and who it is for - was written on the cards and then
    /// shown once. This is the same screen, reachable.
    /// </summary>
    public void ShowCurrent(AppMode mode)
    {
        foreach (var card in Cards) card.IsCurrent = card.Mode == mode;
        CanCancel = true;
    }

    [RelayCommand]
    private void Cancel() => Cancelled?.Invoke();

    [RelayCommand]
    private void Choose(ModeCard? card)
    {
        if (card == null) return;

        try
        {
            var config = _store.LoadConfig();

            // A config that failed to LOAD must not be written back - saving over it would turn a
            // transient read failure into permanent data loss, which is the whole of #7.
            if (config.LoadError == null)
            {
                config.Mode = card.Mode;
                _store.SaveConfig(config);
            }
        }
        catch (Exception ex)
        {
            // The choice still takes effect for this session. Failing to persist a preference is
            // not a reason to refuse to start.
            SetError($"Your choice could not be saved, so it will be asked again next time: {ex.Message}");
        }

        Chosen?.Invoke(card.Mode);
    }
}
