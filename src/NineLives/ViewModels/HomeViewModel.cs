using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Blackcat.NineLives.Models;

namespace Blackcat.NineLives.ViewModels;

/// <summary>
/// The front door (#343): where choosing a mode lands, and where a launch with nothing
/// remembered begins. Says what the app is, walks the three get-going steps, and points at
/// the rest of what the chosen mode offers - because the mode cards say how MUCH of the app
/// you get, and nothing else ever said what the app IS.
///
/// Deliberately almost stateless: the screen is prose and buttons. Navigation stays
/// MainViewModel's job - this only raises the request, the same one-way wiring the browser's
/// restore handoff uses - and the mode arrives from above like every other screen's, so the
/// feature pointers show exactly what the sidebar shows.
/// </summary>
public partial class HomeViewModel : ViewModelBase
{
    /// <summary>Raised with a <see cref="MainViewModel.Nav"/> name. MainViewModel owns the switch.</summary>
    public event Action<string>? NavigateRequested;

    [RelayCommand]
    private void Go(string viewName) => NavigateRequested?.Invoke(viewName);

    /// <summary>Set by MainViewModel whenever the mode changes, like the other screens.</summary>
    [ObservableProperty]
    private AppMode _mode = AppMode.Pro;

    public bool ShowBackup => AppModeCapabilities.CanBackUp(Mode);
    public bool ShowCopyDatabase => AppModeCapabilities.CanCopyBetweenServers(Mode);
    public bool ShowBrowseBackups => AppModeCapabilities.CanBrowseBackups(Mode);

    partial void OnModeChanged(AppMode value)
    {
        OnPropertyChanged(nameof(ShowBackup));
        OnPropertyChanged(nameof(ShowCopyDatabase));
        OnPropertyChanged(nameof(ShowBrowseBackups));
    }

    /// <summary>Read from the assembly so it cannot go stale. Instance rather than static
    /// because a Binding path only sees the DataContext's own properties.</summary>
    public string VersionText => Services.AppVersion.Display;
}
