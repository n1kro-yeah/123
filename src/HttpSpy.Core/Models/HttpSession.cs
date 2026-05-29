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

    // ---- Request -------------------------------------------------------------
    public string Method { get; set; } = "GET";
    public string Url { get; set; } = string.Empty;
    public string Scheme { get; set; } = "http";
    public string Host { get; set; } = string.Empty;
    public string Path { get; set; } = "/";
    public string QueryString { get; set; } = string.Empty;
    public string HttpVersion { get; set; } = "HTTP/1.1";
    public HeaderCollection RequestHeaders { get; set; } = new();
    public byte[] RequestBody { get; set; } = Array.Empty<byte>();

    // ---- Response ------------------------------------------------------------
    public int StatusCode { get; set; }
    public string StatusText { get; set; } = string.Empty;
    public string ResponseHttpVersion { get; set; } = "HTTP/1.1";
    public HeaderCollection ResponseHeaders { get; set; } = new();
    public byte[] ResponseBody { get; set; } = Array.Empty<byte>();

    // ---- Streaming -----------------------------------------------------------
    public List<WebSocketFrame> WebSocketFrames { get; } = new();
    public List<ServerSentEvent> ServerSentEvents { get; } = new();

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

    // ---- Derived display helpers --------------------------------------------
    public long RequestBodySize => RequestBody.LongLength;
    public long ResponseBodySize => ResponseBody.LongLength;

    /// <summary>Best-effort total bytes sent on the wire for this transaction.</summary>
    public long BytesSent { get; set; }
    public long BytesReceived { get; set; }

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

    public BodyContentType ResponseBodyKind => BodyClassifier.Classify(ContentType, ResponseBody);
    public BodyContentType RequestBodyKind =>
        BodyClassifier.Classify(RequestHeaders["Content-Type"] ?? string.Empty, RequestBody);

    public string RequestBodyText => SafeDecode(RequestBody);
    public string ResponseBodyText => SafeDecode(ResponseBody);

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

    private string SafeDecode(byte[] data)
    {
        if (data.Length == 0) return string.Empty;
        try { return Encoding.UTF8.GetString(data); }
        catch { return Convert.ToHexString(data); }
    }
}
