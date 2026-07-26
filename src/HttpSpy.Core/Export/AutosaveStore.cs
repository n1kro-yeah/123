using System.Text.Json;
using HttpSpy.Core.Models;

namespace HttpSpy.Core.Export;

/// <summary>What a recoverable autosave contains, without loading it.</summary>
/// <param name="Path">Where the snapshot lives.</param>
/// <param name="SessionCount">How many transactions it holds.</param>
/// <param name="SavedAt">When it was last written.</param>
/// <param name="ProcessId">The process that wrote it.</param>
public sealed record AutosaveInfo(string Path, int SessionCount, DateTime SavedAt, int ProcessId)
{
    /// <summary>How stale the snapshot is, for the "restore?" prompt.</summary>
    public TimeSpan Age => DateTime.Now - SavedAt;
}

/// <summary>
/// Periodic snapshots of the live capture, so an unclean exit does not throw
/// away an afternoon of traffic.
///
/// A marker file records that a capture session is in progress; a clean shutdown
/// deletes it. Finding a marker on the next start is what distinguishes "the app
/// crashed" from "the user closed it", which is the difference between offering
/// a restore and silently discarding the file.
/// </summary>
public sealed class AutosaveStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _directory;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    /// <summary>Snapshots older than this are treated as abandoned and cleaned up.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(7);

    public AutosaveStore(string? directory = null)
    {
        _directory = directory ?? Path.Combine(SettingsStore.Directory, "autosave");
    }

    public string Directory => _directory;
    public string SnapshotPath => Path.Combine(_directory, "autosave.hsc");
    public string MetadataPath => Path.Combine(_directory, "autosave.json");
    public string MarkerPath => Path.Combine(_directory, "session.lock");

    private sealed class Metadata
    {
        public int SessionCount { get; set; }
        public DateTime SavedAt { get; set; }
        public int ProcessId { get; set; }
    }

    // ---- Session lifecycle ---------------------------------------------------

    /// <summary>
    /// Marks a capture session as live. Called once when capture starts; the
    /// marker surviving to the next launch is what signals a crash.
    /// </summary>
    public void BeginSession()
    {
        System.IO.Directory.CreateDirectory(_directory);
        File.WriteAllText(MarkerPath, Environment.ProcessId.ToString());
    }

    /// <summary>
    /// Records a clean shutdown: the marker and the snapshot both go away, so the
    /// next start has nothing to offer and nothing to apologise for.
    /// </summary>
    public void EndSession()
    {
        Delete(MarkerPath);
        Delete(SnapshotPath);
        Delete(MetadataPath);
    }

    /// <summary>Removes only the marker, keeping the snapshot for inspection.</summary>
    public void ClearMarker() => Delete(MarkerPath);

    // ---- Writing -------------------------------------------------------------

    /// <summary>
    /// Writes a snapshot. Writes go to a temporary file and are then moved into
    /// place, so a crash mid-write cannot leave a half-written snapshot where a
    /// good one used to be — which would turn a recoverable crash into data loss.
    /// </summary>
    public async Task<bool> SaveAsync(IReadOnlyList<HttpSession> sessions, CancellationToken ct = default)
    {
        // Never let two snapshots overlap; the later one would race the rename.
        if (!await _writeLock.WaitAsync(0, ct).ConfigureAwait(false)) return false;
        try
        {
            System.IO.Directory.CreateDirectory(_directory);
            var temp = SnapshotPath + ".tmp";

            await SessionStore.SaveAsync(temp, sessions, ct).ConfigureAwait(false);
            File.Move(temp, SnapshotPath, overwrite: true);

            var metadata = new Metadata
            {
                SessionCount = sessions.Count,
                SavedAt = DateTime.Now,
                ProcessId = Environment.ProcessId,
            };
            var metaTemp = MetadataPath + ".tmp";
            await File.WriteAllTextAsync(metaTemp, JsonSerializer.Serialize(metadata, JsonOptions), ct)
                .ConfigureAwait(false);
            File.Move(metaTemp, MetadataPath, overwrite: true);

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A failed autosave must never take the capture down with it.
            return false;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    // ---- Recovery ------------------------------------------------------------

    /// <summary>
    /// Describes a snapshot worth offering to restore, or null when there is
    /// nothing to recover. A snapshot without a marker came from a clean exit and
    /// is not offered; one from a process still running belongs to that instance.
    /// </summary>
    public AutosaveInfo? FindRecoverable()
    {
        if (!File.Exists(MarkerPath) || !File.Exists(SnapshotPath)) return null;

        var metadata = ReadMetadata();
        var savedAt = metadata?.SavedAt ?? File.GetLastWriteTime(SnapshotPath);

        if (DateTime.Now - savedAt > MaxAge)
        {
            EndSession();
            return null;
        }

        int pid = metadata?.ProcessId ?? 0;
        if (pid != 0 && pid != Environment.ProcessId && IsProcessAlive(pid)) return null;

        int count = metadata?.SessionCount ?? 0;
        return new AutosaveInfo(SnapshotPath, count, savedAt, pid);
    }

    /// <summary>Loads the recovered transactions.</summary>
    public Task<List<HttpSession>> RestoreAsync(CancellationToken ct = default) =>
        SessionStore.LoadAsync(SnapshotPath, ct);

    /// <summary>Throws the snapshot away — "no thanks" on the recovery prompt.</summary>
    public void Discard() => EndSession();

    private Metadata? ReadMetadata()
    {
        try
        {
            if (!File.Exists(MetadataPath)) return null;
            return JsonSerializer.Deserialize<Metadata>(File.ReadAllText(MetadataPath));
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// True when a process with this id is still running. A second HttpSpy
    /// instance owns its own snapshot, so we must not offer to recover it out
    /// from under a live window.
    /// </summary>
    private static bool IsProcessAlive(int pid)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false; // no such process
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static void Delete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* best effort */ }
    }
}
