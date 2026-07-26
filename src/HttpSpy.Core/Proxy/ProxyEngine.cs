using HttpSpy.Core.Models;
using HttpSpy.Core.Rules;

namespace HttpSpy.Core.Proxy;

/// <summary>Aggregate counters describing captured traffic.</summary>
public sealed class ProxyStatistics
{
    public long TotalSessions;
    public long ActiveConnections;
    public long BytesSent;
    public long BytesReceived;
    public long Errors;
    public long WebSocketSessions;

    /// <summary>Transactions dropped by a capture filter rather than recorded.</summary>
    public long SuppressedSessions;

    public ProxyStatistics Snapshot() => (ProxyStatistics)MemberwiseClone();
}

/// <summary>
/// The public capture API. Owns the listening socket, the certificate
/// authority, the rule engine and statistics, and raises events as traffic is
/// captured. The UI subscribes to these events to populate the session grid.
/// </summary>
public sealed class ProxyEngine : IDisposable
{
    private ProxyServer? _server;
    private CancellationTokenSource? _cts;

    public ProxyEngine(ProxyOptions? options = null)
    {
        Options = options ?? new ProxyOptions();
        CertificateAuthority = new CertificateAuthority();
        Rules = new RuleEngine();
        ProcessResolver = new ProcessResolver();
        Statistics = new ProxyStatistics();
    }

    public ProxyOptions Options { get; }
    public CertificateAuthority CertificateAuthority { get; }
    public RuleEngine Rules { get; }
    public ProcessResolver ProcessResolver { get; }
    public ProxyStatistics Statistics { get; }

    public bool IsRunning { get; private set; }

    // ---- Events --------------------------------------------------------------
    /// <summary>Raised when a request line + headers have been parsed (response pending).</summary>
    public event Action<HttpSession>? SessionStarted;

    /// <summary>Raised when a transaction finishes (success or error).</summary>
    public event Action<HttpSession>? SessionCompleted;

    /// <summary>Raised when a streaming session gains a new WS frame / SSE event.</summary>
    public event Action<HttpSession>? SessionUpdated;

    /// <summary>Raised when a breakpoint pauses a transaction.</summary>
    public event Action<PausedTransaction>? TransactionPaused;

    /// <summary>Raised for human-readable diagnostic log lines.</summary>
    public event Action<string>? Log;

    public void Start()
    {
        if (IsRunning) return;

        var cts = new CancellationTokenSource();
        var server = new ProxyServer(this);
        try
        {
            server.Start(cts.Token);
        }
        catch
        {
            // Binding the listener failed (port in use, bad address, …). Leave the
            // engine cleanly stopped instead of half-started with a leaked CTS.
            try { server.Dispose(); } catch { /* ignore */ }
            cts.Dispose();
            throw;
        }

        _cts?.Dispose();
        _cts = cts;
        _server = server;
        IsRunning = true;

        if (Options.SetSystemProxy) EnableSystemProxy();

        RaiseLog($"HttpSpy proxy listening on {Options.ListenAddress}:{Options.ListenPort}");
    }

    public void Stop()
    {
        if (!IsRunning) return;
        try { _cts?.Cancel(); } catch { /* ignore */ }
        try { _server?.Dispose(); } catch { /* ignore */ }
        _server = null;
        IsRunning = false;

        // Release anything still parked at a breakpoint; otherwise those proxy
        // tasks (and the client sockets they hold) would never unwind.
        ReleasePausedTransactions();

        if (Options.SetSystemProxy) DisableSystemProxy();
        RaiseLog("HttpSpy proxy stopped");
    }

