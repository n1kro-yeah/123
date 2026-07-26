using System.Text;
using HttpSpy.Core.Util;

namespace HttpSpy.Core.Models;

/// <summary>
/// A captured HTTP(S) transaction: the request, the response, timings, the
/// originating process, and any streamed messages (WebSocket / SSE).
/// This is the central data record displayed in the session grid.
/// </summary>
public sealed class HttpSession
{
    private static long _counter;

    public HttpSession()
    {
        Id = Guid.NewGuid();
        Index = (int)System.Threading.Interlocked.Increment(ref _counter);
    }

    public Guid Id { get; init; }

    /// <summary>Monotonic 1-based sequence number shown in the grid.</summary>
    public int Index { get; init; }

    // ---- Connection / origin -------------------------------------------------
    public SessionKind Kind { get; set; } = SessionKind.Http;
    public bool IsTls { get; set; }
    public string ClientAddress { get; set; } = string.Empty;
    public int ClientPort { get; set; }
    public string RemoteAddress { get; set; } = string.Empty;
    public int RemotePort { get; set; }
    public int ProcessId { get; set; }
    public string ProcessName { get; set; } = string.Empty;

    /// <summary>
    /// Identifies the transport connection this transaction travelled on. Several
    /// transactions share one id when a connection is reused (HTTP keep-alive) or
    /// multiplexed (HTTP/2), which is what makes the connection tree meaningful.
    /// </summary>
    public long ConnectionId { get; set; }

    /// <summary>The HTTP/2 stream id, or 0 for HTTP/1.x where streams do not exist.</summary>
    public int StreamId { get; set; }

    /// <summary>Sequence of this transaction within its connection, 1-based.</summary>
    public int ConnectionSequence { get; set; }

    /// <summary>Hands out process-wide unique connection ids.</summary>
    private static long _connectionCounter;

    public static long NextConnectionId() =>
        System.Threading.Interlocked.Increment(ref _connectionCounter);

    // ---- Request -------------------------------------------------------------
    public string Method { get; set; } = "GET";
    public string Url { get; set; } = string.Empty;
    public string Scheme { get; set; } = "http";
    public string Host { get; set; } = string.Empty;
    public string Path { get; set; } = "/";
    public string QueryString { get; set; } = string.Empty;
    public string HttpVersion { get; set; } = "HTTP/1.1";
    public HeaderCollection RequestHeaders { get; set; } = new();
    private BodyRef _requestBody;

    /// <summary>
    /// The captured request body. Backed by <see cref="BodyStore"/>, which keeps
    /// small bodies on the heap and spills large ones to disk — the property
    /// still hands back a plain array either way.
    /// </summary>
    public byte[] RequestBody
    {
        get => _requestBody.Bytes;
        set => _requestBody = BodyStore.Shared.Store(value);
    }

    // ---- Response ------------------------------------------------------------
    public int StatusCode { get; set; }
    public string StatusText { get; set; } = string.Empty;
    public string ResponseHttpVersion { get; set; } = "HTTP/1.1";
    public HeaderCollection ResponseHeaders { get; set; } = new();
    private BodyRef _responseBody;

    /// <summary>The captured response body. See <see cref="RequestBody"/>.</summary>
    public byte[] ResponseBody
    {
        get => _responseBody.Bytes;
        set => _responseBody = BodyStore.Shared.Store(value);
    }

    // ---- Streaming -----------------------------------------------------------
    // These are appended to from proxy worker threads while the UI enumerates
    // them, so every mutation and every read goes through the lock below.
    private readonly object _streamGate = new();
    private readonly List<WebSocketFrame> _webSocketFrames = new();
    private readonly List<ServerSentEvent> _serverSentEvents = new();

    /// <summary>Snapshot of the captured WebSocket frames (safe to enumerate).</summary>
    public IReadOnlyList<WebSocketFrame> WebSocketFrames
    {
        get { lock (_streamGate) return _webSocketFrames.ToArray(); }
    }

    /// <summary>Snapshot of the captured Server-Sent Events (safe to enumerate).</summary>
    public IReadOnlyList<ServerSentEvent> ServerSentEvents
    {
        get { lock (_streamGate) return _serverSentEvents.ToArray(); }
    }

    public int WebSocketFrameCount { get { lock (_streamGate) return _webSocketFrames.Count; } }
    public int ServerSentEventCount { get { lock (_streamGate) return _serverSentEvents.Count; } }

    /// <summary>
    /// Appends a WebSocket frame, keeping at most <paramref name="cap"/> of them
    /// (0 = unlimited). Older frames are dropped first so a long-running socket
    /// cannot grow without bound.
    /// </summary>
    public void AddWebSocketFrame(WebSocketFrame frame, int cap = 0)
    {
        lock (_streamGate)
        {
            _webSocketFrames.Add(frame);
            if (cap > 0 && _webSocketFrames.Count > cap)
            {
                _webSocketFrames.RemoveRange(0, _webSocketFrames.Count - cap);
                DroppedWebSocketFrames++;
            }
        }
    }

