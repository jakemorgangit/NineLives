using System.Collections.ObjectModel;
using Blackcat.NineLives.Models;
using Blackcat.NineLives.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Blackcat.NineLives.ViewModels;

/// <summary>
/// Restoring from a backup location both hosts can see (#149).
///
/// The workflow in that issue, in order: pick the source and the target, read what the source
/// recorded backing up, choose what to restore, prove the TARGET can read those files, and only
/// then generate a script.
///
/// The order is not presentation. Each step's output is the next step's input - the chain comes
/// from the source's msdb, and the script is generated from the files the target confirmed it can
/// read - so a screen cannot offer to run a restore it has not checked, because it would have
/// nothing to generate from.
/// </summary>
public partial class SharedLocationRestoreViewModel : ViewModelBase
{
    private readonly ICredentialStore _store;
    private readonly ISqlServerService _sql;
    private readonly BackupHistoryChainBuilder _chains = new();
    private readonly RestoreScriptGenerator _generator = new();
    private readonly OperationCancellation _cancellation = new();

    public SharedLocationRestoreViewModel(ICredentialStore store, ISqlServerService sql)
    {
        _store = store;
        _sql = sql;
        RefreshServers();
    }

    // ── 1. source and target ────────────────────────────────────────────────────

    [ObservableProperty]
    private ObservableCollection<ServerConnection> _servers = [];

    /// <summary>The instance that TOOK the backups - the one whose msdb is read.</summary>
    [ObservableProperty]
    private ServerConnection? _sourceServer;

    /// <summary>
    /// The instance the RESTORE runs on. Not necessarily the one the backups came from - that
    /// distinction is the whole reason this screen exists, and the one people get wrong.
    /// </summary>
    [ObservableProperty]
    private ServerConnection? _targetServer;

    public void RefreshServers()
    {
        try
        {
            Servers = new ObservableCollection<ServerConnection>(_store.LoadConfig().Servers);
        }
        catch
        {
            // A config that will not load is reported where it is loaded for real; here it just
            // means an empty list rather than a screen that cannot open.
            Servers = [];
        }
    }

    // ── 2. the shared location ──────────────────────────────────────────────────

    /// <summary>The path as the source wrote it, when the target reaches it by another name.</summary>
    [ObservableProperty]
    private string _sourcePathPrefix = string.Empty;

    /// <summary>How the target reaches the same place.</summary>
    [ObservableProperty]
    private string _targetPathPrefix = string.Empty;

    public BackupPathMapping Mapping => new(SourcePathPrefix, TargetPathPrefix);

    /// <summary>
    /// Said before anything is checked, because it is the one failure here that can end in a
    /// SUCCESSFUL restore of the wrong backup: a local path on the source may resolve on the target
    /// to the target's own drive of that letter.
    /// </summary>
    public string PathAdvice
    {
        get
        {
            if (SelectedChain == null) return string.Empty;

            var local = SelectedChain.Files.Where(BackupPathMapping.LooksLocalToTheSource).ToList();
            if (local.Count == 0 || Mapping.IsInUse) return string.Empty;

            return $"{local.Count} of these backups were written to a local path on the source " +
                   $"(for example {local[0]}). That path means something different on the target - " +
                   "at best it will not be found, at worst it resolves to the target's own drive. " +
                   "Give the path as the target reaches it below.";
        }
    }

    // ── 3. what the source recorded ─────────────────────────────────────────────

    [ObservableProperty]
    private ObservableCollection<string> _databases = [];

    [ObservableProperty]
    private string? _selectedDatabase;

    private List<BackupHistoryEntry> _history = [];

    [ObservableProperty]
    private ObservableCollection<BackupHistoryChain> _availableChains = [];

    [ObservableProperty]
    private BackupHistoryChain? _selectedChain;

