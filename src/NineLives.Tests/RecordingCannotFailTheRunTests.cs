using Blackcat.NineLives.Models;
using Blackcat.NineLives.Services;
using Blackcat.NineLives.ViewModels;
using Xunit;

namespace Blackcat.NineLives.Tests;

/// <summary>
/// Filing a run cannot fail the run (#469).
///
/// Found as a red build on main, not by looking: a NullReferenceException out of
/// <c>ConsoleBuffer.Text</c>, thrown while assembling the receipt in the run's own finally block,
/// and propagating all the way out of RunAsync. In a test that reads as a failure. In the app it
/// would have read as a completed restore surfacing a crash - the database restored, and an
/// exception on screen.
///
/// Two faults, and both are worth having separately. The console could hand back a torn read;
/// and nothing was catching it if it did. The store already promises never to fail what it
/// records, but that promise lives inside Append - everything before it, reading the console and
/// building the entry, was unguarded.
/// </summary>
public class RecordingCannotFailTheRunTests
{
    // ── the console survives being read from another thread ─────────────────────

    /// <summary>
    /// ObservableCollection is not thread-safe: enumerated mid-write it can hand back a null slot
    /// from the array it is growing. Reproduced by hammering it, which is the only honest way to
    /// test a race - and it fails reliably on the old implementation.
    /// </summary>
    [Fact]
    public async Task ReadingTheTextWhileItIsBeingWrittenDoesNotThrow()
    {
        var console = new ConsoleBuffer(null, dispatcher: null);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        var writer = Task.Run(() =>
        {
            int n = 0;
            while (!stop.IsCancellationRequested)
                console.Append($"line {n++} of the restore output");
        });

        var reader = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                // The assertion IS that this does not throw.
                _ = console.Text;
            }
        });

        await Task.WhenAll(writer, reader);
    }

    [Fact]
    public void TheTextIsStillTheTextWhenNothingIsRacing()
    {
        var console = new ConsoleBuffer(null, dispatcher: null);
        console.Append("first");
        console.Append("second");
        console.Flush();

        var text = console.Text;

        Assert.Contains("first", text);
        Assert.Contains("second", text);
    }

    // ── and a failure to record does not fail the restore ───────────────────────

    private static (RestoreExecutionViewModel vm, FakeOperationHistoryStore history) Execution(
        Exception? appendThrows)
    {
        var history = new FakeOperationHistoryStore { AppendThrows = appendThrows };
        var vm = new RestoreExecutionViewModel(
            new FakeSqlServerService(), history, TestLogs.Temp(), new OperationCancellation());
        return (vm, history);
    }

    private static Task<CredentialPreflight> Proceed(Action<string> _)
        => Task.FromResult(CredentialPreflight.Proceed);

    private static RestoreRun Run() => new(
        new ServerConnection { Id = "s1", Name = "SRV01", ServerName = "SRV01" },
        "RESTORE DATABASE [MyDb_Restored] FROM URL = 'https://acct/backups/full.bak'",
        "MyDb_Restored",
        "MyDb",
        "backups",
        "Full",
        new DateTime(2026, 8, 17, 22, 0, 0),
        "WITH REPLACE=True, recovery=Recovery, stopAt=none");

    /// <summary>
    /// The restore has already happened by the time the receipt is written. Nothing that goes
    /// wrong while filing it may change that - and before this, an exception here came out of
    /// RunAsync and turned a completed restore into a crash.
    /// </summary>
    [Fact]
    public async Task AStoreThatThrowsDoesNotFailTheRestore()
    {
        var (vm, _) = Execution(new InvalidOperationException("history file is read-only"));

        await vm.RunAsync(Run(), Proceed);

        Assert.True(vm.ExecutionComplete);
        Assert.True(vm.ExecutionSuccess);
    }

    /// <summary>Said out loud, though. A receipt that silently did not happen is worse.</summary>
    [Fact]
    public async Task AndItSaysTheRecordingFailed()
    {
        var (vm, _) = Execution(new InvalidOperationException("history file is read-only"));

        await vm.RunAsync(Run(), Proceed);
        vm.Console.Flush();

        Assert.Contains("Recording it in History failed", vm.Console.Text);
        Assert.Contains("read-only", vm.Console.Text);
    }

    [Fact]
    public async Task AndAWorkingStoreStillGetsItsReceipt()
    {
        var (vm, history) = Execution(null);

        await vm.RunAsync(Run(), Proceed);

        var entry = Assert.Single(history.Entries);
        Assert.Equal(OperationOutcome.Succeeded, entry.Outcome);
    }
}
