using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using HttpSpy.Core.Models;

namespace HttpSpy.Core.Export;

/// <summary>
/// Saves/loads the full session list to/from a .hspy file (gzip-compressed JSON).
/// Includes bodies, timings, WS frames, SSE events — everything needed to
/// re-open a captured session later.
/// </summary>
public static class SessionStore
{
    private static readonly JsonSerializerOptions SerOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault,
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>Saves sessions to a compressed .hspy file.</summary>
    public static async Task SaveAsync(string path, IEnumerable<HttpSession> sessions, CancellationToken ct = default)
    {
        var dtos = sessions.Select(SessionDto.From).ToList();
        await using var fs = File.Create(path);
        await using var gz = new GZipStream(fs, CompressionLevel.Fastest);
        await JsonSerializer.SerializeAsync(gz, dtos, SerOptions, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Upper bound on the decompressed size of a .hspy file. A .hspy is gzip-compressed
    /// JSON; without a cap a maliciously crafted file (gzip bomb) could inflate to
    /// gigabytes and exhaust memory when opened. Real captures stay well under this.
    /// </summary>
    public const long MaxDecompressedFileBytes = 512L * 1024 * 1024;

    /// <summary>Loads sessions from a .hspy file.</summary>
    public static async Task<List<HttpSession>> LoadAsync(string path, CancellationToken ct = default)
    {
        await using var fs = File.OpenRead(path);
        await using var gz = new GZipStream(fs, CompressionMode.Decompress);

        // Decompress into memory with a hard ceiling so a crafted file can't inflate
        // without bound before we even attempt to deserialize it.
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        long total = 0;
        int read;
        while ((read = await gz.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
        {
            total += read;
            if (total > MaxDecompressedFileBytes)
                throw new InvalidDataException("Session file is too large or is not a valid .hspy file.");
            buffer.Write(chunk, 0, read);
        }

        buffer.Position = 0;
        var dtos = await JsonSerializer.DeserializeAsync<List<SessionDto>>(buffer, SerOptions, ct).ConfigureAwait(false);
        return dtos?.Select(d => d.ToSession()).ToList() ?? new();
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
        public string? Path { get; set; }
        public string? Query { get; set; }
        public string? HttpVersion { get; set; }
        public List<string[]>? ReqHeaders { get; set; }
        public string? ReqBody { get; set; }

        public int StatusCode { get; set; }
        public string? StatusText { get; set; }
        public string? RespHttpVersion { get; set; }
        public List<string[]>? RespHeaders { get; set; }
        public string? RespBody { get; set; }

        public int Pid { get; set; }
        public string? ProcName { get; set; }
        public int State { get; set; }
        public string? Start { get; set; }
        public string? End { get; set; }
        public double TotalMs { get; set; }
        public string? Error { get; set; }
        public bool Bookmarked { get; set; }
        public string? Comment { get; set; }
        public uint Highlight { get; set; }
        public List<string>? Tags { get; set; }
        public bool IsReplay { get; set; }

        public static SessionDto From(HttpSession s) => new()
        {
            Id = s.Id.ToString(),
            Index = s.Index,
            Kind = (int)s.Kind,
            IsTls = s.IsTls,
            Method = s.Method,
            Url = s.Url,
            Scheme = s.Scheme,
            Host = s.Host,
            Path = s.Path,
            Query = s.QueryString,
            HttpVersion = s.HttpVersion,
            ReqHeaders = s.RequestHeaders.Items.Select(h => new[] { h.Name, h.Value }).ToList(),
            ReqBody = s.RequestBody.Length > 0 ? Convert.ToBase64String(s.RequestBody) : null,
            StatusCode = s.StatusCode,
            StatusText = s.StatusText,
            RespHttpVersion = s.ResponseHttpVersion,
            RespHeaders = s.ResponseHeaders.Items.Select(h => new[] { h.Name, h.Value }).ToList(),
            RespBody = s.ResponseBody.Length > 0 ? Convert.ToBase64String(s.ResponseBody) : null,
            Pid = s.ProcessId,
            ProcName = s.ProcessName,
            State = (int)s.State,
            Start = s.StartTime.ToString("O"),
            End = s.EndTime?.ToString("O"),
            TotalMs = s.Timings.TotalMs,
            Error = s.Error,
            Bookmarked = s.Bookmarked,
            Comment = s.Comment,
            Highlight = s.HighlightColor,
            Tags = s.Tags.Count > 0 ? s.Tags.ToList() : null,
            IsReplay = s.IsReplay,
        };

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
            };
            if (ReqHeaders is not null) foreach (var h in ReqHeaders) s.RequestHeaders.Add(h[0], h[1]);
            if (RespHeaders is not null) foreach (var h in RespHeaders) s.ResponseHeaders.Add(h[0], h[1]);
            if (ReqBody is not null) s.RequestBody = Convert.FromBase64String(ReqBody);
            if (RespBody is not null) s.ResponseBody = Convert.FromBase64String(RespBody);
            if (Start is not null && DateTime.TryParse(Start, out var st)) s.StartTime = st;
            if (End is not null && DateTime.TryParse(End, out var et)) s.EndTime = et;
            s.Timings.TotalMs = TotalMs;
            if (Tags is not null) s.Tags.AddRange(Tags);
            return s;
        }
    }
}
