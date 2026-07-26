using System.Buffers.Binary;
using System.Diagnostics;
using System.Net.Security;
using System.Text;
using HttpSpy.Core.Models;
using HttpSpy.Core.Rules;

namespace HttpSpy.Core.Proxy.Http2;

/// <summary>
/// Handles a single client HTTP/2 connection (after ALPN negotiated "h2").
/// Decodes HPACK header blocks and DATA frames into individual transactions,
/// runs the rule engine, forwards each request to the origin (over h2 when the
/// origin supports it, otherwise HTTP/1.1) and re-encodes the response back to
/// the client. Streams are processed concurrently to preserve multiplexing.
/// </summary>
internal sealed class Http2Connection
{
    private static readonly HashSet<string> ConnectionSpecific = new(StringComparer.OrdinalIgnoreCase)
    {
        "connection", "keep-alive", "proxy-connection", "transfer-encoding", "upgrade", "te",
        "trailer", "host", "proxy-authenticate", "proxy-authorization"
    };

    private readonly ProxyEngine _engine;
    private readonly Upstream _upstream;
    private readonly NetworkThrottle _throttle;
    private readonly Stream _client;
    private readonly string _host;
    private readonly int _port;
    private readonly ClientConnection _conn;
    private readonly CancellationToken _ct;

    private readonly Http2Writer _writer;
    private readonly HpackDecoder _decoder = new();
    private readonly HpackEncoder _encoder = new();
    private readonly Dictionary<int, StreamState> _streams = new();
    private readonly List<Task> _pending = new();

    private int _headerContinuationStream = -1;
    private readonly MemoryStream _headerBuffer = new();
    private bool _headerEndStream;

    private sealed class StreamState
    {
        public List<HpackHeader> Headers = new();
        public readonly MemoryStream Body = new();
        public bool HeadersComplete;
        public bool EndStream;
    }

    public Http2Connection(ProxyEngine engine, Upstream upstream, Stream client,
        string host, int port, ClientConnection conn, NetworkThrottle throttle, CancellationToken ct)
    {
        _engine = engine;
        _upstream = upstream;
        _client = client;
        _throttle = throttle;
        _host = host;
        _port = port;
        _conn = conn;
        _ct = ct;
        _writer = new Http2Writer(client);
    }

    public async Task RunAsync()
    {
        // Verify the client connection preface.
        var preface = new byte[Http2Frame.ClientPreface.Length];
        if (!await ReadExactAsync(preface).ConfigureAwait(false) ||
            !preface.AsSpan().SequenceEqual(Http2Frame.ClientPreface))
        {
            _engine.RaiseLog("HTTP/2: invalid client preface");
            return;
        }

        await _writer.WriteFrameAsync(Http2Frame.Settings(new[]
        {
            (Http2Setting.EnablePush, 0u),
            (Http2Setting.MaxConcurrentStreams, 128u),
            (Http2Setting.InitialWindowSize, 1048576u),
        }), _ct).ConfigureAwait(false);
        await _writer.WriteFrameAsync(Http2Frame.WindowUpdate(0, 0x0FFF0000), _ct).ConfigureAwait(false);

        try
        {
            while (!_ct.IsCancellationRequested)
            {
                var frame = await Http2Frame.ReadAsync(_client, _ct).ConfigureAwait(false);
                if (frame is null) break;
                if (!await HandleFrameAsync(frame).ConfigureAwait(false)) break;
            }
        }
        catch (Exception ex)
        {
            _engine.RaiseLog($"HTTP/2 connection error: {ex.Message}");
        }
        finally
        {
            try { await Task.WhenAll(_pending).ConfigureAwait(false); } catch { /* ignore */ }
        }
    }

