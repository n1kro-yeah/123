using HttpSpy.Core.Models;

namespace HttpSpy.Core.Proxy;

/// <summary>
/// Represents a transaction paused at a breakpoint. The UI mutates the session
/// in place and then calls <see cref="Resume"/> (optionally with abort) to let
/// the proxy continue.
/// </summary>
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

    public Task WaitAsync() => _tcs.Task;

    public void Resume()
    {
        _tcs.TrySetResult(true);
    }

    public void Abort()
    {
        Aborted = true;
        _tcs.TrySetResult(false);
    }
}
