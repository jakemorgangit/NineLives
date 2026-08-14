using System.Reflection;
using Blackcat.NineLives.Models;
using Blackcat.NineLives.Services;
using Blackcat.NineLives.ViewModels;
using Xunit;

namespace Blackcat.NineLives.Tests;

/// <summary>
/// Every kind of run the app performs leaves a receipt (#438).
///
/// Recording is a convention: <c>Append</c> takes a hand-built entry and nothing in the type
/// system requires a run to leave one. Thirteen call sites have to remember, and the convention
/// had already been missed - the recovery panel ran <c>RESTORE ... WITH RECOVERY</c> and
/// <c>DBCC CHECKDB</c> against a production database at the moment a restore had already failed,
/// the highest-stakes statement this app sends, and recorded nothing at all.
///
/// The trouble with one test per recording site is that it can only ever cover the sites somebody
/// already thought of - which is the same weakness as the convention it is checking.
/// <see cref="EveryOperationKindHasAPathThatRecordsIt"/> is the answer to that: it enumerates the
/// kinds from the source of truth, so a new kind added without a covering path fails here rather
/// than shipping silently.
/// </summary>
public class EveryRunLeavesAReceiptTests
{
    /// <summary>
    /// The kinds proven below. Adding a member to <see cref="OperationKind"/> without adding it
    /// here - and writing the path test that earns it - fails the guard.
    /// </summary>
    private static readonly HashSet<string> Covered =
    [
        OperationKind.Restore,
        OperationKind.Rehearsal,
        OperationKind.Backup,
        OperationKind.Copy,
        OperationKind.Recovery
    ];

    [Fact]
    public void EveryOperationKindHasAPathThatRecordsIt()
    {
        var declared = typeof(OperationKind)
            .GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
            .Where(f => f is { IsLiteral: true, IsInitOnly: false } && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToList();

        var uncovered = declared.Where(k => !Covered.Contains(k)).ToList();

        Assert.True(uncovered.Count == 0,
            $"OperationKind declares {string.Join(", ", uncovered)} with no test proving anything " +
            "records it. A kind of run that leaves no receipt is the defect this guard exists for - " +
            "add the path test, then add the kind to Covered.");

        // And the reverse, so this list cannot rot into a claim about kinds that no longer exist.
        var stale = Covered.Where(k => !declared.Contains(k)).ToList();
        Assert.True(stale.Count == 0, $"Covered names kinds that no longer exist: {string.Join(", ", stale)}");
    }

    /// <summary>
    /// And a receipt nobody can find is barely a receipt. The History screen's filter list is
    /// hand-written on the same "remember to add it" basis as the recording sites - so a kind that
    /// records but cannot be filtered for is the same defect one step downstream.
    /// </summary>
    [Fact]
    public void EveryOperationKindCanBeFilteredForOnTheHistoryScreen()
    {
        var offered = new HistoryViewModel(new FakeOperationHistoryStore()).KindFilters;

        foreach (var kind in Covered)
            Assert.Contains(kind, offered);
    }

    // ── the recovery panel, which was recording nothing ─────────────────────────

    private static (RestoreExecutionViewModel vm, FakeSqlServerService sql, FakeOperationHistoryStore history)
        AfterAFailedRun()
    {
        var sql = new FakeSqlServerService();
        var history = new FakeOperationHistoryStore();
        var vm = new RestoreExecutionViewModel(
            sql, history, TestLogs.Temp(), new OperationCancellation());

        var run = new RestoreRun(
            new ServerConnection { Id = ServerConnection.NewId(), Name = "SRV01", ServerName = "SRV01" },
            "RESTORE DATABASE [Sales] FROM DISK = N'D:\\b.bak' WITH REPLACE, NORECOVERY;",
            "Sales", null, null, "1 full", null, "opts");

        vm.RunAsync(run, _ => Task.FromResult(CredentialPreflight.Proceed)).GetAwaiter().GetResult();

        // The restore's own receipt is not what these are about.
        history.Entries.Clear();

        return (vm, sql, history);
    }

    private static RecoveryAction BringOnline() => new(
        "Bring the database online",
        "RESTORE DATABASE [Sales] WITH RECOVERY",
        "caution");

    [Fact]
    public async Task ARecoveryActionThatSucceedsIsRecorded()
    {
        var (vm, _, history) = AfterAFailedRun();

        await vm.RunRecoveryActionCommand.ExecuteAsync(BringOnline());

        var receipt = Assert.Single(history.Entries);
        Assert.Equal(OperationKind.Recovery, receipt.Kind);
        Assert.Equal(OperationOutcome.Succeeded, receipt.Outcome);
        Assert.Equal("Sales", receipt.TargetDatabase);
        Assert.Equal("SRV01", receipt.ServerName);

        // The statement itself, because that is the thing an incident write-up is missing without
        // this - the restore failed, and THIS is what brought the database back.
        Assert.Contains("WITH RECOVERY", receipt.Script);
    }

    [Fact]
    public async Task ARecoveryActionThatFailsIsRecordedWithTheReason()
    {
        var (vm, sql, history) = AfterAFailedRun();
        sql.PollingExecuteThrows = new InvalidOperationException("Msg 3013: RESTORE DATABASE is terminating");

        await vm.RunRecoveryActionCommand.ExecuteAsync(BringOnline());

        var receipt = Assert.Single(history.Entries);
        Assert.Equal(OperationKind.Recovery, receipt.Kind);
        Assert.Equal(OperationOutcome.Failed, receipt.Outcome);
        Assert.Contains("Msg 3013", receipt.ErrorMessage);
    }

    /// <summary>
    /// Cancelled counts. "I stopped it and the database was left alone" is exactly what a change
    /// ticket needs to say, and until now the panel said it only on screen, where it scrolls away.
    /// </summary>
    [Fact]
    public async Task AStoppedRecoveryActionIsRecordedAsCancelledNotFailed()
    {
        var queries = new OperationCancellation();
        var sql = new FakeSqlServerService();
        var history = new FakeOperationHistoryStore();
        var vm = new RestoreExecutionViewModel(sql, history, TestLogs.Temp(), queries);

        var run = new RestoreRun(
            new ServerConnection { Id = ServerConnection.NewId(), Name = "SRV01", ServerName = "SRV01" },
            "RESTORE DATABASE [Sales] FROM DISK = N'D:\b.bak' WITH REPLACE, NORECOVERY;",
            "Sales", null, null, "1 full", null, "opts");
        await vm.RunAsync(run, _ => Task.FromResult(CredentialPreflight.Proceed));
        history.Entries.Clear();

        // The shape the field bug arrives in (#427): the token is signalled mid-statement and the
        // driver throws its OWN error rather than an OperationCanceledException. Staging only the
        // token would let the statement run to completion, and a step that finished is honestly a
        // success - it is the interrupted one that must not be filed as a failure.
        sql.DuringPollingExecute = queries.Cancel;
        sql.PollingExecuteThrows = new InvalidOperationException(
            "A severe error occurred on the current command. The results, if any, should be discarded.");

        await vm.RunRecoveryActionCommand.ExecuteAsync(BringOnline());

        var receipt = Assert.Single(history.Entries);
        Assert.Equal(OperationKind.Recovery, receipt.Kind);
        Assert.Equal(OperationOutcome.Cancelled, receipt.Outcome);
        Assert.Contains("same state as before", receipt.ErrorMessage);

        // And the receipt agrees with what the panel says on screen, which is the whole point of
        // writing it down.
        Assert.Contains("Cancelled", vm.ActionOutcome);
    }
}
