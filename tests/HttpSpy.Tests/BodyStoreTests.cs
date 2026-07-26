using System;
using System.IO;
using System.Linq;
using System.Text;
using HttpSpy.Core.Models;
using Xunit;

namespace HttpSpy.Tests;

/// <summary>
/// A capture holding thousands of multi-megabyte responses kept every byte live
/// for as long as the row stayed in the grid. These cover the store that moves
/// the large ones to disk while keeping the plain byte[] surface intact.
/// </summary>
public class BodyStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "httpspy-bodies-test-" + Guid.NewGuid().ToString("N"));
    private readonly BodyStore _store;

    public BodyStoreTests()
    {
        _store = new BodyStore(_dir) { Threshold = 1024 };
    }

    public void Dispose() => _store.Dispose();

    private static byte[] Payload(int size, byte fill = 0x41)
    {
        var data = new byte[size];
        Array.Fill(data, fill);
        return data;
    }

    [Fact]
    public void An_empty_body_costs_nothing()
    {
        var body = _store.Store(Array.Empty<byte>());
        Assert.False(body.IsSpilled);
        Assert.Equal(0, body.Length);
        Assert.Empty(body.Bytes);
    }

    [Fact]
    public void A_null_body_is_treated_as_empty()
    {
        Assert.Equal(0, _store.Store(null).Length);
    }

    [Fact]
    public void A_small_body_stays_in_memory()
    {
        var data = Payload(512);
        var body = _store.Store(data);

        Assert.False(body.IsSpilled);
        Assert.Same(data, body.Bytes);
        Assert.Equal(0, _store.SpilledCount);
    }

    [Fact]
    public void A_large_body_is_written_to_disk_and_read_back_intact()
    {
        var data = Payload(64 * 1024, 0x5A);
        var body = _store.Store(data);

        Assert.True(body.IsSpilled);
        Assert.Equal(data.Length, body.Length);
        Assert.Equal(data, body.Bytes);
        Assert.Equal(1, _store.SpilledCount);
        Assert.Equal(data.Length, _store.SpilledBytes);
    }

    [Fact]
    public void The_length_of_a_spilled_body_is_available_without_reading_the_file()
    {
        var body = _store.Store(Payload(8192));
        foreach (var file in Directory.EnumerateFiles(_dir)) File.Delete(file);

        // The grid asks for sizes constantly; that must not touch the disk.
        Assert.Equal(8192, body.Length);
    }

    [Fact]
    public void Spilling_can_be_turned_off()
    {
        _store.Enabled = false;
        Assert.False(_store.Store(Payload(64 * 1024)).IsSpilled);
    }

    [Fact]
    public void A_zero_threshold_disables_spilling()
    {
        _store.Threshold = 0;
        Assert.False(_store.Store(Payload(1024 * 1024)).IsSpilled);
    }

    [Fact]
    public void The_threshold_is_the_boundary()
    {
        Assert.False(_store.Store(Payload(1023)).IsSpilled);
        Assert.True(_store.Store(Payload(1024)).IsSpilled);
    }

    [Fact]
    public void Repeated_reads_return_equal_content()
    {
        var data = Payload(4096, 0x7F);
        var body = _store.Store(data);

        Assert.Equal(data, body.Bytes);
        Assert.Equal(data, body.Bytes);
        Assert.Equal(data, body.Bytes);
    }

    [Fact]
    public void Identity_of_a_spilled_body_is_not_the_payload()
    {
        // The cache key must not be the bytes, or caching would pin exactly what
        // spilling exists to release.
        var body = _store.Store(Payload(4096));
        Assert.NotNull(body.Identity);
        Assert.IsNotType<byte[]>(body.Identity);
    }

    [Fact]
    public void Identity_is_stable_across_reads()
    {
        var body = _store.Store(Payload(4096));
        Assert.Same(body.Identity, body.Identity);
    }

    [Fact]
    public void A_missing_spill_file_yields_an_empty_body_rather_than_a_crash()
    {
        var body = _store.Store(Payload(4096));
        _ = body.Bytes; // materialise once so the weak cache may hold it
        foreach (var file in Directory.EnumerateFiles(_dir)) File.Delete(file);
        GC.Collect();
        GC.WaitForPendingFinalizers();

        // Either the weak cache still has it or the read fails softly; a drawing
        // pass through the grid must not throw either way.
        var bytes = body.Bytes;
        Assert.True(bytes.Length is 0 or 4096);
    }

    [Fact]
    public void Dispose_removes_the_spill_directory()
    {
        var store = new BodyStore(Path.Combine(Path.GetTempPath(), "httpspy-bodies-x-" + Guid.NewGuid().ToString("N")))
        {
            Threshold = 16,
        };
        store.Store(Payload(1024));
        Assert.True(Directory.Exists(store.Directory));

        store.Dispose();
        Assert.False(Directory.Exists(store.Directory));
    }

    [Fact]
    public void No_directory_is_created_when_nothing_spills()
    {
        var store = new BodyStore(Path.Combine(Path.GetTempPath(), "httpspy-bodies-y-" + Guid.NewGuid().ToString("N")));
        store.Store(Payload(64));
        Assert.False(Directory.Exists(store.Directory));
        store.Dispose();
    }

    // ---- Integration with HttpSession ----------------------------------------

    [Fact]
    public void A_session_body_round_trips_through_the_shared_store()
    {
        var session = new HttpSession();
        var data = Encoding.UTF8.GetBytes("{\"hello\":\"world\"}");

        session.ResponseBody = data;

        Assert.Equal(data, session.ResponseBody);
        Assert.Equal(data.Length, session.ResponseBodySize);
        Assert.False(session.BodySpilled); // well under the shared threshold
    }

    [Fact]
    public void Body_text_still_decodes_after_a_round_trip()
    {
        var session = new HttpSession();
        session.ResponseHeaders.Add("Content-Type", "application/json; charset=utf-8");
        session.ResponseBody = Encoding.UTF8.GetBytes("{\"ok\":true,\"note\":\"привет\"}");

        Assert.Contains("привет", session.ResponseBodyText);
    }

    [Fact]
    public void Body_text_cache_still_invalidates_when_the_body_is_replaced()
    {
        var session = new HttpSession();
        session.ResponseBody = Encoding.UTF8.GetBytes("first");
        Assert.Equal("first", session.ResponseBodyText);

        session.ResponseBody = Encoding.UTF8.GetBytes("second");
        Assert.Equal("second", session.ResponseBodyText);
    }

    [Fact]
    public void Body_classification_still_works_and_reflects_a_replacement()
    {
        // No Content-Type, so the classifier sniffs the bytes — which is what
        // makes this a real test that the cache is keyed to the body.
        var session = new HttpSession();
        session.ResponseBody = Encoding.UTF8.GetBytes("{\"a\":1}");
        Assert.Equal(BodyContentType.Json, session.ResponseBodyKind);

        session.ResponseBody = new byte[] { 0x00, 0x01, 0x02, 0xFF, 0xFE, 0x00 };
        Assert.Equal(BodyContentType.Binary, session.ResponseBodyKind);
    }

    [Fact]
    public void CleanOrphans_leaves_a_live_directory_alone()
    {
        var mine = new BodyStore(Path.Combine(Path.GetTempPath(),
            $"httpspy-bodies-{Environment.ProcessId}-{Guid.NewGuid():N}")) { Threshold = 16 };
        try
        {
            mine.Store(Payload(64));
            BodyStore.CleanOrphans();
            Assert.True(Directory.Exists(mine.Directory), "the running process's own spill directory was deleted");
        }
        finally { mine.Dispose(); }
    }

    [Fact]
    public void CleanOrphans_removes_a_directory_whose_process_is_gone()
    {
        // int.MaxValue is above every platform's pid ceiling, so this directory
        // can only belong to a process that no longer exists.
        var orphan = Path.Combine(Path.GetTempPath(), $"httpspy-bodies-{int.MaxValue}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(orphan);
        File.WriteAllBytes(Path.Combine(orphan, "stale.body"), Payload(32));

        BodyStore.CleanOrphans();

        Assert.False(Directory.Exists(orphan));
    }
}
