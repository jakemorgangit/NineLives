using System.IO;
using System.Text;
using Blackcat.NineLives.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Blackcat.NineLives.ViewModels;

/// <summary>
/// The operation log, readable inside the app that wrote it (#214).
///
/// The log is the app's own evidence trail - per-run records, header timings, credential
/// decisions - and the app is the only tool guaranteed to be present when that trail is needed.
/// "Open the log folder" hands somebody a directory of dated files and leaves them to it; this
/// shows today's file, filtered, with a refresh - which covers the actual support conversation:
/// "what does the log say about X?".
///
/// Deliberately not a log framework. A read of the current file with a filter over the lines
/// covers the need; the file is opened shared so the app's own appends never fight the viewer.
/// </summary>
public partial class LogViewerViewModel : ViewModelBase
{
    private readonly OperationLog _log;

    private List<string> _allLines = [];

    public LogViewerViewModel(OperationLog? log = null)
    {
        _log = log ?? App.Log;
        Refresh();
    }

    /// <summary>Which file is on screen, so what is being read is never a mystery.</summary>
    [ObservableProperty]
    private string _currentFile = string.Empty;

    /// <summary>The lines after the filter, newest last - the shape the file has.</summary>
    [ObservableProperty]
    private string _visibleText = string.Empty;

    [ObservableProperty]
    private string _lineSummary = string.Empty;

    /// <summary>Case-insensitive substring over whole lines. Empty shows everything.</summary>
    [ObservableProperty]
    private string _filterText = string.Empty;

    partial void OnFilterTextChanged(string value) => ApplyFilter();

    /// <summary>
    /// Re-reads the file. A button rather than a timer: the log grows in bursts around
    /// operations, and a viewer that re-reads on a schedule is churn for a file that is usually
    /// not changing.
    /// </summary>
    [RelayCommand]
    public void Refresh()
    {
        try
        {
            CurrentFile = _log.CurrentFile;

            if (!File.Exists(CurrentFile))
            {
                _allLines = [];
                VisibleText = "Nothing logged today yet.";
                LineSummary = "0 lines";
                return;
            }

            // FileShare.ReadWrite, because the app appends to this exact file while it is open
            // here - the viewer must never make the log un-writable.
            using var stream = new FileStream(
                CurrentFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream, Encoding.UTF8);

            var lines = new List<string>();
            while (reader.ReadLine() is { } line) lines.Add(line);

            _allLines = lines;
            ApplyFilter();
        }
        catch (Exception ex)
        {
            VisibleText = $"The log could not be read: {ex.Message}";
            LineSummary = string.Empty;
        }
    }

    private void ApplyFilter()
    {
        var filter = FilterText.Trim();

        var visible = filter.Length == 0
            ? _allLines
            : _allLines.Where(l => l.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();

        VisibleText = string.Join(Environment.NewLine, visible);

        LineSummary = filter.Length == 0
            ? $"{_allLines.Count:N0} lines"
            : $"{visible.Count:N0} of {_allLines.Count:N0} lines match";
    }

    [RelayCommand]
    private void CopyVisible() =>
        TryCopyToClipboard(VisibleText, "Log lines copied to clipboard.");

    [RelayCommand]
    private void OpenLogFolder()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = _log.Directory,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            SetError($"Could not open the log folder: {ex.Message}");
        }
    }
}
