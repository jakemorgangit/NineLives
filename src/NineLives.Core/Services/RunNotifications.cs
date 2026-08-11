namespace Blackcat.NineLives.Services;

/// <summary>The three moments a run is worth interrupting somebody's other work for (#242).</summary>
public enum RunPhase
{
    Started,
    Succeeded,

    /// <summary>Failed, cancelled, or a problem mid-run - anything where attention helps.</summary>
    Problem
}

/// <summary>
/// One thing that happened, described well enough to act on from a phone (#242).
///
/// The payload builders turn this into each provider's shape; the fields are chosen so the
/// message answers the sofa questions - what ran, where, did it work, how long, and if not, what
/// went wrong - without opening the app.
/// </summary>
/// <param name="Phase">Started, succeeded, or a problem.</param>
/// <param name="Operation">"Restore", "Backup", "Copy" - the verb, capitalised for the title.</param>
/// <param name="Subject">What it operated on - "MyDb", or "3 databases".</param>
/// <param name="Target">Where - "SRV01", or "SRV01 → SRV02" for a copy.</param>
/// <param name="Detail">The line that matters: the failure message, or the outcome summary.</param>
/// <param name="Duration">How long it ran, for finished and failed runs.</param>
public sealed record RunNotification(
    RunPhase Phase,
    string Operation,
    string Subject,
    string Target,
    string? Detail = null,
    TimeSpan? Duration = null);

/// <summary>
/// The seam the screens talk to (#242). Fire-and-forget by contract: a notification is about a
/// run, and must never be able to slow, block or fail the run it is about.
/// </summary>
public interface IRunNotifier
{
    void Notify(RunNotification notification);

    /// <summary>
    /// Waits - bounded - for notifications already handed to Notify to actually deliver (#296).
    /// Notify stays fire-and-forget, which is right in the app where the process lives on; a
    /// short-lived process must drain before exiting or the completion events - the ones the
    /// 3am story exists for - die with it. The timeout keeps a dead endpoint from holding the
    /// process hostage: this waits for deliveries, it does not guarantee them.
    /// </summary>
    Task DrainAsync(TimeSpan timeout);
}

/// <summary>For screens constructed without notification wiring - the tests, mostly.</summary>
public sealed class NullRunNotifier : IRunNotifier
{
    public static readonly NullRunNotifier Instance = new();
    public void Notify(RunNotification notification) { }
    public Task DrainAsync(TimeSpan timeout) => Task.CompletedTask;
}
