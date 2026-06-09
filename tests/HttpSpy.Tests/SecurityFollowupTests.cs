using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using HttpSpy.Core.Export;
using HttpSpy.Core.Models;
using HttpSpy.Core.Proxy;
using HttpSpy.Core.Rules;
using Xunit;

namespace HttpSpy.Tests;

/// <summary>
/// Regression tests for the second-round audit follow-ups: WebSocket frame
/// buffering/oversize cap, Rule compiled-regex cache thread-safety, and the
/// .hspy load round-trip (gzip-bomb bound).
/// </summary>
public class SecurityFollowupTests
{
    // ---- WebSocket frame parser: correctness preserved ----------------------
    [Fact]
    public void WebSocketParser_Parses_Small_Unmasked_Frame()
    {
        var payload = Encoding.UTF8.GetBytes("hello");
        var frameBytes = new List<byte> { 0x81, (byte)payload.Length }; // FIN + Text, no mask
        frameBytes.AddRange(payload);

        var parser = new WebSocketFrameParser(MessageDirection.ServerToClient);
        var frames = parser.Feed(frameBytes.ToArray(), frameBytes.Count).ToList();

        var frame = Assert.Single(frames);
        Assert.Equal(WebSocketOpcode.Text, frame.Opcode);
        Assert.False(frame.Truncated);
        Assert.Equal("hello", Encoding.UTF8.GetString(frame.Payload));
    }

    [Fact]
    public void WebSocketParser_Parses_Frame_Fed_One_Byte_At_A_Time()
    {
        var payload = Encoding.UTF8.GetBytes("streamed");
        var frameBytes = new List<byte> { 0x81, (byte)payload.Length };
        frameBytes.AddRange(payload);

        var parser = new WebSocketFrameParser(MessageDirection.ClientToServer);
        var collected = new List<WebSocketFrame>();
        foreach (var b in frameBytes)
            collected.AddRange(parser.Feed(new[] { b }, 1));

        var frame = Assert.Single(collected);
        Assert.Equal("streamed", Encoding.UTF8.GetString(frame.Payload));
    }

    [Fact]
    public void WebSocketParser_Truncates_Oversized_Frame_And_Skips_Remainder()
    {
        long declared = WebSocketFrameParser.MaxCapturedFrameBytes + 1024L; // just over the cap
        var header = new List<byte> { 0x82, 127 };                          // FIN + Binary, 64-bit length
        for (int shift = 56; shift >= 0; shift -= 8)
            header.Add((byte)((declared >> shift) & 0xFF));

        // Provide only a small prefix of the payload now; the rest "arrives" later.
        var prefix = Enumerable.Repeat((byte)0xAB, 16).ToArray();
        var first = header.Concat(prefix).ToArray();

        var parser = new WebSocketFrameParser(MessageDirection.ServerToClient);
        var frames = parser.Feed(first, first.Length).ToList();

        var frame = Assert.Single(frames);
        Assert.True(frame.Truncated);
        Assert.Equal(declared, frame.DeclaredLength);
        Assert.Equal(prefix.Length, frame.Payload.Length); // only the buffered prefix retained

        // The huge remaining payload must be discarded without buffering, and a
        // following normal frame must parse correctly once the skip is consumed.
        long remaining = declared - prefix.Length;
        var filler = new byte[remaining];
        // Feed the discarded remainder in chunks.
        int chunk = 1 << 20;
        for (long sent = 0; sent < remaining; sent += chunk)
        {
            int len = (int)System.Math.Min(chunk, remaining - sent);
            var produced = parser.Feed(filler, len).ToList();
            Assert.Empty(produced); // still skipping
        }

        var nextPayload = Encoding.UTF8.GetBytes("ok");
        var nextFrame = new List<byte> { 0x81, (byte)nextPayload.Length };
        nextFrame.AddRange(nextPayload);
        var after = parser.Feed(nextFrame.ToArray(), nextFrame.Count).ToList();
        var parsed = Assert.Single(after);
        Assert.False(parsed.Truncated);
        Assert.Equal("ok", Encoding.UTF8.GetString(parsed.Payload));
    }

    // ---- Rule compiled-regex cache thread-safety ----------------------------
    [Fact]
    public async Task Rule_UrlMatches_Is_Thread_Safe_Under_Concurrency()
    {
        var rule = new Rule
        {
            UrlMatchMode = MatchMode.Regex,
            UrlPattern = "^https?://example\\.com/.*$",
        };

        // Many concurrent matchers exercise the shared compiled-regex cache; this
        // must not throw and must always return the correct result.
        var tasks = Enumerable.Range(0, 200).Select(i => Task.Run(() =>
        {
            bool hit = rule.UrlMatches("https://example.com/path");
            bool miss = rule.UrlMatches("https://other.test/path");
            return hit && !miss;
        }));

        bool[] results = await Task.WhenAll(tasks);
        Assert.All(results, Assert.True);
    }

    // ---- .hspy load round-trip (gzip-bomb bound did not break loading) ------
    [Fact]
    public async Task SessionStore_SaveLoad_RoundTrips()
    {
        string path = Path.Combine(Path.GetTempPath(), "httpspy_store_" + System.Guid.NewGuid().ToString("N") + ".hspy");
        try
        {
            var original = new HttpSession
            {
                Index = 7,
                Method = "POST",
                Url = "https://example.com/api",
                Scheme = "https",
                Host = "example.com",
                Path = "/api",
                StatusCode = 201,
                StatusText = "Created",
            };
            original.RequestHeaders.Add("Content-Type", "application/json");
            original.RequestBody = Encoding.UTF8.GetBytes("{\"k\":\"v\"}");

            await SessionStore.SaveAsync(path, new[] { original });
            var loaded = await SessionStore.LoadAsync(path);

            var s = Assert.Single(loaded);
            Assert.Equal("POST", s.Method);
            Assert.Equal("https://example.com/api", s.Url);
            Assert.Equal(201, s.StatusCode);
            Assert.Equal("{\"k\":\"v\"}", Encoding.UTF8.GetString(s.RequestBody));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
