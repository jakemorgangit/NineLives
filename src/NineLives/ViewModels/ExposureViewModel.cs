using System.Collections.ObjectModel;
using Blackcat.NineLives.Models;
using Blackcat.NineLives.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Blackcat.NineLives.ViewModels;

/// <summary>
/// The exposure dashboard (#239): how much data would be lost, right now, per database, across
/// every configured server - with the worst thing first.
///
/// The question a DBA actually lives with is not "do I have backups?" but "how exposed am I right
/// now?", and until this screen answering it meant a query against every instance's msdb and
/// mental arithmetic. The silent failures are the loudest here: FULL recovery with no log backups,
/// chains that stopped days ago, databases never backed up at all - and a server that will not
/// answer is itself a red row, because silence is exactly what this screen exists to make visible.
/// </summary>
public partial class ExposureViewModel : ViewModelBase
{
    private readonly ICredentialStore _store;
    private readonly ISqlServerService _sql;
    private readonly IOperationHistoryStore _history;
    private readonly OperationLog _log;
    private readonly OperationCancellation _sweepCancellation = new();

    public ExposureViewModel(
        ICredentialStore store, ISqlServerService sql, IOperationHistoryStore history,
        OperationLog? log = null)
    {
        _store = store;
        _sql = sql;
        _history = history;
        _log = log ?? App.Log;
    }

    [ObservableProperty]
    private ObservableCollection<ExposureRow> _rows = [];

    [ObservableProperty]
    private string _summary = string.Empty;

    [ObservableProperty]
    private string _lastRefreshed = string.Empty;

    [ObservableProperty]
    private bool _isSweeping;

    /// <summary>True while a sweep is running and has not been asked to stop (#287).</summary>
    [ObservableProperty]
    private bool _canCancelSweep;

    /// <summary>
    /// Whether a sweep has ever been ATTEMPTED (#287). The shell's first-visit auto-sweep used
    /// to infer this from Rows.Count == 0 - so an estate that answered "no rows" (or a sweep
    /// that failed) re-triggered a full sweep of every server on every later visit, the exact
    /// opposite of "Refresh stays deliberate after the first".
    /// </summary>
    public bool HasSwept { get; private set; }

    /// <summary>
    /// Stops an in-progress sweep. A dozen servers with several unreachable is minutes of
    /// timeouts, and until now there was no way out of it.
    /// </summary>
    [RelayCommand]
    private void StopSweep()
    {
        _sweepCancellation.Cancel();
        CanCancelSweep = false;
        SetStatus("Cancelling...");
    }

    /// <summary>
    /// The connection each row's numbers actually came from (#287). By row identity, not by
    /// server name: two entries for the same instance with different credentials are different
    /// connections, and the one-click handoff must hand over the one that produced the row.
    /// </summary>
    private Dictionary<ExposureRow, ServerConnection> _ownerByRow = new();

    /// <summary>Raised when somebody wants to act on a row. The shell wires it (#202's pattern).</summary>
    public event Action<BrowseHandoff>? RestoreRequested;

    /// <summary>
    /// From seeing to acting in one click: the row's server and database, handed to the restore
    /// screen through the source that MUST know these backups - the server's own msdb, which is
    /// exactly where the row's numbers came from. A red row's distance to being acted on should
    /// be one click, or the dashboard is a wall of guilt rather than a to-do list.
    /// </summary>
    [RelayCommand]
    private void RestoreFromRow(ExposureRow? row)
    {
        if (row == null || row.IsUnreachable) return;
        if (!_ownerByRow.TryGetValue(row, out var server)) return;

        RestoreRequested?.Invoke(new BrowseHandoff(
            BackupMedium.SharedPath, null, server, row.ServerName, row.DatabaseName));
    }

