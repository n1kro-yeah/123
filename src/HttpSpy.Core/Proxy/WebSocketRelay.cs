using HttpSpy.Core.Models;

namespace HttpSpy.Core.Proxy;

/// <summary>
/// Incrementally parses a stream of WebSocket bytes (RFC 6455) into frames.
/// Fed with raw bytes as they flow in one direction; emits decoded frames.
/// </summary>
/// <remarks>
/// Uses a compacting byte buffer rather than a <c>List&lt;byte&gt;</c>: the old
/// implementation appended byte-by-byte and called <c>RemoveRange(0, n)</c> after
/// every frame, which is O(n²) in the size of the message and made large binary
/// frames pathologically slow.
/// </remarks>
public sealed class WebSocketFrameParser
{
    /// <summary>Frames larger than this are reported but their payload is not retained.</summary>
    public const long DefaultMaxPayload = 4 * 1024 * 1024;

    private readonly MessageDirection _direction;
    private readonly long _maxPayload;
    private byte[] _buffer = new byte[8 * 1024];
    private int _start;   // first unconsumed byte
    private int _end;     // one past the last buffered byte
    private int _index;

    public WebSocketFrameParser(MessageDirection direction, long maxPayload = DefaultMaxPayload)
    {
        _direction = direction;
        _maxPayload = maxPayload > 0 ? maxPayload : long.MaxValue;
    }

    private int Available => _end - _start;

    public IEnumerable<WebSocketFrame> Feed(byte[] data, int count)
    {
        Append(data, count);
        while (TryParse(out var frame)) yield return frame!;
        Compact();
    }

    private void Append(byte[] data, int count)
    {
        if (count <= 0) return;
        EnsureCapacity(count);
        Buffer.BlockCopy(data, 0, _buffer, _end, count);
        _end += count;
    }

    private void EnsureCapacity(int extra)
    {
        if (_end + extra <= _buffer.Length) return;

        // Reclaim the already-consumed prefix before growing the array.
        if (_start > 0)
        {
            Buffer.BlockCopy(_buffer, _start, _buffer, 0, Available);
            _end -= _start;
            _start = 0;
            if (_end + extra <= _buffer.Length) return;
        }

        int capacity = _buffer.Length;
        while (capacity < _end + extra) capacity *= 2;
        Array.Resize(ref _buffer, capacity);
    }

    /// <summary>Drops the consumed prefix once it dominates the buffer.</summary>
    private void Compact()
    {
        if (_start == 0) return;
        if (Available == 0) { _start = _end = 0; return; }
        if (_start < _buffer.Length / 2) return;
        Buffer.BlockCopy(_buffer, _start, _buffer, 0, Available);
        _end -= _start;
        _start = 0;
    }

    private bool TryParse(out WebSocketFrame? frame)
    {
        frame = null;
        if (Available < 2) return false;

        int p = _start;
        byte b0 = _buffer[p];
        byte b1 = _buffer[p + 1];
        bool fin = (b0 & 0x80) != 0;
        var opcode = (WebSocketOpcode)(b0 & 0x0F);
        bool masked = (b1 & 0x80) != 0;
        long payloadLen = b1 & 0x7F;

        int offset = 2;
        if (payloadLen == 126)
        {
            if (Available < offset + 2) return false;
            payloadLen = (_buffer[p + offset] << 8) | _buffer[p + offset + 1];
            offset += 2;
        }
        else if (payloadLen == 127)
        {
            if (Available < offset + 8) return false;
            payloadLen = 0;
            for (int i = 0; i < 8; i++) payloadLen = (payloadLen << 8) | _buffer[p + offset + i];
            offset += 8;

            // RFC 6455 §5.2: the high bit of a 64-bit length must be 0. Without
            // this check a hostile or corrupt frame yields a negative length and
            // blows up on the payload allocation.
            if (payloadLen < 0) throw new InvalidDataException("WebSocket frame declares a negative payload length.");
        }

        byte[] mask = Array.Empty<byte>();
        if (masked)
        {
            if (Available < offset + 4) return false;
            mask = new[] { _buffer[p + offset], _buffer[p + offset + 1], _buffer[p + offset + 2], _buffer[p + offset + 3] };
            offset += 4;
        }

        if (Available < offset + payloadLen) return false;

        bool truncated = payloadLen > _maxPayload;
        int retain = (int)Math.Min(payloadLen, _maxPayload);
        var payload = new byte[retain];
        int payloadStart = p + offset;
        for (int i = 0; i < retain; i++)
        {
            byte value = _buffer[payloadStart + i];
            if (masked) value ^= mask[i % 4];
            payload[i] = value;
        }

        _start += offset + (int)payloadLen;

        frame = new WebSocketFrame
        {
            Index = ++_index,
            Direction = _direction,
            Opcode = opcode,
            Fin = fin,
            Masked = masked,
            Payload = payload,
            DeclaredLength = payloadLen,
            Truncated = truncated,
        };
        return true;
    }
}

/// <summary>Relays a WebSocket connection bidirectionally while capturing frames into a session.</summary>
public sealed class WebSocketRelay
{
    private readonly HttpSession _session;
    private readonly ProxyEngine _engine;
    private int _globalIndex;
    private readonly object _gate = new();

    public WebSocketRelay(HttpSession session, ProxyEngine engine)
    {
        _session = session;
        _engine = engine;
    }

    public async Task RunAsync(Stream client, Stream server, byte[] clientPrime, byte[] serverPrime,
        CancellationToken ct)
    {
        long maxPayload = _engine.Options.MaxWebSocketFrame;
        var c2s = PumpAsync(client, server,
            new WebSocketFrameParser(MessageDirection.ClientToServer, maxPayload), clientPrime, ct);
        var s2c = PumpAsync(server, client,
            new WebSocketFrameParser(MessageDirection.ServerToClient, maxPayload), serverPrime, ct);
        await Task.WhenAny(c2s, s2c).ConfigureAwait(false);
    }

    private async Task PumpAsync(Stream from, Stream to, WebSocketFrameParser parser, byte[] prime,
        CancellationToken ct)
    {
        try
        {
            if (prime.Length > 0)
            {
                await to.WriteAsync(prime, ct).ConfigureAwait(false);
                Record(parser, prime, prime.Length);
            }

            var buffer = new byte[32 * 1024];
            while (!ct.IsCancellationRequested)
            {
                int read = await from.ReadAsync(buffer, ct).ConfigureAwait(false);
                if (read <= 0) break;
                // Relay first, decode second: capture must never add latency to the
                // socket, and a decode failure must not break the tunnel.
                await to.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                Record(parser, buffer, read);
            }
        }
        catch
        {
            // connection ended
        }
    }

    private void Record(WebSocketFrameParser parser, byte[] data, int count)
    {
        bool any = false;
        int cap = _engine.Options.MaxWebSocketFrames;
        try
        {
            lock (_gate)
            {
                foreach (var f in parser.Feed(data, count))
                {
                    f.Index = ++_globalIndex;
                    _session.AddWebSocketFrame(f, cap);
                    any = true;
                }
            }
        }
        catch (InvalidDataException ex)
        {
            _engine.RaiseLog($"WebSocket frame decode failed for {_session.Host}: {ex.Message}");
        }

        if (any) _engine.RaiseUpdated(_session);
    }
}
