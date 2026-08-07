using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Blackcat.NineLives.Models;

namespace Blackcat.NineLives.ViewModels;

/// <summary>
/// The execution console: the lines on screen, and the batching that keeps them arriving smoothly.
///
/// Lifted out of RestoreViewModel, which was doing thirteen jobs (#115). This one has no coupling
/// to the rest of the restore - it takes text in and offers lines out - and pulling it apart makes
/// the 60 ms batching testable without standing up a whole restore.
///
/// Writes to a COLLECTION rather than concatenating a bound string. Appending to a bound string
/// rebuilds it and re-renders the whole TextBox on every message - O(n^2) - which was a large part
/// of why a restore reporting progress every few percent looked like it arrived in bursts rather
/// than live.
/// </summary>
public partial class ConsoleBuffer : ObservableObject
{
    /// <summary>
    /// How long lines wait before being moved onto the bound collection.
    ///
    /// SQL Server emits progress in clusters - several messages within a millisecond, then nothing
    /// for a second. Adding each one individually meant a layout pass and a scroll per message, in
    /// bursts, which is what made the console judder. Flushing on a fixed tick turns any arrival
    /// pattern into a steady redraw, and a whole cluster costs one layout pass instead of ten.
    /// </summary>
    private static readonly TimeSpan FlushInterval = TimeSpan.FromMilliseconds(60);

    private readonly List<ConsoleLine> _pending = [];
    private readonly Action<string>? _alsoLog;
    private DispatcherTimer? _timer;

    /// <summary>
    /// Whether more output is still expected. While true the flush timer keeps ticking even with
    /// nothing buffered; once false an empty tick stops it rather than spinning forever.
    /// </summary>
    public bool IsRunning { get; set; }

    /// <summary>
    /// What the flush timer runs on. Null means flush inline, because there is nothing to tick.
    ///
    /// Resolved once, at construction, rather than looked up per message. A buffer belongs to the
    /// thread that made it, so asking once is both cheaper and more truthful. Asking repeatedly
    /// meant the answer came from whatever state the CURRENT thread happened to be in - which is
    /// not something this class can reason about, and which broke: a test thread that had been used
    /// for something unrelated came back carrying a dispatcher, so the buffer waited on a timer
    /// that nothing was pumping and the lines never appeared.
    /// </summary>
    private readonly Dispatcher? _dispatcher;

    /// <param name="alsoLog">
    /// Called with every message as it arrives, so the log file cannot drift from what was shown.
    /// </param>
    public ConsoleBuffer(Action<string>? alsoLog = null)
        : this(alsoLog, Dispatcher.FromThread(Thread.CurrentThread))
    {
    }

    /// <summary>
    /// Lets a test say outright whether there is a dispatcher, instead of depending on what its
    /// thread happens to be carrying.
    /// </summary>
    internal ConsoleBuffer(Action<string>? alsoLog, Dispatcher? dispatcher)
    {
        _alsoLog = alsoLog;
        _dispatcher = dispatcher;
    }

    [ObservableProperty]
    private ObservableCollection<ConsoleLine> _lines = [];

    [ObservableProperty]
    private bool _hasOutput;

    /// <summary>The console as plain text, for copying into a bug report.</summary>
    public string Text => string.Join(Environment.NewLine, Lines.Select(l => l.Text));

    /// <summary>
    /// Adds a message, splitting it into lines and collapsing runs of blanks.
    ///
    /// Messages routinely arrive with a leading or trailing newline for spacing, and SQL Server's
    /// own output has its own blank lines. Left alone that produced a gap between almost every
    /// line. One blank line is allowed as a separator; runs of them are not.
    /// </summary>
    public void Append(string message)
    {
        foreach (var raw in message.Split('\n'))
        {
            var line = raw.TrimEnd('\r');

            if (line.Trim().Length == 0)
            {
                if (_pending.Count > 0 && _pending[^1].Text.Length == 0) continue;
                if (_pending.Count == 0 && (Lines.Count == 0 || Lines[^1].Text.Length == 0)) continue;
                _pending.Add(new ConsoleLine(string.Empty));
                continue;
            }

            _pending.Add(ConsoleLine.From(line));
        }

        _alsoLog?.Invoke(message);
        ScheduleFlush();
    }

    /// <summary>Moves everything buffered onto the bound collection now.</summary>
    public void Flush()
    {
        if (_pending.Count == 0)
        {
            // Nothing arriving and nothing running - stop ticking rather than spin forever.
            if (!IsRunning)
            {
                _timer?.Stop();
                _timer = null;
            }
            return;
        }

        foreach (var line in _pending) Lines.Add(line);
        _pending.Clear();
        HasOutput = true;
    }

    /// <summary>Empties the console, for the start of a run.</summary>
    public void Clear()
    {
        _pending.Clear();
        Lines.Clear();
        HasOutput = false;
    }

    /// <summary>Whether there is anything to batch on. Only the tests care.</summary>
    internal bool HasDispatcher => _dispatcher != null;

    /// <summary>
    /// True while lines are waiting. Only the tests care - it is how "batched, not immediate" is
    /// asserted without waiting on a dispatcher timer.
    /// </summary>
    internal bool HasPending => _pending.Count > 0;

    private void ScheduleFlush()
    {
        if (_timer != null) return;

        // No dispatcher (a plain unit test, or a background thread) means no timer to tick, so
        // flush inline instead of buffering forever with nothing to drain it.
        if (_dispatcher == null)
        {
            Flush();
            return;
        }

        _timer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
        {
            Interval = FlushInterval
        };
        _timer.Tick += (_, _) => Flush();
        _timer.Start();
    }
}
