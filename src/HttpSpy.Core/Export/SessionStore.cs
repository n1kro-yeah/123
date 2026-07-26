using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using HttpSpy.Core.Models;

namespace HttpSpy.Core.Export;

/// <summary>
/// Saves/loads the full session list to/from a .hspy file (gzip-compressed JSON).
/// Includes bodies, per-phase timings, WebSocket frames and SSE events —
/// everything needed to re-open a capture later and still have the inspectors,
/// the waterfall and the streaming tabs work.
/// </summary>
public static class SessionStore
{
    /// <summary>Bumped whenever the DTO shape changes in a non-additive way.</summary>
    public const int FormatVersion = 2;

    private static readonly JsonSerializerOptions SerOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault,
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>Saves sessions to a compressed .hspy file.</summary>
    public static async Task SaveAsync(string path, IEnumerable<HttpSession> sessions, CancellationToken ct = default)
    {
        var file = new CaptureFile
        {
            Version = FormatVersion,
            SavedAt = DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture),
            Sessions = sessions.Select(SessionDto.From).ToList(),
        };

        // Write to a temporary file and swap it in, so an interrupted save cannot
        // leave a half-written capture where a good one used to be.
        string temp = path + ".tmp";
        await using (var fs = File.Create(temp))
        await using (var gz = new GZipStream(fs, CompressionLevel.Fastest))
        {
            await JsonSerializer.SerializeAsync(gz, file, SerOptions, ct).ConfigureAwait(false);
        }

