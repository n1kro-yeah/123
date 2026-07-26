using System.Diagnostics;

namespace HttpSpy.Core.Proxy;

/// <summary>
/// The bandwidth budget behind the "Network simulation" feature.
///
/// A single token bucket is shared by every client-bound write path — plain
/// HTTP/1.1 responses, Server-Sent Event streams, WebSocket frames, HTTP/2 DATA
/// frames and opaque CONNECT tunnels — so the configured limit describes the
/// simulated link as a whole rather than being applied independently (and
/// therefore multiplied) per transaction.
///
/// Settings are read live from <see cref="ProxyOptions"/> on every acquisition,
/// so changing the throttle in the options dialog takes effect on connections
/// that are already open.
/// </summary>
public sealed class NetworkThrottle
{
    /// <summary>How much of a burst the bucket may accumulate, in seconds of bandwidth.</summary>
    private const double BurstSeconds = 0.05;

    /// <summary>Never hand out more than this per write, so a big buffer still paces smoothly.</summary>
    private const int MaxSlice = 64 * 1024;

    /// <summary>Lower bound on a sleep, so we do not spin on sub-millisecond waits.</summary>
    private static readonly TimeSpan MinWait = TimeSpan.FromMilliseconds(2);

    private readonly ProxyOptions _options;
    private readonly object _gate = new();

    private double _tokens;
    private long _lastRefill = Stopwatch.GetTimestamp();
    private double _lastRate;

    public NetworkThrottle(ProxyOptions options) => _options = options;

    /// <summary>True when a bandwidth ceiling is in force.</summary>
    public bool LimitsBandwidth => _options.ThrottleEnabled && _options.ThrottleKbps > 0;

    /// <summary>True when artificial latency is injected before each response.</summary>
    public bool AddsLatency => _options.ThrottleEnabled && _options.ExtraLatencyMs > 0;

    /// <summary>True when the throttle does anything at all; wrapping is skipped otherwise.</summary>
    public bool IsActive => LimitsBandwidth || AddsLatency;

    private double BytesPerSecond => Math.Max(1.0, _options.ThrottleKbps * 1024.0 / 8.0);

    /// <summary>
    /// Injects the configured one-off latency. Called once per response rather
    /// than per write, so a long-lived event stream is delayed when it starts
    /// instead of stuttering on every chunk.
    /// </summary>
    public Task DelayAsync(CancellationToken ct) =>
        AddsLatency ? Task.Delay(_options.ExtraLatencyMs, ct) : Task.CompletedTask;

    /// <summary>
    /// Reserves budget for the next write. Returns how many bytes may be written
    /// immediately; when that is zero, <paramref name="wait"/> says how long to
    /// sleep before asking again.
    /// </summary>
    internal int Acquire(int wanted, out TimeSpan wait)
    {
        if (wanted <= 0) { wait = TimeSpan.Zero; return 0; }
        if (!LimitsBandwidth) { wait = TimeSpan.Zero; return wanted; }

        lock (_gate)
        {
            double rate = BytesPerSecond;
            Refill(rate);

            if (_tokens >= 1.0)
            {
                int grant = (int)Math.Min(wanted, Math.Min(_tokens, MaxSlice));
                _tokens -= grant;
                wait = TimeSpan.Zero;
                return grant;
            }

            // Not enough budget yet. Sleep for a whole slice rather than for the
            // single byte we are short, so we wake up able to do useful work.
            double need = Math.Max(1.0 - _tokens, rate * BurstSeconds);
            wait = TimeSpan.FromSeconds(need / rate);
            if (wait < MinWait) wait = MinWait;
            return 0;
        }
    }

    /// <summary>Adds the bandwidth earned since the last call, capped at one burst.</summary>
    private void Refill(double rate)
    {
        long now = Stopwatch.GetTimestamp();
        double elapsed = (now - _lastRefill) / (double)Stopwatch.Frequency;
        _lastRefill = now;

        // A rate change should not let a bucket filled at the old rate leak
        // through; re-cap against the new ceiling.
        if (rate != _lastRate)
        {
            _lastRate = rate;
            _tokens = Math.Min(_tokens, rate * BurstSeconds);
        }

        if (elapsed > 0) _tokens = Math.Min(_tokens + elapsed * rate, rate * BurstSeconds);
    }

    /// <summary>Writes the whole buffer to <paramref name="stream"/>, pacing it to the budget.</summary>
    public async Task WriteAsync(Stream stream, ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        if (!LimitsBandwidth)
        {
            await stream.WriteAsync(data, ct).ConfigureAwait(false);
            return;
        }

        int offset = 0;
        while (offset < data.Length)
        {
            int grant = Acquire(data.Length - offset, out var wait);
            if (grant == 0)
            {
                await Task.Delay(wait, ct).ConfigureAwait(false);
                continue;
            }

            await stream.WriteAsync(data.Slice(offset, grant), ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);
            offset += grant;
        }
    }

    /// <summary>
    /// Wraps <paramref name="inner"/> so that everything written to it is paced,
    /// or returns it unchanged when no bandwidth limit is configured. Wrapping at
    /// the socket means TLS records, HTTP/2 frames and tunnelled bytes are all
    /// covered without every write site having to know about throttling.
    /// </summary>
    public Stream Wrap(Stream inner) => LimitsBandwidth ? new ThrottledStream(inner, this) : inner;
}

/// <summary>
/// A write-limiting <see cref="Stream"/> decorator. Reads pass straight through —
/// only the client-bound direction is simulated, which is what a "download
/// bandwidth" setting means.
/// </summary>
internal sealed class ThrottledStream : Stream
{
    private readonly Stream _inner;
    private readonly NetworkThrottle _throttle;

    public ThrottledStream(Stream inner, NetworkThrottle throttle)
    {
        _inner = inner;
        _throttle = throttle;
    }

    public override bool CanRead => _inner.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => _inner.CanWrite;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

    public override int Read(Span<byte> buffer) => _inner.Read(buffer);

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        _inner.ReadAsync(buffer, offset, count, cancellationToken);

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        _inner.ReadAsync(buffer, cancellationToken);

    public override void Write(byte[] buffer, int offset, int count) =>
        Write(new ReadOnlySpan<byte>(buffer, offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        // SslStream's handshake still issues synchronous writes on some paths;
        // pace them with the same bucket rather than letting them bypass it.
        int offset = 0;
        while (offset < buffer.Length)
        {
            int grant = _throttle.Acquire(buffer.Length - offset, out var wait);
            if (grant == 0)
            {
                Thread.Sleep(wait);
                continue;
            }

            _inner.Write(buffer.Slice(offset, grant));
            _inner.Flush();
            offset += grant;
        }
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        _throttle.WriteAsync(_inner, new ReadOnlyMemory<byte>(buffer, offset, count), cancellationToken);

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
        new(_throttle.WriteAsync(_inner, buffer, cancellationToken));

    public override void Flush() => _inner.Flush();

    public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);

    protected override void Dispose(bool disposing)
    {
        if (disposing) _inner.Dispose();
        base.Dispose(disposing);
    }

    public override ValueTask DisposeAsync() => _inner.DisposeAsync();
}