    private async Task<bool> HandleFrameAsync(Http2Frame frame)
    {
        // While awaiting CONTINUATION, only CONTINUATION frames on the same stream are legal.
        if (_headerContinuationStream != -1 &&
            !(frame.Type == Http2FrameType.Continuation && frame.StreamId == _headerContinuationStream))
        {
            await GoAwayAsync(Http2ErrorCode.ProtocolError).ConfigureAwait(false);
            return false;
        }

        switch (frame.Type)
        {
            case Http2FrameType.Settings:
                if (!frame.HasFlag(Http2Flags.Ack))
                {
                    ApplySettings(frame);
                    await _writer.WriteFrameAsync(Http2Frame.SettingsAck(), _ct).ConfigureAwait(false);
                }
                break;
            case Http2FrameType.WindowUpdate:
                _writer.OnWindowUpdate(frame.StreamId, ReadUInt31(frame.Payload));
                break;
            case Http2FrameType.Ping:
                if (!frame.HasFlag(Http2Flags.Ack))
                    await _writer.WriteFrameAsync(Http2Frame.PingAck(frame.Payload), _ct).ConfigureAwait(false);
                break;
            case Http2FrameType.Headers:
                HandleHeaders(frame);
                break;
            case Http2FrameType.Continuation:
                HandleContinuation(frame);
                break;
            case Http2FrameType.Data:
                await HandleDataAsync(frame).ConfigureAwait(false);
                break;
            case Http2FrameType.RstStream:
                _streams.Remove(frame.StreamId);
                break;
            case Http2FrameType.GoAway:
                return false;
            case Http2FrameType.Priority:
                break;
        }
        return true;
    }

    private void HandleHeaders(Http2Frame frame)
    {
        var state = GetOrCreateStream(frame.StreamId);
        state.EndStream = frame.HasFlag(Http2Flags.EndStream);
        if (frame.HasFlag(Http2Flags.EndHeaders))
        {
            state.Headers = _decoder.Decode(frame.HeaderBlockFragment()).ToList();
            state.HeadersComplete = true;
            MaybeDispatch(frame.StreamId, state);
        }
        else
        {
            _headerBuffer.SetLength(0);
            _headerBuffer.Write(frame.HeaderBlockFragment());
            _headerContinuationStream = frame.StreamId;
            _headerEndStream = state.EndStream;
        }
    }

    private void HandleContinuation(Http2Frame frame)
    {
        _headerBuffer.Write(frame.Payload);
        if (!frame.HasFlag(Http2Flags.EndHeaders)) return;

        var state = GetOrCreateStream(frame.StreamId);
        state.Headers = _decoder.Decode(_headerBuffer.ToArray()).ToList();
        state.HeadersComplete = true;
        state.EndStream = _headerEndStream;
        _headerContinuationStream = -1;
        _headerBuffer.SetLength(0);
        MaybeDispatch(frame.StreamId, state);
    }

    private async Task HandleDataAsync(Http2Frame frame)
    {
        if (!_streams.TryGetValue(frame.StreamId, out var state)) return;
        state.Body.Write(frame.DataPayload());
        // Replenish the flow-control window so the client can keep sending.
        if (frame.Payload.Length > 0)
        {
            await _writer.WriteFrameAsync(Http2Frame.WindowUpdate(0, frame.Payload.Length), _ct).ConfigureAwait(false);
            await _writer.WriteFrameAsync(Http2Frame.WindowUpdate(frame.StreamId, frame.Payload.Length), _ct).ConfigureAwait(false);
        }
        if (frame.HasFlag(Http2Flags.EndStream))
        {
            state.EndStream = true;
            MaybeDispatch(frame.StreamId, state);
        }
    }

    private StreamState GetOrCreateStream(int streamId)
    {
        if (!_streams.TryGetValue(streamId, out var state))
        {
            state = new StreamState();
            _streams[streamId] = state;
        }
        return state;
    }

    private void MaybeDispatch(int streamId, StreamState state)
    {
        if (!state.HeadersComplete || !state.EndStream) return;
        _streams.Remove(streamId);
        byte[] body = state.Body.ToArray();
        var headers = state.Headers;
        _pending.Add(Task.Run(() => ProcessStreamAsync(streamId, headers, body), _ct));
    }

    private async Task ProcessStreamAsync(int streamId, List<HpackHeader> headers, byte[] body)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            string method = "GET", path = "/", scheme = "https", authority = _host;
            var requestHeaders = new HeaderCollection();
            foreach (var h in headers)
            {
                switch (h.Name)
                {
                    case ":method": method = h.Value; break;
                    case ":path": path = h.Value; break;
                    case ":scheme": scheme = h.Value; break;
                    case ":authority": authority = h.Value; break;
                    default:
                        if (!h.Name.StartsWith(':')) requestHeaders.Add(h.Name, h.Value);
                        break;
                }
            }

            var (host, port) = SplitHostPort(authority, _port);
            var session = BuildSession(method, scheme, host, port, path, requestHeaders, body, streamId);
            _engine.RaiseStarted(session);

