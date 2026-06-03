namespace HttpSpy.Core.Proxy.Transparent;

/// <summary>
/// Wraps an inner stream so that a block of bytes already read from it (the
/// peeked TLS ClientHello or HTTP request line) is replayed first. Used by the
/// transparent listener, which must inspect the start of the stream to find the
/// target host but still hand the full byte stream to <see cref="System.Net.Security.SslStream"/>
/// or the HTTP parser.
/// </summary>
public sealed class PrefixedStream : Stream
{
    private readonly Stream _inner;
    private byte[] _prefix;
    private int _prefixPos;

    public PrefixedStream(byte[] prefix, Stream inner)
    {
        _prefix = prefix;
        _inner = inner;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => _inner.CanWrite;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (_prefixPos < _prefix.Length)
        {
            int n = Math.Min(count, _prefix.Length - _prefixPos);
            Array.Copy(_prefix, _prefixPos, buffer, offset, n);
            _prefixPos += n;
            return n;
        }
        return _inner.Read(buffer, offset, count);
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        if (_prefixPos < _prefix.Length)
        {
            int n = Math.Min(buffer.Length, _prefix.Length - _prefixPos);
            _prefix.AsMemory(_prefixPos, n).CopyTo(buffer);
            _prefixPos += n;
            return n;
        }
        return await _inner.ReadAsync(buffer, ct).ConfigureAwait(false);
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
        ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

    public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) =>
        _inner.WriteAsync(buffer, ct);
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
        _inner.WriteAsync(buffer.AsMemory(offset, count), ct).AsTask();

    public override void Flush() => _inner.Flush();
    public override Task FlushAsync(CancellationToken ct) => _inner.FlushAsync(ct);
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) _inner.Dispose();
        base.Dispose(disposing);
    }
}
