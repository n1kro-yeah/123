using HttpSpy.Core.Models;

namespace HttpSpy.Core.Proxy;

/// <summary>
/// Incrementally parses a stream of WebSocket bytes (RFC 6455) into frames.
/// Fed with raw bytes as they flow in one direction; emits decoded frames.
/// </summary>
public sealed class WebSocketFrameParser
{
    private readonly List<byte> _buffer = new();
    private readonly MessageDirection _direction;
    private int _index;

    public WebSocketFrameParser(MessageDirection direction) => _direction = direction;

    public IEnumerable<WebSocketFrame> Feed(byte[] data, int count)
    {
        for (int i = 0; i < count; i++) _buffer.Add(data[i]);
        while (TryParse(out var frame)) yield return frame!;
    }

    private bool TryParse(out WebSocketFrame? frame)
    {
        frame = null;
        if (_buffer.Count < 2) return false;

        byte b0 = _buffer[0];
        byte b1 = _buffer[1];
        bool fin = (b0 & 0x80) != 0;
        var opcode = (WebSocketOpcode)(b0 & 0x0F);
        bool masked = (b1 & 0x80) != 0;
        long payloadLen = b1 & 0x7F;

        int offset = 2;
        if (payloadLen == 126)
        {
            if (_buffer.Count < offset + 2) return false;
            payloadLen = (_buffer[offset] << 8) | _buffer[offset + 1];
            offset += 2;
        }
        else if (payloadLen == 127)
        {
            if (_buffer.Count < offset + 8) return false;
            payloadLen = 0;
            for (int i = 0; i < 8; i++) payloadLen = (payloadLen << 8) | _buffer[offset + i];
            offset += 8;
        }

        byte[] mask = Array.Empty<byte>();
        if (masked)
        {
            if (_buffer.Count < offset + 4) return false;
            mask = new[] { _buffer[offset], _buffer[offset + 1], _buffer[offset + 2], _buffer[offset + 3] };
            offset += 4;
        }

        if (_buffer.Count < offset + payloadLen) return false;

        var payload = new byte[payloadLen];
        for (long i = 0; i < payloadLen; i++)
        {
            byte value = _buffer[offset + (int)i];
            if (masked) value ^= mask[i % 4];
            payload[i] = value;
        }

        _buffer.RemoveRange(0, offset + (int)payloadLen);

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
