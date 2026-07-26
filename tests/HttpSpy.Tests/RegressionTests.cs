using System.IO.Compression;
using System.Text;
using HttpSpy.Core.Export;
using HttpSpy.Core.Models;
using HttpSpy.Core.Proxy;
using HttpSpy.Core.Rules;
using HttpSpy.Core.Util;
using Xunit;

namespace HttpSpy.Tests;

/// <summary>
/// Regression coverage for the defects fixed in the audit pass. Each test names
/// the behaviour that was wrong, so a future change that reintroduces it fails
/// with an explanation rather than a bare assertion.
/// </summary>
public class HttpWireDecompressionTests
{
    private static byte[] GzipOf(string text)
    {
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Fastest, leaveOpen: true))
            gz.Write(Encoding.UTF8.GetBytes(text));
        return ms.ToArray();
    }

    private static byte[] ZlibOf(string text)
    {
        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Fastest, leaveOpen: true))
            z.Write(Encoding.UTF8.GetBytes(text));
        return ms.ToArray();
    }

    private static byte[] RawDeflateOf(string text)
    {
        using var ms = new MemoryStream();
        using (var d = new DeflateStream(ms, CompressionLevel.Fastest, leaveOpen: true))
            d.Write(Encoding.UTF8.GetBytes(text));
        return ms.ToArray();
    }

    [Fact]
    public void Gzip_is_decoded()
    {
        var decoded = HttpWire.Decompress(GzipOf("hello gzip"), "gzip");
        Assert.Equal("hello gzip", Encoding.UTF8.GetString(decoded));
    }

    [Fact]
    public void Deflate_accepts_the_zlib_wrapped_form_servers_actually_send()
    {
        // Previously only raw DEFLATE was attempted, so zlib-wrapped bodies (the
        // common case) silently stayed compressed and rendered as binary noise.
        var decoded = HttpWire.Decompress(ZlibOf("hello zlib"), "deflate");
        Assert.Equal("hello zlib", Encoding.UTF8.GetString(decoded));
    }

    [Fact]
    public void Deflate_still_accepts_the_raw_form()
    {
        var decoded = HttpWire.Decompress(RawDeflateOf("hello raw"), "deflate");
        Assert.Equal("hello raw", Encoding.UTF8.GetString(decoded));
    }

    [Theory]
    [InlineData(" gzip ")]
    [InlineData("GZIP")]
    [InlineData("x-gzip")]
    public void Content_encoding_matching_tolerates_whitespace_and_casing(string encoding)
    {
        var decoded = HttpWire.Decompress(GzipOf("padded"), encoding);
        Assert.Equal("padded", Encoding.UTF8.GetString(decoded));
    }

    [Fact]
    public void Stacked_encodings_are_undone_in_reverse_order()
    {
        // "identity, gzip" means gzip was applied last, so it is undone first.
        var decoded = HttpWire.Decompress(GzipOf("stacked"), "identity, gzip");
        Assert.Equal("stacked", Encoding.UTF8.GetString(decoded));
    }

    [Fact]
    public void Unknown_encoding_returns_the_original_instance()
    {
        var body = Encoding.UTF8.GetBytes("untouched");
        Assert.Same(body, HttpWire.Decompress(body, "exotic-codec"));
    }

    [Fact]
    public void Corrupt_body_is_returned_unchanged_rather_than_throwing()
    {
        var garbage = new byte[] { 0x1F, 0x8B, 0x08, 0x00, 0xAA, 0xBB, 0xCC };
        Assert.Same(garbage, HttpWire.Decompress(garbage, "gzip"));
    }
}

public class StreamReaderExTests
{
    private static StreamReaderEx ReaderOver(string text) =>
        new(new MemoryStream(Encoding.UTF8.GetBytes(text)));

    [Fact]
    public async Task ReadExact_caps_retained_bytes_but_still_consumes_the_wire()
    {
        var reader = ReaderOver("0123456789TAIL");

        var head = await reader.ReadExactAsync(10, default, maxRetained: 4);

        Assert.Equal("0123", Encoding.UTF8.GetString(head));
        Assert.True(reader.LastReadTruncated);

        // The framing must stay correct: the remaining bytes are what follow the
        // capped region, not the bytes that were dropped.
        var rest = await reader.ReadToEndAsync(default);
        Assert.Equal("TAIL", Encoding.UTF8.GetString(rest));
    }

