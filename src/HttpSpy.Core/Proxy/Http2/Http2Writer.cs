namespace HttpSpy.Core.Proxy.Http2;

/// <summary>
/// Serializes frame writes to a single HTTP/2 connection and enforces outbound
/// flow control (RFC 7540 §6.9). DATA frames are split to fit the connection
/// and per-stream send windows and the peer's MAX_FRAME_SIZE; the writer blocks
/// asynchronously until WINDOW_UPDATE frames replenish the windows.
/// </summary>
internal sealed class Http2Writer
{
    private readonly Stream _stream;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly object _windowGate = new();
    private readonly Dictionary<int, long> _streamWindow = new();
    private long _connWindow = 65535;
    private long _peerInitialWindow = 65535;
    private int _maxFrameSize = 16384;
    private volatile TaskCompletionSource _windowSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Http2Writer(Stream stream) => _stream = stream;

    public void SetMaxFrameSize(int value)
    {
        if (value is >= 16384 and <= 16777215) _maxFrameSize = value;
    }

    /// <summary>Applies a new peer SETTINGS_INITIAL_WINDOW_SIZE, adjusting open streams (§6.9.2).</summary>
    public void SetPeerInitialWindow(long value)
    {
        lock (_windowGate)
        {
            long delta = value - _peerInitialWindow;
            _peerInitialWindow = value;
            foreach (var id in _streamWindow.Keys.ToList())
                _streamWindow[id] += delta;
        }
        Signal();
    }

    public void EnsureStream(int streamId)
    {
        lock (_windowGate)
            if (!_streamWindow.ContainsKey(streamId))
                _streamWindow[streamId] = _peerInitialWindow;
    }

    public void OnWindowUpdate(int streamId, long increment)
    {
        lock (_windowGate)
        {
            if (streamId == 0) _connWindow += increment;
            else
            {
                _streamWindow.TryGetValue(streamId, out long cur);
                _streamWindow[streamId] = cur + increment;
            }
        }
        Signal();
    }

    public async Task WriteFrameAsync(Http2Frame frame, CancellationToken ct)
    {
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try { await frame.WriteAsync(_stream, ct).ConfigureAwait(false); }
        finally { _writeLock.Release(); }
    }

    public async Task WriteDataAsync(int streamId, byte[] data, bool endStream, CancellationToken ct)
    {
        EnsureStream(streamId);
        int offset = 0;
        do
        {
            int remaining = data.Length - offset;
            int allowed;
            TaskCompletionSource wait;
            lock (_windowGate)
            {
                wait = _windowSignal;
                long streamWin = _streamWindow.TryGetValue(streamId, out long sw) ? sw : _peerInitialWindow;
                long capacity = Math.Min(_connWindow, streamWin);
                allowed = (int)Math.Min(Math.Min(capacity, _maxFrameSize), remaining);
                if (allowed > 0)
                {
                    _connWindow -= allowed;
                    _streamWindow[streamId] = streamWin - allowed;
                }
            }

            if (allowed <= 0 && remaining > 0)
            {
                await wait.Task.WaitAsync(ct).ConfigureAwait(false);
                continue;
            }

            bool last = offset + allowed >= data.Length;
            var chunk = new byte[allowed];
            Array.Copy(data, offset, chunk, 0, allowed);
            await WriteFrameAsync(Http2Frame.Data(streamId, chunk, last && endStream), ct).ConfigureAwait(false);
            offset += allowed;
        }
        while (offset < data.Length);

        if (data.Length == 0 && endStream)
            await WriteFrameAsync(Http2Frame.Data(streamId, Array.Empty<byte>(), true), ct).ConfigureAwait(false);
    }

    private void Signal()
    {
        var old = Interlocked.Exchange(ref _windowSignal,
            new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        old.TrySetResult();
    }
}