    /// <summary>
    /// Asks every configured server, in parallel, and shows the worst first.
    ///
    /// A server that will not answer becomes an alarm row rather than a silent absence - a
    /// dashboard that quietly omits the unreachable server reads as "all clear" at the exact
    /// moment it should not.
    /// </summary>
    [RelayCommand]
    private async Task RefreshAsync()
    {
        var ct = _sweepCancellation.Begin();
        IsSweeping = true;
        CanCancelSweep = true;
        ClearStatus();

        try
        {
            List<ServerConnection> servers;
            try
            {
                servers = _store.LoadConfig().Servers;
            }
            catch (Exception ex)
            {
                SetError($"The server list could not be read: {ex.Message}");
                return;
            }

            if (servers.Count == 0)
            {
                Rows = [];
                Summary = "No servers configured. Add one on the SQL Servers screen and the " +
                          "exposure of every database on it appears here.";
                return;
            }

            var now = DateTime.Now;

            var sweeps = servers.Select(async server =>
            {
                try
                {
                    return (server, rows: await _sql.GetBackupExposureAsync(server, ct));
                }
                catch (OperationCanceledException)
                {
                    // Stopping is the user's doing, not the server's silence - it must not
                    // fabricate an UNREACHABLE alarm row.
                    throw;
                }
                catch (Exception ex)
                {
                    _log.Warn($"[exposure] {server.ServerName}: {ex.Message}");
                    return (server, rows: (List<ExposureRow>)
                    [
                        new ExposureRow
                        {
                            ServerName = server.ServerName,
                            DatabaseName = "(server did not answer)",
                            StateDescription = "UNREACHABLE",
                            IsUnreachable = true,
                            Level = ExposureLevel.Alarm,
                            Verdict = $"The server would not answer: {ex.Message} Its databases' " +
                                      "exposure is UNKNOWN, which is not the same as fine."
                        }
                    ]);
                }
            }).ToList();

            var results = await Task.WhenAll(sweeps);

            var owners = new Dictionary<ExposureRow, ServerConnection>();
            foreach (var (server, serverRows) in results)
                foreach (var row in serverRows)
                    owners[row] = server;
            _ownerByRow = owners;

            var rows = results.SelectMany(r => r.rows).ToList();

            // The advisor judges everything that answered; unreachable rows arrive pre-judged.
            foreach (var row in rows.Where(r => !r.IsUnreachable))
                ExposureAdvisor.Judge(row, now);

            AttachRehearsalReceipts(rows);

            Rows = new ObservableCollection<ExposureRow>(rows
                .OrderByDescending(r => r.Level)
                .ThenBy(r => r.RecoverableTo ?? DateTime.MinValue)
                .ThenBy(r => r.ServerName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(r => r.DatabaseName, StringComparer.OrdinalIgnoreCase));

            var alarms = rows.Count(r => r.Level == ExposureLevel.Alarm);
            var warnings = rows.Count(r => r.Level == ExposureLevel.Warning);

            Summary = alarms == 0 && warnings == 0
                ? $"All {rows.Count} database(s) across {servers.Count} server(s) are inside their windows."
                : $"{alarms} alarm(s), {warnings} warning(s) across {servers.Count} server(s) - worst first.";

            LastRefreshed = $"as of {now:yyyy-MM-dd HH:mm:ss}";
        }
        catch (OperationCanceledException)
        {
            // The rows on screen are the previous sweep's complete answer; stopping this one
            // does not invalidate them.
            SetStatus("Sweep cancelled.");
        }
        catch (Exception ex)
        {
            // The auto-sweep is fired without an awaiter, so anything escaping here used to
            // vanish - an empty dashboard with no explanation (#287).
            SetError($"The sweep failed: {ex.Message}");
        }
        finally
        {
            HasSwept = true;
            _sweepCancellation.End();
            IsSweeping = false;
            CanCancelSweep = false;
        }
    }

    /// <summary>
    /// Joins the app's own receipts (#238): when a rehearsal last PROVED each database restores.
    /// Recovery-point arithmetic says what COULD be restored; only a rehearsal says it actually
    /// does.
    /// </summary>
    private void AttachRehearsalReceipts(List<ExposureRow> rows)
    {
        try
        {
            var proofs = _history.Load()
                .Where(e => e.Kind == "Rehearsal" && e.Outcome == OperationOutcome.Succeeded &&
                            e.SourceDatabase != null)
                .GroupBy(e => (e.ServerName, e.SourceDatabase!),
                    StringTupleComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    g => g.Key,
                    g => g.OrderByDescending(e => e.CompletedAt).First(),
                    StringTupleComparer.OrdinalIgnoreCase);

            foreach (var row in rows)
            {
                if (!proofs.TryGetValue((row.ServerName, row.DatabaseName), out var proof)) continue;

                row.LastProven = proof.CompletedAt;

                // The receipt measured the real restore-plus-CHECKDB time - the RTO number that
                // conversations otherwise invent.
                if (proof.CompletedAt > proof.StartedAt)
                    row.MeasuredRestore = proof.CompletedAt - proof.StartedAt;
            }
        }
        catch
        {
            // Receipts are a bonus column; their absence must not empty the dashboard.
        }
    }

    /// <summary>Case-insensitive comparer for (server, database) pairs.</summary>
    private sealed class StringTupleComparer : IEqualityComparer<(string, string)>
    {
        public static readonly StringTupleComparer OrdinalIgnoreCase = new();

        public bool Equals((string, string) x, (string, string) y) =>
            string.Equals(x.Item1, y.Item1, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(x.Item2, y.Item2, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string, string) pair) =>
            HashCode.Combine(
                pair.Item1.ToUpperInvariant(), pair.Item2.ToUpperInvariant());
    }
}
