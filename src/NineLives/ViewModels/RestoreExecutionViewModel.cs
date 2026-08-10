using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using Blackcat.NineLives.Models;
using Blackcat.NineLives.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;

namespace Blackcat.NineLives.ViewModels;

/// <summary>
/// Everything one execution needs to know, captured at the moment the confirm is pressed.
///
/// A record rather than a set of properties read as the run goes along: the restore takes minutes,
/// the screen behind it stays live, and reading the target database name at the end of a run that
/// started against a different one is the class of bug this whole seam exists to make impossible.
/// </summary>
/// <summary>What a run IS - a real restore, or a rehearsal of one (#238).</summary>
public enum RunKind
{
    Restore = 0,
    Rehearsal = 1
}

public sealed record RestoreRun(
    ServerConnection Server,
    string Script,
    string TargetDatabase,
    string? SourceDatabase,
    string? ContainerName,
    string ChainSummary,
    DateTime? RestorePointTimestamp,
    string OptionsForLog,
    RunKind Kind = RunKind.Restore);

/// <summary>
/// Running a restore: arming, the countdown, execution, cancellation, what to do when it fails,
/// and filing the record afterwards.
///
/// Sixth seam out of RestoreViewModel (#115), and the one the issue called highest-stakes: it is
/// the only code here that writes to somebody's database. It had no reason to sit next to timeline
/// tick-mark maths, and the parts of it that matter - that a cancelled restore is reported as
/// loudly as a failed one, that the history records executions rather than button presses, that
/// the console is flushed before it is recorded - were reachable only by standing up a container,
/// a listing, a chain and a connection.
/// </summary>
public partial class RestoreExecutionViewModel : ViewModelBase
{
    private readonly IRunNotifier _notifier;
    private readonly ISqlServerService _sql;
    private readonly IRestoreHistoryStore _history;
    private readonly OperationLog _log;

    /// <summary>Its own source: the Stop button during a restore is not the same button as the
    /// one that stops a verify, and the two must be able to be in different states.</summary>
    private readonly OperationCancellation _executeCancellation = new();

    /// <summary>
    /// The parent's, shared with its other server calls, because a recovery action runs after the
    /// restore has finished and belongs to the same Stop button as the rest of them (#111).
    /// </summary>
    private readonly OperationCancellation _queryCancellation;

    public RestoreExecutionViewModel(
        ISqlServerService sql,
        IRestoreHistoryStore history,
        OperationLog log,
        OperationCancellation queryCancellation,
        IRunNotifier? notifier = null)
    {
        _notifier = notifier ?? NullRunNotifier.Instance;
        _sql = sql;
        _history = history;
        _log = log;
        _queryCancellation = queryCancellation;

        // Every console message is written to the log file as it arrives, so the file cannot drift
        // from what was on screen.
        Console = new ConsoleBuffer(message => _log.Info($"[execute] {message.Trim()}"));
    }

    /// <summary>
    /// The execution console. Its batching and line handling live in ConsoleBuffer (#115 seam 1) -
    /// this type only feeds it and shows it.
    /// </summary>
    public ConsoleBuffer Console { get; }

    /// <summary>Raised whenever the cancel state changes, so the parent can refresh its own view of it.</summary>
    public event Action? CancelStateChanged;

    // ── arming ──────────────────────────────────────────────────────────────────

    [ObservableProperty]
    private bool _isArmed;

    [ObservableProperty]
    private string _buttonText = "Execute on Server";

    [ObservableProperty]
    private int _countdown;

    private CancellationTokenSource? _armTimeoutCts;

    /// <summary>
    /// Arms the button for five seconds.
    ///
    /// The countdown is not decoration: it is the gap in which somebody reads the confirmation
    /// banner naming the server and the target, and it disarms itself so a button left armed
    /// while attention moved elsewhere cannot be pressed later by accident.
    /// </summary>
    public void Arm()
    {
        IsArmed = true;
        ButtonText = "Confirm Execute (5)";
        Countdown = 5;

        _armTimeoutCts?.Cancel();
        _armTimeoutCts = new CancellationTokenSource();

        _ = RunArmCountdownAsync(_armTimeoutCts.Token);
    }