        if (File.Exists(path)) File.Delete(path);
        File.Move(temp, path);
    }

    /// <summary>Loads sessions from a .hspy file (both the v1 and v2 layouts).</summary>
    public static async Task<List<HttpSession>> LoadAsync(string path, CancellationToken ct = default)
    {
        await using var fs = File.OpenRead(path);
        await using var gz = new GZipStream(fs, CompressionMode.Decompress);
        using var buffered = new MemoryStream();
        await gz.CopyToAsync(buffered, ct).ConfigureAwait(false);
        buffered.Position = 0;

        // v1 files are a bare array of sessions; v2 wraps them in an envelope.
        // Sniff the first token instead of guessing from the file name.
        bool isArray = PeekIsArray(buffered);
        buffered.Position = 0;

        if (isArray)
        {
            var legacy = await JsonSerializer.DeserializeAsync<List<SessionDto>>(buffered, SerOptions, ct)
                .ConfigureAwait(false);
            return legacy?.Select(d => d.ToSession()).ToList() ?? new List<HttpSession>();
        }

        var envelope = await JsonSerializer.DeserializeAsync<CaptureFile>(buffered, SerOptions, ct)
            .ConfigureAwait(false);
        return envelope?.Sessions?.Select(d => d.ToSession()).ToList() ?? new List<HttpSession>();
    }

    private static bool PeekIsArray(Stream stream)
    {
        int b;
        while ((b = stream.ReadByte()) >= 0)
        {
            if (b is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n') continue;
            return b == '[';
        }
        return false;
    }

    private sealed class CaptureFile
    {
        public int Version { get; set; }
        public string? SavedAt { get; set; }
        public List<SessionDto>? Sessions { get; set; }
    }

    // A serializable DTO mirroring HttpSession but with byte[] as Base64.
    private sealed class SessionDto
    {
        public string? Id { get; set; }
        public int Index { get; set; }
        public int Kind { get; set; }
        public bool IsTls { get; set; }
        public string? Method { get; set; }
        public string? Url { get; set; }
        public string? Scheme { get; set; }
        public string? Host { get; set; }
        public int RemotePort { get; set; }
        public string? Path { get; set; }
        public string? Query { get; set; }
        public string? HttpVersion { get; set; }
        public List<string[]>? ReqHeaders { get; set; }
        public string? ReqBody { get; set; }
        public bool ReqBodyTruncated { get; set; }

        public int StatusCode { get; set; }
        public string? StatusText { get; set; }
        public string? RespHttpVersion { get; set; }
        public List<string[]>? RespHeaders { get; set; }
        public string? RespBody { get; set; }
        public bool RespBodyTruncated { get; set; }

        public int Pid { get; set; }
        public string? ProcName { get; set; }
        public int State { get; set; }
        public string? Start { get; set; }
        public string? End { get; set; }

        // Full timing breakdown — v1 kept only TotalMs, which left the waterfall
        // empty for every reloaded session.
        public double TotalMs { get; set; }
        public double BlockedMs { get; set; } = -1;
        public double DnsMs { get; set; } = -1;
        public double ConnectMs { get; set; } = -1;
        public double TlsMs { get; set; } = -1;
        public double SendMs { get; set; } = -1;
        public double WaitMs { get; set; } = -1;
        public double ReceiveMs { get; set; } = -1;

        public long BytesSent { get; set; }
        public long BytesReceived { get; set; }
        public long EncodedBodySize { get; set; }
        public string? OriginalContentEncoding { get; set; }

        public string? Error { get; set; }
        public bool Bookmarked { get; set; }
        public string? Comment { get; set; }
        public uint Highlight { get; set; }
        public List<string>? Tags { get; set; }
        public bool IsReplay { get; set; }
        public bool Imported { get; set; }

        // Streaming payloads — v1 dropped these entirely despite claiming to save them.
        public List<WsFrameDto>? WsFrames { get; set; }
        public List<SseDto>? SseEvents { get; set; }

        public sealed class WsFrameDto
        {
            public int Index { get; set; }
            public string? Timestamp { get; set; }
            public int Direction { get; set; }
            public int Opcode { get; set; }
            public bool Fin { get; set; }
            public bool Masked { get; set; }
            public string? Payload { get; set; }
            public long DeclaredLength { get; set; }
            public bool Truncated { get; set; }
        }

        public sealed class SseDto
        {
            public int Index { get; set; }
            public string? Timestamp { get; set; }
            public string? Id { get; set; }
            public string? EventName { get; set; }
            public string? Data { get; set; }
            public int? RetryMs { get; set; }
        }

        public static SessionDto From(HttpSession s)
        {
            var t = s.Timings;
            return new SessionDto
            {
                Id = s.Id.ToString(),
                Index = s.Index,
                Kind = (int)s.Kind,
                IsTls = s.IsTls,
                Method = s.Method,
                Url = s.Url,
                Scheme = s.Scheme,
                Host = s.Host,
                RemotePort = s.RemotePort,
                Path = s.Path,
                Query = s.QueryString,
                HttpVersion = s.HttpVersion,
                ReqHeaders = s.RequestHeaders.Items.Select(h => new[] { h.Name, h.Value }).ToList(),
                ReqBody = s.RequestBody.Length > 0 ? Convert.ToBase64String(s.RequestBody) : null,
                ReqBodyTruncated = s.RequestBodyTruncated,
                StatusCode = s.StatusCode,
                StatusText = s.StatusText,
                RespHttpVersion = s.ResponseHttpVersion,
                RespHeaders = s.ResponseHeaders.Items.Select(h => new[] { h.Name, h.Value }).ToList(),
                RespBody = s.ResponseBody.Length > 0 ? Convert.ToBase64String(s.ResponseBody) : null,
                RespBodyTruncated = s.ResponseBodyTruncated,
                Pid = s.ProcessId,
                ProcName = s.ProcessName,
                State = (int)s.State,
                Start = s.StartTime.ToString("O", CultureInfo.InvariantCulture),
                End = s.EndTime?.ToString("O", CultureInfo.InvariantCulture),
                TotalMs = t.TotalMs,
                BlockedMs = t.BlockedMs,
                DnsMs = t.DnsMs,
                ConnectMs = t.ConnectMs,
                TlsMs = t.TlsMs,
                SendMs = t.SendMs,
                WaitMs = t.WaitMs,
                ReceiveMs = t.ReceiveMs,
                BytesSent = s.BytesSent,
                BytesReceived = s.BytesReceived,
                EncodedBodySize = s.EncodedBodySize,
                OriginalContentEncoding = s.OriginalContentEncoding,
                Error = s.Error,
                Bookmarked = s.Bookmarked,
                Comment = s.Comment,
                Highlight = s.HighlightColor,
                Tags = s.Tags.Count > 0 ? s.Tags.ToList() : null,
                IsReplay = s.IsReplay,
                Imported = s.Imported,
                WsFrames = s.WebSocketFrameCount == 0 ? null : s.WebSocketFrames.Select(f => new WsFrameDto
                {
                    Index = f.Index,
                    Timestamp = f.Timestamp.ToString("O", CultureInfo.InvariantCulture),
                    Direction = (int)f.Direction,
                    Opcode = (int)f.Opcode,
                    Fin = f.Fin,
                    Masked = f.Masked,
                    Payload = f.Payload.Length > 0 ? Convert.ToBase64String(f.Payload) : null,
                    DeclaredLength = f.DeclaredLength,
                    Truncated = f.Truncated,
                }).ToList(),
                SseEvents = s.ServerSentEventCount == 0 ? null : s.ServerSentEvents.Select(e => new SseDto
                {
                    Index = e.Index,
                    Timestamp = e.Timestamp.ToString("O", CultureInfo.InvariantCulture),
                    Id = e.Id,
                    EventName = e.EventName,
                    Data = e.Data,
                    RetryMs = e.RetryMs,
                }).ToList(),
            };
        }

        public HttpSession ToSession()
        {
            var s = new HttpSession
            {
                Kind = (SessionKind)Kind,
                IsTls = IsTls,
                Method = Method ?? "GET",
                Url = Url ?? "",
                Scheme = Scheme ?? "http",
                Host = Host ?? "",
                RemotePort = RemotePort,
                Path = Path ?? "/",
                QueryString = Query ?? "",
                HttpVersion = HttpVersion ?? "HTTP/1.1",
                StatusCode = StatusCode,
                StatusText = StatusText ?? "",
                ResponseHttpVersion = RespHttpVersion ?? "HTTP/1.1",
                ProcessId = Pid,
                ProcessName = ProcName ?? "",
                State = (SessionState)State,
                Error = Error,
                Bookmarked = Bookmarked,
                Comment = Comment,
                HighlightColor = Highlight,
                IsReplay = IsReplay,
                Imported = Imported,
                BytesSent = BytesSent,
                BytesReceived = BytesReceived,
                EncodedBodySize = EncodedBodySize,
                OriginalContentEncoding = OriginalContentEncoding,
                RequestBodyTruncated = ReqBodyTruncated,
                ResponseBodyTruncated = RespBodyTruncated,
            };

            // Header rows come from a file that may have been edited or truncated,
            // so never index blindly into them.
            AddHeaders(s.RequestHeaders, ReqHeaders);
            AddHeaders(s.ResponseHeaders, RespHeaders);

            s.RequestBody = FromBase64(ReqBody);
            s.ResponseBody = FromBase64(RespBody);

            if (ParseDate(Start) is { } st) s.StartTime = st;
            if (ParseDate(End) is { } et) s.EndTime = et;

            s.Timings.TotalMs = TotalMs;
            s.Timings.BlockedMs = BlockedMs;
            s.Timings.DnsMs = DnsMs;
            s.Timings.ConnectMs = ConnectMs;
            s.Timings.TlsMs = TlsMs;
            s.Timings.SendMs = SendMs;
            s.Timings.WaitMs = WaitMs;
            s.Timings.ReceiveMs = ReceiveMs;

            if (Tags is not null) s.Tags.AddRange(Tags);

            if (WsFrames is not null)
                foreach (var f in WsFrames)
                    s.AddWebSocketFrame(new WebSocketFrame
                    {
                        Index = f.Index,
                        Timestamp = ParseDate(f.Timestamp) ?? DateTime.Now,
                        Direction = (MessageDirection)f.Direction,
                        Opcode = (WebSocketOpcode)f.Opcode,
                        Fin = f.Fin,
                        Masked = f.Masked,
                        Payload = FromBase64(f.Payload),
                        DeclaredLength = f.DeclaredLength,
                        Truncated = f.Truncated,
                    });

            if (SseEvents is not null)
                foreach (var e in SseEvents)
                    s.AddServerSentEvent(new ServerSentEvent
                    {
                        Index = e.Index,
                        Timestamp = ParseDate(e.Timestamp) ?? DateTime.Now,
                        Id = e.Id,
                        EventName = e.EventName,
                        Data = e.Data ?? string.Empty,
                        RetryMs = e.RetryMs,
                    });

            return s;
        }

        private static void AddHeaders(HeaderCollection target, List<string[]>? rows)
        {
            if (rows is null) return;
            foreach (var row in rows)
            {
                if (row is null || row.Length == 0) continue;
                target.Add(row[0] ?? string.Empty, row.Length > 1 ? row[1] ?? string.Empty : string.Empty);
            }
        }

        private static byte[] FromBase64(string? value)
        {
            if (string.IsNullOrEmpty(value)) return Array.Empty<byte>();
            // A corrupt or hand-edited file should degrade to an empty body rather
            // than failing the whole load.
            try { return Convert.FromBase64String(value); }
            catch (FormatException) { return Array.Empty<byte>(); }
        }

        /// <summary>Parses a round-trip ("O") timestamp, preserving its kind.</summary>
        private static DateTime? ParseDate(string? value) =>
            DateTime.TryParse(value, CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out var parsed)
                ? parsed
                : null;
    }
}