    /// <summary>
    /// Points the OS at the proxy where that can be done per-user and undone
    /// again: the WinINET settings on Windows, the GNOME proxy settings on Linux.
    /// Anywhere else the user is told which variables to export instead — see
    /// <see cref="PlatformIntegration.ProxySteps"/>.
    /// </summary>
    private void EnableSystemProxy()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                SystemProxy.Enable(Options.ListenAddress, Options.ListenPort);
            }
            else if (OperatingSystem.IsLinux() && GSettings.IsAvailable)
            {
                GSettings.Enable(Options.ListenAddress, Options.ListenPort);
                RaiseLog("GNOME proxy settings pointed at HttpSpy.");
            }
            else
            {
                RaiseLog($"No automatic system proxy on {PlatformIntegration.PlatformName}; " +
                         $"export HTTPS_PROXY=http://{Options.ListenAddress}:{Options.ListenPort} instead.");
            }
        }
        catch (Exception ex) { RaiseLog($"Failed to set system proxy: {ex.Message}"); }
    }

    private void DisableSystemProxy()
    {
        try
        {
            if (OperatingSystem.IsWindows()) SystemProxy.Disable();
            else if (OperatingSystem.IsLinux() && GSettings.IsAvailable) GSettings.Disable();
        }
        catch (Exception ex) { RaiseLog($"Failed to clear system proxy: {ex.Message}"); }
    }

    private void ReleasePausedTransactions()
    {
        PausedTransaction[] pending;
        lock (_pausedGate)
        {
            if (_paused.Count == 0) return;
            pending = _paused.ToArray();
            _paused.Clear();
        }
        foreach (var p in pending) p.Resume();
        RaiseLog($"Released {pending.Length} transaction(s) held at breakpoints.");
    }

    // ---- Internal raise helpers (called by ProxyServer) ----------------------
    internal void RaiseStarted(HttpSession s)
    {
        // Capture filters are applied here, at the one point every transaction
        // passes through, so a dropped session never allocates a view model or
        // retains its bodies. The flag is remembered so the later Completed /
        // Updated events for the same transaction stay consistent.
        if (!CaptureFilterSet.ShouldRecord(Options.CaptureFilters, s))
        {
            s.Suppressed = true;
            Interlocked.Increment(ref Statistics.SuppressedSessions);
            return;
        }

        Interlocked.Increment(ref Statistics.TotalSessions);
        SessionStarted?.Invoke(s);
    }

    internal void RaiseCompleted(HttpSession s)
    {
        // Byte counters still tally suppressed traffic: it did cross the wire,
        // and the status bar would otherwise under-report throughput.
        Interlocked.Add(ref Statistics.BytesSent, s.BytesSent);
        Interlocked.Add(ref Statistics.BytesReceived, s.BytesReceived);
        if (s.Error is not null) Interlocked.Increment(ref Statistics.Errors);
        if (s.Suppressed) return;
        SessionCompleted?.Invoke(s);
    }

    internal void RaiseUpdated(HttpSession s)
    {
        if (s.Suppressed) return;
        SessionUpdated?.Invoke(s);
    }

    internal void RaiseLog(string message) => Log?.Invoke(message);

    /// <summary>
    /// Raises a breakpoint and blocks (asynchronously) until the UI resumes, the
    /// engine stops, or <see cref="ProxyOptions.BreakpointTimeoutMs"/> elapses.
    /// </summary>
    internal async Task<PausedTransaction> RaiseBreakpointAsync(HttpSession session, BreakpointPhase phase)
    {
        var paused = new PausedTransaction(session, phase);
        lock (_pausedGate) _paused.Add(paused);
        paused.Resolved += p => { lock (_pausedGate) _paused.Remove(p); };

        TransactionPaused?.Invoke(paused);
        await paused.WaitAsync(Options.BreakpointTimeoutMs, _cts?.Token ?? CancellationToken.None)
            .ConfigureAwait(false);

        if (paused.TimedOut)
            RaiseLog($"Breakpoint on {session.Method} {session.FullUrl} timed out and was released automatically.");
        return paused;
    }

    /// <summary>Transactions currently held at a breakpoint (snapshot).</summary>
    public IReadOnlyList<PausedTransaction> PausedTransactions
    {
        get { lock (_pausedGate) return _paused.ToArray(); }
    }

    private readonly object _pausedGate = new();
    private readonly List<PausedTransaction> _paused = new();

    public void Dispose()
    {
        Stop();
        ReleasePausedTransactions();
        CertificateAuthority.Dispose();
        _cts?.Dispose();
        _cts = null;
    }
}