    public void Disarm()
    {
        _armTimeoutCts?.Cancel();
        IsArmed = false;
        ButtonText = "Execute on Server";
    }

    private async Task RunArmCountdownAsync(CancellationToken ct)
    {
        try
        {
            for (var remaining = 5; remaining > 0; remaining--)
            {
                Countdown = remaining;
                ButtonText = $"Confirm Execute ({remaining})";
                await Task.Delay(1000, ct);
            }

            Disarm();
        }
        catch (OperationCanceledException)
        {
            // Either it was pressed, or something else disarmed it. Both are handled where they
            // happen; there is nothing to do here.
        }
    }

    // ── running ─────────────────────────────────────────────────────────────────

    [ObservableProperty]
    private bool _isExecuting;

    [ObservableProperty]
    private bool _executionSuccess;

    [ObservableProperty]
    private bool _executionComplete;

    /// <summary>
    /// True while a restore is running and has not been asked to stop.
    ///
    /// Held rather than computed, as it was before this seam: the Stop button binds to it, and a
    /// test has to be able to put the screen into the state where that button is offered without
    /// standing up a whole restore to do it.
    /// </summary>
    [ObservableProperty]
    private bool _canCancel;

    [ObservableProperty]
    private bool _isCancelling;

    /// <summary>
    /// Runs one restore, start to finish.
    /// </summary>
    /// <param name="run">What to execute, captured before anything started.</param>
    /// <param name="preflight">
    /// The server-side credential check (#115 seam 5). A refusal has written nothing, so the run is
    /// abandoned without a history entry - it was never attempted.
    /// </param>
    public async Task RunAsync(RestoreRun run, Func<Action<string>, Task<CredentialPreflight>> preflight)
    {
        Disarm();

        // The recovery steps that may follow must agree with the run that produced them, even if
        // the screen behind has moved on to a different server or target since.
        _lastServer = run.Server;
        _lastTarget = run.TargetDatabase;

        var startedAt = DateTime.Now;
        var outcome = RestoreOutcome.Failed;
        string? failure = null;

        // Set false when the run is abandoned before anything was attempted, so the history
        // records executions rather than button presses.
        var attempted = true;

        var executeToken = _executeCancellation.Begin();
        IsExecuting = true;
        Console.IsRunning = true;

        // Reset alongside ExecutionComplete. Left over from a previous run it could combine with an
        // early bail-out to show the success banner - and write "Outcome: Succeeded" into a saved
        // log - for a restore that never ran.
        ExecutionSuccess = false;
        ExecutionComplete = false;
        Console.Clear();
        RecoveryActions = [];
        HasRecoveryActions = false;
        PostRestoreActions = [];
        HasPostRestoreActions = false;
        PostRestoreMessage = string.Empty;

        // The denominator comes from the script itself, so it can never disagree with what runs.
        _progress = new RestoreProgress(RestoreProgress.CountStatements(run.Script));
        ProgressValue = 0;
        ProgressText = string.Empty;
        TaskbarValue = 0;
        TaskbarState = System.Windows.Shell.TaskbarItemProgressState.Normal;

        RaiseCancelStateChanged();

        try
        {
            var check = await preflight(AppendLog);
            if (!check.CanProceed)
            {
                attempted = false;
                SetError(check.Refusal!);
                return;
            }

            _log.ServerChange(run.Server.ServerName,
                $"restore starting: target [{run.TargetDatabase}], {run.ChainSummary}, {run.OptionsForLog}");

            AppendLog("Beginning restore execution...\n");

            // Announced AFTER the preflights: a run the preflight refused never started, and
            // "started" messages for runs that went nowhere teach people to ignore the channel.
            _notifier.Notify(new RunNotification(
                RunPhase.Started, run.Kind == RunKind.Rehearsal ? "Rehearsal" : "Restore",
                run.TargetDatabase, run.Server.ServerName, run.ChainSummary));

            await _sql.ExecuteWithProgressAsync(
                run.Server,
                run.Script,
                // InvokeAsync, not Invoke. This callback runs on the connection's thread when SQL
                // Server sends an info message, and a synchronous Invoke BLOCKS that thread until
                // the UI has finished handling it. With the UI busy re-rendering, progress backed
                // up and then arrived in bursts - which is precisely what "not live" looked like.
                msg =>
                {
                    // The progress model reads every line on this thread - it is cheap and its
                    // outputs are scalars, which WPF marshals itself (#204).
                    TrackProgress(msg);

                    // InvokeAsync when a dispatcher exists and this is not its thread - see the
                    // note above. Null when there is no Application at all (headless: the tests),
                    // where appending directly is both safe and the only option.
                    var dispatcher = Application.Current?.Dispatcher;
                    if (dispatcher == null || dispatcher.CheckAccess()) AppendLog(msg);
                    else dispatcher.InvokeAsync(() => AppendLog(msg));
                },
                executeToken);

            ExecutionSuccess = true;
            outcome = RestoreOutcome.Succeeded;

            // The run IS over, whatever the last STATS line said - a small final statement often
            // finishes without reporting 100.
            ProgressValue = 100;
            ProgressText = _progress is { TotalStatements: > 1 }
                ? $"All {_progress.TotalStatements} statements complete"
                : "Complete";
            TaskbarValue = 1;

            AppendLog("\nRestore completed successfully!");
            SetStatus("Restore execution completed successfully.");

            _notifier.Notify(new RunNotification(
                RunPhase.Succeeded, run.Kind == RunKind.Rehearsal ? "Rehearsal" : "Restore",
                run.TargetDatabase, run.Server.ServerName,
                run.Kind == RunKind.Rehearsal
                    ? "Restored, CHECKDB passed, scratch copy dropped. The backup is proven."
                    : run.ChainSummary,
                DateTime.Now - startedAt));

            // The restore is not the end of the job (#205): nobody has verified the data yet, and
            // on a different server every SQL-auth user is orphaned. Reported while the outcome is
            // on screen, in the same shape as the recovery panel - read, then run.
            //
            // Not for a rehearsal (#238): its CHECKDB already ran inside the script, and the
            // database the panel would offer statements against was deliberately dropped.
            if (run.Kind != RunKind.Rehearsal)
                await ReportPostRestoreAsync(run.Server, run.TargetDatabase);
        }
        catch (OperationCanceledException)
        {
            outcome = RestoreOutcome.Cancelled;
            failure = "Cancelled by the user part-way through the chain.";

            // Cancelling a restore is not the same as never having run it. SqlCommand.Cancel stops
            // the client waiting and SQL Server rolls back the statement that was in flight, but
            // the target stays mid-restore - so this must be as loud as a failure, and it goes
            // through the same recovery guidance (#14, #25).
            ExecutionSuccess = false;
            AppendLog("\nCANCELLED. The statement in flight was rolled back by SQL Server, but the " +
                      "restore stopped part-way through the chain.");

            _log.ServerChange(run.Server.ServerName,
                $"restore CANCELLED by user, target [{run.TargetDatabase}]");

            SetError("Restore cancelled. The target database has been left mid-restore - see below.");

            _notifier.Notify(new RunNotification(
                RunPhase.Problem, run.Kind == RunKind.Rehearsal ? "Rehearsal" : "Restore",
                run.TargetDatabase, run.Server.ServerName,
                "Cancelled part-way through the chain - the target is left mid-restore.",
                DateTime.Now - startedAt));

            await ReportRecoveryStateAsync(run.Server, run.TargetDatabase);
        }
        catch (Exception ex)
        {
            ExecutionSuccess = false;
            outcome = RestoreOutcome.Failed;
            failure = ex.Message;
            AppendLog($"\nERROR: {ex.Message}");
            SetError($"Restore failed: {ex.Message}");

            _notifier.Notify(new RunNotification(
                RunPhase.Problem, run.Kind == RunKind.Rehearsal ? "Rehearsal" : "Restore",
                run.TargetDatabase, run.Server.ServerName,
                ex.Message, DateTime.Now - startedAt));

            // The restore has stopped part-way and the target is almost certainly not usable. Find
            // out exactly how, and say so, while the connection is still open (#14).
            await ReportRecoveryStateAsync(run.Server, run.TargetDatabase);
        }
        finally
        {
            // Error stays red on the taskbar until the next run - it is the signal somebody who
            // alt-tabbed away most needs to see. Success clears: a full green bar that never
            // leaves reads as "still running" from the taskbar alone.
            TaskbarState = ExecutionSuccess
                ? System.Windows.Shell.TaskbarItemProgressState.None
                : System.Windows.Shell.TaskbarItemProgressState.Error;
            if (!ExecutionSuccess) TaskbarValue = 1;

            // Told, not interrupted: the amber taskbar flash, only when none of our windows is
            // foreground, and stops the moment the app is clicked.
            WindowAttention.FlashIfInBackground();

            _executeCancellation.End();
            IsExecuting = false;
            Console.IsRunning = false;
            ExecutionComplete = true;
            Console.Flush();   // nothing buffered may outlive the run
            RaiseCancelStateChanged();

            // After the flush, so the recorded log is the whole console rather than whatever had
            // made it out of the batching buffer. Recorded for every outcome including cancelled -
            // "I stopped it" is exactly the kind of thing a change ticket needs to say.
            if (attempted) RecordHistory(run, startedAt, outcome, failure);
        }
    }