    [Fact]
    public async Task Mark_measures_one_message_not_the_whole_connection()
    {
        var reader = ReaderOver("AAAABBBB");

        await reader.ReadExactAsync(4, default);
        reader.Mark();
        await reader.ReadExactAsync(4, default);

        // BytesSinceMark is what per-request accounting uses; TotalBytesRead is
        // cumulative and would over-report every keep-alive request after the first.
        Assert.Equal(4, reader.BytesSinceMark);
        Assert.Equal(8, reader.TotalBytesRead);
    }

    [Fact]
    public async Task Unterminated_line_is_rejected_instead_of_growing_without_bound()
    {
        var reader = ReaderOver(new string('x', StreamReaderEx.MaxLineLength + 64));
        await Assert.ThrowsAsync<InvalidDataException>(() => reader.ReadLineAsync(default));
    }

    [Fact]
    public async Task ReadToEnd_caps_retained_bytes()
    {
        var reader = ReaderOver("abcdefghij");
        var data = await reader.ReadToEndAsync(default, maxRetained: 3);

        Assert.Equal("abc", Encoding.UTF8.GetString(data));
        Assert.True(reader.LastReadTruncated);
    }
}

public class HostParsingTests
{
    [Theory]
    [InlineData("example.com", 443, "example.com", 443)]
    [InlineData("example.com:8443", 443, "example.com", 8443)]
    [InlineData("[::1]:8443", 443, "[::1]", 8443)]
    [InlineData("[2001:db8::1]", 443, "[2001:db8::1]", 443)]
    public void Authority_splits_correctly(string input, int fallback, string host, int port)
    {
        var (h, p) = ProxyServer.SplitHostPort(input, fallback);
        Assert.Equal(host, h);
        Assert.Equal(port, p);
    }

    [Fact]
    public void Bare_ipv6_literal_is_not_mistaken_for_host_and_port()
    {
        // "::1" used to parse as host "::" on port 1.
        var (host, port) = ProxyServer.SplitHostPort("::1", 443);
        Assert.Equal("::1", host);
        Assert.Equal(443, port);
    }

    [Theory]
    [InlineData("http", "example.com", 80, "/a?b=c", "http://example.com/a?b=c")]
    [InlineData("https", "example.com", 443, "/", "https://example.com/")]
    [InlineData("http", "example.com", 8080, "/x", "http://example.com:8080/x")]
    public void Absolute_form_target_is_built_for_chained_proxies(
        string scheme, string host, int port, string origin, string expected)
    {
        Assert.Equal(expected, ProxyServer.BuildAbsoluteTarget(scheme, host, port, origin));
    }

    [Fact]
    public void Absolute_form_target_leaves_an_already_absolute_uri_alone()
    {
        Assert.Equal("http://a.test/p",
            ProxyServer.BuildAbsoluteTarget("http", "b.test", 80, "http://a.test/p"));
    }
}

public class RuleSafetyTests
{
    [Fact]
    public void Invalid_regex_pattern_is_reported_instead_of_throwing()
    {
        var rule = new Rule { UrlMatchMode = MatchMode.Regex, UrlPattern = "([unclosed" };

        // Evaluating a bad rule used to throw straight out of the proxy pipeline.
        Assert.False(rule.UrlMatches("http://example.com/"));
        Assert.NotNull(rule.ValidatePattern());
        Assert.NotNull(rule.PatternError);
    }

    [Fact]
    public void Valid_pattern_reports_no_error()
    {
        var rule = new Rule { UrlMatchMode = MatchMode.Regex, UrlPattern = @"^https?://example\.com/" };

        Assert.True(rule.UrlMatches("http://example.com/x"));
        Assert.Null(rule.ValidatePattern());
    }

    [Fact]
    public void Switching_pattern_recompiles_rather_than_reusing_the_stale_regex()
    {
        var rule = new Rule { UrlMatchMode = MatchMode.Regex, UrlPattern = "alpha" };
        Assert.True(rule.UrlMatches("see alpha here"));

        rule.UrlPattern = "beta";
        Assert.False(rule.UrlMatches("see alpha here"));
        Assert.True(rule.UrlMatches("see beta here"));
    }

    [Fact]
    public void Hit_counting_is_atomic_under_concurrency()
    {
        var rule = new Rule();
        Parallel.For(0, 2000, _ => rule.RecordHit());
        Assert.Equal(2000, rule.HitCount);
    }

