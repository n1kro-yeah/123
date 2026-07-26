using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using HttpSpy.Core.Proxy;
using Xunit;

namespace HttpSpy.Tests;

/// <summary>
/// The throttle used to exist only on the buffered HTTP/1.1 response path, so a
/// configured limit silently did nothing to HTTP/2, WebSocket, SSE or tunnelled
/// traffic. These cover the shared bucket and the stream decorator that now
/// carries it everywhere.
/// </summary>
public class NetworkThrottleTests
{
    private static ProxyOptions Options(bool enabled, int kbps = 0, int latencyMs = 0) => new()
    {
        ThrottleEnabled = enabled,
        ThrottleKbps = kbps,
        ExtraLatencyMs = latencyMs,
    };

    [Fact]
    public void Inactive_when_the_master_switch_is_off()
    {
        var t = new NetworkThrottle(Options(enabled: false, kbps: 100, latencyMs: 500));
        Assert.False(t.IsActive);
        Assert.False(t.LimitsBandwidth);
        Assert.False(t.AddsLatency);
    }

    [Fact]
    public void Latency_alone_counts_as_active()
    {
        // Regression: latency used to require a non-zero bandwidth limit to have
        // any effect at all.
        var t = new NetworkThrottle(Options(enabled: true, kbps: 0, latencyMs: 250));
        Assert.True(t.IsActive);
        Assert.True(t.AddsLatency);
        Assert.False(t.LimitsBandwidth);
    }

    [Fact]
    public void Acquire_grants_everything_when_unlimited()
    {
        var t = new NetworkThrottle(Options(enabled: true, kbps: 0));
        int granted = t.Acquire(1_000_000, out var wait);
        Assert.Equal(1_000_000, granted);
        Assert.Equal(TimeSpan.Zero, wait);
    }

    /// <summary>
    /// Drains the bucket, returning how many bytes came out. Bounded so a
    /// regression that keeps handing out budget fails the test instead of
    /// hanging the run.
    /// </summary>
    private static int Drain(NetworkThrottle t, int maxIterations = 512)
    {
        int total = 0;
        for (int i = 0; i < maxIterations; i++)
        {
            int n = t.Acquire(1_000_000, out _);
            if (n == 0) return total;
            total += n;
        }
        Assert.Fail($"bucket still granting budget after {maxIterations} acquisitions ({total} bytes)");
        return total;
    }

    [Fact]
    public void Acquire_hands_out_one_burst_then_asks_the_caller_to_wait()
    {
        // 800 kbps = 102 400 bytes/sec; the bucket holds 50 ms of that ≈ 5 120 bytes.
        var t = new NetworkThrottle(Options(enabled: true, kbps: 800));
        t.Acquire(1, out _);   // establish the rate
        Thread.Sleep(100);     // let the bucket fill past its cap

        int burst = Drain(t);
        int afterwards = Drain(t);

        Assert.InRange(burst, 1, 8_000);
        // Once the burst is spent only the trickle earned since the last call is
        // available, so a caller cannot keep pulling full slices.
        Assert.True(afterwards * 4 < burst,
            $"burst {burst} bytes but {afterwards} bytes still came out immediately afterwards");
    }

    [Fact]
    public void Acquire_never_asks_for_a_busy_wait()
    {
        var t = new NetworkThrottle(Options(enabled: true, kbps: 64));
        t.Acquire(1, out _);
        Thread.Sleep(100);

        TimeSpan wait = TimeSpan.Zero;
        bool exhausted = false;
        for (int i = 0; i < 512 && !exhausted; i++)
            exhausted = t.Acquire(1_000_000, out wait) == 0;

        Assert.True(exhausted, "the bucket never ran dry");
        Assert.True(wait >= TimeSpan.FromMilliseconds(2), $"wait of {wait.TotalMilliseconds} ms would spin");
    }

    [Fact]
    public void Acquire_caps_a_single_grant_so_large_buffers_still_pace()
    {
        // Without a per-grant ceiling a fast link would emit one enormous write
        // and the pacing would be invisible.
        var t = new NetworkThrottle(Options(enabled: true, kbps: 100_000));
        t.Acquire(1, out _);
        Thread.Sleep(100);

        Assert.True(t.Acquire(8 * 1024 * 1024, out _) <= 64 * 1024);
    }

