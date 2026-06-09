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

    public async Task<Connection> ConnectAsync(string host, int port, bool tls, CancellationToken ct,
        IReadOnlyList<SslApplicationProtocol>? alpnProtocols = null)
    {
        var tcp = new TcpClient { NoDelay = true };
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(_options.ConnectTimeoutMs);

        bool useChain = !string.IsNullOrEmpty(_options.UpstreamProxyHost);
        if (useChain)
        {
            await tcp.ConnectAsync(_options.UpstreamProxyHost!, _options.UpstreamProxyPort, timeoutCts.Token)
                .ConfigureAwait(false);
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
            var ssl = new SslStream(stream, false, (_, cert, _, errors) =>
            {
                if (cert is not null) serverCert = new X509Certificate2(cert);
                // A debugging proxy accepts upstream certs by default to remain useful behind
                // interception / against self-signed origins. Opt in to real validation via options.
                if (_options.ValidateUpstreamCertificate)
                    return errors == SslPolicyErrors.None;
                return true;
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

        return new Connection { Tcp = tcp, Stream = stream, ServerCertificate = serverCert, NegotiatedProtocol = negotiated };
    }

    private static async Task SendConnectAsync(Stream stream, string host, int port, CancellationToken ct)
    {
        var req = $"CONNECT {host}:{port} HTTP/1.1\r\nHost: {host}:{port}\r\n\r\n";
        var bytes = Encoding.ASCII.GetBytes(req);
        await stream.WriteAsync(bytes, ct).ConfigureAwait(false);

        var reader = new StreamReaderEx(stream);
        // Read status line + headers until blank line.
        string? line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
        if (line is null || !line.Contains("200")) throw new IOException($"Upstream proxy CONNECT failed: {line}");
        while (!string.IsNullOrEmpty(await reader.ReadLineAsync(ct).ConfigureAwait(false))) { }
    }
}
