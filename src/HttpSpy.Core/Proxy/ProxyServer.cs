using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using HttpSpy.Core.Models;
using HttpSpy.Core.Rules;

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

        bool passthrough = !Options.DecryptHttps ||
                           Options.TlsPassthroughHosts.Any(h => host.EndsWith(h, StringComparison.OrdinalIgnoreCase));

        if (passthrough)
        {
            await TunnelOpaqueAsync(host, port, reader, clientStream, origin).ConfigureAwait(false);
            return;
        }

        SslStream sslClient;
        try
        {
            var leaf = _engine.CertificateAuthority.GetCertificateForHost(host);
            sslClient = new SslStream(clientStream, leaveInnerStreamOpen: false);
            await sslClient.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
            {
                ServerCertificate = leaf,
                ClientCertificateRequired = false,
            }, _ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _engine.RaiseLog($"TLS handshake with client failed for {host}: {ex.Message}");
            return;
        }

        var sslReader = new StreamReaderEx(sslClient);
        bool keepAlive = true;
        while (keepAlive && !_ct.IsCancellationRequested)
        {
            var req = await HttpWire.ReadRequestHeadAsync(sslReader, _ct).ConfigureAwait(false);
            if (req is null) break;
            keepAlive = await ProcessRequestAsync(req, sslReader, sslClient, "https", host, port, origin)
                .ConfigureAwait(false);
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

    private static async Task RelayBytesAsync(Stream a, Stream b)
    {
        var t1 = a.CopyToAsync(b);
        var t2 = b.CopyToAsync(a);
        await Task.WhenAny(t1, t2).ConfigureAwait(false);
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
        session.RequestBody = await HttpWire.ReadBodyAsync(reader, head.Headers, requestBodyForbidden, _ct)
            .ConfigureAwait(false);
        session.BytesSent = reader.TotalBytesRead;

        bool clientWantsKeepAlive = WantsKeepAlive(head.Version, head.Headers["Connection"]);
        bool isWebSocket = IsWebSocketUpgrade(head.Headers);

        _engine.RaiseStarted(session);

        // ---- Request-phase rules --------------------------------------------
        var decision = _engine.Rules.EvaluateRequest(session);
        if (decision.Block)
        {
            await WriteSimpleResponseAsync(clientStream, session, 403, "Blocked by HttpSpy",
                "Request blocked by an HttpSpy rule.").ConfigureAwait(false);
            return clientWantsKeepAlive;
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
        if (decision.DelayMs > 0)
            await Task.Delay(decision.DelayMs, _ct).ConfigureAwait(false);

        // ---- Connect upstream + forward -------------------------------------
        Upstream.Connection up;
        try
        {
            up = await _upstream.ConnectAsync(host, port, scheme == "https", _ct).ConfigureAwait(false);
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
            var upstreamHeaders = BuildUpstreamHeaders(session, head, isWebSocket);
            var requestBytes = HttpWire.SerializeRequest(head.Method, head.Target, head.Version,
                upstreamHeaders, session.RequestBody);
            session.State = SessionState.SentToServer;
            await up.Stream.WriteAsync(requestBytes, _ct).ConfigureAwait(false);

            var upReader = new StreamReaderEx(up.Stream);
            var respHead = await HttpWire.ReadResponseHeadAsync(upReader, _ct).ConfigureAwait(false);
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
            var rawBody = await HttpWire.ReadResponseBodyAsync(upReader, respHead.Headers, bodyForbidden, _ct)
                .ConfigureAwait(false);
            session.BytesReceived = upReader.TotalBytesRead;

            // We de-chunk while reading and decode Content-Encoding so the body is
            // human-readable; the wire headers are adjusted to match (single
            // collection — session.ResponseHeaders — to avoid header drift).
            var encoding = respHead.Headers["Content-Encoding"];
            var decoded = HttpWire.Decompress(rawBody, encoding);
            bool wasDecoded = !ReferenceEquals(decoded, rawBody);
            session.ResponseBody = decoded;

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

            session.ResponseHeaders.Set("Content-Length", session.ResponseBody.Length.ToString());
            session.ResponseHeaders.Set("Connection", clientWantsKeepAlive ? "keep-alive" : "close");

            var responseBytes = HttpWire.SerializeResponse(respHead.Version, session.StatusCode,
                session.StatusText, session.ResponseHeaders, session.ResponseBody);
            await clientStream.WriteAsync(responseBytes, _ct).ConfigureAwait(false);

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
        foreach (var e in events) { session.ServerSentEvents.Add(e); any = true; }
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

    private static (string host, int port) SplitHostPort(string value, int defaultPort)
    {
        if (string.IsNullOrEmpty(value)) return ("", defaultPort);
        int idx = value.LastIndexOf(':');
        if (idx > 0 && int.TryParse(value[(idx + 1)..], out int p))
            return (value[..idx], p);
        return (value, defaultPort);
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
    }
}