    /// <summary>Appends an SSE record, keeping at most <paramref name="cap"/> (0 = unlimited).</summary>
    public void AddServerSentEvent(ServerSentEvent evt, int cap = 0)
    {
        lock (_streamGate)
        {
            _serverSentEvents.Add(evt);
            if (cap > 0 && _serverSentEvents.Count > cap)
            {
                _serverSentEvents.RemoveRange(0, _serverSentEvents.Count - cap);
                DroppedServerSentEvents++;
            }
        }
    }

    /// <summary>How many old frames/events were discarded to honour the retention cap.</summary>
    public long DroppedWebSocketFrames { get; private set; }
    public long DroppedServerSentEvents { get; private set; }

    /// <summary>True when the captured body is only a prefix of what crossed the wire.</summary>
    public bool RequestBodyTruncated { get; set; }
    public bool ResponseBodyTruncated { get; set; }

    // ---- Bookkeeping ---------------------------------------------------------
    public SessionState State { get; set; } = SessionState.Pending;
    public DateTime StartTime { get; set; } = DateTime.Now;
    public DateTime? EndTime { get; set; }
    public SessionTimings Timings { get; set; } = new();
    public string? Error { get; set; }
    public bool Bookmarked { get; set; }
    public string? Comment { get; set; }

    /// <summary>ARGB highlight color applied by a highlighting rule (0 = none).</summary>
    public uint HighlightColor { get; set; }

    public List<string> Tags { get; } = new();

    /// <summary>True when this session was produced by the Submitter / replay.</summary>
    public bool IsReplay { get; set; }

    /// <summary>
    /// True when this transaction was read in from a HAR, a cURL command or a
    /// request file rather than captured here. Worth distinguishing: an imported
    /// session may carry only a request, and its timings come from another tool.
    /// </summary>
    public bool Imported { get; set; }

    /// <summary>
    /// True when a capture filter excluded this transaction from recording. The
    /// proxy still relays it normally; it just never reaches the session list.
    /// </summary>
    public bool Suppressed { get; set; }

    // ---- Derived display helpers --------------------------------------------
    // Sizes come from the reference, so the grid can show them without pulling a
    // spilled body back off disk on every refresh.
    public long RequestBodySize => _requestBody.Length;
    public long ResponseBodySize => _responseBody.Length;

    /// <summary>True when either body was large enough to be written to disk.</summary>
    public bool BodySpilled => _requestBody.IsSpilled || _responseBody.IsSpilled;

    /// <summary>Best-effort total bytes sent on the wire for this transaction.</summary>
    public long BytesSent { get; set; }
    public long BytesReceived { get; set; }

    /// <summary>Size of the response body as it arrived on the wire (before decompression).</summary>
    public long EncodedBodySize { get; set; }

    /// <summary>The original Content-Encoding (e.g. gzip/br) before HttpSpy decoded the body.</summary>
    public string? OriginalContentEncoding { get; set; }

    public string ContentType => ResponseHeaders["Content-Type"] ?? string.Empty;

    public string ResponseContentTypeShort
    {
        get
        {
            var ct = ContentType;
            if (string.IsNullOrEmpty(ct)) return string.Empty;
            int semi = ct.IndexOf(';');
            return semi >= 0 ? ct[..semi].Trim() : ct.Trim();
        }
    }

    public double DurationMs =>
        EndTime.HasValue ? (EndTime.Value - StartTime).TotalMilliseconds : Timings.TotalMs;

    /// <summary>
    /// Effective download throughput in bytes/second, measured over the whole
    /// transaction. Returns 0 when there is nothing to measure — a duration of
    /// zero would otherwise produce infinity.
    /// </summary>
    public double SpeedBytesPerSecond
    {
        get
        {
            double ms = DurationMs;
            if (ms <= 0) return 0;
            long wire = EncodedBodySize > 0 ? EncodedBodySize : ResponseBodySize;
            return wire / (ms / 1000.0);
        }
    }

    public string StatusDisplay => StatusCode > 0 ? $"{StatusCode} {StatusText}".Trim() : (Error is null ? "—" : "ERR");

    public bool IsError => Error is not null || StatusCode >= 400;

    public string FullUrl
    {
        get
        {
            if (!string.IsNullOrEmpty(Url) && Url.Contains("://")) return Url;
            var sb = new StringBuilder();
            sb.Append(Scheme).Append("://").Append(Host).Append(Path);
            if (!string.IsNullOrEmpty(QueryString)) sb.Append('?').Append(QueryString);
            return sb.ToString();
        }
    }