    [Fact]
    public void Clone_resets_the_hit_count()
    {
        var rule = new Rule();
        rule.RecordHit();
        Assert.Equal(0, rule.Clone().HitCount);
    }

    [Fact]
    public void Map_local_pointing_at_a_missing_file_yields_404_not_an_exception()
    {
        var engine = new RuleEngine();
        engine.SetRules(new[]
        {
            new Rule
            {
                Action = RuleAction.MapLocal,
                UrlMatchMode = MatchMode.Contains,
                UrlPattern = "example.com",
                MapLocalPath = Path.Combine(Path.GetTempPath(), "definitely-not-here-" + Guid.NewGuid() + ".json"),
            }
        });

        var decision = engine.EvaluateRequest(new HttpSession { Host = "example.com", Url = "http://example.com/x" });

        Assert.NotNull(decision.AutoReply);
        Assert.Equal(404, decision.AutoReply!.Status);
    }

    [Fact]
    public void Map_local_serves_the_file_with_a_sniffed_content_type()
    {
        var path = Path.Combine(Path.GetTempPath(), $"httpspy-map-{Guid.NewGuid()}.json");
        File.WriteAllText(path, """{"mocked":true}""");
        try
        {
            var engine = new RuleEngine();
            engine.SetRules(new[]
            {
                new Rule
                {
                    Action = RuleAction.MapLocal,
                    UrlMatchMode = MatchMode.Contains,
                    UrlPattern = "example.com",
                    MapLocalPath = path,
                }
            });

            var decision = engine.EvaluateRequest(
                new HttpSession { Host = "example.com", Url = "http://example.com/x" });

            Assert.Equal(200, decision.AutoReply!.Status);
            Assert.Contains("application/json", decision.AutoReply.ContentType);
            Assert.Contains("mocked", Encoding.UTF8.GetString(decision.AutoReply.Body));
        }
        finally { File.Delete(path); }
    }
}

public class CodeGeneratorTests
{
    private static HttpSession SessionWith(string body, string contentType = "application/json")
    {
        var s = new HttpSession
        {
            Method = "POST",
            Scheme = "https",
            Host = "api.example.com",
            Path = "/v1/items",
            Url = "https://api.example.com/v1/items",
            RequestBody = Encoding.UTF8.GetBytes(body),
        };
        s.RequestHeaders.Add("Content-Type", contentType);
        s.RequestHeaders.Add("Content-Length", "999");   // deliberately wrong
        s.RequestHeaders.Add("Host", "api.example.com");
        return s;
    }

    [Fact]
    public void CSharp_verbatim_literal_doubles_quotes_and_leaves_backslashes_alone()
    {
        // The old generator escaped C-style inside an @"" literal, producing code
        // that either failed to compile or changed the payload.
        var code = CodeGenerator.Generate(SessionWith("""{"path":"C:\\tmp","q":"say \"hi\""}"""),
            CodeGenerator.Language.CSharp);

        Assert.Contains("@\"", code);
        Assert.DoesNotContain("\\\\\\\\", code);           // no doubled-up backslash escaping
        Assert.Contains("\"\"", code);                      // quotes doubled, verbatim style
    }

    [Fact]
    public void Generators_drop_wire_framing_headers()
    {
        foreach (var language in new[]
                 {
                     CodeGenerator.Language.Curl, CodeGenerator.Language.CSharp,
                     CodeGenerator.Language.Python, CodeGenerator.Language.JavaScript,
                     CodeGenerator.Language.PowerShell,
                 })
        {
            var code = CodeGenerator.Generate(SessionWith("{}"), language);
            Assert.DoesNotContain("Content-Length", code);
        }
    }

    [Fact]
    public void Curl_single_quoting_survives_an_embedded_quote()
    {
        var code = CodeGenerator.Generate(SessionWith("it's fine"), CodeGenerator.Language.Curl);
        Assert.Contains(@"'\''", code);
    }

    [Fact]
    public void Javascript_body_is_a_quoted_string_not_a_template_literal()
    {
        // A backtick or ${...} in the body would break a template literal.
        var code = CodeGenerator.Generate(SessionWith("`${danger}`"), CodeGenerator.Language.JavaScript);
        Assert.DoesNotContain("body: `", code);
        Assert.Contains("body: \"", code);
    }

