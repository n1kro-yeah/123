using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace HttpSpy.Core.Proxy.Grpc;

/// <summary>One gRPC length-prefixed message extracted from a request/response body.</summary>
public sealed class GrpcMessage
{
    public bool Compressed { get; init; }
    public byte[] Payload { get; init; } = Array.Empty<byte>();
    public List<ProtobufField>? Decoded { get; set; }

    public string Render() =>
        Decoded is not null
            ? ProtobufDecoder.Render(Decoded)
            : $"<{Payload.Length} bytes, undecodable>";
}

/// <summary>
/// Decodes gRPC message framing (RFC: gRPC over HTTP/2). Each message is a
/// 1-byte compressed flag, a 4-byte big-endian length, then the payload. The
/// payload is protobuf, which we decode schema-lessly.
/// </summary>
public static class GrpcDecoder
{
    /// <summary>True when the content type marks a gRPC message stream.</summary>
    public static bool IsGrpc(string? contentType) =>
        contentType is not null &&
        contentType.StartsWith("application/grpc", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Splits a gRPC body into its constituent messages and decodes each one.
    /// <paramref name="grpcEncoding"/> is the value of the <c>grpc-encoding</c>
    /// header (e.g. "gzip"), used to inflate compressed messages.
    /// </summary>
    public static List<GrpcMessage> Decode(ReadOnlySpan<byte> body, string? grpcEncoding = null)
    {
        var messages = new List<GrpcMessage>();
        int offset = 0;
        while (offset + 5 <= body.Length)
        {
            bool compressed = body[offset] != 0;
            uint length = BinaryPrimitives.ReadUInt32BigEndian(body.Slice(offset + 1, 4));
            offset += 5;
            if (offset + length > body.Length) break; // truncated / streaming in progress
            var payload = body.Slice(offset, (int)length).ToArray();
            offset += (int)length;

            byte[] decodedBytes = payload;
            if (compressed)
                decodedBytes = Inflate(payload, grpcEncoding);

            var msg = new GrpcMessage { Compressed = compressed, Payload = decodedBytes };
            msg.Decoded = ProtobufDecoder.TryDecode(decodedBytes);
            messages.Add(msg);
        }
        return messages;
    }

    private static byte[] Inflate(byte[] data, string? encoding)
    {
        try
        {
            using var input = new MemoryStream(data);
            using var output = new MemoryStream();
            Stream decompressor = (encoding?.ToLowerInvariant()) switch
            {
                "gzip" => new GZipStream(input, CompressionMode.Decompress),
                "deflate" => new DeflateStream(input, CompressionMode.Decompress),
                _ => input,
            };
            if (ReferenceEquals(decompressor, input)) return data;
            HttpWire.CopyBounded(decompressor, output, HttpWire.MaxDecompressedBytes);
            decompressor.Dispose();
            return output.ToArray();
        }
        catch { return data; }
    }

    /// <summary>Renders all messages in a body as a single text block for the inspector.</summary>
    public static string RenderAll(IReadOnlyList<GrpcMessage> messages)
    {
        if (messages.Count == 0) return "(no gRPC messages)";
        var sb = new StringBuilder();
        for (int i = 0; i < messages.Count; i++)
        {
            sb.AppendLine($"=== Message {i + 1} ({messages[i].Payload.Length} bytes" +
                          (messages[i].Compressed ? ", decompressed" : "") + ") ===");
            sb.Append(messages[i].Render());
            if (i < messages.Count - 1) sb.AppendLine();
        }
        return sb.ToString();
    }
}
