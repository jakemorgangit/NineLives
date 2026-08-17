using System.IO;
using Blackcat.NineLives.Models;
using Blackcat.NineLives.Services;
using Blackcat.NineLives.ViewModels;
using Xunit;

namespace Blackcat.NineLives.Tests;

/// <summary>
/// The execution seam on its own (#115 seam 6).
///
/// This is the only code on the Restore screen that writes to somebody's database, and reaching it
/// used to mean standing up a container, a listing, a chain and a connection. The rules that matter
/// most - a cancelled restore is recorded as loudly as a failed one, the history records executions
/// rather than button presses, the console is flushed before it is recorded - are now reachable
/// directly.
/// </summary>
[Collection(WpfCollection.Name)]
public class RestoreExecutionSeamTests
{
    private static OperationLog ThrowawayLog() => new(Path.Combine(
        Path.GetTempPath(), "ninelives-exec-seam", Guid.NewGuid().ToString("n")));

    private static ServerConnection Server() => new()
    {
        Id = ServerConnection.NewId(),
        Name = "SRV01",
        ServerName = "SRV01"
    };

    private static RestoreRun Run() => new(
        Server(),
        "RESTORE DATABASE [MyDb_Restored] FROM URL = 'https://acct/backups/full.bak'",
        "MyDb_Restored",
        "MyDb",
        "backups",
        "Full + 2 logs",
        new DateTime(2026, 8, 1, 22, 9, 12),
        "WITH REPLACE=True, recovery=Recovery, stopAt=none");

    private static (RestoreExecutionViewModel vm, FakeSqlServerService sql, FakeOperationHistoryStore history) New()
    {
        var sql = new FakeSqlServerService();
        var history = new FakeOperationHistoryStore();
        var vm = new RestoreExecutionViewModel(sql, history, ThrowawayLog(), new OperationCancellation());
        return (vm, sql, history);
    }

    private static Task<CredentialPreflight> Proceed(Action<string> _) => Task.FromResult(CredentialPreflight.Proceed);

    // ── arming ──────────────────────────────────────────────────────────────────

    [Fact]
    public void ArmingChangesTheButtonAndDisarmingPutsItBack()
    {
        var (vm, _, _) = New();

        vm.Arm();
        Assert.True(vm.IsArmed);
        Assert.Equal(5, vm.Countdown);
        Assert.Contains("Confirm", vm.ButtonText);

        vm.Disarm();
        Assert.False(vm.IsArmed);
        Assert.Equal("Execute on Server", vm.ButtonText);
    }

    // ── running ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ASuccessfulRunIsRecordedWithEnoughToPutInATicket()
    {
        var (vm, _, history) = New();

        await vm.RunAsync(Run(), Proceed);

        Assert.True(vm.ExecutionSuccess);
        Assert.True(vm.ExecutionComplete);

        var entry = Assert.Single(history.Entries);
        Assert.Equal(OperationOutcome.Succeeded, entry.Outcome);
        Assert.Equal("MyDb_Restored", entry.TargetDatabase);
        Assert.Equal("MyDb", entry.SourceDatabase);
        Assert.Equal("backups", entry.ContainerName);
        Assert.Equal("Full + 2 logs", entry.ChainSummary);
        Assert.NotEmpty(entry.Script);
    }

    [Fact]
    public async Task AFailedRunIsRecordedAsFailedWithTheReason()
    {
        var (vm, sql, history) = New();
        sql.ExecuteThrows = new InvalidOperationException("RESTORE terminating abnormally.");

        await vm.RunAsync(Run(), Proceed);

        Assert.False(vm.ExecutionSuccess);
        var entry = Assert.Single(history.Entries);
        Assert.Equal(OperationOutcome.Failed, entry.Outcome);
        Assert.Contains("terminating abnormally", entry.ErrorMessage);
    }

    /// <summary>
    /// A refused credential has written nothing to the server, so nothing was attempted - and the
    /// history records executions, not button presses.
    /// </summary>
    [Fact]
    public async Task ARefusedPreflightRunsNothingAndFilesNothing()
    {
        var (vm, sql, history) = New();

        await vm.RunAsync(Run(), _ => Task.FromResult(
            CredentialPreflight.Stop("Credential exists with identity 'MYDOMAIN\\svc_sql'.")));

        Assert.Empty(history.Entries);
        Assert.Empty(sql.ExecutedScripts);
        Assert.True(vm.HasError);
        Assert.Contains("MYDOMAIN\\svc_sql", vm.ErrorMessage);
    }

    /// <summary>
    /// The recorded log is the console as it stood when the run ended, from its first line - not
    /// whatever had made it out of the batching buffer, which is why the flush happens before the
    /// history entry is written.
    ///
    /// A PREFIX rather than equality, and the distinction is a real one rather than a way of making
    /// a test pass (#461). Progress&lt;T&gt; POSTS its callbacks, so a report issued just before the
    /// execution returned can still be queued when the run's finally block flushes and records -
    /// and it then reaches the console after the receipt was taken. The receipt is one line short
    /// of the console, on CI, perhaps one run in twenty.
    ///
    /// Prefix keeps everything worth keeping: nothing in the receipt that is not in the console, in
    /// the same order, starting at the same place. A receipt truncated early, out of order, or
    /// holding text the console never had all still fail. Only a trailing progress line arriving
    /// late is tolerated, which is the one thing that is genuinely not guaranteed.
    /// </summary>
    [Fact]
    public async Task TheRecordedLogIsTheConsoleAsItStoodWhenTheRunEnded()
    {
        var (vm, _, history) = New();

        await vm.RunAsync(Run(), Proceed);

        var entry = Assert.Single(history.Entries);

        Assert.Contains("Beginning restore execution", entry.Log);
        Assert.StartsWith(entry.Log, vm.Console.Text);

        // And it is the run, not a fragment of it: the outcome has to be in there, because that is
        // what somebody opens a receipt to find.
        Assert.Contains("Restore completed", entry.Log);
    }

    [Fact]
    public async Task TheRunIsAimedAtWhatItWasGiven()
    {
        var (vm, sql, _) = New();
        var run = Run();

        await vm.RunAsync(run, Proceed);

        Assert.Same(run.Server, Assert.Single(sql.ExecutedAgainst));
        Assert.Equal(run.Script, Assert.Single(sql.ExecutedScripts));
    }

    // ── cancelling ──────────────────────────────────────────────────────────────

    [Fact]
    public void CancellingWithNothingRunningDoesNothing()
    {
        var (vm, _, _) = New();

        vm.CancelCommand.Execute(null);

        Assert.False(vm.IsCancelling);
        Assert.Equal(string.Empty, vm.Console.Text);
    }

    /// <summary>
    /// Nothing is offered to stop before a run starts, and nothing is left offered after one ends.
    /// The Stop button is the only way out of a restore aimed at the wrong server (#25).
    /// </summary>
    [Fact]
    public async Task StoppingIsOfferedOnlyWhileSomethingIsRunning()
    {
        var (vm, _, _) = New();
        Assert.False(vm.CanCancel);

        await vm.RunAsync(Run(), Proceed);

        Assert.False(vm.CanCancel);
    }
}