    [Fact]
    public void Python_body_is_escaped_rather_than_triple_quoted()
    {
        var code = CodeGenerator.Generate(SessionWith("\"\"\"quotes\"\"\""), CodeGenerator.Language.Python);
        Assert.DoesNotContain("\"\"\"", code);
    }

    [Fact]
    public void Head_requests_use_the_curl_head_flag()
    {
        var s = SessionWith("");
        s.Method = "HEAD";
        s.RequestBody = Array.Empty<byte>();

        var code = CodeGenerator.Generate(s, CodeGenerator.Language.Curl);
        Assert.Contains("curl -I", code);
    }

    [Fact]
    public void Non_standard_method_round_trips_in_csharp()
    {
        var s = SessionWith("{}");
        s.Method = "PURGE";

        var code = CodeGenerator.Generate(s, CodeGenerator.Language.CSharp);
        Assert.Contains("new HttpMethod(\"PURGE\")", code);
    }

    [Fact]
    public void Every_language_name_maps_to_an_enum_member()
    {
        Assert.Equal(Enum.GetValues<CodeGenerator.Language>().Length, CodeGenerator.LanguageNames.Length);
    }
}

public class SessionStoreTests
{
    private static HttpSession BuildRichSession()
    {
        var s = new HttpSession
        {
            Method = "GET",
            Scheme = "https",
            Host = "example.com",
            RemotePort = 443,
            Path = "/data",
            QueryString = "a=1",
            Url = "https://example.com/data?a=1",
            StatusCode = 200,
            StatusText = "OK",
            IsTls = true,
            Kind = SessionKind.WebSocket,
            ResponseBody = Encoding.UTF8.GetBytes("body"),
            RequestBody = Encoding.UTF8.GetBytes("req"),
            Bookmarked = true,
            Comment = "note",
            EncodedBodySize = 2,
            OriginalContentEncoding = "gzip",
            ResponseBodyTruncated = true,
        };
        s.RequestHeaders.Add("Accept", "*/*");
        s.ResponseHeaders.Add("Content-Type", "text/plain");
        s.Timings.ConnectMs = 11;
        s.Timings.WaitMs = 22;
        s.Timings.ReceiveMs = 33;
        s.Timings.TotalMs = 66;
        s.AddWebSocketFrame(new WebSocketFrame
        {
            Index = 1,
            Opcode = WebSocketOpcode.Text,
            Direction = MessageDirection.ClientToServer,
            Payload = Encoding.UTF8.GetBytes("ws-hello"),
            DeclaredLength = 8,
        });
        s.AddServerSentEvent(new ServerSentEvent { Index = 1, EventName = "tick", Data = "1" });
        return s;
    }

