using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using Blackcat.NineLives.Models;
using Blackcat.NineLives.Services;

namespace Blackcat.NineLives.ViewModels;

/// <summary>
/// Past restores, read back from disk (#31).
///
/// Read-only on purpose beyond clearing: this is the record of what was done, and a view that
/// could edit it would be worth less than one that cannot.
/// </summary>
public partial class HistoryViewModel : ViewModelBase
{
    private readonly IRestoreHistoryStore _history;

    public HistoryViewModel(IRestoreHistoryStore? history = null)
    {
        _history = history ?? new RestoreHistoryStore();
        Refresh();
    }

    [ObservableProperty]
    private ObservableCollection<RestoreHistoryEntry> _entries = [];

    [ObservableProperty]
    private RestoreHistoryEntry? _selectedEntry;

    [ObservableProperty]
    private bool _hasEntries;

    [ObservableProperty]
    private string _filterText = string.Empty;

    /// <summary>True while a confirmation is pending, so Clear takes two presses.</summary>
    [ObservableProperty]
    private bool _isClearArmed;

    private List<RestoreHistoryEntry> _all = [];

    [RelayCommand]
    public void Refresh()
    {
        _all = _history.Load();
        ApplyFilter();

        // Selecting the newest is what someone opening this view is nearly always after: the
        // restore they just ran.
        SelectedEntry = Entries.FirstOrDefault();
        SetStatus(HasEntries
            ? $"{_all.Count} restore(s) recorded."
            : "No restores recorded yet.");
    }

    partial void OnFilterTextChanged(string value) => ApplyFilter();

    private void ApplyFilter()
    {
        var matching = string.IsNullOrWhiteSpace(FilterText)
            ? _all
            : _all.Where(Matches).ToList();

        Entries = new ObservableCollection<RestoreHistoryEntry>(matching);
        HasEntries = Entries.Count > 0;

        if (SelectedEntry != null && !Entries.Contains(SelectedEntry))
            SelectedEntry = Entries.FirstOrDefault();
    }

    private bool Matches(RestoreHistoryEntry e)
        => Contains(e.TargetDatabase) || Contains(e.ServerName)
        || Contains(e.SourceDatabase) || Contains(e.ContainerName)
        || Contains(e.OutcomeDisplay);

    private bool Contains(string? value)
        => value != null && value.Contains(FilterText, StringComparison.OrdinalIgnoreCase);

    [RelayCommand]
    private void CopyScript()
    {
        if (SelectedEntry == null) return;
        TryCopyToClipboard(SelectedEntry.Script, "Script copied to clipboard.");
    }

    [RelayCommand]
    private void CopyLog()
    {
        if (SelectedEntry == null) return;
        TryCopyToClipboard(SelectedEntry.Log, "Execution log copied to clipboard.");
    }

    [RelayCommand]
    private void SaveEntry()
    {
        if (SelectedEntry == null) return;

        var dialog = new SaveFileDialog
        {
            Filter = "Text Files (*.txt)|*.txt|All Files (*.*)|*.*",
            DefaultExt = ".txt",
            FileName = $"ninelives_{SelectedEntry.TargetDatabase}_{SelectedEntry.StartedAt:yyyyMMdd_HHmmss}.txt"
        };

        if (dialog.ShowDialog() != true) return;

        try
        {
            File.WriteAllText(dialog.FileName, Format(SelectedEntry));
            LastSavedPath = dialog.FileName;
            SetStatus($"Saved to {dialog.FileName}");
        }
        catch (Exception ex)
        {
            SetError($"Could not save: {ex.Message}");
        }
    }

    /// <summary>
    /// The file the last save wrote, so it can be opened without going and finding it (#117 item 8).
    /// Saving the record of a restore and then having to hunt for it is a strange place to stop.
    /// </summary>
    [ObservableProperty]
    private string? _lastSavedPath;

    [RelayCommand]
    private void OpenSavedFile()
    {
        if (string.IsNullOrEmpty(LastSavedPath)) return;

        try
        {
            Process.Start(new ProcessStartInfo(LastSavedPath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            // Whatever is registered for .txt failing is not this app's problem to crash over.
            SetError($"Could not open {LastSavedPath}: {ex.Message}");
        }
    }

    /// <summary>
    /// Two presses. This is the only destructive action in the view, and what it destroys is the
    /// evidence of what was done to someone's production databases.
    /// </summary>
    [RelayCommand]
    private void ClearHistory()
    {
        if (!IsClearArmed)
        {
            IsClearArmed = true;
            SetStatus("Press Clear again to delete every recorded restore. This cannot be undone.");
            return;
        }

        IsClearArmed = false;
        _history.Clear();
        Refresh();
        SetStatus("Restore history cleared.");
    }

    [RelayCommand]
    private void CancelClear()
    {
        IsClearArmed = false;
        ClearStatus();
    }

    /// <summary>The entry as a self-contained document: what ran, where, and what came back.</summary>
    public static string Format(RestoreHistoryEntry e)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Nine Lives - restore record");
        sb.AppendLine($"Started:    {e.StartedAt:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"Completed:  {e.CompletedAt:yyyy-MM-dd HH:mm:ss}  ({e.DurationDisplay})");
        sb.AppendLine($"Server:     {e.ServerName}");
        sb.AppendLine($"Target:     {e.TargetDatabase}");
        if (e.SourceDatabase != null) sb.AppendLine($"Source:     {e.SourceDatabase}");
        if (e.ContainerName != null) sb.AppendLine($"Container:  {e.ContainerName}");
        if (e.RestorePointTimestamp.HasValue)
            sb.AppendLine($"Point:      {e.RestorePointTimestamp:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"Chain:      {e.ChainSummary}");
        sb.AppendLine($"Outcome:    {e.OutcomeDisplay}");
        if (e.ErrorMessage != null) sb.AppendLine($"Error:      {e.ErrorMessage}");

        sb.AppendLine();
        sb.AppendLine("--- script ---------------------------------------------------");
        sb.AppendLine(e.Script);
        sb.AppendLine("--- execution log --------------------------------------------");
        sb.AppendLine(e.Log);

        return sb.ToString();
    }
}
