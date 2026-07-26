using System.Text;

namespace HttpSpy.Core.Proxy;

/// <summary>
/// A small buffered reader over a network <see cref="Stream"/> that can read
/// CRLF-terminated lines and raw byte counts without consuming past what was
/// requested. Tracks the total number of bytes read for traffic accounting.
/// </summary>
public sealed class StreamReaderEx
{
    private readonly Stream _stream;
    private readonly byte[] _buffer;
    private int _pos;
    private int _len;

    public StreamReaderEx(Stream stream, int bufferSize = 16 * 1024)
    {
        _stream = stream;
        _buffer = new byte[bufferSize];
    }

    /// <summary>Total bytes pulled off the underlying stream on this connection.</summary>
    public long TotalBytesRead { get; private set; }

    /// <summary>
    /// Total bytes handed to callers so far. This is deliberately distinct from
    /// <see cref="TotalBytesRead"/>: a single socket read can fill the buffer with
    /// several pipelined messages, so counting filled bytes would bill all of them
    /// to whichever message happened to trigger the read.
    /// </summary>
    public long TotalBytesConsumed { get; private set; }

    /// <summary>Consumption offset at which the current logical message began.</summary>
    public long MarkedOffset { get; private set; }

    /// <summary>Starts a new measurement window at the current position.</summary>
    public void Mark() => MarkedOffset = TotalBytesConsumed;

    /// <summary>Bytes consumed since the last <see cref="Mark"/>.</summary>
    public long BytesSinceMark => TotalBytesConsumed - MarkedOffset;

    /// <summary>Records bytes taken out of the buffer by a caller.</summary>
    private void Consumed(long count) => TotalBytesConsumed += count;

    private async Task<bool> FillAsync(CancellationToken ct)
    {
        if (_pos < _len) return true;
        _pos = 0;
        _len = await _stream.ReadAsync(_buffer.AsMemory(), ct).ConfigureAwait(false);
        if (_len > 0) TotalBytesRead += _len;
        return _len > 0;
    }

    /// <summary>
    /// The largest single header/status line accepted. A peer that never sends a
    /// newline would otherwise grow the line buffer without bound.
    /// </summary>
    public const int MaxLineLength = 64 * 1024;

    /// <summary>Reads a single line terminated by CRLF (or LF). Returns null at EOF.</summary>
    /// <exception cref="InvalidDataException">The line exceeded <see cref="MaxLineLength"/>.</exception>
    public async Task<string?> ReadLineAsync(CancellationToken ct)
    {
        var sb = new StringBuilder(128);
        bool any = false;
        while (true)
        {
            if (!await FillAsync(ct).ConfigureAwait(false))
                return any ? sb.ToString() : null;

            while (_pos < _len)
            {
                byte b = _buffer[_pos++];
                Consumed(1);
                any = true;
                if (b == (byte)'\n')
                {
                    if (sb.Length > 0 && sb[^1] == '\r') sb.Length--;
                    return sb.ToString();
                }
                if (sb.Length >= MaxLineLength)
                    throw new InvalidDataException($"HTTP line exceeded {MaxLineLength} bytes without a line terminator.");
                sb.Append((char)b);
            }
        }
    }

    /// <summary>
    /// True when the last size-capped read hit its cap, i.e. the returned buffer
    /// is a prefix of the data that was actually on the wire.
    /// </summary>
    public bool LastReadTruncated { get; private set; }

    /// <summary>
    /// Reads exactly <paramref name="count"/> bytes (or fewer at EOF).
    /// <paramref name="maxRetained"/> caps how much is kept in memory: bytes past
    /// the cap are consumed from the socket but discarded, so message framing
    /// stays correct on a keep-alive connection while the heap stays bounded.
    /// </summary>
    public async Task<byte[]> ReadExactAsync(long count, CancellationToken ct, long maxRetained = long.MaxValue)
    {
        LastReadTruncated = false;
        if (count <= 0) return Array.Empty<byte>();

        // Pre-size the buffer when the length is known and modest — avoids the
        // repeated doubling a growing MemoryStream would do for a large body.
        long capacity = Math.Min(count, maxRetained);
        using var ms = new MemoryStream(capacity < int.MaxValue ? (int)capacity : 0);
        long remaining = count;
        long retained = 0;
        while (remaining > 0)
        {
            if (_pos >= _len && !await FillAsync(ct).ConfigureAwait(false)) break;
            int take = (int)Math.Min(remaining, _len - _pos);
            int keep = (int)Math.Min(take, Math.Max(0, maxRetained - retained));
            if (keep > 0)
            {
                ms.Write(_buffer, _pos, keep);
                retained += keep;
            }
            if (keep < take) LastReadTruncated = true;
            _pos += take;
            Consumed(take);
            remaining -= take;
        }
        return ms.ToArray();
    }

    /// <summary>
    /// Reads until the underlying stream is closed, retaining at most
    /// <paramref name="maxRetained"/> bytes (the remainder is drained and dropped).
    /// </summary>
    public async Task<byte[]> ReadToEndAsync(CancellationToken ct, long maxRetained = long.MaxValue)
    {
        LastReadTruncated = false;
        using var ms = new MemoryStream();
        long retained = 0;
        while (true)
        {
            if (_pos >= _len && !await FillAsync(ct).ConfigureAwait(false)) break;
            int available = _len - _pos;
            int keep = (int)Math.Min(available, Math.Max(0, maxRetained - retained));
            if (keep > 0)
            {
                ms.Write(_buffer, _pos, keep);
                retained += keep;
            }
            if (keep < available) LastReadTruncated = true;
            _pos = _len;
            Consumed(available);
        }
        return ms.ToArray();
    }

    /// <summary>Reads a single raw chunk of currently-buffered/available bytes.</summary>
    public async Task<byte[]> ReadSomeAsync(CancellationToken ct)
    {
        if (_pos >= _len && !await FillAsync(ct).ConfigureAwait(false))
            return Array.Empty<byte>();
        var slice = new byte[_len - _pos];
        Array.Copy(_buffer, _pos, slice, 0, slice.Length);
        _pos = _len;
        Consumed(slice.Length);
        return slice;
    }

    public bool HasBufferedData => _pos < _len;

    /// <summary>Returns and consumes any bytes already buffered but not yet read.</summary>
    public byte[] DrainBuffered()
    {
        if (_pos >= _len) return Array.Empty<byte>();
        var slice = new byte[_len - _pos];
        Array.Copy(_buffer, _pos, slice, 0, slice.Length);
        _pos = _len;
        Consumed(slice.Length);
        return slice;
    }
}
