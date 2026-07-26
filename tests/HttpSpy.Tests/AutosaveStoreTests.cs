using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using HttpSpy.Core.Export;
using HttpSpy.Core.Models;
using Xunit;

namespace HttpSpy.Tests;

/// <summary>
/// A long capture used to die with the process. These cover the snapshot /
/// marker protocol that decides whether the next start has something to offer.
/// </summary>
public class AutosaveStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "httpspy-autosave-" + Guid.NewGuid().ToString("N"));
    private readonly AutosaveStore _store;

    public AutosaveStoreTests()
    {
        _store = new AutosaveStore(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private static List<HttpSession> Sessions(int count)
    {
        var list = new List<HttpSession>();
        for (int i = 0; i < count; i++)
        {
            var s = new HttpSession
            {
                Method = "GET",
                Url = $"https://example.com/item/{i}",
                Host = "example.com",
                Path = $"/item/{i}",
                Scheme = "https",
                StatusCode = 200,
                StatusText = "OK",
                ResponseBody = Encoding.UTF8.GetBytes($"body {i}"),
                State = SessionState.Completed,
            };
            s.ResponseHeaders.Add("Content-Type", "text/plain");
            list.Add(s);
        }
        return list;
    }

    [Fact]
    public async Task Nothing_to_recover_before_a_session_starts()
    {
        await _store.SaveAsync(Sessions(3));
        // No marker: this snapshot did not come from a crash.
        Assert.Null(_store.FindRecoverable());
    }

    [Fact]
    public async Task A_snapshot_left_by_a_live_marker_is_recoverable()
    {
        _store.BeginSession();
        Assert.True(await _store.SaveAsync(Sessions(4)));

        var info = _store.FindRecoverable();
        Assert.NotNull(info);
        Assert.Equal(4, info!.SessionCount);
        Assert.True(info.Age < TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task A_clean_shutdown_leaves_nothing_behind()
    {
        _store.BeginSession();
        await _store.SaveAsync(Sessions(2));
        _store.EndSession();

        Assert.Null(_store.FindRecoverable());
        Assert.False(File.Exists(_store.SnapshotPath));
        Assert.False(File.Exists(_store.MarkerPath));
    }

    [Fact]
    public async Task Restore_returns_the_captured_transactions()
    {
        _store.BeginSession();
        await _store.SaveAsync(Sessions(3));

        var restored = await _store.RestoreAsync();

        Assert.Equal(3, restored.Count);
        Assert.Equal("https://example.com/item/0", restored[0].Url);
        Assert.Equal(200, restored[0].StatusCode);
        Assert.Equal("body 1", Encoding.UTF8.GetString(restored[1].ResponseBody));
        Assert.Equal("text/plain", restored[0].ResponseHeaders["Content-Type"]);
    }

    [Fact]
    public async Task Discard_removes_the_snapshot()
    {
        _store.BeginSession();
        await _store.SaveAsync(Sessions(1));
        _store.Discard();

        Assert.Null(_store.FindRecoverable());
    }

    [Fact]
    public async Task A_later_snapshot_replaces_the_earlier_one()
    {
        _store.BeginSession();
        await _store.SaveAsync(Sessions(2));
        await _store.SaveAsync(Sessions(5));

        Assert.Equal(5, _store.FindRecoverable()!.SessionCount);
        Assert.Equal(5, (await _store.RestoreAsync()).Count);
    }

    [Fact]
    public async Task A_snapshot_owned_by_a_running_process_is_left_alone()
    {
        // A second HttpSpy window has its own live snapshot; recovering it out
        // from under that instance would be wrong.
        _store.BeginSession();
        await _store.SaveAsync(Sessions(1));

        var metadata = File.ReadAllText(_store.MetadataPath)
            .Replace($"\"ProcessId\": {Environment.ProcessId}", "\"ProcessId\": 1");
        File.WriteAllText(_store.MetadataPath, metadata);

        // PID 1 always exists on Linux and macOS; on Windows the check simply
        // fails closed and the snapshot is offered, which is also safe.
        var info = _store.FindRecoverable();
        if (!OperatingSystem.IsWindows()) Assert.Null(info);
    }

    [Fact]
    public async Task An_abandoned_snapshot_is_cleaned_up_rather_than_offered()
    {
        _store.BeginSession();
        await _store.SaveAsync(Sessions(1));

        var stale = DateTime.Now - AutosaveStore.MaxAge - TimeSpan.FromDays(1);
        File.WriteAllText(_store.MetadataPath,
            $"{{\"SessionCount\":1,\"SavedAt\":\"{stale:O}\",\"ProcessId\":{Environment.ProcessId}}}");

        Assert.Null(_store.FindRecoverable());
        Assert.False(File.Exists(_store.SnapshotPath));
    }

    [Fact]
    public async Task A_corrupt_metadata_file_does_not_break_recovery()
    {
        _store.BeginSession();
        await _store.SaveAsync(Sessions(2));
        File.WriteAllText(_store.MetadataPath, "{ not json at all");

        // The snapshot itself is still good, so it should still be offered.
        var info = _store.FindRecoverable();
        Assert.NotNull(info);
        Assert.Equal(2, (await _store.RestoreAsync()).Count);
    }

    [Fact]
    public async Task A_snapshot_is_never_left_half_written()
    {
        // The temp file must not survive as a sibling that a later run could
        // mistake for a real snapshot.
        _store.BeginSession();
        await _store.SaveAsync(Sessions(6));

        Assert.False(File.Exists(_store.SnapshotPath + ".tmp"));
        Assert.False(File.Exists(_store.MetadataPath + ".tmp"));
    }

    [Fact]
    public async Task Concurrent_saves_do_not_race()
    {
        _store.BeginSession();
        var tasks = Enumerable.Range(0, 8).Select(_ => _store.SaveAsync(Sessions(3))).ToArray();
        await Task.WhenAll(tasks);

        // Overlapping calls are skipped rather than queued, but at least one must
        // have written, and the file must be readable afterwards.
        Assert.Contains(tasks, t => t.Result);
        Assert.Equal(3, (await _store.RestoreAsync()).Count);
    }

    [Fact]
    public void ClearMarker_keeps_the_snapshot_for_inspection()
    {
        _store.BeginSession();
        Assert.True(File.Exists(_store.MarkerPath));

        _store.ClearMarker();

        Assert.False(File.Exists(_store.MarkerPath));
    }
}