    /// <summary>
    /// Files this execution in the history (#31). Never throws: the store swallows its own
    /// failures, and a restore must not be reported as failed because a record could not be kept.
    /// </summary>
    private void RecordHistory(RestoreRun run, DateTime startedAt, RestoreOutcome outcome, string? failure)
        => _history.Append(new RestoreHistoryEntry
        {
            StartedAt = startedAt,
            CompletedAt = DateTime.Now,
            ServerName = run.Server.ServerName,
            TargetDatabase = run.TargetDatabase,
            ContainerName = run.ContainerName,
            SourceDatabase = run.SourceDatabase,
            RestorePointTimestamp = run.RestorePointTimestamp,
            ChainSummary = run.ChainSummary,
            Kind = run.Kind == RunKind.Rehearsal ? "Rehearsal" : "Restore",
            Outcome = outcome,
            ErrorMessage = failure,
            Script = run.Script,
            Log = Console.Text
        });

    /// <summary>
    /// Stops a running restore.
    ///
    /// Deliberately worded as a warning rather than a neutral action: stopping a restore part-way
    /// leaves the target database in RESTORING, which is not a state anyone wants to discover by
    /// accident. The recovery panel that appears afterwards explains how to get out of it.
    /// </summary>
    [RelayCommand]
    private void Cancel()
    {
        if (!_executeCancellation.CanCancel) return;

        _executeCancellation.Cancel();
        RaiseCancelStateChanged();
        AppendLog("\nCancellation requested - waiting for SQL Server to roll back the current statement...");
    }

