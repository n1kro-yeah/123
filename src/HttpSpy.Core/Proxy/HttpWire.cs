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
        string? line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
        if (string.IsNullOrEmpty(line)) return null;

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

    /// <summary>Reads a message body using the framing implied by the headers.</summary>
    public static async Task<byte[]> ReadBodyAsync(StreamReaderEx reader, HeaderCollection headers,
        bool bodyForbidden, CancellationToken ct)
    {
        if (bodyForbidden) return Array.Empty<byte>();

        var te = headers["Transfer-Encoding"];
        if (te is not null && te.Contains("chunked", StringComparison.OrdinalIgnoreCase))
            return await ReadChunkedAsync(reader, ct).ConfigureAwait(false);

        var clHeader = headers["Content-Length"];
        if (clHeader is not null && long.TryParse(clHeader, out var cl))
            return await reader.ReadExactAsync(cl, ct).ConfigureAwait(false);

        return Array.Empty<byte>();
    }

    /// <summary>Reads a response body, falling back to read-until-close when unframed.</summary>
    public static async Task<byte[]> ReadResponseBodyAsync(StreamReaderEx reader, HeaderCollection headers,
        bool bodyForbidden, CancellationToken ct)
    {
        if (bodyForbidden) return Array.Empty<byte>();

        var te = headers["Transfer-Encoding"];
        if (te is not null && te.Contains("chunked", StringComparison.OrdinalIgnoreCase))
            return await ReadChunkedAsync(reader, ct).ConfigureAwait(false);

        var clHeader = headers["Content-Length"];
        if (clHeader is not null && long.TryParse(clHeader, out var cl))
            return await reader.ReadExactAsync(cl, ct).ConfigureAwait(false);

        // No framing headers: read until the server closes the connection.
        return await reader.ReadToEndAsync(ct).ConfigureAwait(false);
    }

    private static async Task<byte[]> ReadChunkedAsync(StreamReaderEx reader, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        while (true)
        {
            string? sizeLine = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            if (sizeLine is null) break;
            int semi = sizeLine.IndexOf(';');
            if (semi >= 0) sizeLine = sizeLine[..semi];
            sizeLine = sizeLine.Trim();
            if (sizeLine.Length == 0) continue;
            if (!int.TryParse(sizeLine, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int size))
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
            var chunk = await reader.ReadExactAsync(size, ct).ConfigureAwait(false);
            ms.Write(chunk, 0, chunk.Length);
            await reader.ReadLineAsync(ct).ConfigureAwait(false); // trailing CRLF
        }
        return ms.ToArray();
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

    /// <summary>Decompresses a body according to its Content-Encoding header.</summary>
    public static byte[] Decompress(byte[] body, string? contentEncoding)
    {
        if (body.Length == 0 || string.IsNullOrEmpty(contentEncoding)) return body;
        try
        {
            using var input = new MemoryStream(body);
            using var output = new MemoryStream();
            Stream decompressor = contentEncoding.ToLowerInvariant() switch
            {
                "gzip" or "x-gzip" => new System.IO.Compression.GZipStream(input, System.IO.Compression.CompressionMode.Decompress),
                "deflate" => new System.IO.Compression.DeflateStream(input, System.IO.Compression.CompressionMode.Decompress),
                "br" => new System.IO.Compression.BrotliStream(input, System.IO.Compression.CompressionMode.Decompress),
                _ => input
            };
            if (ReferenceEquals(decompressor, input)) return body;
            decompressor.CopyTo(output);
            decompressor.Dispose();
            return output.ToArray();
        }
        catch
        {
            return body;
        }
    }
}
