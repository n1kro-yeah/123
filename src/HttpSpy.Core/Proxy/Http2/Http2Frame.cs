using System.Buffers.Binary;

namespace HttpSpy.Core.Proxy.Http2;

/// <summary>HTTP/2 frame types (RFC 7540 §6).</summary>
public enum Http2FrameType : byte
{
    Data = 0x0,
    Headers = 0x1,
    Priority = 0x2,
    RstStream = 0x3,
    Settings = 0x4,
    PushPromise = 0x5,
    Ping = 0x6,
    GoAway = 0x7,
    WindowUpdate = 0x8,
    Continuation = 0x9,
}

/// <summary>HTTP/2 frame flags (bit values vary by frame type).</summary>
[Flags]
public enum Http2Flags : byte
{
    None = 0,
    Ack = 0x1,        // SETTINGS / PING
    EndStream = 0x1,  // DATA / HEADERS
    EndHeaders = 0x4, // HEADERS / CONTINUATION / PUSH_PROMISE
    Padded = 0x8,     // DATA / HEADERS / PUSH_PROMISE
    Priority = 0x20,  // HEADERS
}

/// <summary>HTTP/2 error codes (RFC 7540 §7).</summary>
public enum Http2ErrorCode : uint
{
    NoError = 0x0,
    ProtocolError = 0x1,
    InternalError = 0x2,
    FlowControlError = 0x3,
    SettingsTimeout = 0x4,
    StreamClosed = 0x5,
    FrameSizeError = 0x6,
    RefusedStream = 0x7,
    Cancel = 0x8,
    CompressionError = 0x9,
    ConnectError = 0xa,
    EnhanceYourCalm = 0xb,
    InadequateSecurity = 0xc,
    Http11Required = 0xd,
}

/// <summary>SETTINGS parameter identifiers (RFC 7540 §6.5.2).</summary>
public enum Http2Setting : ushort
{
    HeaderTableSize = 0x1,
    EnablePush = 0x2,
    MaxConcurrentStreams = 0x3,
    InitialWindowSize = 0x4,
    MaxFrameSize = 0x5,
    MaxHeaderListSize = 0x6,
}

/// <summary>A single HTTP/2 frame: 9-byte header plus payload.</summary>
public sealed class Http2Frame
{
    public Http2FrameType Type { get; init; }
    public Http2Flags Flags { get; init; }
    public int StreamId { get; init; }
    public byte[] Payload { get; init; } = Array.Empty<byte>();

    public bool HasFlag(Http2Flags flag) => (Flags & flag) == flag;

    /// <summary>The 24-byte client connection preface (RFC 7540 §3.5).</summary>
    public static readonly byte[] ClientPreface =
        "PRI * HTTP/2.0\r\n\r\nSM\r\n\r\n"u8.ToArray();

    public static async Task<Http2Frame?> ReadAsync(Stream stream, CancellationToken ct)
    {
        var header = new byte[9];
        if (!await ReadExactAsync(stream, header, ct).ConfigureAwait(false)) return null;

        int length = (header[0] << 16) | (header[1] << 8) | header[2];
        var type = (Http2FrameType)header[3];
        var flags = (Http2Flags)header[4];
        int streamId = (int)(BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(5)) & 0x7FFFFFFF);

        var payload = new byte[length];
        if (length > 0 && !await ReadExactAsync(stream, payload, ct).ConfigureAwait(false))
            return null;

