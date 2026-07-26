namespace HttpSpy.Core.Proxy;

/// <summary>Configuration for the capture proxy.</summary>
public sealed class ProxyOptions
{
    /// <summary>Local address the proxy listens on.</summary>
    public string ListenAddress { get; set; } = "127.0.0.1";

    /// <summary>Local port the proxy listens on.</summary>
    public int ListenPort { get; set; } = 8888;

    /// <summary>When true, decrypt HTTPS via MITM. When false, tunnel CONNECT opaquely.</summary>
    public bool DecryptHttps { get; set; } = true;

    /// <summary>When true, set the OS/system proxy on start and clear it on stop (Windows).</summary>
    public bool SetSystemProxy { get; set; } = true;

    /// <summary>Resolve the originating Windows process for each connection.</summary>
    public bool ResolveProcess { get; set; } = true;

    /// <summary>Capture and decode WebSocket frames on upgraded connections.</summary>
    public bool CaptureWebSockets { get; set; } = true;

    /// <summary>Offer HTTP/2 (ALPN "h2") to clients and decode h2 transactions (incl. gRPC).</summary>
    public bool EnableHttp2 { get; set; } = true;

    /// <summary>Hosts that should be tunneled without decryption (e.g. cert-pinned apps).</summary>
    public List<string> TlsPassthroughHosts { get; set; } = new();

    /// <summary>
    /// EXPERIMENTAL, Windows-only. When true, use the WinDivert driver to divert
    /// outbound TCP:80/443 to a local transparent listener with no system-proxy
    /// configuration (the HTTP Debugger "no proxy" capture mode). Requires
    /// Administrator rights and the WinDivert driver alongside the executable.
    /// </summary>
    public bool TransparentCapture { get; set; }

    /// <summary>Local port the transparent-capture listener binds to (all interfaces).</summary>
    public int TransparentListenPort { get; set; } = 8889;

    /// <summary>
    /// Maximum body size (bytes) buffered per message. Anything beyond this is
    /// still relayed to the peer byte-for-byte, but only the first
    /// <see cref="MaxBufferedBody"/> bytes are retained for inspection — this is
    /// what keeps a multi-gigabyte download from exhausting the process heap.
    /// </summary>
    public long MaxBufferedBody { get; set; } = 32 * 1024 * 1024;

    /// <summary>Maximum WebSocket frame payload retained per frame (bytes).</summary>
    public long MaxWebSocketFrame { get; set; } = 4 * 1024 * 1024;

    /// <summary>Maximum number of WebSocket frames retained per session (0 = unlimited).</summary>
    public int MaxWebSocketFrames { get; set; } = 5000;

    /// <summary>Maximum number of Server-Sent Events retained per session (0 = unlimited).</summary>
    public int MaxServerSentEvents { get; set; } = 5000;

    /// <summary>Upstream proxy to chain through (optional).</summary>
    public string? UpstreamProxyHost { get; set; }
    public int UpstreamProxyPort { get; set; }

    public int ConnectTimeoutMs { get; set; } = 15000;

    /// <summary>
    /// How long a transaction may stay paused at a breakpoint before it is
    /// released automatically. Prevents a forgotten breakpoint from wedging a
    /// client connection forever. 0 disables the timeout.
    /// </summary>
    public int BreakpointTimeoutMs { get; set; } = 300_000;

    // ---- Network simulation (throttling) ------------------------------------
    /// <summary>When true, responses to the client are rate-limited / delayed to simulate slow links.</summary>
    public bool ThrottleEnabled { get; set; }

    /// <summary>Simulated downstream bandwidth in kilobits/sec (0 = unlimited).</summary>
    public int ThrottleKbps { get; set; }

    /// <summary>Extra latency (ms) injected before each response is delivered.</summary>
    public int ExtraLatencyMs { get; set; }
}
