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
/// Past operations, read back from disk (#31, #434).
///
/// Restores and rehearsals at first; backups and copies since, because each of them is a thing
/// that happened to somebody's server and none of them is recoverable from the app afterwards.
///
/// Read-only on purpose beyond clearing: this is the record of what was done, and a view that
/// could edit it would be worth less than one that cannot.
/// </summary>
public partial class HistoryViewModel : ViewModelBase
{
    private readonly IOperationHistoryStore _history;

    public HistoryViewModel(IOperationHistoryStore? history = null)
    {
        _history = history ?? new OperationHistoryStore();
        Refresh();
    }

    [ObservableProperty]
    private ObservableCollection<OperationHistoryEntry> _entries = [];

    [ObservableProperty]
    private OperationHistoryEntry? _selectedEntry;

    [ObservableProperty]
    private bool _hasEntries;

    [ObservableProperty]
    private string _filterText = string.Empty;

    /// <summary>
    /// The kinds on offer, "All" first (#434). Built as a fixed list rather than from what happens
    /// to be in the file, so the choices do not move around as history accumulates - and so
    /// "Backup" is still offered on the day somebody is looking for one and there is none.
    /// </summary>
    public IReadOnlyList<string> KindFilters { get; } =
    [
        AllKinds,
        OperationKind.Backup,
        OperationKind.Restore,
        OperationKind.Copy,
        OperationKind.Rehearsal,
    ];

    public const string AllKinds = "All";

    /// <summary>
    /// Which kind is shown. Now that a backup of everything before a patch writes one entry per
    /// database, "when did we last restore this" would otherwise mean reading past fifty backups.
    /// </summary>
    [ObservableProperty]
    private string _kindFilter = AllKinds;

    partial void OnKindFilterChanged(string value) => ApplyFilter();

    /// <summary>True while a confirmation is pending, so Clear takes two presses.</summary>
    [ObservableProperty]
    private bool _isClearArmed;

    private List<OperationHistoryEntry> _all = [];

    [RelayCommand]
    public void Refresh()
    {
        _all = _history.Load();
        ApplyFilter();

        // Selecting the newest is what someone opening this view is nearly always after: the
        // restore they just ran.
        SelectedEntry = Entries.FirstOrDefault();
        // "Nothing here" and "cannot read what is here" are opposite facts, and both used to
        // arrive as an empty list (#370). Reporting the second as the first hides a damaged
        // file - and Clear sits on this screen, one click from overwriting it.
        if (_history.CouldNotRead)
            SetError(
                "The history file exists but could not be read, so this list is not what it " +
                "holds. Do not clear it - copy it somewhere safe first: " + _history.FilePath);
        else
            SetStatus(HasEntries
                ? $"{_all.Count} operation(s) recorded."
                : "No operations recorded yet.");
    }

    partial void OnFilterTextChanged(string value) => ApplyFilter();

    private void ApplyFilter()
    {
        var matching = _all.AsEnumerable();

        if (KindFilter != AllKinds)
        {
            // Entries written before Kind existed hold "Restore", which is what they were - so
            // filtering to Restore correctly includes them without a special case here.
            matching = matching.Where(e =>
                string.Equals(e.Kind, KindFilter, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(FilterText))
            matching = matching.Where(Matches);

        Entries = new ObservableCollection<OperationHistoryEntry>(matching);
        HasEntries = Entries.Count > 0;

        if (SelectedEntry != null && !Entries.Contains(SelectedEntry))
            SelectedEntry = Entries.FirstOrDefault();
    }

    private bool Matches(OperationHistoryEntry e)
        => Contains(e.TargetDatabase) || Contains(e.ServerName)
        || Contains(e.SourceDatabase) || Contains(e.ContainerName)
        || Contains(e.OutcomeDisplay)
        // The source server is the only record of the machine a copy READ, and searching for a
        // server by name should find the copies that touched it in either direction.
        || Contains(e.SourceServerName)
        || Contains(e.Kind);

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
        // Never over a file that could not be read (#370). The list is empty because the read
        // failed, not because there is nothing there - clearing would write an empty file over
        // what may still hold every receipt, and a receipt is the proof a restore happened.
        if (_history.CouldNotRead)
        {
            IsClearArmed = false;
            SetError(
                "This history could not be read, so there is no telling what clearing it would " +
                "destroy. Move or repair the file first: " + _history.FilePath);
            return;
        }

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
    public static string Format(OperationHistoryEntry e)
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
