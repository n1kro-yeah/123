using HttpSpy.Core.Models;

namespace HttpSpy.Core.Proxy;

/// <summary>
/// Incrementally parses a stream of WebSocket bytes (RFC 6455) into frames.
/// Fed with raw bytes as they flow in one direction; emits decoded frames.
/// </summary>
public sealed class WebSocketFrameParser
{
    /// <summary>
    /// Largest payload (bytes) buffered for display per frame. A frame whose declared
    /// length exceeds this is captured as a truncated prefix and the remainder is
    /// discarded as it streams in, so a peer declaring a huge (up to 2^63) payload
    /// can't make the capture buffer grow without bound. The bytes are still relayed
    /// verbatim by <see cref="WebSocketRelay"/> regardless of this cap.
    /// </summary>
    public const int MaxCapturedFrameBytes = 8 * 1024 * 1024;

    private readonly MessageDirection _direction;
    private int _index;

    // Growable byte buffer addressed by [_start, _start + _count). Bytes are consumed by
    // advancing _start (O(1)) and the backing array is compacted in place, avoiding the
    // per-byte List<byte>.Add and O(n) RemoveRange(0, …) of the previous implementation.
    private byte[] _buf = new byte[4096];
    private int _start;
    private int _count;

    // Remaining payload bytes of an oversized frame still to be discarded as they arrive.
    private long _skip;

    public WebSocketFrameParser(MessageDirection direction) => _direction = direction;

    public IEnumerable<WebSocketFrame> Feed(byte[] data, int count)
    {
        int pos = 0;
        if (_skip > 0)
        {
            int drop = (int)Math.Min(_skip, count);
            _skip -= drop;
            pos += drop;
        }
        if (pos < count) Append(data, pos, count - pos);
        while (TryParse(out var frame)) yield return frame!;
    }

    private bool TryParse(out WebSocketFrame? frame)
    {
        frame = null;
        if (_count < 2) return false;

        byte b0 = At(0);
        byte b1 = At(1);
        bool fin = (b0 & 0x80) != 0;
        var opcode = (WebSocketOpcode)(b0 & 0x0F);
        bool masked = (b1 & 0x80) != 0;
        long payloadLen = b1 & 0x7F;

        int offset = 2;
        if (payloadLen == 126)
        {
            if (_count < offset + 2) return false;
            payloadLen = (At(offset) << 8) | At(offset + 1);
            offset += 2;
        }
        else if (payloadLen == 127)
        {
            if (_count < offset + 8) return false;
            payloadLen = 0;
            for (int i = 0; i < 8; i++) payloadLen = (payloadLen << 8) | At(offset + i);
            offset += 8;
        }

        byte[] mask = Array.Empty<byte>();
        if (masked)
        {
            if (_count < offset + 4) return false;
            mask = new[] { At(offset), At(offset + 1), At(offset + 2), At(offset + 3) };
            offset += 4;
        }

        // Oversized / hostile payload: capture only a prefix, then discard the rest as it
        // streams in (never wait to buffer the whole declared length).
        if (payloadLen < 0 || payloadLen > MaxCapturedFrameBytes)
        {
            int available = _count - offset;
            if (available < 0) available = 0;
            int captured = (int)Math.Min(available, MaxCapturedFrameBytes);

            var prefix = new byte[captured];
            for (int i = 0; i < captured; i++)
            {
                byte value = At(offset + i);
                if (masked) value ^= mask[i % 4];
                prefix[i] = value;
            }

            Consume(offset + available);
            _skip = payloadLen - available;

            frame = new WebSocketFrame
            {
                Index = ++_index,
                Direction = _direction,
                Opcode = opcode,
                Fin = fin,
                Masked = masked,
                Payload = prefix,
                Truncated = true,
                DeclaredLength = payloadLen
            };
            return true;
        }

        if (_count < offset + payloadLen) return false;

        var payload = new byte[payloadLen];
        for (long i = 0; i < payloadLen; i++)
        {
            byte value = At(offset + (int)i);
            if (masked) value ^= mask[i % 4];
            payload[i] = value;
        }

        Consume(offset + (int)payloadLen);

        frame = new WebSocketFrame
        {
            Index = ++_index,
            Direction = _direction,
            Opcode = opcode,
            Fin = fin,
            Masked = masked,
            Payload = payload
        };
        return true;
    }

    private byte At(int i) => _buf[_start + i];

    private void Consume(int n)
    {
        _start += n;
        _count -= n;
        if (_count == 0) _start = 0;
    }

    private void Append(byte[] data, int offset, int len)
    {
        if (len == 0) return;
        if (_start + _count + len > _buf.Length)
        {
            int needed = _count + len;
            if (needed <= _buf.Length)
            {
                Buffer.BlockCopy(_buf, _start, _buf, 0, _count);
            }
            else
            {
                int newSize = Math.Max(_buf.Length * 2, needed);
                var grown = new byte[newSize];
                Buffer.BlockCopy(_buf, _start, grown, 0, _count);
                _buf = grown;
            }
            _start = 0;
        }
        Buffer.BlockCopy(data, offset, _buf, _start + _count, len);
        _count += len;
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
        var c2s = PumpAsync(client, server, new WebSocketFrameParser(MessageDirection.ClientToServer), clientPrime, ct);
        var s2c = PumpAsync(server, client, new WebSocketFrameParser(MessageDirection.ServerToClient), serverPrime, ct);
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
                Record(parser.Feed(prime, prime.Length));
            }

            var buffer = new byte[16 * 1024];
            while (!ct.IsCancellationRequested)
            {
                int read = await from.ReadAsync(buffer, ct).ConfigureAwait(false);
                if (read <= 0) break;
                await to.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                Record(parser.Feed(buffer, read));
            }
        }
        catch
        {
            // connection ended
        }
    }

    private void Record(IEnumerable<WebSocketFrame> frames)
    {
        bool any = false;
        lock (_gate)
        {
            foreach (var f in frames)
            {
                f.Index = ++_globalIndex;
                _session.WebSocketFrames.Add(f);
                any = true;
            }
        }
        if (any) _engine.RaiseUpdated(_session);
    }
}
