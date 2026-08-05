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
}
