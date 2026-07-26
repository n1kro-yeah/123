using System.Globalization;
using System.Text;
using HttpSpy.Core.Models;

namespace HttpSpy.Core.Proxy;

/// <summary>Low-level helpers to read and serialize HTTP/1.x messages on the wire.</summary>
public static class HttpWire
{
    public sealed class RequestHead
    {
        public string Method = "GET";
        public string Target = "/";
        public string Version = "HTTP/1.1";
        public HeaderCollection Headers = new();
    }

    public sealed class ResponseHead
    {
        public string Version = "HTTP/1.1";
        public int StatusCode = 200;
        public string ReasonPhrase = "OK";
        public HeaderCollection Headers = new();
    }

    public static async Task<RequestHead?> ReadRequestHeadAsync(StreamReaderEx reader, CancellationToken ct)
    {
        // RFC 9112 §2.2: a server should ignore at least one empty line received
        // before the request line (clients sometimes leave a stray CRLF behind on
        // a keep-alive connection). Skip a bounded number of them.
        string? line;
        int blanks = 0;
        do
        {
            line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            if (line is null) return null;
        } while (line.Length == 0 && ++blanks <= 2);

        if (line.Length == 0) return null;

        var parts = line.Split(' ', 3);
        if (parts.Length < 3) return null;

        var head = new RequestHead { Method = parts[0], Target = parts[1], Version = parts[2] };
        await ReadHeadersAsync(reader, head.Headers, ct).ConfigureAwait(false);
        return head;
    }

    public static async Task<ResponseHead?> ReadResponseHeadAsync(StreamReaderEx reader, CancellationToken ct)
    {
        string? line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
        if (string.IsNullOrEmpty(line)) return null;

        var parts = line.Split(' ', 3);
        if (parts.Length < 2) return null;

        var head = new ResponseHead { Version = parts[0] };
        head.StatusCode = int.TryParse(parts[1], out var sc) ? sc : 0;
        head.ReasonPhrase = parts.Length > 2 ? parts[2] : string.Empty;
        await ReadHeadersAsync(reader, head.Headers, ct).ConfigureAwait(false);
        return head;
    }

    private static async Task ReadHeadersAsync(StreamReaderEx reader, HeaderCollection headers, CancellationToken ct)
    {
        while (true)
        {
            string? line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            if (string.IsNullOrEmpty(line)) break; // blank line terminates header block
            int colon = line.IndexOf(':');
            if (colon <= 0) continue;
            string name = line[..colon].Trim();
            string value = line[(colon + 1)..].Trim();
            headers.Add(name, value);
        }
    }

    /// <summary>The outcome of reading a message body off the wire.</summary>
    public readonly record struct BodyResult(byte[] Data, bool Truncated)
    {
        public static readonly BodyResult Empty = new(Array.Empty<byte>(), false);
        public static implicit operator byte[](BodyResult r) => r.Data;
    }

    /// <summary>
    /// Reads a message body using the framing implied by the headers, retaining at
    /// most <paramref name="maxRetained"/> bytes. Excess bytes are still consumed
    /// from the socket so the connection stays correctly framed.
    /// </summary>
    public static async Task<BodyResult> ReadBodyAsync(StreamReaderEx reader, HeaderCollection headers,
        bool bodyForbidden, CancellationToken ct, long maxRetained = long.MaxValue)
    {
        if (bodyForbidden) return BodyResult.Empty;

        var te = headers["Transfer-Encoding"];
        if (te is not null && te.Contains("chunked", StringComparison.OrdinalIgnoreCase))
            return await ReadChunkedAsync(reader, ct, maxRetained).ConfigureAwait(false);

        var clHeader = headers["Content-Length"];
        if (clHeader is not null && long.TryParse(clHeader, out var cl) && cl > 0)
        {
            var data = await reader.ReadExactAsync(cl, ct, maxRetained).ConfigureAwait(false);
            return new BodyResult(data, reader.LastReadTruncated);
        }

        return BodyResult.Empty;
    }

    /// <summary>Reads a response body, falling back to read-until-close when unframed.</summary>
    public static async Task<BodyResult> ReadResponseBodyAsync(StreamReaderEx reader, HeaderCollection headers,
        bool bodyForbidden, CancellationToken ct, long maxRetained = long.MaxValue)
    {
        if (bodyForbidden) return BodyResult.Empty;

        var te = headers["Transfer-Encoding"];
        if (te is not null && te.Contains("chunked", StringComparison.OrdinalIgnoreCase))
            return await ReadChunkedAsync(reader, ct, maxRetained).ConfigureAwait(false);

        var clHeader = headers["Content-Length"];
        if (clHeader is not null && long.TryParse(clHeader, out var cl))
        {
            if (cl <= 0) return BodyResult.Empty;
            var data = await reader.ReadExactAsync(cl, ct, maxRetained).ConfigureAwait(false);
            return new BodyResult(data, reader.LastReadTruncated);
        }

        // No framing headers: read until the server closes the connection.
        var rest = await reader.ReadToEndAsync(ct, maxRetained).ConfigureAwait(false);
        return new BodyResult(rest, reader.LastReadTruncated);
    }