    // Classification sniffs the first bytes, and the grid asks for it on every
    // refresh; caching it keeps a spilled body from being read back off disk each
    // time. The identity of the body reference is the cache key.
    private object? _responseKindSource;
    private BodyContentType _responseKind;
    private object? _requestKindSource;
    private BodyContentType _requestKind;

    public BodyContentType ResponseBodyKind
    {
        get
        {
            var identity = _responseBody.Identity;
            if (!ReferenceEquals(_responseKindSource, identity))
            {
                _responseKind = BodyClassifier.Classify(ContentType, ResponseBody);
                _responseKindSource = identity;
            }
            return _responseKind;
        }
    }

    public BodyContentType RequestBodyKind
    {
        get
        {
            var identity = _requestBody.Identity;
            if (!ReferenceEquals(_requestKindSource, identity))
            {
                _requestKind = BodyClassifier.Classify(RequestHeaders["Content-Type"] ?? string.Empty, RequestBody);
                _requestKindSource = identity;
            }
            return _requestKind;
        }
    }

    // Decoding a multi-megabyte body allocates a string just as large, and the UI
    // touches these properties from filters, search and every inspector tab. Cache
    // the result and invalidate it when the underlying bytes change.
    //
    // Spilled bodies are deliberately not cached: holding the decoded text would
    // pin as much memory as keeping the bytes did, which is the whole thing
    // spilling exists to avoid.
    private byte[]? _requestTextSource;
    private string? _requestText;
    private byte[]? _responseTextSource;
    private string? _responseText;

    public string RequestBodyText
    {
        get
        {
            var body = RequestBody;
            if (_requestBody.IsSpilled) return SafeDecode(body, RequestHeaders["Content-Type"]);

            if (!ReferenceEquals(_requestTextSource, body))
            {
                _requestText = SafeDecode(body, RequestHeaders["Content-Type"]);
                _requestTextSource = body;
            }
            return _requestText!;
        }
    }

    public string ResponseBodyText
    {
        get
        {
            var body = ResponseBody;
            if (_responseBody.IsSpilled) return SafeDecode(body, ContentType);

            if (!ReferenceEquals(_responseTextSource, body))
            {
                _responseText = SafeDecode(body, ContentType);
                _responseTextSource = body;
            }
            return _responseText!;
        }
    }

    /// <summary>Parsed query parameters (name → value), order preserved.</summary>
    public IEnumerable<KeyValuePair<string, string>> QueryParameters() =>
        UrlCodec.ParseQuery(QueryString);

    /// <summary>Cookies sent by the client (from the Cookie header).</summary>
    public IEnumerable<KeyValuePair<string, string>> RequestCookies()
    {
        var header = RequestHeaders["Cookie"];
        if (string.IsNullOrEmpty(header)) yield break;
        foreach (var part in header.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = part.Split('=', 2);
            yield return new KeyValuePair<string, string>(kv[0].Trim(), kv.Length > 1 ? kv[1].Trim() : string.Empty);
        }
    }

    /// <summary>Cookies set by the server (from Set-Cookie headers).</summary>
    public IEnumerable<string> ResponseSetCookies() => ResponseHeaders.GetAll("Set-Cookie");

    /// <summary>
    /// Decodes a body to text using the charset advertised in its Content-Type,
    /// falling back to UTF-8. Undecodable bytes become U+FFFD rather than throwing,
    /// so a binary payload still renders something usable in the viewers.
    /// </summary>
    private static string SafeDecode(byte[] data, string? contentType)
    {
        if (data.Length == 0) return string.Empty;
        var encoding = ResolveEncoding(contentType);
        try { return encoding.GetString(data); }
        catch
        {
            try { return Encoding.UTF8.GetString(data); }
            catch { return Convert.ToHexString(data); }
        }
    }

    /// <summary>Resolves the <c>charset=</c> parameter of a Content-Type header.</summary>
    internal static Encoding ResolveEncoding(string? contentType)
    {
        // Replacement-fallback decoders never throw on malformed input.
        if (string.IsNullOrEmpty(contentType)) return Utf8Lenient;

        int idx = contentType.IndexOf("charset=", StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return Utf8Lenient;

        var charset = contentType[(idx + "charset=".Length)..].Trim().Trim('"', '\'');
        int end = charset.IndexOfAny(new[] { ';', ' ', '\t' });
        if (end >= 0) charset = charset[..end];
        if (charset.Length == 0) return Utf8Lenient;

        try
        {
            return Encoding.GetEncoding(charset, EncoderFallback.ReplacementFallback,
                DecoderFallback.ReplacementFallback);
        }
        catch (ArgumentException)
        {
            return Utf8Lenient; // unknown/unsupported charset label
        }
    }

    private static readonly Encoding Utf8Lenient =
        Encoding.GetEncoding("utf-8", EncoderFallback.ReplacementFallback, DecoderFallback.ReplacementFallback);
}
