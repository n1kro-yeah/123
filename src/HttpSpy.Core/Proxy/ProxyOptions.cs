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

    /// <summary>Hosts that should be tunneled without decryption (e.g. cert-pinned apps).</summary>
    public List<string> TlsPassthroughHosts { get; set; } = new();

    /// <summary>Maximum body size (bytes) buffered per message; larger bodies are streamed but truncated for display.</summary>
    public long MaxBufferedBody { get; set; } = 32 * 1024 * 1024;

    /// <summary>Upstream proxy to chain through (optional).</summary>
    public string? UpstreamProxyHost { get; set; }
    public int UpstreamProxyPort { get; set; }

    public int ConnectTimeoutMs { get; set; } = 15000;
}