    private static async Task<BodyResult> ReadChunkedAsync(StreamReaderEx reader, CancellationToken ct,
        long maxRetained)
    {
        using var ms = new MemoryStream();
        long retained = 0;
        bool truncated = false;
        while (true)
        {
            string? sizeLine = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            if (sizeLine is null) break;
            int semi = sizeLine.IndexOf(';');
            if (semi >= 0) sizeLine = sizeLine[..semi];
            sizeLine = sizeLine.Trim();
            if (sizeLine.Length == 0) continue;
            // Chunk sizes are hex and may legally exceed int.MaxValue, so parse as long.
            if (!long.TryParse(sizeLine, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out long size) ||
                size < 0)
                break;
            if (size == 0)
            {
                // consume trailing headers up to the final blank line
                while (true)
                {
                    string? trailer = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                    if (string.IsNullOrEmpty(trailer)) break;
                }
                break;
            }

            long room = Math.Max(0, maxRetained - retained);
            var chunk = await reader.ReadExactAsync(size, ct, room).ConfigureAwait(false);
            if (reader.LastReadTruncated) truncated = true;
            ms.Write(chunk, 0, chunk.Length);
            retained += chunk.Length;
            await reader.ReadLineAsync(ct).ConfigureAwait(false); // trailing CRLF
        }
        return new BodyResult(ms.ToArray(), truncated);
    }

    // ---- Serialization -------------------------------------------------------

    public static byte[] SerializeRequest(string method, string target, string version,
        HeaderCollection headers, byte[] body)
    {
        var sb = new StringBuilder();
        sb.Append(method).Append(' ').Append(target).Append(' ').Append(version).Append("\r\n");
        AppendHeaders(sb, headers);
        sb.Append("\r\n");
        return Combine(Encoding.ASCII.GetBytes(sb.ToString()), body);
    }

    public static byte[] SerializeResponse(string version, int status, string reason,
        HeaderCollection headers, byte[] body)
    {
        var sb = new StringBuilder();
        sb.Append(version).Append(' ').Append(status).Append(' ').Append(reason).Append("\r\n");
        AppendHeaders(sb, headers);
        sb.Append("\r\n");
        return Combine(Encoding.ASCII.GetBytes(sb.ToString()), body);
    }

    private static void AppendHeaders(StringBuilder sb, HeaderCollection headers)
    {
        foreach (var h in headers)
            sb.Append(h.Name).Append(": ").Append(h.Value).Append("\r\n");
    }

    public static byte[] Combine(byte[] a, byte[] b)
    {
        if (b.Length == 0) return a;
        var result = new byte[a.Length + b.Length];
        Buffer.BlockCopy(a, 0, result, 0, a.Length);
        Buffer.BlockCopy(b, 0, result, a.Length, b.Length);
        return result;
    }

    /// <summary>
    /// Decompresses a body according to its <c>Content-Encoding</c>. Handles
    /// stacked encodings (<c>"gzip, br"</c>, applied last-to-first as required by
    /// RFC 9110 §8.4), surrounding whitespace and quality-style casing, and both
    /// flavours of <c>deflate</c> — the zlib-wrapped form most servers actually
    /// send as well as the raw form the RFC nominally specifies.
    /// Returns the original array (by reference) when nothing could be decoded.
    /// </summary>
    public static byte[] Decompress(byte[] body, string? contentEncoding)
    {
        if (body.Length == 0 || string.IsNullOrWhiteSpace(contentEncoding)) return body;

        // "gzip, br" means br was applied last, so it must be undone first.
        var codings = contentEncoding
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Reverse()
            .ToArray();

        byte[] current = body;
        bool any = false;
        foreach (var raw in codings)
        {
            var coding = raw.ToLowerInvariant();
            if (coding is "identity" or "") continue;

            var decoded = DecompressOne(current, coding);
            if (decoded is null) return any ? current : body; // unknown/corrupt — stop where we are
            current = decoded;
            any = true;
        }
        return any ? current : body;
    }

    /// <summary>Applies a single content coding; returns null when it cannot be decoded.</summary>
    private static byte[]? DecompressOne(byte[] body, string coding)
    {
        switch (coding)
        {
            case "gzip":
            case "x-gzip":
                return TryInflate(body, static i =>
                    new System.IO.Compression.GZipStream(i, System.IO.Compression.CompressionMode.Decompress));

            case "br":
                return TryInflate(body, static i =>
                    new System.IO.Compression.BrotliStream(i, System.IO.Compression.CompressionMode.Decompress));

            case "deflate":
            case "x-deflate":
                // Most servers send zlib-wrapped deflate (RFC 1950) even though the
                // HTTP spec names the raw form (RFC 1951); try the common one first.
                return TryInflate(body, static i =>
                           new System.IO.Compression.ZLibStream(i, System.IO.Compression.CompressionMode.Decompress))
                       ?? TryInflate(body, static i =>
                           new System.IO.Compression.DeflateStream(i, System.IO.Compression.CompressionMode.Decompress));

            default:
                return null;
        }
    }

    private static byte[]? TryInflate(byte[] body, Func<Stream, Stream> factory)
    {
        try
        {
            using var input = new MemoryStream(body, writable: false);
            using var output = new MemoryStream(body.Length * 4);
            using (var decompressor = factory(input))
                decompressor.CopyTo(output);

            // A truncated stream can present a valid header and then simply end,
            // which decodes to nothing without throwing. Producing an empty body
            // would silently discard the bytes we did capture, so treat it as a
            // failure and let the caller keep the original payload.
            if (output.Length == 0 && body.Length > 0) return null;

            return output.ToArray();
        }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or IOException)
        {
            return null;
        }
    }
}