            var decision = _engine.Rules.EvaluateRequest(session);
            if (decision.Block)
            {
                await RespondSyntheticAsync(streamId, session, 403, "text/plain",
                    Encoding.UTF8.GetBytes("Request blocked by an HttpSpy rule."), sw).ConfigureAwait(false);
                return;
            }
            if (decision.AutoReply is { } reply)
            {
                await RespondSyntheticAsync(streamId, session, reply.Status, reply.ContentType, reply.Body, sw)
                    .ConfigureAwait(false);
                return;
            }
            if (decision.Breakpoint)
            {
                var paused = await _engine.RaiseBreakpointAsync(session, BreakpointPhase.BeforeRequest)
                    .ConfigureAwait(false);
                if (paused.Aborted)
                {
                    await RespondSyntheticAsync(streamId, session, 502, "text/plain",
                        Encoding.UTF8.GetBytes("Aborted at breakpoint."), sw).ConfigureAwait(false);
                    return;
                }
            }
            if (!string.IsNullOrEmpty(decision.RedirectUrl) &&
                Uri.TryCreate(decision.RedirectUrl, UriKind.Absolute, out var rd))
            {
                scheme = rd.Scheme; host = rd.Host; port = rd.Port; path = rd.PathAndQuery;
                session.Scheme = scheme; session.Host = host; session.RemotePort = port;
                session.Path = rd.AbsolutePath; session.QueryString = rd.Query.TrimStart('?');
                session.Url = session.FullUrl;
            }
            string connectHost = host;
            int connectPort = port;
            if (!string.IsNullOrEmpty(decision.ConnectHost)) connectHost = decision.ConnectHost!;
            if (decision.ConnectPort > 0) connectPort = decision.ConnectPort;
            if (decision.RewriteHost && !string.IsNullOrEmpty(decision.ConnectHost))
            {
                authority = decision.ConnectPort is > 0 and not 443 ? $"{connectHost}:{connectPort}" : connectHost;
                session.Host = connectHost;
            }
            if (decision.DelayMs > 0) await Task.Delay(decision.DelayMs, _ct).ConfigureAwait(false);