    // ── afterwards ──────────────────────────────────────────────────────────────

    [ObservableProperty]
    private ObservableCollection<RecoveryAction> _recoveryActions = [];

    [ObservableProperty]
    private bool _hasRecoveryActions;

    [ObservableProperty]
    private string _recoveryStateMessage = string.Empty;

    /// <summary>
    /// After a failed restore, works out what state the target database was left in and offers the
    /// statements that put it right.
    ///
    /// This is the moment of maximum stress - a restore has failed mid-incident and the database is
    /// now in a state that blocks other connections. Leaving somebody to work that out from a raw
    /// SQL error, when the app is still holding the connection that could tell them, is the wrong
    /// place to stop.
    /// </summary>
    // ── progress, as a number again (#204) ──────────────────────────────────────

    /// <summary>Overall progress across the chain, 0-100, from the server's own STATS lines.</summary>
    [ObservableProperty]
    private double _progressValue;

    /// <summary>"Statement 2 of 5 - 60%", or just the percent for a one-statement run.</summary>
    [ObservableProperty]
    private string _progressText = string.Empty;

    /// <summary>
    /// The taskbar mirror, so the app conveys progress while minimised. 0-1 because that is the
    /// scale TaskbarItemInfo speaks.
    /// </summary>
    [ObservableProperty]
    private double _taskbarValue;

