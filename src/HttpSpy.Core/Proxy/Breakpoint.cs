using HttpSpy.Core.Models;

namespace HttpSpy.Core.Proxy;

/// <summary>
/// Represents a transaction paused at a breakpoint. The UI mutates the session
/// in place and then calls <see cref="Resume"/> (optionally with abort) to let
/// the proxy continue.
/// </summary>
/// <remarks>
/// The wait is always bounded: it ends when the user resumes/aborts, when the
/// capture engine shuts down, or when the configured breakpoint timeout expires.
/// Without that, closing the app (or simply forgetting an open breakpoint) would
/// leave the client connection blocked indefinitely.
/// </remarks>
public sealed class PausedTransaction
{
    private readonly TaskCompletionSource<bool> _tcs =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public PausedTransaction(HttpSession session, BreakpointPhase phase)
    {
        Session = session;
        Phase = phase;
    }

    public HttpSession Session { get; }

    /// <summary>Which phase this pause occurred at (request or response).</summary>
    public BreakpointPhase Phase { get; }

    /// <summary>True if the user asked to abort the transaction instead of continuing.</summary>
    public bool Aborted { get; private set; }

    /// <summary>True when the pause ended without the user acting on it.</summary>
    public bool TimedOut { get; private set; }

    /// <summary>True once this transaction has been released, however that happened.</summary>
    public bool IsResolved => _tcs.Task.IsCompleted;

    /// <summary>Raised when the pause ends, so the UI can drop it from its queue.</summary>
    public event Action<PausedTransaction>? Resolved;

    public Task WaitAsync() => _tcs.Task;

    /// <summary>
    /// Waits for the user to resume or abort, giving up after
    /// <paramref name="timeoutMs"/> (0 = wait indefinitely) or when
    /// <paramref name="ct"/> fires. A timeout or shutdown resumes the
    /// transaction rather than aborting it, so traffic keeps flowing.
    /// </summary>
    public async Task WaitAsync(int timeoutMs, CancellationToken ct)
    {
        if (_tcs.Task.IsCompleted)
        {
            await _tcs.Task.ConfigureAwait(false);
            return;
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (timeoutMs > 0) cts.CancelAfter(timeoutMs);

        using (cts.Token.Register(static state =>
               {
                   var self = (PausedTransaction)state!;
                   self.TimedOut = true;
                   self.Resume();
               }, this))
        {
            await _tcs.Task.ConfigureAwait(false);
        }
    }

    public void Resume()
    {
        if (_tcs.TrySetResult(true)) Resolved?.Invoke(this);
    }

    public void Abort()
    {
        Aborted = true;
        if (_tcs.TrySetResult(false)) Resolved?.Invoke(this);
    }
}
