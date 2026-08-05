using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.Input;

namespace Blackcat.NineLives.ViewModels;

public partial class AboutViewModel : ViewModelBase
{
    public string AppName => "Nine Lives";
    public string Version => Services.AppVersion.Display;
    public string Year => "2026";
    public string Author => "Jake Morgan";
    public string Company => "Blackcat Data Solutions Ltd";
    public string Website => "https://blackcat.wales";
    public string Description => "Every database deserves nine lives. A production-ready utility for restoring SQL Server databases from Azure Blob Storage backups with full support for point-in-time recovery using Full, Differential, and Transaction Log backup chains.";

    /// <summary>Shown so the path can be read even if opening the folder fails.</summary>
    public string LogFolder => App.Log.Directory;

    /// <summary>
    /// Opens the log folder in Explorer. The logs are what someone attaches to a bug report, so
    /// there needs to be a way to find them that is not "know where LocalAppData is" (#40).
    /// </summary>
    [RelayCommand]
    private void OpenLogFolder()
    {
        try
        {
            Directory.CreateDirectory(App.Log.Directory);
            Process.Start(new ProcessStartInfo(App.Log.Directory) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            SetError($"Could not open the log folder: {ex.Message}. It is at {App.Log.Directory}");
        }
    }
}
