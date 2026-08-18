using Blackcat.NineLives.ViewModels;
using System.Windows.Threading;
using Xunit;

namespace Blackcat.NineLives.Tests;

/// <summary>
/// The console never marshals onto a dispatcher that is not pumping (#497).
///
/// Flush and Clear marshal with a BLOCKING Invoke, deliberately: callers flush in order to read,
/// and an async hop would hand them the text from before the flush. The cost of that choice is
/// that the dispatcher on the other end has to be one that answers.
///
/// It was whichever dispatcher the constructing thread happened to be carrying.
/// Dispatcher.FromThread does not create one - but a plain thread pool thread acquires a
/// dispatcher the moment anything constructs a DispatcherObject on it, and pool threads are
/// reused. So a console built on such a thread captured a dispatcher with no message loop behind
/// it, and the first Flush running on a DIFFERENT thread - which is every continuation after an
/// await, there being no synchronization context to return to - blocked for ever.
///
/// That is what hung the CI test step (#495). It took a hang dump to find, having been written
/// off as flaky infrastructure for months: intermittent because it needs both a thread carrying a
/// stray dispatcher and a continuation landing elsewhere, and invisible on a developer machine
/// because more cores means less pool-thread reuse.
/// </summary>
public class ConsoleNeverWaitsOnADeadDispatcherTests
{
    /// <summary>
    /// The exact shape: a thread that has acquired a dispatcher without ever running one.
    /// Constructing the buffer there must not adopt it.
    /// </summary>
    [Fact]
    public void AThreadCarryingADispatcherNobodyIsPumpingIsNotAdopted()
    {
        ConsoleBuffer? buffer = null;
        Dispatcher? strayDispatcher = null;

        var worker = new Thread(() =>
        {
            // What a DispatcherObject's constructor does to the thread it runs on, and what makes
            // this reachable from an ordinary pool thread that has run one earlier test.
            strayDispatcher = Dispatcher.CurrentDispatcher;

            buffer = new ConsoleBuffer();
        });

        worker.Start();
        Assert.True(worker.Join(TimeSpan.FromSeconds(10)), "the worker did not finish");

        Assert.NotNull(buffer);
        Assert.NotNull(strayDispatcher);

        // Whatever it adopted, it is not the one nobody is pumping. With an Application in the
        // process it takes that instead, which is the point: a dispatcher that answers.
        Assert.NotSame(strayDispatcher, buffer!.AdoptedDispatcher);
    }

    /// <summary>
    /// And the consequence that matters: a flush from another thread returns rather than waiting
    /// on a queue nothing is draining. Without the fix this never comes back.
    /// </summary>
    [Fact]
    public void AFlushFromAnotherThreadDoesNotWaitForEver()
    {
        ConsoleBuffer? buffer = null;

        var builder = new Thread(() =>
        {
            _ = Dispatcher.CurrentDispatcher;
            buffer = new ConsoleBuffer();
            buffer.Append("something happened");
        });

        builder.Start();
        Assert.True(builder.Join(TimeSpan.FromSeconds(10)), "the builder did not finish");

        // A different thread entirely, as an await continuation is.
        var flushed = Task.Run(() =>
        {
            buffer!.Flush();
            return buffer.Text;
        });

        Assert.True(flushed.Wait(TimeSpan.FromSeconds(10)), "Flush never returned");
        Assert.Contains("something happened", flushed.Result);
    }
}