            await ForwardAsync(streamId, session, method, path, authority, host, connectHost, connectPort, scheme, sw)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _engine.RaiseLog($"HTTP/2 stream {streamId} error: {ex.Message}");
            try { await _writer.WriteFrameAsync(Http2Frame.RstStream(streamId, Http2ErrorCode.InternalError), _ct).ConfigureAwait(false); }
            catch { /* ignore */ }
        }
    }

    private async Task ForwardAsync(int streamId, HttpSession session, string method, string path,
        string authority, string host, string connectHost, int connectPort, string scheme, Stopwatch sw)
    {
        var alpn = new[] { SslApplicationProtocol.Http2, SslApplicationProtocol.Http11 };
        using var up = await _upstream.ConnectAsync(connectHost, connectPort, tls: true, _ct, alpn).ConfigureAwait(false);
        session.Timings.ConnectMs = sw.Elapsed.TotalMilliseconds;
        session.State = SessionState.SentToServer;

        // The h2 path has its own upstream connect, so it has to record the peer
        // address itself — otherwise the Server column was blank for every
        // HTTP/2 transaction while HTTP/1.1 rows showed one.
        if (up.Tcp.Client.RemoteEndPoint is System.Net.IPEndPoint peer)
        {
            session.RemoteAddress = peer.Address.ToString();
            _conn.RemoteAddress = session.RemoteAddress;
        }

        if (up.NegotiatedProtocol == "h2")
            await ForwardOverHttp2Async(streamId, session, method, path, authority, scheme, up, sw).ConfigureAwait(false);
        else
            await ForwardOverHttp11Async(streamId, session, method, path, authority, up, sw).ConfigureAwait(false);
    }

    private async Task ForwardOverHttp2Async(int streamId, HttpSession session, string method, string path,
        string authority, string scheme, Upstream.Connection up, Stopwatch sw)
    {
        var reqHeaders = new List<HpackHeader>
        {
            new(":method", method),
            new(":path", path),
            new(":scheme", scheme),
            new(":authority", authority),
        };
        foreach (var h in session.RequestHeaders)
            if (!ConnectionSpecific.Contains(h.Name) && !h.Name.StartsWith(':'))
                reqHeaders.Add(new HpackHeader(h.Name.ToLowerInvariant(), h.Value));

        var client = new Http2OriginClient(up.Stream);
        var resp = await client.SendAsync(reqHeaders, session.RequestBody, _ct).ConfigureAwait(false);
        session.Timings.WaitMs = sw.Elapsed.TotalMilliseconds - session.Timings.ConnectMs;

        var respHeaders = new HeaderCollection();
        foreach (var h in resp.Headers) respHeaders.Add(h.Name, h.Value);
        foreach (var h in resp.Trailers) respHeaders.Add(h.Name, h.Value);

        FinishResponse(session, resp.StatusCode, respHeaders, resp.Body);
        await SendResponseToClientAsync(streamId, session, sw).ConfigureAwait(false);
    }

    private async Task ForwardOverHttp11Async(int streamId, HttpSession session, string method, string path,
        string authority, Upstream.Connection up, Stopwatch sw)
    {
        var headers = new HeaderCollection();
        foreach (var h in session.RequestHeaders)
            if (!ConnectionSpecific.Contains(h.Name) && !h.Name.StartsWith(':'))
                headers.Add(h.Name, h.Value);
        headers.Set("Host", authority);
        if (session.RequestBody.Length > 0)
            headers.Set("Content-Length", session.RequestBody.Length.ToString());
        headers.Set("Connection", "close");

        var requestBytes = HttpWire.SerializeRequest(method, path, "HTTP/1.1", headers, session.RequestBody);
        await up.Stream.WriteAsync(requestBytes, _ct).ConfigureAwait(false);

        var upReader = new StreamReaderEx(up.Stream);
        var respHead = await HttpWire.ReadResponseHeadAsync(upReader, _ct).ConfigureAwait(false);
        session.Timings.WaitMs = sw.Elapsed.TotalMilliseconds - session.Timings.ConnectMs;
        if (respHead is null)
        {
            await RespondSyntheticAsync(streamId, session, 502, "text/plain",
                Encoding.UTF8.GetBytes("Empty response from server."), sw).ConfigureAwait(false);
            return;
        }

        bool bodyForbidden = string.Equals(method, "HEAD", StringComparison.OrdinalIgnoreCase) ||
                             respHead.StatusCode is 204 or 304;
        var body = await HttpWire
            .ReadResponseBodyAsync(upReader, respHead.Headers, bodyForbidden, _ct, _engine.Options.MaxBufferedBody)
            .ConfigureAwait(false);
        var rawBody = body.Data;
        session.ResponseBodyTruncated = body.Truncated;
        var encoding = respHead.Headers["Content-Encoding"];
        var decoded = body.Truncated ? rawBody : HttpWire.Decompress(rawBody, encoding);
        bool wasDecoded = !ReferenceEquals(decoded, rawBody);
        respHead.Headers.Remove("Transfer-Encoding");
        if (wasDecoded) respHead.Headers.Remove("Content-Encoding");

        var respHeaders = respHead.Headers.Clone();
        FinishResponse(session, respHead.StatusCode, respHeaders, decoded);
        session.EncodedBodySize = rawBody.LongLength;
        if (wasDecoded) session.OriginalContentEncoding = encoding;
        session.StatusText = respHead.ReasonPhrase;
        await SendResponseToClientAsync(streamId, session, sw).ConfigureAwait(false);
    }

    private void FinishResponse(HttpSession session, int status, HeaderCollection headers, byte[] body)
    {
        session.State = SessionState.ReceivingResponse;
        session.ResponseHttpVersion = "HTTP/2";
        session.StatusCode = status;
        if (string.IsNullOrEmpty(session.StatusText)) session.StatusText = StatusReason(status);
        session.ResponseHeaders = headers;
        session.ResponseBody = body;
        _engine.Rules.EvaluateResponse(session, out _);
    }

    private async Task SendResponseToClientAsync(int streamId, HttpSession session, Stopwatch sw)
    {
        var headerList = new List<HpackHeader> { new(":status", session.StatusCode.ToString()) };
        foreach (var h in session.ResponseHeaders)
        {
            string name = h.Name.ToLowerInvariant();
            if (ConnectionSpecific.Contains(name) || name == "content-length" || name.StartsWith(':')) continue;
            headerList.Add(new HpackHeader(name, h.Value));
        }
        headerList.Add(new HpackHeader("content-length", session.ResponseBody.Length.ToString()));

        byte[] block = _encoder.Encode(headerList);
        bool noBody = session.ResponseBody.Length == 0;
        // Bandwidth is paced by the throttled client stream underneath the writer;
        // the per-response latency belongs here, once per stream.
        await _throttle.DelayAsync(_ct).ConfigureAwait(false);
        await _writer.WriteFrameAsync(Http2Frame.Headers(streamId, block, endStream: noBody, endHeaders: true), _ct)
            .ConfigureAwait(false);
        if (!noBody)
            await _writer.WriteDataAsync(streamId, session.ResponseBody, endStream: true, _ct).ConfigureAwait(false);

        sw.Stop();
        session.Timings.TotalMs = sw.Elapsed.TotalMilliseconds;
        session.State = SessionState.Completed;
        session.EndTime = DateTime.Now;
        session.BytesReceived = session.ResponseBody.LongLength;
        _engine.RaiseCompleted(session);
    }

    private async Task RespondSyntheticAsync(int streamId, HttpSession session, int status,
        string contentType, byte[] body, Stopwatch sw)
    {
        var headers = new HeaderCollection();
        headers.Add("content-type", contentType);
        session.StatusCode = status;
        session.StatusText = StatusReason(status);
        session.ResponseHttpVersion = "HTTP/2";
        session.ResponseHeaders = headers;
        session.ResponseBody = body;
        await SendResponseToClientAsync(streamId, session, sw).ConfigureAwait(false);
    }

    private HttpSession BuildSession(string method, string scheme, string host, int port, string fullPath,
        HeaderCollection headers, byte[] body, int streamId)
    {
        string path = fullPath;
        string query = string.Empty;
        int q = path.IndexOf('?');
        if (q >= 0) { query = path[(q + 1)..]; path = path[..q]; }

        var session = new HttpSession
        {
            Kind = SessionKind.Https,
            IsTls = true,
            Method = method,
            Scheme = scheme,
            Host = host,
            RemotePort = port,
            Path = path,
            QueryString = query,
            HttpVersion = "HTTP/2",
            RequestHeaders = headers,
            RequestBody = body,
            State = SessionState.RequestReceived,
            StartTime = DateTime.Now,
        };
        // Stamping the stream id is what lets the UI show how these requests
        // were multiplexed over the single h2 connection.
        _conn.Stamp(session, streamId);
        session.Url = session.FullUrl;
        return session;
    }

    private void ApplySettings(Http2Frame frame)
    {
        for (int i = 0; i + 6 <= frame.Payload.Length; i += 6)
        {
            var id = (Http2Setting)BinaryPrimitives.ReadUInt16BigEndian(frame.Payload.AsSpan(i));
            uint value = BinaryPrimitives.ReadUInt32BigEndian(frame.Payload.AsSpan(i + 2));
            switch (id)
            {
                case Http2Setting.InitialWindowSize: _writer.SetPeerInitialWindow(value); break;
                case Http2Setting.MaxFrameSize: _writer.SetMaxFrameSize((int)value); break;
                case Http2Setting.HeaderTableSize: _decoder.SetMaxDynamicTableSize((int)value); break;
            }
        }
    }

    private async Task GoAwayAsync(Http2ErrorCode error)
    {
        try { await _writer.WriteFrameAsync(Http2Frame.GoAway(0, error), _ct).ConfigureAwait(false); }
        catch { /* ignore */ }
    }

    private async Task<bool> ReadExactAsync(byte[] buffer)
    {
        int read = 0;
        while (read < buffer.Length)
        {
            int n = await _client.ReadAsync(buffer.AsMemory(read), _ct).ConfigureAwait(false);
            if (n == 0) return false;
            read += n;
        }
        return true;
    }

    private static (string host, int port) SplitHostPort(string value, int defaultPort)
    {
        if (string.IsNullOrEmpty(value)) return ("", defaultPort);
        int idx = value.LastIndexOf(':');
        if (idx > 0 && int.TryParse(value[(idx + 1)..], out int p)) return (value[..idx], p);
        return (value, defaultPort);
    }

    private static long ReadUInt31(byte[] payload) =>
        payload.Length >= 4 ? BinaryPrimitives.ReadUInt32BigEndian(payload) & 0x7FFFFFFF : 0;

    private static string StatusReason(int status) => status switch
    {
        200 => "OK", 201 => "Created", 204 => "No Content", 206 => "Partial Content",
        301 => "Moved Permanently", 302 => "Found", 304 => "Not Modified",
        400 => "Bad Request", 401 => "Unauthorized", 403 => "Forbidden", 404 => "Not Found",
        500 => "Internal Server Error", 502 => "Bad Gateway", 503 => "Service Unavailable",
        _ => ""
    };
}