    [RelayCommand]
    private async Task ReadHistoryAsync()
    {
        if (SourceServer == null)
        {
            SetError("Choose the server the backups were taken on.");
            return;
        }

        var ct = _cancellation.Begin();
        IsBusy = true;
        ClearStatus();

        try
        {
            _history = await _sql.ReadBackupHistoryAsync(SourceServer, SelectedDatabase, ct);

            Databases = new ObservableCollection<string>(
                _history.Select(h => h.DatabaseName)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(n => n, StringComparer.OrdinalIgnoreCase));

            RebuildChains();

            SetStatus(_history.Count == 0
                ? $"{SourceServer.ServerName} has no backup history for that database."
                : $"Read {_history.Count} backup(s) from {SourceServer.ServerName}.");
        }
        catch (OperationCanceledException)
        {
            SetStatus("Stopped.");
        }
        catch (Exception ex)
        {
            SetError($"Could not read the backup history: {ex.Message}");
        }
        finally
        {
            _cancellation.End();
            IsBusy = false;
        }
    }

    partial void OnSelectedDatabaseChanged(string? value) => RebuildChains();

    private void RebuildChains()
    {
        var forDatabase = string.IsNullOrEmpty(SelectedDatabase)
            ? _history
            : _history.Where(h => string.Equals(h.DatabaseName, SelectedDatabase, StringComparison.OrdinalIgnoreCase));

        AvailableChains = new ObservableCollection<BackupHistoryChain>(_chains.Build(forDatabase));

        // Nothing is chosen for the user - the same rule the Restore screen learned. Picking a
        // chain is choosing what to restore, and this screen's last button overwrites a database.
        SelectedChain = null;
        ClearVerification();
    }

    partial void OnSelectedChainChanged(BackupHistoryChain? value)
    {
        // A different chain has different files, so anything proved about the last one no longer
        // applies. Leaving the ticks up would let a script be generated from a verification that
        // was never run against these backups.
        ClearVerification();
        OnPropertyChanged(nameof(PathAdvice));
    }

    // ── 4. can the target actually read them ────────────────────────────────────

    [ObservableProperty]
    private ObservableCollection<FileCheckRow> _fileChecks = [];

    [ObservableProperty]
    private bool _filesVerified;

    [ObservableProperty]
    private string _verificationSummary = string.Empty;

    private void ClearVerification()
    {
        FileChecks = [];
        FilesVerified = false;
        VerificationSummary = string.Empty;
        GeneratedScript = string.Empty;
        OnPropertyChanged(nameof(CanGenerate));
    }

    /// <summary>
    /// Asks the TARGET whether it can read every file in the chain.
    ///
    /// On the target and not here, because this app's process can see a share the SQL Server
    /// service account cannot, and the restore runs as that account on that host. A check from here
    /// would pass and the restore would then fail with "Operating system error 5".
    /// </summary>
    [RelayCommand]
    private async Task VerifyFilesAsync()
    {
        if (TargetServer == null)
        {
            SetError("Choose the server the restore will run on.");
            return;
        }

        if (SelectedChain == null)
        {
            SetError("Choose what to restore first.");
            return;
        }

        var ct = _cancellation.Begin();
        IsBusy = true;
        ClearStatus();
        ClearVerification();

        try
        {
            var paths = SelectedChain.Files.Select(Mapping.Apply).ToList();
            var checks = await _sql.CheckBackupFilesAsync(TargetServer, paths, ct);

            FileChecks = new ObservableCollection<FileCheckRow>(
                checks.Select(c => new FileCheckRow(
                    c.Path,
                    c.CanBeRestored,
                    c.CanBeRestored ? "Readable" : c.Explain(TargetServer.ServerName))));

            var failed = checks.FirstOrDefault(c => !c.CanBeRestored);
            FilesVerified = failed == null && checks.Count == paths.Count;

            VerificationSummary = FilesVerified
                ? $"{TargetServer.ServerName} can read all {checks.Count} file(s)."
                : failed?.Explain(TargetServer.ServerName)
                  ?? $"{TargetServer.ServerName} could not read every file.";

            if (!FilesVerified) SetError(VerificationSummary);
            else SetStatus(VerificationSummary);
        }
        catch (OperationCanceledException)
        {
            SetStatus("Stopped.");
        }
        catch (Exception ex)
        {
            SetError($"Could not check the files: {ex.Message}");
        }
        finally
        {
            _cancellation.End();
            IsBusy = false;
            OnPropertyChanged(nameof(CanGenerate));
        }
    }

