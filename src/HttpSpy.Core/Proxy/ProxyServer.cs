using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using HttpSpy.Core.Models;
using HttpSpy.Core.Rules;
using HttpSpy.Core.Proxy.Transparent;

namespace HttpSpy.Core.Proxy;

/// <summary>
/// The TCP listener and connection state machine that implements the capturing
/// proxy. Handles plain HTTP (absolute-form proxy requests), HTTPS via CONNECT
/// + TLS man-in-the-middle, keep-alive, WebSocket upgrades and Server-Sent
/// Events. Created and driven by <see cref="ProxyEngine"/>.
/// </summary>
internal sealed class ProxyServer : IDisposable
{
    private static readonly HashSet<string> HopByHopHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Connection", "Proxy-Connection", "Keep-Alive", "Transfer-Encoding", "TE",
        "Trailer", "Proxy-Authenticate", "Proxy-Authorization"
    };

    private readonly ProxyEngine _engine;
    private readonly Upstream _upstream;
    private TcpListener? _listener;
    private TcpListener? _transparentListener;
    private TransparentRedirector? _redirector;
    private CancellationToken _ct;

    public ProxyServer(ProxyEngine engine)
    {
        _engine = engine;
        _upstream = new Upstream(engine.Options);
    }

    private ProxyOptions Options => _engine.Options;

    public void Start(CancellationToken ct)
    {
        _ct = ct;
        var address = IPAddress.Parse(Options.ListenAddress);
        _listener = new TcpListener(address, Options.ListenPort);
        _listener.Start();
        _ = AcceptLoopAsync();

        if (Options.TransparentCapture)
            StartTransparentCapture();
    }

    /// <summary>
    /// Brings up the WinDivert redirector and the transparent listener (Windows
    /// only). Diverted connections arrive here with no CONNECT/absolute-URI, so
    /// the host is recovered from the TLS SNI or the HTTP Host header and the port
    /// from the redirector's original-destination table.
    /// </summary>
    private void StartTransparentCapture()
    {
        if (!OperatingSystem.IsWindows())
        {
            _engine.RaiseLog("Transparent capture is Windows-only (WinDivert); ignoring on this platform.");
            return;
        }

        try
        {
            _redirector = new TransparentRedirector(Options.TransparentListenPort);
            _redirector.Start();
            _transparentListener = new TcpListener(IPAddress.Any, Options.TransparentListenPort);
            _transparentListener.Start();
            _ = TransparentAcceptLoopAsync();
            _engine.RaiseLog($"Transparent capture active (WinDivert) on port {Options.TransparentListenPort}.");
        }
        catch (Exception ex)
        {
            _engine.RaiseLog($"Transparent capture failed to start: {ex.Message}");
            _redirector?.Dispose();
            _redirector = null;
        }
    }

    private async Task TransparentAcceptLoopAsync()
    {
        while (!_ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _transparentListener!.AcceptTcpClientAsync(_ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (Exception ex) { _engine.RaiseLog($"Transparent accept error: {ex.Message}"); continue; }

            _ = Task.Run(() => HandleTransparentClientAsync(client), _ct);
        }
    }

    private async Task HandleTransparentClientAsync(TcpClient client)
    {
        Interlocked.Increment(ref _engine.Statistics.ActiveConnections);
        try
        {
            client.NoDelay = true;
            int srcPort = client.Client.RemoteEndPoint is IPEndPoint rep ? rep.Port : 0;

            // Recover where the application originally intended to connect.
            int originalPort = 0;
            if (_redirector?.TryGetOriginalDestination(srcPort, out _, out var op) == true)
                originalPort = op;

            (int pid, string name) origin = Options.ResolveProcess
                ? _engine.ProcessResolver.Resolve(srcPort)
                : (0, string.Empty);

            var stream = client.GetStream();

            // Peek the start of the stream to determine TLS vs plain HTTP and host.
            var peek = new byte[8192];
            int read = await stream.ReadAsync(peek, _ct).ConfigureAwait(false);
            if (read <= 0) return;
            var prefix = peek.AsSpan(0, read).ToArray();
            var prefixed = new PrefixedStream(prefix, stream);

            if (prefix[0] == 0x16) // TLS handshake record
            {
                string host = TlsClientHello.TryParseSni(prefix) ?? "unknown";
                int port = originalPort == 0 ? 443 : originalPort;
                await RunTlsMitmAsync(prefixed, host, port, origin).ConfigureAwait(false);
            }
            else
            {
                int port = originalPort == 0 ? 80 : originalPort;
                var reader = new StreamReaderEx(prefixed);
                bool keepAlive = true;
                while (keepAlive && !_ct.IsCancellationRequested)
                {
                    reader.Mark();
                    var req = await HttpWire.ReadRequestHeadAsync(reader, _ct).ConfigureAwait(false);
                    if (req is null) break;
                    string host = req.Headers["Host"] ?? "unknown";
                    keepAlive = await ProcessRequestAsync(req, reader, prefixed, "http", host, port, origin)
                        .ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex)
        {
            _engine.RaiseLog($"Transparent connection error: {ex.Message}");
        }
        finally
        {
            Interlocked.Decrement(ref _engine.Statistics.ActiveConnections);
            try { client.Dispose(); } catch { /* ignore */ }
        }
    }

    private async Task AcceptLoopAsync()
    {
        while (!_ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener!.AcceptTcpClientAsync(_ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (Exception ex)
            {
                _engine.RaiseLog($"Accept error: {ex.Message}");
                continue;
            }

            _ = Task.Run(() => HandleClientAsync(client), _ct);
        }
    }

    private async Task HandleClientAsync(TcpClient client)
    {
        Interlocked.Increment(ref _engine.Statistics.ActiveConnections);
        int clientPort = 0;
        try
        {
            client.NoDelay = true;
            if (client.Client.RemoteEndPoint is IPEndPoint rep) clientPort = rep.Port;

            (int pid, string name) origin = Options.ResolveProcess
                ? _engine.ProcessResolver.Resolve(clientPort)
                : (0, string.Empty);

            using var stream = client.GetStream();
            var reader = new StreamReaderEx(stream);

            bool keepAlive = true;
            while (keepAlive && !_ct.IsCancellationRequested)
            {
                reader.Mark();
                var head = await HttpWire.ReadRequestHeadAsync(reader, _ct).ConfigureAwait(false);
                if (head is null) break;

                if (string.Equals(head.Method, "CONNECT", StringComparison.OrdinalIgnoreCase))
                {
                    await HandleConnectAsync(head, reader, stream, origin).ConfigureAwait(false);
                    break; // CONNECT consumes the connection
                }

                keepAlive = await HandlePlainRequestAsync(head, reader, stream, origin).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _engine.RaiseLog($"Connection error: {ex.Message}");
        }
        finally
        {
            Interlocked.Decrement(ref _engine.Statistics.ActiveConnections);
            try { client.Dispose(); } catch { /* ignore */ }
        }
    }

    // ---- HTTPS via CONNECT ---------------------------------------------------
    private async Task HandleConnectAsync(HttpWire.RequestHead head, StreamReaderEx reader,
        Stream clientStream, (int pid, string name) origin)
    {
        var (host, port) = SplitHostPort(head.Target, 443);

        // Acknowledge the tunnel.
        var ack = Encoding.ASCII.GetBytes("HTTP/1.1 200 Connection Established\r\n\r\n");
        await clientStream.WriteAsync(ack, _ct).ConfigureAwait(false);

        // Guard against blank entries in the passthrough list: string.EndsWith("")
        // is always true, so one empty line in the Options textbox would silently
        // disable decryption for every host.
        bool passthrough = !Options.DecryptHttps ||
                           Options.TlsPassthroughHosts.Any(h =>
                               !string.IsNullOrWhiteSpace(h) &&
                               host.EndsWith(h.Trim(), StringComparison.OrdinalIgnoreCase));

        if (passthrough)
        {
            await TunnelOpaqueAsync(host, port, reader, clientStream, origin).ConfigureAwait(false);
            return;
        }

        await RunTlsMitmAsync(clientStream, host, port, origin).ConfigureAwait(false);
    }

    /// <summary>
    /// Performs the TLS man-in-the-middle for an already-established client byte
    /// stream: presents a leaf cert for <paramref name="host"/>, negotiates ALPN,
    /// and dispatches to the HTTP/2 connection handler or the HTTP/1.1 loop. Shared
    /// by the explicit-proxy CONNECT path and the transparent-capture listener.
    /// </summary>
    private async Task RunTlsMitmAsync(Stream clientStream, string host, int port,
        (int pid, string name) origin)
    {
        SslStream sslClient;
        X509Certificate2 leaf;
        try
        {
            leaf = _engine.CertificateAuthority.GetCertificateForHost(host);
            sslClient = new SslStream(clientStream, leaveInnerStreamOpen: false);
        }
        catch (Exception ex)
        {
            _engine.RaiseLog($"Could not issue a leaf certificate for {host}: {ex.Message}");
            return;
        }

        // From here on the SslStream owns the client stream, so it must always be
        // disposed — the previous code leaked one per intercepted connection.
        await using (sslClient.ConfigureAwait(false))
        {
            try
            {
                var serverOptions = new SslServerAuthenticationOptions
                {
                    ServerCertificate = leaf,
                    ClientCertificateRequired = false,
                };
                // Offer HTTP/2 via ALPN so h2 clients negotiate it; fall back to HTTP/1.1.
                if (Options.EnableHttp2)
                    serverOptions.ApplicationProtocols = new List<SslApplicationProtocol>
                    {
                        SslApplicationProtocol.Http2, SslApplicationProtocol.Http11
                    };
                await sslClient.AuthenticateAsServerAsync(serverOptions, _ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _engine.RaiseLog($"TLS handshake with client failed for {host}: {ex.Message}");
                return;
            }

            if (sslClient.NegotiatedApplicationProtocol == SslApplicationProtocol.Http2)
            {
                var h2 = new Http2.Http2Connection(_engine, _upstream, sslClient, host, port, origin, _ct);
                await h2.RunAsync().ConfigureAwait(false);
                return;
            }

            var sslReader = new StreamReaderEx(sslClient);
            bool keepAlive = true;
            while (keepAlive && !_ct.IsCancellationRequested)
            {
                sslReader.Mark();
                var req = await HttpWire.ReadRequestHeadAsync(sslReader, _ct).ConfigureAwait(false);
                if (req is null) break;
                keepAlive = await ProcessRequestAsync(req, sslReader, sslClient, "https", host, port, origin)
                    .ConfigureAwait(false);
            }
        }
    }

    private async Task TunnelOpaqueAsync(string host, int port, StreamReaderEx reader, Stream clientStream,
        (int pid, string name) origin)
    {
        var session = new HttpSession
        {
            Kind = SessionKind.Tunnel, IsTls = true, Method = "CONNECT",
            Host = host, RemotePort = port, Scheme = "https",
            Url = $"{host}:{port}", ProcessId = origin.pid, ProcessName = origin.name,
            State = SessionState.SentToServer
        };
        _engine.RaiseStarted(session);

        try
        {
            using var up = await _upstream.ConnectAsync(host, port, tls: false, _ct).ConfigureAwait(false);
            var prime = reader.DrainBuffered();
            if (prime.Length > 0) await up.Stream.WriteAsync(prime, _ct).ConfigureAwait(false);
            await RelayBytesAsync(clientStream, up.Stream).ConfigureAwait(false);
            session.State = SessionState.Completed;
        }
        catch (Exception ex)
        {
            session.Error = ex.Message;
            session.State = SessionState.Faulted;
        }
        finally
        {
            session.EndTime = DateTime.Now;
            _engine.RaiseCompleted(session);
        }
    }

    /// <summary>
    /// Pumps bytes in both directions until either side closes. Faults on the
    /// copy tasks are expected (the peer just went away) and are swallowed so a
    /// half-closed tunnel does not surface as an unobserved task exception.
    /// </summary>
    private async Task RelayBytesAsync(Stream a, Stream b)
    {
        var t1 = CopySafeAsync(a, b);
        var t2 = CopySafeAsync(b, a);
        await Task.WhenAny(t1, t2).ConfigureAwait(false);
    }

    private async Task CopySafeAsync(Stream from, Stream to)
    {
        try { await from.CopyToAsync(to, 32 * 1024, _ct).ConfigureAwait(false); }
        catch { /* peer closed or capture stopped */ }
    }

    // ---- Plain HTTP proxy request -------------------------------------------
    private async Task<bool> HandlePlainRequestAsync(HttpWire.RequestHead head, StreamReaderEx reader,
        Stream clientStream, (int pid, string name) origin)
    {
        string scheme = "http";
        string host;
        int port = 80;
        if (head.Target.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            head.Target.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            var uri = new Uri(head.Target);
            scheme = uri.Scheme;
            host = uri.Host;
            port = uri.Port;
            head.Target = uri.PathAndQuery;
        }
        else
        {
            host = head.Headers["Host"] ?? "";
            var hp = SplitHostPort(host, 80);
            host = hp.host;
            port = hp.port;
        }

        return await ProcessRequestAsync(head, reader, clientStream, scheme, host, port, origin)
            .ConfigureAwait(false);
    }

    // ---- The shared request pipeline ----------------------------------------
    private async Task<bool> ProcessRequestAsync(HttpWire.RequestHead head, StreamReaderEx reader,
        Stream clientStream, string scheme, string host, int port, (int pid, string name) origin)
    {
        var sw = Stopwatch.StartNew();
        var session = BuildSession(head, scheme, host, port, origin);

        bool requestBodyForbidden = string.Equals(head.Method, "TRACE", StringComparison.OrdinalIgnoreCase);
        var requestBody = await HttpWire
            .ReadBodyAsync(reader, head.Headers, requestBodyForbidden, _ct, Options.MaxBufferedBody)
            .ConfigureAwait(false);
        session.RequestBody = requestBody.Data;
        session.RequestBodyTruncated = requestBody.Truncated;

        // Measured from the mark the read loop set before the request line, so
        // this counts the head plus the body of *this* request — not the running
        // total of every request that shared the keep-alive connection.
        session.BytesSent = reader.BytesSinceMark;

        bool clientWantsKeepAlive = WantsKeepAlive(head.Version, head.Headers["Connection"]);
        bool isWebSocket = IsWebSocketUpgrade(head.Headers);

        _engine.RaiseStarted(session);

        // ---- Request-phase rules --------------------------------------------
        var decision = _engine.Rules.EvaluateRequest(session);
        if (decision.Block)
        {
            // WriteSimpleResponseAsync sends "Connection: close", so the client
            // must not be told to keep the connection alive.
            await WriteSimpleResponseAsync(clientStream, session, 403, "Blocked by HttpSpy",
                "Request blocked by an HttpSpy rule.").ConfigureAwait(false);
            return false;
        }
        if (decision.AutoReply is { } reply)
        {
            await WriteAutoReplyAsync(clientStream, session, reply, clientWantsKeepAlive).ConfigureAwait(false);
            return clientWantsKeepAlive;
        }
        if (decision.Breakpoint)
        {
            var paused = await _engine.RaiseBreakpointAsync(session, BreakpointPhase.BeforeRequest)
                .ConfigureAwait(false);
            if (paused.Aborted)
            {
                await WriteSimpleResponseAsync(clientStream, session, 502, "Aborted",
                    "Transaction aborted at breakpoint.").ConfigureAwait(false);
                return false;
            }
        }
        if (!string.IsNullOrEmpty(decision.RedirectUrl) && Uri.TryCreate(decision.RedirectUrl, UriKind.Absolute, out var rd))
        {
            scheme = rd.Scheme; host = rd.Host; port = rd.Port;
            head.Target = rd.PathAndQuery;
            session.Scheme = scheme; session.Host = host; session.RemotePort = port;
            session.Path = rd.AbsolutePath; session.QueryString = rd.Query.TrimStart('?');
            session.Url = decision.RedirectUrl;
        }
        // ---- Endpoint redirect (TCP/IP Redirector analog) ------------------
        // Connect to a different host/port while leaving the request line and
        // (optionally) the Host header untouched.
        string connectHost = host;
        int connectPort = port;
        if (!string.IsNullOrEmpty(decision.ConnectHost)) connectHost = decision.ConnectHost!;
        if (decision.ConnectPort > 0) connectPort = decision.ConnectPort;
        if (decision.RewriteHost && !string.IsNullOrEmpty(decision.ConnectHost))
        {
            string newHost = decision.ConnectPort is > 0 and not 80 and not 443
                ? $"{decision.ConnectHost}:{decision.ConnectPort}"
                : decision.ConnectHost!;
            session.RequestHeaders.Set("Host", newHost);
            session.Host = decision.ConnectHost!;
        }

        if (decision.DelayMs > 0)
            await Task.Delay(decision.DelayMs, _ct).ConfigureAwait(false);

        // ---- Connect upstream + forward -------------------------------------
        Upstream.Connection up;
        var connectStart = sw.Elapsed.TotalMilliseconds;
        try
        {
            up = await _upstream.ConnectAsync(connectHost, connectPort, scheme == "https", _ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            session.Error = $"Connect failed: {ex.Message}";
            session.State = SessionState.Faulted;
            session.EndTime = DateTime.Now;
            await WriteSimpleResponseAsync(clientStream, session, 502, "Bad Gateway", ex.Message).ConfigureAwait(false);
            _engine.RaiseCompleted(session);
            return false;
        }

        using (up)
        {
            session.Timings.ConnectMs = sw.Elapsed.TotalMilliseconds - connectStart;

            var upstreamHeaders = BuildUpstreamHeaders(session, head, isWebSocket);

            // A chained forward proxy expects an absolute-form target
            // ("GET http://host/path HTTP/1.1"); only the origin server takes the
            // origin-form path. Getting this wrong made upstream-proxy chaining
            // fail for every plain-HTTP request.
            string requestTarget = _upstream.RequiresAbsoluteForm(scheme == "https")
                ? BuildAbsoluteTarget(scheme, host, port, head.Target)
                : head.Target;

            var requestBytes = HttpWire.SerializeRequest(head.Method, requestTarget, head.Version,
                upstreamHeaders, session.RequestBody);
            session.State = SessionState.SentToServer;
            var sendStart = sw.Elapsed.TotalMilliseconds;
            await up.Stream.WriteAsync(requestBytes, _ct).ConfigureAwait(false);
            session.Timings.SendMs = sw.Elapsed.TotalMilliseconds - sendStart;

            var upReader = new StreamReaderEx(up.Stream);
            var waitStart = sw.Elapsed.TotalMilliseconds;
            var respHead = await HttpWire.ReadResponseHeadAsync(upReader, _ct).ConfigureAwait(false);
            session.Timings.WaitMs = sw.Elapsed.TotalMilliseconds - waitStart;
            if (respHead is null)
            {
                session.Error = "Empty response from server";
                session.State = SessionState.Faulted;
                session.EndTime = DateTime.Now;
                _engine.RaiseCompleted(session);
                return false;
            }

            session.State = SessionState.ReceivingResponse;
            session.ResponseHttpVersion = respHead.Version;
            session.StatusCode = respHead.StatusCode;
            session.StatusText = respHead.ReasonPhrase;
            session.ResponseHeaders = respHead.Headers.Clone();

            // ---- WebSocket upgrade ------------------------------------------
            if (isWebSocket && respHead.StatusCode == 101)
            {
                Interlocked.Increment(ref _engine.Statistics.WebSocketSessions);
                session.Kind = SessionKind.WebSocket;
                var resp101 = HttpWire.SerializeResponse(respHead.Version, 101, respHead.ReasonPhrase,
                    respHead.Headers, Array.Empty<byte>());
                await clientStream.WriteAsync(resp101, _ct).ConfigureAwait(false);
                _engine.RaiseUpdated(session);

                if (Options.CaptureWebSockets)
                {
                    var relay = new WebSocketRelay(session, _engine);
                    await relay.RunAsync(clientStream, up.Stream, reader.DrainBuffered(),
                        upReader.DrainBuffered(), _ct).ConfigureAwait(false);
                }
                else
                {
                    await RelayBytesAsync(clientStream, up.Stream).ConfigureAwait(false);
                }
                session.State = SessionState.Completed;
                session.EndTime = DateTime.Now;
                _engine.RaiseCompleted(session);
                return false;
            }

            bool bodyForbidden = IsBodyForbidden(head.Method, respHead.StatusCode);

            // ---- Server-Sent Events streaming -------------------------------
            if (!bodyForbidden && IsEventStream(respHead.Headers))
            {
                session.Kind = SessionKind.ServerSentEvents;
                await StreamSseAsync(session, respHead, upReader, clientStream, sw, clientWantsKeepAlive)
                    .ConfigureAwait(false);
                return false;
            }

            // ---- Ordinary response ------------------------------------------
            var recvStart = sw.Elapsed.TotalMilliseconds;
            var responseBody = await HttpWire
                .ReadResponseBodyAsync(upReader, respHead.Headers, bodyForbidden, _ct, Options.MaxBufferedBody)
                .ConfigureAwait(false);
            var rawBody = responseBody.Data;
            session.ResponseBodyTruncated = responseBody.Truncated;
            session.Timings.ReceiveMs = sw.Elapsed.TotalMilliseconds - recvStart;
            session.BytesReceived = upReader.TotalBytesRead;

            // We de-chunk while reading and decode Content-Encoding so the body is
            // human-readable; the wire headers are adjusted to match (single
            // collection — session.ResponseHeaders — to avoid header drift).
            // A truncated body is a prefix of the compressed stream, so decoding it
            // would fail or yield garbage; keep it as-is and say so instead.
            var encoding = respHead.Headers["Content-Encoding"];
            var decoded = session.ResponseBodyTruncated ? rawBody : HttpWire.Decompress(rawBody, encoding);
            bool wasDecoded = !ReferenceEquals(decoded, rawBody);
            session.ResponseBody = decoded;
            session.EncodedBodySize = rawBody.LongLength;
            if (wasDecoded) session.OriginalContentEncoding = encoding;

            session.ResponseHeaders.Remove("Transfer-Encoding");
            if (wasDecoded) session.ResponseHeaders.Remove("Content-Encoding");

            // ---- Response-phase rules + breakpoint --------------------------
            _engine.Rules.EvaluateResponse(session, out bool respBreak);
            if (respBreak)
            {
                var paused = await _engine.RaiseBreakpointAsync(session, BreakpointPhase.BeforeResponse)
                    .ConfigureAwait(false);
                if (paused.Aborted) return false;
            }

            // A 204/304 or a response to HEAD must not carry a body, and per
            // RFC 9110 §8.6 a 304 keeps the origin's Content-Length untouched —
            // stamping "Content-Length: 0" on it corrupts the client's cache.
            if (!bodyForbidden)
                session.ResponseHeaders.Set("Content-Length", session.ResponseBody.Length.ToString());
            session.ResponseHeaders.Set("Connection", clientWantsKeepAlive ? "keep-alive" : "close");

            var responseBytes = HttpWire.SerializeResponse(respHead.Version, session.StatusCode,
                session.StatusText, session.ResponseHeaders, session.ResponseBody);
            await WriteThrottledAsync(clientStream, responseBytes).ConfigureAwait(false);

            sw.Stop();
            session.Timings.TotalMs = sw.Elapsed.TotalMilliseconds;
            session.State = SessionState.Completed;
            session.EndTime = DateTime.Now;
            _engine.RaiseCompleted(session);

            return clientWantsKeepAlive;
        }
    }

    // ---- Server-Sent Events --------------------------------------------------
    private async Task StreamSseAsync(HttpSession session, HttpWire.ResponseHead respHead,
        StreamReaderEx upReader, Stream clientStream, Stopwatch sw, bool keepAlive)
    {
        var outHeaders = respHead.Headers.Clone();
        outHeaders.Remove("Content-Length");
        outHeaders.Set("Connection", "keep-alive");
        var headBytes = HttpWire.SerializeResponse(respHead.Version, respHead.StatusCode,
            respHead.ReasonPhrase, outHeaders, Array.Empty<byte>());
        await clientStream.WriteAsync(headBytes, _ct).ConfigureAwait(false);
        _engine.RaiseUpdated(session);

        var parser = new SseParser();
        var prime = upReader.DrainBuffered();
        if (prime.Length > 0)
        {
            await clientStream.WriteAsync(prime, _ct).ConfigureAwait(false);
            RecordSse(session, parser.Feed(prime));
        }

        try
        {
            while (!_ct.IsCancellationRequested)
            {
                var chunk = await upReader.ReadSomeAsync(_ct).ConfigureAwait(false);
                if (chunk.Length == 0) break;
                await clientStream.WriteAsync(chunk, _ct).ConfigureAwait(false);
                RecordSse(session, parser.Feed(chunk));
            }
        }
        catch { /* stream ended */ }

        sw.Stop();
        session.Timings.TotalMs = sw.Elapsed.TotalMilliseconds;
        session.State = SessionState.Completed;
        session.EndTime = DateTime.Now;
        session.BytesReceived = upReader.TotalBytesRead;
        _engine.RaiseCompleted(session);
    }

    private void RecordSse(HttpSession session, IEnumerable<ServerSentEvent> events)
    {
        bool any = false;
        int cap = Options.MaxServerSentEvents;
        foreach (var e in events)
        {
            // A long-lived event stream is unbounded by nature; keep a rolling
            // window so an overnight capture cannot exhaust memory.
            session.AddServerSentEvent(e, cap);
            any = true;
        }
        if (any) _engine.RaiseUpdated(session);
    }

    // ---- Helpers -------------------------------------------------------------
    private HttpSession BuildSession(HttpWire.RequestHead head, string scheme, string host, int port,
        (int pid, string name) origin)
    {
        string path = head.Target;
        string query = string.Empty;
        int q = path.IndexOf('?');
        if (q >= 0) { query = path[(q + 1)..]; path = path[..q]; }

        var session = new HttpSession
        {
            Kind = scheme == "https" ? SessionKind.Https : SessionKind.Http,
            IsTls = scheme == "https",
            Method = head.Method,
            Scheme = scheme,
            Host = host,
            RemotePort = port,
            Path = path,
            QueryString = query,
            HttpVersion = head.Version,
            RequestHeaders = head.Headers.Clone(),
            ProcessId = origin.pid,
            ProcessName = origin.name,
            State = SessionState.RequestReceived,
            StartTime = DateTime.Now,
        };
        session.Url = session.FullUrl;
        return session;
    }

    private static HeaderCollection BuildUpstreamHeaders(HttpSession session, HttpWire.RequestHead head, bool isWebSocket)
    {
        var headers = new HeaderCollection();
        foreach (var h in session.RequestHeaders)
        {
            if (isWebSocket && (h.Name.Equals("Connection", StringComparison.OrdinalIgnoreCase) ||
                                h.Name.Equals("Upgrade", StringComparison.OrdinalIgnoreCase)))
            {
                headers.Add(h.Name, h.Value);
                continue;
            }
            if (HopByHopHeaders.Contains(h.Name)) continue;
            headers.Add(h.Name, h.Value);
        }
        if (!headers.Contains("Host")) headers.Set("Host", session.Host);
        // We de-chunked the request body, so advertise an accurate length.
        if (session.RequestBody.Length > 0 || HasBodyMethod(head.Method))
            headers.Set("Content-Length", session.RequestBody.Length.ToString());
        if (!isWebSocket) headers.Set("Connection", "close");
        return headers;
    }

    private async Task WriteSimpleResponseAsync(Stream clientStream, HttpSession session, int status,
        string reason, string message)
    {
        var body = Encoding.UTF8.GetBytes(message);
        var headers = new HeaderCollection();
        headers.Add("Content-Type", "text/plain; charset=utf-8");
        headers.Add("Content-Length", body.Length.ToString());
        headers.Add("Connection", "close");
        var bytes = HttpWire.SerializeResponse("HTTP/1.1", status, reason, headers, body);
        try { await clientStream.WriteAsync(bytes, _ct).ConfigureAwait(false); } catch { /* ignore */ }

        session.StatusCode = status;
        session.StatusText = reason;
        session.ResponseHeaders = headers;
        session.ResponseBody = body;
        session.State = SessionState.Completed;
        session.EndTime = DateTime.Now;
        _engine.RaiseCompleted(session);
    }

    private async Task WriteAutoReplyAsync(Stream clientStream, HttpSession session, AutoReply reply, bool keepAlive)
    {
        var headers = new HeaderCollection();
        headers.Add("Content-Type", reply.ContentType);
        headers.Add("Content-Length", reply.Body.Length.ToString());
        headers.Add("Connection", keepAlive ? "keep-alive" : "close");
        headers.Add("X-HttpSpy-AutoReply", "1");
        var bytes = HttpWire.SerializeResponse("HTTP/1.1", reply.Status, reply.Reason, headers, reply.Body);
        await clientStream.WriteAsync(bytes, _ct).ConfigureAwait(false);

        session.StatusCode = reply.Status;
        session.StatusText = reply.Reason;
        session.ResponseHeaders = headers;
        session.ResponseBody = reply.Body;
        session.State = SessionState.Completed;
        session.EndTime = DateTime.Now;
        _engine.RaiseCompleted(session);
    }

    /// <summary>
    /// Writes a response to the client, optionally injecting latency and rate-limiting
    /// throughput to simulate slow networks (the "Network simulation" feature).
    /// </summary>
    private async Task WriteThrottledAsync(Stream stream, byte[] data)
    {
        var opt = Options;
        if (opt.ExtraLatencyMs > 0 && opt.ThrottleEnabled)
            await Task.Delay(opt.ExtraLatencyMs, _ct).ConfigureAwait(false);

        if (!opt.ThrottleEnabled || opt.ThrottleKbps <= 0)
        {
            await stream.WriteAsync(data, _ct).ConfigureAwait(false);
            return;
        }

        double bytesPerSec = opt.ThrottleKbps * 1024.0 / 8.0;
        int chunk = Math.Max(64, (int)(bytesPerSec / 20.0)); // ~50 ms slices
        int offset = 0;
        while (offset < data.Length)
        {
            int n = Math.Min(chunk, data.Length - offset);
            await stream.WriteAsync(data.AsMemory(offset, n), _ct).ConfigureAwait(false);
            await stream.FlushAsync(_ct).ConfigureAwait(false);
            offset += n;
            await Task.Delay(TimeSpan.FromSeconds(n / bytesPerSec), _ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Splits an authority into host and port. IPv6 literals are handled: a
    /// bracketed form keeps its brackets ("[::1]:443" → "[::1]", 443) and a bare
    /// literal is never mistaken for a host:port pair ("::1" → "::1", default).
    /// </summary>
    internal static (string host, int port) SplitHostPort(string value, int defaultPort)
    {
        if (string.IsNullOrEmpty(value)) return ("", defaultPort);
        value = value.Trim();

        if (value[0] == '[')
        {
            int close = value.IndexOf(']');
            if (close < 0) return (value, defaultPort);
            string literal = value[..(close + 1)];
            if (close + 1 < value.Length && value[close + 1] == ':' &&
                int.TryParse(value[(close + 2)..], out int bracketed))
                return (literal, bracketed);
            return (literal, defaultPort);
        }

        int idx = value.LastIndexOf(':');
        // More than one colon and no brackets means a bare IPv6 literal, not a port.
        if (idx > 0 && value.IndexOf(':') == idx && int.TryParse(value[(idx + 1)..], out int p) &&
            p is > 0 and <= 65535)
            return (value[..idx], p);
        return (value, defaultPort);
    }

    /// <summary>
    /// Builds the absolute-form request target a chained forward proxy expects,
    /// omitting the port when it is the scheme default.
    /// </summary>
    internal static string BuildAbsoluteTarget(string scheme, string host, int port, string originForm)
    {
        if (originForm.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            originForm.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return originForm;

        bool defaultPort = (scheme == "https" && port == 443) || (scheme == "http" && port == 80) || port <= 0;
        string authority = defaultPort ? host : $"{host}:{port}";
        if (!originForm.StartsWith('/')) originForm = "/" + originForm;
        return $"{scheme}://{authority}{originForm}";
    }

    private static bool WantsKeepAlive(string version, string? connectionHeader)
    {
        bool http11 = version.Contains("1.1");
        if (connectionHeader is null) return http11;
        if (connectionHeader.Contains("close", StringComparison.OrdinalIgnoreCase)) return false;
        if (connectionHeader.Contains("keep-alive", StringComparison.OrdinalIgnoreCase)) return true;
        return http11;
    }

    private static bool IsWebSocketUpgrade(HeaderCollection headers)
    {
        var upgrade = headers["Upgrade"];
        var connection = headers["Connection"];
        return upgrade is not null && upgrade.Contains("websocket", StringComparison.OrdinalIgnoreCase) &&
               connection is not null && connection.Contains("upgrade", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsEventStream(HeaderCollection headers)
    {
        var ct = headers["Content-Type"];
        return ct is not null && ct.Contains("text/event-stream", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsBodyForbidden(string method, int status) =>
        string.Equals(method, "HEAD", StringComparison.OrdinalIgnoreCase) ||
        status is 204 or 304 || (status >= 100 && status < 200);

    private static bool HasBodyMethod(string method) =>
        method is "POST" or "PUT" or "PATCH" or "DELETE";

    public void Dispose()
    {
        try { _listener?.Stop(); } catch { /* ignore */ }
        try { _transparentListener?.Stop(); } catch { /* ignore */ }
        try { _redirector?.Dispose(); } catch { /* ignore */ }
    }
}