    [ObservableProperty]
    private System.Windows.Shell.TaskbarItemProgressState _taskbarState =
        System.Windows.Shell.TaskbarItemProgressState.None;

    private RestoreProgress? _progress;

    /// <summary>
    /// Feeds one console line into the progress model and mirrors the result. Runs on the
    /// connection's message thread; the scalar properties are safe to set there - WPF marshals
    /// scalar INPC to the UI thread itself.
    /// </summary>
    private void TrackProgress(string message)
    {
        if (_progress == null || !_progress.Feed(message)) return;

        ProgressValue = _progress.OverallPercent;
        ProgressText = _progress.Describe();
        TaskbarValue = _progress.OverallPercent / 100.0;
    }

    // ── after a SUCCESSFUL restore (#205) ───────────────────────────────────────

    /// <summary>What finishing the job involves, shown under the success banner.</summary>
    [ObservableProperty]
    private ObservableCollection<RecoveryAction> _postRestoreActions = [];

    [ObservableProperty]
    private bool _hasPostRestoreActions;

    /// <summary>The overview and the orphan verdict, as one readable paragraph.</summary>
    [ObservableProperty]
    private string _postRestoreMessage = string.Empty;

    /// <summary>
    /// The counterpart of <see cref="ReportRecoveryStateAsync"/> for the restore that WORKED.
    ///
    /// CHECKDB is offered on every success - the restore is the cheapest moment to find corruption,
    /// and it proves the backup rather than just the copy. The orphan scan runs automatically
    /// because it is one cheap query, and its findings are the single most common post-restore
    /// fault: login succeeds, database access fails, and nothing on screen says why.
    ///
    /// Best effort throughout. A restore that succeeded must never LOOK failed because the
    /// follow-up queries could not run.
    /// </summary>
    private async Task ReportPostRestoreAsync(ServerConnection server, string targetDatabase)
    {
        var actions = new List<RecoveryAction> { PostRestoreAdvice.CheckDb(targetDatabase) };
        var message = new List<string>();

        try
        {
            var overview = await _sql.GetDatabaseOverviewAsync(server, targetDatabase);
            if (overview != null) message.Add(overview.Describe(targetDatabase));
        }
        catch (Exception ex)
        {
            AppendLog($"\nCould not read [{targetDatabase}]'s properties: {ex.Message}");
        }

        try
        {
            var orphans = await _sql.FindOrphanedUsersAsync(server, targetDatabase);
            message.Add(PostRestoreAdvice.DescribeOrphans(orphans));

            foreach (var orphan in orphans)
            {
                actions.Add(orphan.HasSameNamedLogin
                    ? PostRestoreAdvice.FixOrphan(targetDatabase, orphan)
                    : PostRestoreAdvice.ExplainUnmappableOrphan(targetDatabase, orphan));
            }
        }
        catch (Exception ex)
        {
            AppendLog($"\nCould not check [{targetDatabase}] for orphaned users: {ex.Message}");
        }

        PostRestoreMessage = string.Join(" ", message);
        PostRestoreActions = new ObservableCollection<RecoveryAction>(actions);
        HasPostRestoreActions = true;

        AppendLog($"\n{PostRestoreMessage}");
    }

