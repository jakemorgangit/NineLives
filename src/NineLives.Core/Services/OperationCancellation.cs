namespace Blackcat.NineLives.Services;

/// <summary>
/// The token source behind one cancellable operation, and the bookkeeping that goes with it (#25).
///
/// Every service method in this app already takes a CancellationToken; none of them were ever
/// given a real one. This is the missing half - a viewmodel holds one of these per long-running
/// operation, hands <see cref="Begin"/> to the service, and exposes <see cref="Cancel"/> to a
/// button.
///
/// Kept as its own type rather than a loose CancellationTokenSource field because the lifecycle is
/// where the bugs are: reusing a cancelled source silently cancels the next run before it starts,
/// and disposing one that a callback still holds throws. Doing it once, here, means the viewmodels
/// cannot each get it subtly wrong.
///
/// Not thread-safe by design: it is driven from the UI thread.
/// </summary>
public sealed class OperationCancellation
{
    private CancellationTokenSource? _current;

    /// <summary>True while an operation is running and has not already been asked to stop.</summary>
    public bool CanCancel => _current is { IsCancellationRequested: false };

    /// <summary>True once Cancel has been called and before the operation has finished unwinding.</summary>
    public bool IsCancelling => _current is { IsCancellationRequested: true };

    /// <summary>
    /// Starts a new operation and returns its token. Any previous source is cancelled and disposed
    /// first, so a run that was somehow left open cannot outlive the one replacing it.
    /// </summary>
    public CancellationToken Begin()
    {
        Cancel();
        Dispose();

        _current = new CancellationTokenSource();
        return _current.Token;
    }

    /// <summary>Asks the running operation to stop. Safe to call when nothing is running.</summary>
    public void Cancel()
    {
        if (_current is { IsCancellationRequested: false } cts)
        {
            // Between the check and the call the source could already be disposed by a racing
            // End(); the operation is over either way, which is what Cancel wanted.
            try { cts.Cancel(); } catch (ObjectDisposedException) { }
        }
    }

    /// <summary>
    /// Marks the operation finished. Called from a finally block, so it must not throw whatever
    /// state things are in.
    /// </summary>
    public void End()
    {
        Dispose();
        _current = null;
    }

    private void Dispose()
    {
        try { _current?.Dispose(); } catch (ObjectDisposedException) { }
    }
}
