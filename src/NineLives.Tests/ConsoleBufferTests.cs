using Blackcat.NineLives.Models;
using Blackcat.NineLives.ViewModels;
using Xunit;

namespace Blackcat.NineLives.Tests;

/// <summary>
/// The console's line handling and batching, now that it is its own type rather than 90 lines in
/// the middle of a 2,400-line viewmodel (#115).
///
/// None of this needed a restore, a server or a container to exercise - it just could not be
/// reached without one.
/// </summary>
public class ConsoleBufferTests
{
    /// <summary>
    /// No dispatcher, said outright rather than inferred from the thread.
    ///
    /// These used to construct the plain way and rely on an xUnit worker thread having no
    /// dispatcher on it. That is not something a test can assume: a worker that had already run
    /// something which left a dispatcher behind made the buffer wait for a timer nothing was
    /// pumping, and every assertion here saw an empty console. It reproduced only on CI, because
    /// fewer cores means more thread reuse.
    /// </summary>
    private static ConsoleBuffer New(Action<string>? alsoLog = null) => new(alsoLog, dispatcher: null);

    [Fact]
    public void AMessageBecomesALine()
    {
        var console = New();

        console.Append("Beginning restore execution...");

        var line = Assert.Single(console.Lines);
        Assert.Equal("Beginning restore execution...", line.Text);
        Assert.True(console.HasOutput);
    }

    [Fact]
    public void AMultiLineMessageIsSplit()
    {
        var console = New();

        console.Append("first\nsecond\nthird");

        Assert.Equal(["first", "second", "third"], console.Lines.Select(l => l.Text));
    }

    [Fact]
    public void CarriageReturnsAreNotLeftOnTheEndOfLines()
    {
        var console = New();

        console.Append("first\r\nsecond");

        Assert.Equal(["first", "second"], console.Lines.Select(l => l.Text));
    }

    /// <summary>
    /// Messages routinely arrive with a leading or trailing newline for spacing, and SQL Server's
    /// own output has its own blank lines. Left alone that put a gap between almost every line.
    /// </summary>
    [Fact]
    public void RunsOfBlankLinesCollapseToOne()
    {
        var console = New();

        console.Append("first\n\n\n\nsecond");

        Assert.Equal(["first", "", "second"], console.Lines.Select(l => l.Text));
    }

    [Fact]
    public void ABlankLineIsNotAddedAtTheVeryStart()
    {
        var console = New();

        console.Append("\nfirst");

        Assert.Equal(["first"], console.Lines.Select(l => l.Text));
    }

    [Fact]
    public void ABlankAcrossTwoSeparateMessagesStillCollapses()
    {
        // The buffer has to look at what is already on screen, not just what is pending - the
        // messages that end and begin with a newline arrive separately.
        var console = New();

        console.Append("first\n");
        console.Append("\nsecond");

        Assert.Equal(["first", "", "second"], console.Lines.Select(l => l.Text));
    }

    [Fact]
    public void LinesAreClassifiedAsTheyArrive()
    {
        var console = New();

        console.Append("ERROR: could not open backup device");
        console.Append("50 percent processed.");

        Assert.Equal(ConsoleLineKind.Error, console.Lines[0].Kind);
        Assert.NotEqual(ConsoleLineKind.Error, console.Lines[1].Kind);
    }

    [Fact]
    public void EveryMessageAlsoGoesToTheLog()
    {
        // The file must not drift from what was on screen, so both are written from one place.
        var logged = new List<string>();
        var console = New(logged.Add);

        console.Append("first");
        console.Append("second");

        Assert.Equal(["first", "second"], logged);
    }

    [Fact]
    public void TextIsTheWholeConsoleForCopying()
    {
        var console = New();

        console.Append("first\nsecond");

        Assert.Equal($"first{Environment.NewLine}second", console.Text);
    }

    [Fact]
    public void ClearingEmptiesEverything()
    {
        var console = New();
        console.Append("first");

        console.Clear();

        Assert.Empty(console.Lines);
        Assert.False(console.HasOutput);
        Assert.Empty(console.Text);
    }

    /// <summary>
    /// The CI failure, reproduced deterministically.
    ///
    /// Eight tests in this class went red on CI and nowhere else. The cause was not the console at
    /// all: a worker thread had been used for something that left a Dispatcher on it, and the
    /// buffer used to ask the CURRENT thread whether one existed. It found one, waited for a timer
    /// nothing was pumping, and every assertion here saw an empty console. Fewer cores on CI meant
    /// more thread reuse, which is why a local run never showed it.
    ///
    /// The buffer now takes its dispatcher at construction instead of sniffing for one, so what the
    /// thread happens to be carrying no longer decides anything.
    /// </summary>
    [Fact]
    public void AThreadThatAlreadyCarriesADispatcherChangesNothing()
    {
        _ = System.Windows.Threading.Dispatcher.CurrentDispatcher;

        var console = New();
        console.Append("first\nsecond");

        Assert.Equal(["first", "second"], console.Lines.Select(l => l.Text));
    }

    [Fact]
    public void ClearingDropsAnythingStillBuffered()
    {
        // A run that starts while the previous one's tail is still pending must not inherit it.
        var console = New();
        console.IsRunning = true;
        console.Append("from the last run");
        console.Clear();
        console.Flush();

        Assert.Empty(console.Lines);
    }
}

/// <summary>
/// The batching itself, which needs a dispatcher to tick.
/// </summary>
[Collection(WpfCollection.Name)]
public class ConsoleBufferBatchingTests(WpfFixture wpf)
{
    /// <summary>
    /// On a dispatcher thread lines wait for the timer rather than landing one at a time.
    ///
    /// SQL Server emits progress in clusters - several messages within a millisecond, then nothing
    /// for a second. Adding each individually meant a layout pass and a scroll per message, which
    /// is what made the console judder.
    /// </summary>
    [Fact]
    public void LinesAreBatchedRatherThanAppliedImmediately()
    {
        wpf.Invoke(() =>
        {
            // Constructed on the dispatcher thread, so it picks one up the ordinary way.
            var console = new ConsoleBuffer { IsRunning = true };
            Assert.True(console.HasDispatcher, "the batching test needs a real dispatcher to batch on");

            console.Append("first");
            console.Append("second");

            // Still buffered - the timer has not ticked.
            Assert.Empty(console.Lines);
            Assert.True(console.HasPending);

            console.Flush();

            Assert.Equal(2, console.Lines.Count);
            Assert.False(console.HasPending);
        });
    }

    /// <summary>
    /// Off a dispatcher there is nothing to drain the buffer, so it applies inline instead of
    /// holding lines forever - which is what a plain unit test or a background thread gets.
    /// </summary>
    [Fact]
    public async Task WithNoDispatcherTheLinesApplyImmediately()
    {
        var console = new ConsoleBuffer(null, dispatcher: null) { IsRunning = true };

        await Task.Run(() => console.Append("from a thread with no dispatcher"));

        Assert.Single(console.Lines);
    }
}
