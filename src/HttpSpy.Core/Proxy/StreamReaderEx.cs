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
    private readonly int _maxLineLength;
    private int _pos;
    private int _len;

    public StreamReaderEx(Stream stream, int bufferSize = 16 * 1024, int maxLineLength = 64 * 1024)
    {
        _stream = stream;
        _buffer = new byte[bufferSize];
        _maxLineLength = maxLineLength;
    }

    public long TotalBytesRead { get; private set; }

    private async Task<bool> FillAsync(CancellationToken ct)
    {
        if (_pos < _len) return true;
        _pos = 0;
        _len = await _stream.ReadAsync(_buffer.AsMemory(), ct).ConfigureAwait(false);
        if (_len > 0) TotalBytesRead += _len;
        return _len > 0;
    }

    /// <summary>Reads a single line terminated by CRLF (or LF). Returns null at EOF.</summary>
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
                any = true;
                if (b == (byte)'\n')
                {
                    if (sb.Length > 0 && sb[^1] == '\r') sb.Length--;
                    return sb.ToString();
                }
                if (sb.Length >= _maxLineLength)
                    throw new InvalidDataException(
                        $"HTTP line exceeds the {_maxLineLength}-byte limit (no CRLF terminator)");
                sb.Append((char)b);
            }
        }
    }

    /// <summary>Reads exactly <paramref name="count"/> bytes (or fewer at EOF).</summary>
    public async Task<byte[]> ReadExactAsync(long count, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        long remaining = count;
        while (remaining > 0)
        {
            if (_pos >= _len && !await FillAsync(ct).ConfigureAwait(false)) break;
            int take = (int)Math.Min(remaining, _len - _pos);
            ms.Write(_buffer, _pos, take);
            _pos += take;
            remaining -= take;
        }
        return ms.ToArray();
    }

    /// <summary>Reads until the underlying stream is closed.</summary>
    public async Task<byte[]> ReadToEndAsync(CancellationToken ct)
    {
        using var ms = new MemoryStream();
        while (true)
        {
            if (_pos >= _len && !await FillAsync(ct).ConfigureAwait(false)) break;
            ms.Write(_buffer, _pos, _len - _pos);
            _pos = _len;
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
        return slice;
    }
}