        return new Http2Frame { Type = type, Flags = flags, StreamId = streamId, Payload = payload };
    }

    public async Task WriteAsync(Stream stream, CancellationToken ct)
    {
        var header = new byte[9];
        header[0] = (byte)((Payload.Length >> 16) & 0xFF);
        header[1] = (byte)((Payload.Length >> 8) & 0xFF);
        header[2] = (byte)(Payload.Length & 0xFF);
        header[3] = (byte)Type;
        header[4] = (byte)Flags;
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(5), (uint)StreamId & 0x7FFFFFFF);
        await stream.WriteAsync(header, ct).ConfigureAwait(false);
        if (Payload.Length > 0) await stream.WriteAsync(Payload, ct).ConfigureAwait(false);
    }

    /// <summary>Builds a HEADERS frame for the given (already-encoded) header block.</summary>
    public static Http2Frame Headers(int streamId, byte[] headerBlock, bool endStream, bool endHeaders) =>
        new()
        {
            Type = Http2FrameType.Headers,
            StreamId = streamId,
            Flags = (endStream ? Http2Flags.EndStream : 0) | (endHeaders ? Http2Flags.EndHeaders : 0),
            Payload = headerBlock,
        };

    public static Http2Frame Continuation(int streamId, byte[] headerBlock, bool endHeaders) =>
        new()
        {
            Type = Http2FrameType.Continuation,
            StreamId = streamId,
            Flags = endHeaders ? Http2Flags.EndHeaders : 0,
            Payload = headerBlock,
        };

    public static Http2Frame Data(int streamId, byte[] data, bool endStream) =>
        new()
        {
            Type = Http2FrameType.Data,
            StreamId = streamId,
            Flags = endStream ? Http2Flags.EndStream : 0,
            Payload = data,
        };

    public static Http2Frame Settings(IReadOnlyList<(Http2Setting Id, uint Value)> settings)
    {
        var payload = new byte[settings.Count * 6];
        for (int i = 0; i < settings.Count; i++)
        {
            BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(i * 6), (ushort)settings[i].Id);
            BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(i * 6 + 2), settings[i].Value);
        }
        return new Http2Frame { Type = Http2FrameType.Settings, StreamId = 0, Payload = payload };
    }

    public static Http2Frame SettingsAck() =>
        new() { Type = Http2FrameType.Settings, Flags = Http2Flags.Ack, StreamId = 0 };

    public static Http2Frame PingAck(byte[] opaque) =>
        new() { Type = Http2FrameType.Ping, Flags = Http2Flags.Ack, StreamId = 0, Payload = opaque };

    public static Http2Frame WindowUpdate(int streamId, int increment)
    {
        var payload = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(payload, (uint)increment & 0x7FFFFFFF);
        return new Http2Frame { Type = Http2FrameType.WindowUpdate, StreamId = streamId, Payload = payload };
    }

    public static Http2Frame RstStream(int streamId, Http2ErrorCode error)
    {
        var payload = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(payload, (uint)error);
        return new Http2Frame { Type = Http2FrameType.RstStream, StreamId = streamId, Payload = payload };
    }

    public static Http2Frame GoAway(int lastStreamId, Http2ErrorCode error)
    {
        var payload = new byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(0), (uint)lastStreamId & 0x7FFFFFFF);
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(4), (uint)error);
        return new Http2Frame { Type = Http2FrameType.GoAway, StreamId = 0, Payload = payload };
    }

    /// <summary>Strips DATA/HEADERS padding and (for HEADERS) an optional priority field.</summary>
    public ReadOnlySpan<byte> HeaderBlockFragment()
    {
        ReadOnlySpan<byte> span = Payload;
        int start = 0, end = span.Length;
        if (HasFlag(Http2Flags.Padded))
        {
            int padLength = span[0];
            start = 1;
            end -= padLength;
        }
        if (Type == Http2FrameType.Headers && HasFlag(Http2Flags.Priority))
            start += 5; // 4-byte stream dependency + 1-byte weight
        if (start > end) throw new InvalidDataException("HTTP/2: invalid padding/priority");
        return span[start..end];
    }

    /// <summary>Strips DATA padding to yield the application data.</summary>
    public ReadOnlySpan<byte> DataPayload()
    {
        ReadOnlySpan<byte> span = Payload;
        if (!HasFlag(Http2Flags.Padded)) return span;
        int padLength = span[0];
        int end = span.Length - padLength;
        if (end < 1) throw new InvalidDataException("HTTP/2: invalid DATA padding");
        return span[1..end];
    }

    private static async Task<bool> ReadExactAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        int read = 0;
        while (read < buffer.Length)
        {
            int n = await stream.ReadAsync(buffer.AsMemory(read), ct).ConfigureAwait(false);
            if (n == 0) return false;
            read += n;
        }
        return true;
    }
}