    [RelayCommand]
    private void Cancel()
    {
        if (_cancellation.CanCancel) _cancellation.Cancel();
    }

    // ── 5. the script ───────────────────────────────────────────────────────────

    [ObservableProperty]
    private string _targetDatabaseName = string.Empty;

    [ObservableProperty]
    private bool _withReplace;

    [ObservableProperty]
    private string _generatedScript = string.Empty;

    /// <summary>
    /// A script is only offered once the target has said it can read the files.
    ///
    /// Not caution for its own sake: the point of this screen is that the source knowing about a
    /// backup says nothing about whether the target can reach it, and a script generated before
    /// that is answered is a script that fails part-way through - after WITH REPLACE has already
    /// dropped the database being restored over.
    /// </summary>
    public bool CanGenerate =>
        FilesVerified && SelectedChain != null && !string.IsNullOrWhiteSpace(TargetDatabaseName);

    partial void OnTargetDatabaseNameChanged(string value) => OnPropertyChanged(nameof(CanGenerate));

    [RelayCommand]
    private void GenerateScript()
    {
        if (!CanGenerate || SelectedChain == null) return;

        // The paths the TARGET confirmed, not the ones msdb recorded - if a mapping is in use, the
        // script has to name the files the way the machine running it reaches them.
        var mapped = MapChain(SelectedChain);

        GeneratedScript = _generator.Generate(
            BackupHistoryChainAdapter.ToRestorableChain(mapped),
            new RestoreOptions
            {
                TargetDatabaseName = TargetDatabaseName,
                WithReplace = WithReplace,
                RecoveryMode = RecoveryMode.Recovery
            });

        SetStatus("Script generated.");
    }

    /// <summary>The chain with every path as the target reaches it.</summary>
    internal BackupHistoryChain MapChain(BackupHistoryChain chain)
    {
        if (!Mapping.IsInUse) return chain;

        return new BackupHistoryChain(
            MapEntry(chain.Full),
            chain.Differential == null ? null : MapEntry(chain.Differential),
            chain.Logs.Select(MapEntry).ToList());
    }

    private BackupHistoryEntry MapEntry(BackupHistoryEntry entry) => new()
    {
        DatabaseName = entry.DatabaseName,
        Type = entry.Type,
        StartedAt = entry.StartedAt,
        FinishedAt = entry.FinishedAt,
        IsCopyOnly = entry.IsCopyOnly,
        FirstLsn = entry.FirstLsn,
        LastLsn = entry.LastLsn,
        CheckpointLsn = entry.CheckpointLsn,
        DatabaseBackupLsn = entry.DatabaseBackupLsn,
        BackupSizeBytes = entry.BackupSizeBytes,
        ServerName = entry.ServerName,
        Files = entry.Files.Select(Mapping.Apply).ToList()
    };

    public bool HasScript => !string.IsNullOrWhiteSpace(GeneratedScript);

    partial void OnGeneratedScriptChanged(string value) => OnPropertyChanged(nameof(HasScript));

    [RelayCommand]
    private void CopyScript() => TryCopyToClipboard(GeneratedScript, "Script copied to clipboard.");
}

/// <summary>
/// One line of the target's answer, for the list on screen.
///
/// The explanation is worked out once, when the check comes back, rather than by the view: it
/// needs the target's name to say anything useful, and a row that outlives the server selection
/// would otherwise start naming the wrong machine.
/// </summary>
/// <param name="Path">The file as the target was asked about it.</param>
/// <param name="CanBeRestored">Whether the target could read it.</param>
/// <param name="Message">What to do about it, in the target's terms.</param>
public sealed record FileCheckRow(string Path, bool CanBeRestored, string Message);
