using System.Collections.ObjectModel;
using Blackcat.NineLives.Models;
using Blackcat.NineLives.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Blackcat.NineLives.ViewModels;

/// <summary>One card. What the mode is, who it is for, and what it turns on.</summary>
public sealed class ModeCard(AppMode mode)
{
    public AppMode Mode { get; } = mode;

    public string Title { get; } = AppModeCapabilities.Title(mode);

    /// <summary>
    /// The button's text, built here rather than by a StringFormat on the binding.
    ///
    /// Button.Content is an object, so a StringFormat on it is quietly ignored - the button read
    /// "Basic" where it should have said "Use Basic", which is a label describing the card rather
    /// than the action.
    /// </summary>
    public string ChooseLabel { get; } = $"Use {AppModeCapabilities.Title(mode)}";
    public string Tagline { get; } = AppModeCapabilities.Tagline(mode);
    public string WhoFor { get; } = AppModeCapabilities.WhoFor(mode);

    public IReadOnlyList<string> Highlights { get; } = AppModeCapabilities.Highlights(mode);

    /// <summary>
    /// Which shade the card takes.
    ///
    /// Deliberately not a traffic light. These are not better and worse, they are more and less -
    /// so the progression is one accent getting stronger rather than green-amber-red, which would
    /// read as "the safe one and the dangerous ones".
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