    [Fact]
    public async Task WriteAsync_paces_a_buffer_to_the_configured_rate()
    {
        // 512 kbps = 65 536 bytes/sec, so 32 KiB should take about half a second.
        var t = new NetworkThrottle(Options(enabled: true, kbps: 512));
        var sink = new MemoryStream();
        var payload = new byte[32 * 1024];

        var sw = Stopwatch.StartNew();
        await t.WriteAsync(sink, payload, CancellationToken.None);
        sw.Stop();

        Assert.Equal(payload.Length, sink.Length);
        Assert.True(sw.ElapsedMilliseconds >= 250,
            $"32 KiB at 512 kbps finished in {sw.ElapsedMilliseconds} ms — not throttled");
    }

    [Fact]
    public async Task WriteAsync_is_immediate_when_unlimited()
    {
        var t = new NetworkThrottle(Options(enabled: false));
        var sink = new MemoryStream();
        var payload = new byte[512 * 1024];

        var sw = Stopwatch.StartNew();
        await t.WriteAsync(sink, payload, CancellationToken.None);
        sw.Stop();

        Assert.Equal(payload.Length, sink.Length);
        Assert.True(sw.ElapsedMilliseconds < 500, $"unthrottled write took {sw.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void Wrap_is_a_no_op_without_a_bandwidth_limit()
    {
        var t = new NetworkThrottle(Options(enabled: true, latencyMs: 100));
        var inner = new MemoryStream();
        Assert.Same(inner, t.Wrap(inner));
    }

    [Fact]
    public void Wrap_decorates_when_a_bandwidth_limit_is_set()
    {
        var t = new NetworkThrottle(Options(enabled: true, kbps: 256));
        var inner = new MemoryStream();
        var wrapped = t.Wrap(inner);
        Assert.NotSame(inner, wrapped);
        Assert.True(wrapped.CanWrite);
    }

    [Fact]
    public async Task Wrapped_stream_passes_reads_through_untouched()
    {
        // Only the client-bound direction is simulated; throttling reads would
        // slow the origin fetch, which is not what a download limit means.
        var t = new NetworkThrottle(Options(enabled: true, kbps: 8));
        var source = new MemoryStream(new byte[64 * 1024]);
        var wrapped = t.Wrap(source);

        var buffer = new byte[64 * 1024];
        var sw = Stopwatch.StartNew();
        int read = await wrapped.ReadAsync(buffer, CancellationToken.None);
        sw.Stop();

        Assert.Equal(64 * 1024, read);
        Assert.True(sw.ElapsedMilliseconds < 500, $"read was throttled ({sw.ElapsedMilliseconds} ms)");
    }

    [Fact]
    public async Task Wrapped_stream_throttles_writes_that_go_through_it()
    {
        var t = new NetworkThrottle(Options(enabled: true, kbps: 512));
        var inner = new MemoryStream();
        var wrapped = t.Wrap(inner);

        var sw = Stopwatch.StartNew();
        await wrapped.WriteAsync(new byte[32 * 1024], CancellationToken.None);
        sw.Stop();

        Assert.Equal(32 * 1024, inner.Length);
        Assert.True(sw.ElapsedMilliseconds >= 250,
            $"write through the decorator finished in {sw.ElapsedMilliseconds} ms");
    }

    [Fact]
    public async Task DelayAsync_only_waits_when_latency_is_configured()
    {
        var without = new NetworkThrottle(Options(enabled: true, kbps: 512));
        var sw = Stopwatch.StartNew();
        await without.DelayAsync(CancellationToken.None);
        sw.Stop();
        Assert.True(sw.ElapsedMilliseconds < 100);

        var with = new NetworkThrottle(Options(enabled: true, latencyMs: 150));
        sw.Restart();
        await with.DelayAsync(CancellationToken.None);
        sw.Stop();
        Assert.True(sw.ElapsedMilliseconds >= 120, $"latency was {sw.ElapsedMilliseconds} ms, expected ~150");
    }

    [Fact]
    public void Lowering_the_rate_immediately_shrinks_the_bucket()
    {
        var options = Options(enabled: true, kbps: 100_000);
        var t = new NetworkThrottle(options);

        // Let a large bucket build up at the fast rate.
        t.Acquire(1, out _);
        Thread.Sleep(60);

        options.ThrottleKbps = 8; // 1 024 bytes/sec → 50 ms burst ≈ 51 bytes
        int granted = t.Acquire(1_000_000, out _);
        Assert.True(granted <= 128, $"granted {granted} bytes at the new rate — stale budget leaked through");
    }
}