    private async Task ReportRecoveryStateAsync(ServerConnection server, string targetDatabase)
    {
        RecoveryActions = [];
        HasRecoveryActions = false;
        RecoveryStateMessage = string.Empty;

        try
        {
            var state = await _sql.GetDatabaseRecoveryStateAsync(server, targetDatabase);
            if (!state.NeedsAttention)
            {
                if (!state.Exists)
                    AppendLog($"\n[{targetDatabase}] is not on the server - nothing was left behind.");
                return;
            }

            RecoveryStateMessage = state.Explain(targetDatabase);
            RecoveryActions = new ObservableCollection<RecoveryAction>(state.SuggestedActions(targetDatabase));
            HasRecoveryActions = RecoveryActions.Count > 0;

            AppendLog($"\n{RecoveryStateMessage}");
            foreach (var action in RecoveryActions)
                AppendLog($"\n  {action.Title}:  {action.Sql}");
        }
        catch (Exception ex)
        {
            // Best effort. The restore failure is the news; failing to describe the aftermath must
            // not replace the error the user actually needs to read.
            AppendLog($"\nCould not check the state of [{targetDatabase}]: {ex.Message}");
        }
    }

    /// <summary>The server and target the last run used, so a recovery step knows where to go.</summary>
    private ServerConnection? _lastServer;
    private string _lastTarget = string.Empty;

    /// <summary>Runs one remediation the user picked, then re-reads the state.</summary>
    [RelayCommand]
    private async Task RunRecoveryActionAsync(RecoveryAction? action)
    {
        if (action == null || _lastServer == null) return;

        // Cancellable, and it needs it more than most: this runs when a restore has already failed,
        // RESTORE ... WITH RECOVERY can take a long time on a large database, and it goes out at
        // CommandTimeout = 0. Without a Stop the only way out was killing the process - at the
        // exact moment the user is trying to get their database back.
        var ct = _queryCancellation.Begin();
        RaiseCancelStateChanged();

        try
        {
            AppendLog($"\nRunning: {action.Sql}");
            await _sql.ExecuteRecoveryActionAsync(_lastServer, action.Sql, ct);
            AppendLog("Completed.");
            await ReportRecoveryStateAsync(_lastServer, _lastTarget);

            if (!HasRecoveryActions)
                SetStatus($"[{_lastTarget}] is back to a usable state.");
        }
        catch (OperationCanceledException)
        {
            // SQL Server rolls back the statement that was in flight, so the database is where it
            // was before this step - which is still whatever the failed restore left behind.
            AppendLog("\nCancelled. The database is in the same state as before this step.");
            SetStatus("Recovery step cancelled.");
        }
        catch (Exception ex)
        {
            AppendLog($"\nERROR: {ex.Message}");
            SetError($"Recovery step failed: {ex.Message}");
        }
        finally
        {
            _queryCancellation.End();
            RaiseCancelStateChanged();
        }
    }

    [RelayCommand]
    private void CopyRecoveryAction(RecoveryAction? action)
    {
        if (action != null)
            TryCopyToClipboard(action.Sql, "Recovery statement copied to clipboard.");
    }

    // ── the console's own actions ───────────────────────────────────────────────

    [RelayCommand]
    private void CopyConsole() => TryCopyToClipboard(Console.Text, "Execution log copied to clipboard.");

    [RelayCommand]
    private void SaveConsole()
    {
        if (string.IsNullOrEmpty(Console.Text)) return;

        var dialog = new SaveFileDialog
        {
            Filter = "Text Files (*.txt)|*.txt|All Files (*.*)|*.*",
            DefaultExt = ".txt",
            FileName = $"ninelives_execution_{DateTime.Now:yyyyMMdd_HHmmss}.txt"
        };

        if (dialog.ShowDialog() != true) return;

        try
        {
            File.WriteAllText(dialog.FileName, Console.Text);
            SetStatus($"Execution log saved to {dialog.FileName}");
        }
        catch (Exception ex)
        {
            SetError($"Could not save the log: {ex.Message}");
        }
    }

    /// <summary>
    /// Appends to the on-screen console and to the log file at the same time, so the file cannot
    /// drift from what the user was shown. Redaction happens inside the log, not here (#40).
    /// </summary>
    private void AppendLog(string message) => Console.Append(message);

    private void RaiseCancelStateChanged()
    {
        CanCancel = _executeCancellation.CanCancel;
        IsCancelling = _executeCancellation.IsCancelling;
        CancelStateChanged?.Invoke();
    }

}
