using System.Windows.Threading;
using Blackcat.NineLives.ViewModels;
using Xunit;

namespace Blackcat.NineLives.Tests;

/// <summary>
/// The console fed from the connection's thread (#233).
///
/// SQL Server's progress callbacks fire on the connection's worker thread, and the copy screen
/// handed them straight to a bound collection - two seconds into a real copy the ItemsControl
/// tore: "An ItemsControl is inconsistent with its items source", run dead. The buffer now owns
/// its thread-affinity, so every caller is safe by construction.
///
/// The deterministic trick throughout: GATE the dispatcher with a blocking job, act from the
/// foreign thread, and only then release - so "nothing happened yet" and "everything arrived
/// after" are facts, not races.
/// </summary>
[Collection(WpfCollection.Name)]
public class ConsoleBufferThreadingTests
{
    private readonly WpfFixture _wpf;

    public ConsoleBufferThreadingTests(WpfFixture wpf) => _wpf = wpf;

    private ConsoleBuffer OnFixtureThread()
    {
        ConsoleBuffer buffer = null!;
        _wpf.Invoke(() => buffer = new ConsoleBuffer());
        return buffer;
    }

    /// <summary>Waits until everything queued at Normal and below has run.</summary>
    private void Drain() =>
        _wpf.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);

    /// <summary>
    /// The pin for the crash: an append from a foreign thread must not touch the buffer's state
    /// on that thread. With the dispatcher gated, nothing can have processed the append - so any
    /// pending line now was put there by the CALLING thread, which is the #233 race itself.
    /// </summary>
    [Fact]
    public void AnAppendFromAForeignThreadDoesNotTouchTheBufferThere()
    {
        var buffer = OnFixtureThread();

        using var gate = new ManualResetEventSlim(false);
        _wpf.Dispatcher.InvokeAsync(() => gate.Wait());

        try
        {
            buffer.Append("10 percent processed.");

            Assert.False(buffer.HasPending,
                "the append mutated the buffer on the calling thread - the #233 race");
        }
        finally
        {
            gate.Set();
        }

        Drain();
        _wpf.Invoke(() => buffer.Flush());
        Assert.Contains("10 percent processed", buffer.Text);
    }

    /// <summary>
    /// A foreign flush returns with the flush DONE, including appends queued before it - its
    /// contract is "flush, then read Text", and both overtaking (Send priority) and an async hop
    /// would hand back text from before the run's tail.
    /// </summary>
    [Fact]
    public void AForeignFlushIsCompleteWhenItReturnsAndDoesNotOvertakeAppends()
    {
        var buffer = OnFixtureThread();

        buffer.Append("the last line of the run");
        buffer.Flush();

        Assert.Contains("the last line of the run", buffer.Text);
    }

    /// <summary>Order survives the queue - progress lines out of order would misreport the run.</summary>
    [Fact]
    public void ArrivalOrderIsPreserved()
    {
        var buffer = OnFixtureThread();

        for (var i = 1; i <= 5; i++)
            buffer.Append($"line {i}");

        buffer.Flush();

        var text = buffer.Text;
        for (var i = 1; i < 5; i++)
            Assert.True(
                text.IndexOf($"line {i}", StringComparison.Ordinal) <
                text.IndexOf($"line {i + 1}", StringComparison.Ordinal),
                text);
    }

    /// <summary>A foreign Clear empties everything that was queued before it, not a snapshot.</summary>
    [Fact]
    public void AForeignClearSweepsWhatWasQueuedBeforeIt()
    {
        var buffer = OnFixtureThread();

        buffer.Append("stale line from the previous run");
        buffer.Clear();

        Drain();
        _wpf.Invoke(() => buffer.Flush());

        Assert.DoesNotContain("stale", buffer.Text);
    }
}