    [Fact]
    public async Task Round_trip_preserves_timings_streams_and_flags()
    {
        var path = Path.Combine(Path.GetTempPath(), $"httpspy-{Guid.NewGuid()}.hspy");
        try
        {
            await SessionStore.SaveAsync(path, new[] { BuildRichSession() });
            var loaded = await SessionStore.LoadAsync(path);

            var s = Assert.Single(loaded);

            // The v1 format dropped all of these on save.
            Assert.Equal(11, s.Timings.ConnectMs);
            Assert.Equal(22, s.Timings.WaitMs);
            Assert.Equal(33, s.Timings.ReceiveMs);
            Assert.Equal(66, s.Timings.TotalMs);

            var frame = Assert.Single(s.WebSocketFrames);
            Assert.Equal("ws-hello", Encoding.UTF8.GetString(frame.Payload));

            var evt = Assert.Single(s.ServerSentEvents);
            Assert.Equal("tick", evt.EventName);

            Assert.True(s.Bookmarked);
            Assert.Equal("note", s.Comment);
            Assert.Equal(443, s.RemotePort);
            Assert.Equal("gzip", s.OriginalContentEncoding);
            Assert.True(s.ResponseBodyTruncated);
            Assert.Equal("body", Encoding.UTF8.GetString(s.ResponseBody));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Saving_over_an_existing_capture_replaces_it_atomically()
    {
        var path = Path.Combine(Path.GetTempPath(), $"httpspy-{Guid.NewGuid()}.hspy");
        try
        {
            await SessionStore.SaveAsync(path, new[] { BuildRichSession() });
            await SessionStore.SaveAsync(path, new[] { BuildRichSession(), BuildRichSession() });

            Assert.Equal(2, (await SessionStore.LoadAsync(path)).Count);
            Assert.False(File.Exists(path + ".tmp"));
        }
        finally { File.Delete(path); }
    }
}

public class HarExporterTests
{
    [Fact]
    public void Binary_bodies_are_base64_encoded_and_declared_as_such()
    {
        var s = new HttpSession
        {
            Method = "GET",
            Host = "cdn.example.com",
            Path = "/logo.png",
            Url = "https://cdn.example.com/logo.png",
            StatusCode = 200,
            // A PNG magic number plus bytes that are not valid UTF-8.
            ResponseBody = new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A, 0xFF, 0xFE },
        };
        s.ResponseHeaders.Add("Content-Type", "image/png");

        var har = HarExporter.Export(new[] { s });

        // Emitting these bytes as UTF-8 "text" silently corrupted every image.
        Assert.Contains("\"encoding\": \"base64\"", har);
        Assert.Contains(Convert.ToBase64String(s.ResponseBody), har);
    }

    [Fact]
    public void Text_bodies_stay_readable()
    {
        var s = new HttpSession
        {
            Method = "GET",
            Host = "example.com",
            Path = "/",
            Url = "http://example.com/",
            StatusCode = 200,
            ResponseBody = Encoding.UTF8.GetBytes("{\"ok\":true}"),
        };
        s.ResponseHeaders.Add("Content-Type", "application/json");

        var har = HarExporter.Export(new[] { s });

        Assert.DoesNotContain("base64", har);
        Assert.Contains("ok", har);
    }

    [Fact]
    public void Log_carries_the_pages_array_har_readers_expect()
    {
        Assert.Contains("\"pages\"", HarExporter.Export(Array.Empty<HttpSession>()));
    }
}

public class BodyDecodingTests
{
    [Fact]
    public void Charset_from_content_type_is_honoured()
    {
        var s = new HttpSession { ResponseBody = new byte[] { 0xE9, 0x74, 0xE9 } }; // "été" in Latin-1
        s.ResponseHeaders.Add("Content-Type", "text/plain; charset=iso-8859-1");

        Assert.Equal("été", s.ResponseBodyText);
    }

    [Fact]
    public void Unknown_charset_falls_back_to_utf8_without_throwing()
    {
        var s = new HttpSession { ResponseBody = Encoding.UTF8.GetBytes("plain") };
        s.ResponseHeaders.Add("Content-Type", "text/plain; charset=not-a-real-charset");

        Assert.Equal("plain", s.ResponseBodyText);
    }

    [Fact]
    public void Invalid_utf8_becomes_replacement_characters_rather_than_throwing()
    {
        var s = new HttpSession { ResponseBody = new byte[] { 0xC3, 0x28 } };
        s.ResponseHeaders.Add("Content-Type", "text/plain; charset=utf-8");

        Assert.NotEmpty(s.ResponseBodyText);
    }

    [Fact]
    public void Decoded_text_is_cached_until_the_body_changes()
    {
        var s = new HttpSession { ResponseBody = Encoding.UTF8.GetBytes("first") };
        Assert.Same(s.ResponseBodyText, s.ResponseBodyText);

        s.ResponseBody = Encoding.UTF8.GetBytes("second");
        Assert.Equal("second", s.ResponseBodyText);
    }
}

public class HexDumpTests
{
    [Fact]
    public void Large_bodies_are_capped_so_the_ui_thread_is_not_blocked()
    {
        var data = new byte[BodyFormatter.MaxHexDumpBytes + 4096];
        var dump = BodyFormatter.HexDump(data);

        Assert.Contains("not shown", dump);
        // Roughly 4 characters of output per byte; assert the cap actually bounds it.
        Assert.True(dump.Length < (BodyFormatter.MaxHexDumpBytes + 1024) * 5,
            $"hex dump was {dump.Length} characters, which suggests the cap did not apply");
    }

    [Fact]
    public void Small_bodies_dump_completely()
    {
        var dump = BodyFormatter.HexDump(Encoding.ASCII.GetBytes("AB"));

        Assert.Contains("41 42", dump);
        Assert.Contains("AB", dump);
        Assert.DoesNotContain("not shown", dump);
    }
}

public class WebSocketFrameParserTests
{
    /// <summary>Builds an unmasked server-to-client text frame.</summary>
    private static byte[] TextFrame(string payload)
    {
        var body = Encoding.UTF8.GetBytes(payload);
        using var ms = new MemoryStream();
        ms.WriteByte(0x81); // FIN + text
        if (body.Length < 126)
        {
            ms.WriteByte((byte)body.Length);
        }
        else
        {
            ms.WriteByte(126);
            ms.WriteByte((byte)(body.Length >> 8));
            ms.WriteByte((byte)(body.Length & 0xFF));
        }
        ms.Write(body);
        return ms.ToArray();
    }

