using System.Diagnostics;

namespace HttpSpy.Core.Models;

/// <summary>
/// Keeps large captured bodies out of the managed heap.
///
/// A capture retaining thousands of transactions with multi-megabyte responses
/// held every byte of every body live for as long as the row stayed in the grid,
/// which is the shape of an out-of-memory crash on a long session. Bodies over
/// the threshold are written to a temporary file and read back on demand; the
/// small ones — which are the overwhelming majority — stay inline, because
/// paging out a 200-byte JSON response would cost far more than it saves.
///
/// A spilled body deletes its file when the owning session is collected, so an
/// evicted row releases its disk as well as its memory.
/// </summary>
public sealed class BodyStore : IDisposable
{
    /// <summary>The store used by captured sessions unless one is injected for tests.</summary>
    public static BodyStore Shared { get; } = new();

    private readonly string _directory;
    private readonly object _gate = new();
    private bool _created;
    private bool _disposed;

    private long _spilledCount;
    private long _spilledBytes;

    public BodyStore(string? directory = null)
    {
        _directory = directory ?? Path.Combine(Path.GetTempPath(),
            $"httpspy-bodies-{Environment.ProcessId}-{Stopwatch.GetTimestamp():x}");
    }

    /// <summary>Bodies at or above this size are written to disk. 0 disables spilling.</summary>
    public long Threshold { get; set; } = 512 * 1024;

    /// <summary>Turns spilling off entirely, keeping everything in memory.</summary>
    public bool Enabled { get; set; } = true;

    public string Directory => _directory;
    public long SpilledCount => Interlocked.Read(ref _spilledCount);
    public long SpilledBytes => Interlocked.Read(ref _spilledBytes);

    /// <summary>
    /// Wraps a body, spilling it to disk when it is large enough to be worth it.
    /// Falls back to keeping the body in memory if the file cannot be written —
    /// running out of temp space must not lose captured data.
    /// </summary>
    public BodyRef Store(byte[]? data)
    {
        if (data is null || data.Length == 0) return BodyRef.Empty;
        if (_disposed || !Enabled || Threshold <= 0 || data.LongLength < Threshold)
            return BodyRef.Inline(data);

        try
        {
            EnsureDirectory();
            var path = Path.Combine(_directory, Guid.NewGuid().ToString("N") + ".body");
            File.WriteAllBytes(path, data);

            Interlocked.Increment(ref _spilledCount);
            Interlocked.Add(ref _spilledBytes, data.LongLength);

            return BodyRef.Spilled(new SpilledBody(path, data.LongLength), data);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return BodyRef.Inline(data);
        }
    }

    private void EnsureDirectory()
    {
        if (_created) return;
        lock (_gate)
        {
            if (_created) return;
            System.IO.Directory.CreateDirectory(_directory);
            _created = true;
        }
    }

    /// <summary>Removes every spill file this store created.</summary>
    public void Dispose()
    {
        _disposed = true;
        try
        {
            if (_created && System.IO.Directory.Exists(_directory))
                System.IO.Directory.Delete(_directory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* best effort */ }
    }

    /// <summary>
    /// Deletes spill directories left behind by processes that are gone. A crash
    /// skips <see cref="Dispose"/>, and nobody wants yesterday's capture sitting
    /// in the temp directory forever.
    /// </summary>
    public static void CleanOrphans()
    {
        try
        {
            foreach (var dir in System.IO.Directory.EnumerateDirectories(Path.GetTempPath(), "httpspy-bodies-*"))
            {
                var parts = Path.GetFileName(dir).Split('-');
                if (parts.Length < 3 || !int.TryParse(parts[2], out int pid)) continue;
                if (pid == Environment.ProcessId || IsAlive(pid)) continue;
                try { System.IO.Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* best effort */ }
    }

    private static bool IsAlive(int pid)
    {
        // Pid 0 is the kernel scheduler on Unix and "System Idle" on Windows;
        // treating it as live would strand a directory forever.
        if (pid <= 0) return false;
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
    }
}

/// <summary>
/// A body written to disk. The finalizer deletes the file, so a session evicted
/// from the grid releases its spill without anyone having to remember to.
/// </summary>
internal sealed class SpilledBody
{
    private readonly string _path;
    private int _deleted;

    public SpilledBody(string path, long length)
    {
        _path = path;
        Length = length;
    }

    public long Length { get; }

    public byte[] Read()
    {
        try { return File.ReadAllBytes(_path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The spill file is gone (temp cleaner, disk full at write time).
            // An empty body is wrong but a crash while drawing the grid is worse.
            return Array.Empty<byte>();
        }
    }

    ~SpilledBody() => Delete();

    private void Delete()
    {
        if (Interlocked.Exchange(ref _deleted, 1) != 0) return;
        try { if (File.Exists(_path)) File.Delete(_path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* best effort */ }
    }
}

/// <summary>
/// A captured body: either the bytes themselves, or a reference to the file they
/// were spilled to.
///
/// A materialized copy is held weakly, so repeated reads while a body is being
/// inspected do not re-read the file, while a body nobody is looking at any more
/// can be collected. This is what makes <see cref="HttpSession.ResponseBody"/>
/// keep its plain <c>byte[]</c> shape without pinning every byte forever.
/// </summary>
public readonly struct BodyRef
{
    private readonly byte[]? _inline;
    private readonly SpilledBody? _spilled;
    private readonly WeakReference<byte[]>? _cache;

    private BodyRef(byte[]? inline, SpilledBody? spilled, WeakReference<byte[]>? cache)
    {
        _inline = inline;
        _spilled = spilled;
        _cache = cache;
    }

    public static BodyRef Empty => default;

    internal static BodyRef Inline(byte[] data) => new(data, null, null);

    internal static BodyRef Spilled(SpilledBody body, byte[] justWritten) =>
        new(null, body, new WeakReference<byte[]>(justWritten));

    /// <summary>Length in bytes, available without touching the disk.</summary>
    public long Length => _spilled?.Length ?? _inline?.LongLength ?? 0;

    /// <summary>True when the bytes live in a file rather than on the heap.</summary>
    public bool IsSpilled => _spilled is not null;

    /// <summary>
    /// A stable object identifying this body, usable as a cache key. For a
    /// spilled body it is the file handle rather than the bytes, so a cache keyed
    /// on it does not pin the payload in memory.
    /// </summary>
    public object? Identity => (object?)_spilled ?? _inline;

    /// <summary>The bytes, read back from disk if they were spilled.</summary>
    public byte[] Bytes
    {
        get
        {
            if (_inline is not null) return _inline;
            if (_spilled is null) return Array.Empty<byte>();

            if (_cache is not null && _cache.TryGetTarget(out var cached)) return cached;

            var bytes = _spilled.Read();
            _cache?.SetTarget(bytes);
            return bytes;
        }
    }
}
