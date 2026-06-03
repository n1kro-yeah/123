using System.Buffers.Binary;

namespace HttpSpy.Core.Proxy.Http2;

/// <summary>The response of a single HTTP/2 origin exchange.</summary>
public sealed class Http2OriginResponse
{
    public int StatusCode { get; set; }
    public List<HpackHeader> Headers { get; } = new();
    public List<HpackHeader> Trailers { get; } = new();
    public byte[] Body { get; set; } = Array.Empty<byte>();
}

/// <summary>
/// A minimal HTTP/2 client that performs one request/response on a freshly
/// established (ALPN "h2") origin connection using stream 1. This is what lets
/// HttpSpy man-in-the-middle h2-only origins (notably gRPC), forwarding the
/// decrypted request and capturing the decrypted response.
/// </summary>
public sealed class Http2OriginClient
{
    private readonly Stream _stream;
    private readonly Http2Writer _writer;
    private readonly HpackEncoder _encoder = new();
    private readonly HpackDecoder _decoder = new();

    public Http2OriginClient(Stream stream)
    {
        _stream = stream;
        _writer = new Http2Writer(stream);
    }

    public async Task<Http2OriginResponse> SendAsync(IReadOnlyList<HpackHeader> requestHeaders,
        byte[] body, CancellationToken ct)
    {
        // Connection preface + our SETTINGS.
        await _stream.WriteAsync(Http2Frame.ClientPreface, ct).ConfigureAwait(false);
        await _writer.WriteFrameAsync(Http2Frame.Settings(new[]
        {
            ((Http2Setting.EnablePush, 0u)),
            ((Http2Setting.InitialWindowSize, 1048576u)),
        }), ct).ConfigureAwait(false);
        // Enlarge our connection receive window so large responses flow freely.
        await _writer.WriteFrameAsync(Http2Frame.WindowUpdate(0, 0x0FFF0000), ct).ConfigureAwait(false);

        const int streamId = 1;
        byte[] headerBlock = _encoder.Encode(requestHeaders);
        bool noBody = body.Length == 0;
        await WriteHeaderBlockAsync(streamId, headerBlock, endStream: noBody, ct).ConfigureAwait(false);
        if (!noBody)
            await _writer.WriteDataAsync(streamId, body, endStream: true, ct).ConfigureAwait(false);

        var response = new Http2OriginResponse();
        using var bodyStream = new MemoryStream();
        bool headersDone = false;
        long received = 0;

        while (true)
        {
            var frame = await Http2Frame.ReadAsync(_stream, ct).ConfigureAwait(false);
            if (frame is null) break;

            switch (frame.Type)
            {
                case Http2FrameType.Settings:
                    if (!frame.HasFlag(Http2Flags.Ack))
                    {
                        ApplySettings(frame);
                        await _writer.WriteFrameAsync(Http2Frame.SettingsAck(), ct).ConfigureAwait(false);
                    }
                    break;
                case Http2FrameType.WindowUpdate:
                    _writer.OnWindowUpdate(frame.StreamId, ReadUInt31(frame.Payload));
                    break;
                case Http2FrameType.Ping:
                    if (!frame.HasFlag(Http2Flags.Ack))
                        await _writer.WriteFrameAsync(Http2Frame.PingAck(frame.Payload), ct).ConfigureAwait(false);
                    break;
                case Http2FrameType.Headers:
                {
                    var headers = _decoder.Decode(frame.HeaderBlockFragment());
                    var target = headersDone ? response.Trailers : response.Headers;
                    foreach (var h in headers)
                    {
                        if (h.Name == ":status" && int.TryParse(h.Value, out int sc)) response.StatusCode = sc;
                        else target.Add(h);
                    }
                    headersDone = true;
                    if (frame.HasFlag(Http2Flags.EndStream)) goto done;
                    break;
                }
                case Http2FrameType.Data:
                {
                    bodyStream.Write(frame.DataPayload());
                    received += frame.Payload.Length;
                    if (received > 32768)
                    {
                        await _writer.WriteFrameAsync(Http2Frame.WindowUpdate(0, (int)received), ct).ConfigureAwait(false);
                        await _writer.WriteFrameAsync(Http2Frame.WindowUpdate(streamId, (int)received), ct).ConfigureAwait(false);
                        received = 0;
                    }
                    if (frame.HasFlag(Http2Flags.EndStream)) goto done;
                    break;
                }
                case Http2FrameType.RstStream:
                    goto done;
                case Http2FrameType.GoAway:
                    goto done;
            }
        }
    done:
        response.Body = bodyStream.ToArray();
        try { await _writer.WriteFrameAsync(Http2Frame.GoAway(streamId, Http2ErrorCode.NoError), ct).ConfigureAwait(false); }
        catch { /* origin may already be closing */ }
        return response;
    }

    private async Task WriteHeaderBlockAsync(int streamId, byte[] block, bool endStream, CancellationToken ct)
    {
        const int maxChunk = 16384;
        if (block.Length <= maxChunk)
        {
            await _writer.WriteFrameAsync(Http2Frame.Headers(streamId, block, endStream, endHeaders: true), ct)
                .ConfigureAwait(false);
            return;
        }
        await _writer.WriteFrameAsync(Http2Frame.Headers(streamId, block[..maxChunk], endStream, endHeaders: false), ct)
            .ConfigureAwait(false);
        for (int offset = maxChunk; offset < block.Length; offset += maxChunk)
        {
            int end = Math.Min(offset + maxChunk, block.Length);
            bool last = end == block.Length;
            await _writer.WriteFrameAsync(Http2Frame.Continuation(streamId, block[offset..end], last), ct)
                .ConfigureAwait(false);
        }
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
            }
        }
    }

    private static long ReadUInt31(byte[] payload) =>
        payload.Length >= 4 ? BinaryPrimitives.ReadUInt32BigEndian(payload) & 0x7FFFFFFF : 0;
}