    [Fact]
    public void Frames_split_across_reads_are_reassembled()
    {
        var parser = new WebSocketFrameParser(MessageDirection.ServerToClient);
        var frame = TextFrame("hello world");

        // Feed one byte at a time — the parser must buffer until the frame completes.
        var produced = new List<WebSocketFrame>();
        foreach (var b in frame)
            produced.AddRange(parser.Feed(new[] { b }, 1));

        var only = Assert.Single(produced);
        Assert.Equal("hello world", Encoding.UTF8.GetString(only.Payload));
    }

    [Fact]
    public void Multiple_frames_in_one_read_all_surface()
    {
        var parser = new WebSocketFrameParser(MessageDirection.ClientToServer);
        var buffer = TextFrame("one").Concat(TextFrame("two")).Concat(TextFrame("three")).ToArray();

        var frames = parser.Feed(buffer, buffer.Length).ToList();

        Assert.Equal(3, frames.Count);
        Assert.Equal(new[] { "one", "two", "three" },
            frames.Select(f => Encoding.UTF8.GetString(f.Payload)));
    }

    [Fact]
    public void Oversized_payload_is_truncated_but_reported_at_its_true_length()
    {
        var parser = new WebSocketFrameParser(MessageDirection.ServerToClient, maxPayload: 8);
        var frame = TextFrame(new string('z', 300));

        var only = Assert.Single(parser.Feed(frame, frame.Length));

        Assert.True(only.Truncated);
        Assert.Equal(8, only.Payload.Length);
        Assert.Equal(300, only.DeclaredLength);
    }

    [Fact]
    public void Negative_64bit_length_is_rejected_instead_of_overflowing()
    {
        // High bit set in the 64-bit length field is illegal per RFC 6455 §5.2 and
        // used to reach `new byte[negative]`.
        var hostile = new byte[] { 0x82, 127, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF };
        var parser = new WebSocketFrameParser(MessageDirection.ClientToServer);

        Assert.Throws<InvalidDataException>(() => parser.Feed(hostile, hostile.Length).ToList());
    }

    [Fact]
    public void Masked_client_frames_are_unmasked()
    {
        var payload = Encoding.UTF8.GetBytes("secret");
        var mask = new byte[] { 0x11, 0x22, 0x33, 0x44 };
        var masked = payload.Select((b, i) => (byte)(b ^ mask[i % 4])).ToArray();

        var frame = new byte[] { 0x81, (byte)(0x80 | payload.Length) }
            .Concat(mask).Concat(masked).ToArray();

        var parser = new WebSocketFrameParser(MessageDirection.ClientToServer);
        var only = Assert.Single(parser.Feed(frame, frame.Length));

        Assert.True(only.Masked);
        Assert.Equal("secret", Encoding.UTF8.GetString(only.Payload));
    }
}

public class SessionStreamingTests
{
    [Fact]
    public void Frame_retention_cap_drops_the_oldest_entries()
    {
        var s = new HttpSession();
        for (int i = 1; i <= 10; i++)
            s.AddWebSocketFrame(new WebSocketFrame { Index = i }, cap: 4);

        Assert.Equal(4, s.WebSocketFrameCount);
        Assert.Equal(new[] { 7, 8, 9, 10 }, s.WebSocketFrames.Select(f => f.Index));
        Assert.True(s.DroppedWebSocketFrames > 0);
    }

    [Fact]
    public async Task Snapshots_are_safe_to_enumerate_while_frames_are_appended()
    {
        var s = new HttpSession();
        var stop = false;

        // The UI enumerates while proxy workers append; this used to be a plain
        // List<T> shared across threads, which threw "collection was modified".
        var writer = Task.Run(() =>
        {
            for (int i = 0; !Volatile.Read(ref stop) && i < 20_000; i++)
                s.AddWebSocketFrame(new WebSocketFrame { Index = i });
        });

        for (int i = 0; i < 500; i++)
            foreach (var _ in s.WebSocketFrames) { }

        Volatile.Write(ref stop, true);
        await writer.WaitAsync(TimeSpan.FromSeconds(30));
    }
}
