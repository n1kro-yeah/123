using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace HttpSpy.Core.Proxy;

/// <summary>Establishes outbound connections to origin servers (optionally TLS / via a chained proxy).</summary>
public sealed class Upstream
{
    private readonly ProxyOptions _options;

    public Upstream(ProxyOptions options) => _options = options;

    public sealed class Connection : IDisposable
    {
        public required TcpClient Tcp { get; init; }
        public required Stream Stream { get; init; }
        public X509Certificate2? ServerCertificate { get; set; }

        /// <summary>The ALPN protocol negotiated with the origin ("h2", "http/1.1", or empty).</summary>
        public string NegotiatedProtocol { get; set; } = string.Empty;

        public void Dispose()
        {
            try { Stream.Dispose(); } catch { /* ignore */ }
            try { Tcp.Dispose(); } catch { /* ignore */ }
        }
    }

    /// <summary>
    /// True when requests on this connection must use absolute-form request
    /// targets (<c>GET http://host/path</c>) because they are being handed to a
    /// chained forward proxy rather than to the origin server itself.
    /// </summary>
    public bool RequiresAbsoluteForm(bool tls) => !tls && !string.IsNullOrEmpty(_options.UpstreamProxyHost);

    public async Task<Connection> ConnectAsync(string host, int port, bool tls, CancellationToken ct,
        IReadOnlyList<SslApplicationProtocol>? alpnProtocols = null)
    {
        var tcp = new TcpClient { NoDelay = true };
        SslStream? ssl = null;
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(_options.ConnectTimeoutMs);

            bool useChain = !string.IsNullOrEmpty(_options.UpstreamProxyHost);
            if (useChain)
            {
                await tcp.ConnectAsync(_options.UpstreamProxyHost!, _options.UpstreamProxyPort, timeoutCts.Token)
                    .ConfigureAwait(false);
                // Only TLS needs a tunnel; plain HTTP is forwarded to the chained
                // proxy using an absolute-form request target instead.
                if (tls)
                    await SendConnectAsync(tcp.GetStream(), host, port, timeoutCts.Token).ConfigureAwait(false);
            }
            else
            {
                await tcp.ConnectAsync(host, port, timeoutCts.Token).ConfigureAwait(false);
            }

            Stream stream = tcp.GetStream();
            X509Certificate2? serverCert = null;

            string negotiated = string.Empty;
            if (tls)
            {
                ssl = new SslStream(stream, leaveInnerStreamOpen: false, (_, cert, _, _) =>
                {
                    if (cert is not null) serverCert = new X509Certificate2(cert);
                    return true; // a debugging proxy accepts upstream certs to remain useful behind interception
                });
                var options = new SslClientAuthenticationOptions
                {
                    TargetHost = host,
                    EnabledSslProtocols = SslProtocols.None,
                };
                if (alpnProtocols is not null)
                    options.ApplicationProtocols = alpnProtocols.ToList();
                await ssl.AuthenticateAsClientAsync(options, timeoutCts.Token).ConfigureAwait(false);
                negotiated = ssl.NegotiatedApplicationProtocol.ToString();
                stream = ssl;
            }

            return new Connection
            {
                Tcp = tcp,
                Stream = stream,
                ServerCertificate = serverCert,
                NegotiatedProtocol = negotiated,
            };
        }
        catch
        {
            // Connect / handshake failed: nothing owns the socket yet, so release
            // it here rather than leaking it until finalization.
            try { ssl?.Dispose(); } catch { /* ignore */ }
            try { tcp.Dispose(); } catch { /* ignore */ }
            throw;
        }
    }

    private static async Task SendConnectAsync(Stream stream, string host, int port, CancellationToken ct)
    {
        var req = $"CONNECT {host}:{port} HTTP/1.1\r\nHost: {host}:{port}\r\nProxy-Connection: keep-alive\r\n\r\n";
        var bytes = Encoding.ASCII.GetBytes(req);
        await stream.WriteAsync(bytes, ct).ConfigureAwait(false);

        // Read the CONNECT response one byte at a time: a buffered reader could
        // swallow bytes belonging to the tunnelled TLS handshake that follows.
        string statusLine = await ReadLineUnbufferedAsync(stream, ct).ConfigureAwait(false);
        var parts = statusLine.Split(' ', 3);
        if (parts.Length < 2 || !int.TryParse(parts[1], out int status) || status is < 200 or > 299)
            throw new IOException($"Upstream proxy CONNECT failed: {statusLine}");

        while (true)
        {
            var line = await ReadLineUnbufferedAsync(stream, ct).ConfigureAwait(false);
            if (line.Length == 0) break;
        }
    }

    /// <summary>Reads a CRLF-terminated line without reading ahead past it.</summary>
    private static async Task<string> ReadLineUnbufferedAsync(Stream stream, CancellationToken ct)
    {
        var sb = new StringBuilder(128);
        var one = new byte[1];
        while (sb.Length <= StreamReaderEx.MaxLineLength)
        {
            int n = await stream.ReadAsync(one.AsMemory(0, 1), ct).ConfigureAwait(false);
            if (n <= 0) break;
            if (one[0] == (byte)'\n') break;
            if (one[0] != (byte)'\r') sb.Append((char)one[0]);
        }
        return sb.ToString();
    }
}
