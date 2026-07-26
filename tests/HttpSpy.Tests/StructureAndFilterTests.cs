using HttpSpy.Core;
using HttpSpy.Core.Analysis;
using HttpSpy.Core.Models;
using HttpSpy.Core.Proxy;
using HttpSpy.Core.Rules;
using Xunit;

namespace HttpSpy.Tests;

/// <summary>Coverage for the host/path structure tree and the connection tree.</summary>
public class StructureTreeTests
{
    private static HttpSession S(string url, long bytes = 100, double ms = 10, int status = 200,
        long connectionId = 0, int streamId = 0, DateTime? start = null, DateTime? end = null)
    {
        var uri = new Uri(url);
        var s = new HttpSession
        {
            Method = "GET",
            Url = url,
            Scheme = uri.Scheme,
            Host = uri.Host,
            Path = uri.AbsolutePath,
            QueryString = uri.Query.TrimStart('?'),
            IsTls = uri.Scheme == "https",
            StatusCode = status,
            ResponseBody = new byte[bytes],
            BytesReceived = bytes,
            ConnectionId = connectionId,
            StreamId = streamId,
            HttpVersion = streamId > 0 ? "HTTP/2" : "HTTP/1.1",
        };
        if (start is { } st) s.StartTime = st;
        s.EndTime = end ?? s.StartTime.AddMilliseconds(ms);
        s.Timings.TotalMs = ms;
        return s;
    }

    [Fact]
    public void Hosts_become_the_top_level()
    {
        var root = StructureTreeBuilder.Build(new[]
        {
            S("https://a.test/one"), S("https://a.test/two"), S("https://b.test/"),
        });

        Assert.Equal(2, root.Children.Count);
        Assert.Contains(root.Children, c => c.Name == "a.test" && c.Kind == StructureNodeKind.Host);
    }

    [Fact]
    public void Path_segments_nest_and_totals_roll_up()
    {
        var root = StructureTreeBuilder.Build(new[]
        {
            S("https://api.test/v1/users", bytes: 100),
            S("https://api.test/v1/orders", bytes: 200),
            S("https://api.test/health", bytes: 50),
        });

        var host = Assert.Single(root.Children);
        Assert.Equal(3, host.Subtree.Requests);
        Assert.Equal(350, host.Subtree.BodyBytes);

        var v1 = host.Children.Single(c => c.Name == "v1");
        Assert.Equal(2, v1.Subtree.Requests);
        Assert.Equal(300, v1.Subtree.BodyBytes);
        // The folder itself was never requested directly.
        Assert.Equal(0, v1.Own.Requests);
    }

    [Fact]
    public void Identifier_segments_collapse_so_a_rest_api_stays_readable()
    {
        var sessions = Enumerable.Range(1, 50)
            .Select(i => S($"https://api.test/v1/users/{i}"))
            .ToArray();

        var root = StructureTreeBuilder.Build(sessions);
        var users = StructureTreeBuilder.Flatten(root).Single(n => n.Name == "users");

        // Without collapsing this would be 50 sibling leaves.
        var leaf = Assert.Single(users.Children);
        Assert.Equal("{id}", leaf.Name);
        Assert.Equal(50, leaf.Own.Requests);
    }

    [Fact]
    public void Collapsing_can_be_turned_off()
    {
        var root = StructureTreeBuilder.Build(new[]
        {
            S("https://api.test/users/1"), S("https://api.test/users/2"),
        }, collapseIdentifiers: false);

        var users = StructureTreeBuilder.Flatten(root).Single(n => n.Name == "users");
        Assert.Equal(2, users.Children.Count);
    }

    [Theory]
    [InlineData("123", "{id}")]
    // A 40-char SHA-1. Note a bare 32-hex string is a valid GUID in "N" format
    // and is therefore reported as {uuid}, which is why this uses a longer digest.
    [InlineData("da39a3ee5e6b4b0d3255bfef95601890afd80709", "{hash}")]
    [InlineData("users", "users")]
    [InlineData("v1", "v1")]
    [InlineData("v2.1", "v2.1")]
    public void Segment_collapsing_only_targets_identifier_shapes(string input, string expected)
    {
        Assert.Equal(expected, StructureTreeBuilder.CollapseSegment(input));
    }

    [Fact]
    public void A_dashless_32_hex_segment_is_recognised_as_a_uuid()
    {
        Assert.Equal("{uuid}", StructureTreeBuilder.CollapseSegment("0f8fad5bd9cb469fa16570867728950e"));
    }

    [Fact]
    public void Uuid_segments_collapse()
    {
        Assert.Equal("{uuid}", StructureTreeBuilder.CollapseSegment(Guid.NewGuid().ToString()));
    }

    [Fact]
    public void Root_path_gets_its_own_node()
    {
        var root = StructureTreeBuilder.Build(new[] { S("https://a.test/") });
        var host = Assert.Single(root.Children);
        var leaf = Assert.Single(host.Children);

        Assert.Equal("/", leaf.Name);
        Assert.Equal(StructureNodeKind.Endpoint, leaf.Kind);
    }

    [Fact]
    public void Errors_and_warnings_are_tallied_separately()
    {
        var root = StructureTreeBuilder.Build(new[]
        {
            S("https://a.test/ok", status: 200),
            S("https://a.test/missing", status: 404),
            S("https://a.test/broken", status: 500),
        });

        var host = Assert.Single(root.Children);
        Assert.Equal(1, host.Subtree.Errors);    // 5xx
        Assert.Equal(1, host.Subtree.Warnings);  // 4xx
    }

    [Fact]
    public void Children_are_ordered_by_weight()
    {
        var root = StructureTreeBuilder.Build(new[]
        {
            S("https://light.test/", bytes: 10),
            S("https://heavy.test/", bytes: 10_000),
        });

        Assert.Equal("heavy.test", root.Children[0].Name);
    }

    // ---- Connection tree -----------------------------------------------------

    [Fact]
    public void Transactions_group_by_connection()
    {
        var connections = StructureTreeBuilder.BuildConnections(new[]
        {
            S("https://a.test/1", connectionId: 7),
            S("https://a.test/2", connectionId: 7),
            S("https://a.test/3", connectionId: 8),
        });

        Assert.Equal(2, connections.Count);
        Assert.Equal(2, connections[0].Streams.Count);
        Assert.True(connections[0].IsMultiplexed);
    }

    [Fact]
    public void Sequential_reuse_is_not_reported_as_concurrency()
    {
        var t0 = new DateTime(2026, 1, 1, 12, 0, 0);
        var connections = StructureTreeBuilder.BuildConnections(new[]
        {
            S("https://a.test/1", connectionId: 1, start: t0, end: t0.AddMilliseconds(50)),
            S("https://a.test/2", connectionId: 1, start: t0.AddMilliseconds(60), end: t0.AddMilliseconds(100)),
        });

        var conn = Assert.Single(connections);
        Assert.True(conn.IsMultiplexed);            // reused
        Assert.False(conn.HasConcurrentStreams);    // but not overlapping
        Assert.Contains("reused", conn.Display);
    }

    [Fact]
    public void Overlapping_streams_are_reported_as_multiplexed()
    {
        var t0 = new DateTime(2026, 1, 1, 12, 0, 0);
        var connections = StructureTreeBuilder.BuildConnections(new[]
        {
            S("https://a.test/1", connectionId: 1, streamId: 1, start: t0, end: t0.AddMilliseconds(100)),
            S("https://a.test/2", connectionId: 1, streamId: 3, start: t0.AddMilliseconds(20), end: t0.AddMilliseconds(80)),
        });

        var conn = Assert.Single(connections);
        Assert.True(conn.HasConcurrentStreams);
        Assert.Contains("multiplexed", conn.Display);
    }

    [Fact]
    public void Sessions_without_connection_identity_do_not_collapse_together()
    {
        // Replayed and reloaded sessions carry ConnectionId 0; bucketing them all
        // into one node would invent a connection that never existed.
        var a = S("https://a.test/1");
        var b = S("https://a.test/2");

        var connections = StructureTreeBuilder.BuildConnections(new[] { a, b });
        Assert.Equal(2, connections.Count);
    }

    [Fact]
    public void Streams_are_ordered_by_stream_id()
    {
        var connections = StructureTreeBuilder.BuildConnections(new[]
        {
            S("https://a.test/c", connectionId: 1, streamId: 5),
            S("https://a.test/a", connectionId: 1, streamId: 1),
            S("https://a.test/b", connectionId: 1, streamId: 3),
        });

        var conn = Assert.Single(connections);
        Assert.Equal(new[] { 1, 3, 5 }, conn.Streams.Select(s => s.StreamId));
    }

    [Fact]
    public void An_empty_capture_produces_an_empty_tree()
    {
        var root = StructureTreeBuilder.Build(Array.Empty<HttpSession>());
        Assert.Empty(root.Children);
        Assert.Equal(0, root.Subtree.Requests);
    }
}

/// <summary>Coverage for capture-level filtering.</summary>
public class CaptureFilterTests
{
    private static HttpSession S(string host = "example.com", string url = "https://example.com/x",
        string process = "chrome", string method = "GET") => new()
    {
        Host = host,
        Url = url,
        Method = method,
        ProcessName = process,
    };

    [Fact]
    public void No_filters_records_everything()
    {
        Assert.True(CaptureFilterSet.ShouldRecord(Array.Empty<CaptureFilter>(), S()));
    }

    [Fact]
    public void A_drop_rule_excludes_matches_and_keeps_the_rest()
    {
        var filters = new[]
        {
            new CaptureFilter { Field = CaptureFilterField.Host, Exclude = true, Pattern = "telemetry" },
        };

        Assert.False(CaptureFilterSet.ShouldRecord(filters, S(host: "telemetry.example.com")));
        Assert.True(CaptureFilterSet.ShouldRecord(filters, S(host: "api.example.com")));
    }

    [Fact]
    public void Keep_only_rules_restrict_to_their_matches()
    {
        var filters = new[]
        {
            new CaptureFilter { Field = CaptureFilterField.Host, Exclude = false, Pattern = "api.example.com" },
        };

        Assert.True(CaptureFilterSet.ShouldRecord(filters, S(host: "api.example.com")));
        Assert.False(CaptureFilterSet.ShouldRecord(filters, S(host: "cdn.example.com")));
    }

    [Fact]
    public void Several_keep_only_rules_are_an_or()
    {
        var filters = new[]
        {
            new CaptureFilter { Field = CaptureFilterField.Host, Exclude = false, Pattern = "api." },
            new CaptureFilter { Field = CaptureFilterField.Host, Exclude = false, Pattern = "cdn." },
        };

        Assert.True(CaptureFilterSet.ShouldRecord(filters, S(host: "api.example.com")));
        Assert.True(CaptureFilterSet.ShouldRecord(filters, S(host: "cdn.example.com")));
        Assert.False(CaptureFilterSet.ShouldRecord(filters, S(host: "www.example.com")));
    }

    [Fact]
    public void A_drop_rule_wins_over_a_keep_rule()
    {
        var filters = new[]
        {
            new CaptureFilter { Field = CaptureFilterField.Host, Exclude = false, Pattern = "example.com" },
            new CaptureFilter { Field = CaptureFilterField.Url, Exclude = true, Pattern = "/health" },
        };

        Assert.True(CaptureFilterSet.ShouldRecord(filters, S(url: "https://example.com/api")));
        Assert.False(CaptureFilterSet.ShouldRecord(filters, S(url: "https://example.com/health")));
    }

    [Fact]
    public void Disabled_and_blank_rules_are_ignored()
    {
        var filters = new[]
        {
            new CaptureFilter { Enabled = false, Exclude = true, Pattern = "example.com" },
            new CaptureFilter { Enabled = true, Exclude = true, Pattern = "   " },
        };

        Assert.True(CaptureFilterSet.ShouldRecord(filters, S()));
    }

    [Fact]
    public void Regex_patterns_are_supported()
    {
        var filters = new[]
        {
            new CaptureFilter
            {
                Field = CaptureFilterField.Url, Exclude = true, UseRegex = true,
                Pattern = @"\.(png|jpe?g|gif|woff2?)$",
            },
        };

        Assert.False(CaptureFilterSet.ShouldRecord(filters, S(url: "https://a.test/logo.png")));
        Assert.True(CaptureFilterSet.ShouldRecord(filters, S(url: "https://a.test/data.json")));
    }

    [Fact]
    public void An_invalid_regex_never_matches_rather_than_throwing()
    {
        var filters = new[]
        {
            new CaptureFilter { Exclude = true, UseRegex = true, Pattern = "([unclosed" },
        };

        Assert.True(CaptureFilterSet.ShouldRecord(filters, S()));
    }

    [Theory]
    [InlineData(CaptureFilterField.Process, "chrome", true)]
    [InlineData(CaptureFilterField.Process, "firefox", false)]
    [InlineData(CaptureFilterField.Method, "GET", true)]
    [InlineData(CaptureFilterField.Method, "POST", false)]
    public void Each_field_matches_the_right_value(CaptureFilterField field, string pattern, bool dropped)
    {
        var filters = new[] { new CaptureFilter { Field = field, Exclude = true, Pattern = pattern } };
        Assert.Equal(!dropped, CaptureFilterSet.ShouldRecord(filters, S()));
    }

    [Fact]
    public void Cloning_is_deep_enough_to_edit_independently()
    {
        var original = new CaptureFilter { Pattern = "a", UseRegex = true };
        var copy = original.Clone();
        copy.Pattern = "b";

        Assert.Equal("a", original.Pattern);
        Assert.True(copy.UseRegex);
    }
}

/// <summary>Coverage for the header-edit operations used by Modify rules.</summary>
public class HeaderEditTests
{
    private static HttpSession WithResponseHeaders(params (string Name, string Value)[] headers)
    {
        var s = new HttpSession { Host = "example.com", Url = "https://example.com/", StatusCode = 200 };
        foreach (var (n, v) in headers) s.ResponseHeaders.Add(n, v);
        return s;
    }

    private static RuleEngine EngineWith(params HeaderEdit[] edits)
    {
        var engine = new RuleEngine();
        engine.SetRules(new[]
        {
            new Rule
            {
                Action = RuleAction.ModifyResponse,
                UrlMatchMode = MatchMode.Wildcard,
                UrlPattern = "*",
                HeaderEdits = edits.ToList(),
            },
        });
        return engine;
    }

    [Fact]
    public void Set_replaces_every_existing_value()
    {
        var s = WithResponseHeaders(("X-Trial", "one"), ("X-Trial", "two"));
        EngineWith(new HeaderEdit { Operation = HeaderEdit.Op.Set, Name = "X-Trial", Value = "final" })
            .EvaluateResponse(s, out _);

        Assert.Equal(new[] { "final" }, s.ResponseHeaders.GetAll("X-Trial"));
    }

    [Fact]
    public void Append_adds_a_second_line_for_repeatable_headers()
    {
        var s = WithResponseHeaders(("Set-Cookie", "a=1"));
        EngineWith(new HeaderEdit { Operation = HeaderEdit.Op.Append, Name = "Set-Cookie", Value = "b=2" })
            .EvaluateResponse(s, out _);

        Assert.Equal(new[] { "a=1", "b=2" }, s.ResponseHeaders.GetAll("Set-Cookie"));
    }

    [Fact]
    public void Remove_deletes_all_values()
    {
        var s = WithResponseHeaders(("Server", "nginx"), ("Server", "extra"));
        EngineWith(new HeaderEdit { Operation = HeaderEdit.Op.Remove, Name = "Server" })
            .EvaluateResponse(s, out _);

        Assert.False(s.ResponseHeaders.Contains("Server"));
    }

    [Fact]
    public void A_blank_header_name_is_ignored_rather_than_producing_a_broken_header()
    {
        var s = WithResponseHeaders(("Server", "nginx"));
        EngineWith(new HeaderEdit { Operation = HeaderEdit.Op.Set, Name = "  ", Value = "x" })
            .EvaluateResponse(s, out _);

        Assert.Equal(1, s.ResponseHeaders.Count);
    }
}

/// <summary>Coverage for the portable settings bundle.</summary>
public class SettingsBundleTests
{
    [Fact]
    public void Bundle_round_trips_settings_and_rules()
    {
        var settings = new HttpSpySettings { ListenPort = 9999, Theme = "Light", DecryptHttps = false };
        settings.CaptureFilters.Add(new CaptureFilter { Pattern = "telemetry", Exclude = true });
        var rules = new[] { new Rule { Name = "Block ads", Action = RuleAction.Block, UrlPattern = "*ads*" } };

        var json = SettingsStore.ExportBundle(settings, rules);
        var (loaded, loadedRules) = SettingsStore.ImportBundle(json);

        Assert.Equal(9999, loaded.ListenPort);
        Assert.Equal("Light", loaded.Theme);
        Assert.False(loaded.DecryptHttps);
        Assert.Equal("telemetry", Assert.Single(loaded.CaptureFilters).Pattern);

        var rule = Assert.Single(loadedRules);
        Assert.Equal("Block ads", rule.Name);
        Assert.Equal(RuleAction.Block, rule.Action);
    }

    [Fact]
    public void A_bare_settings_document_is_accepted_too()
    {
        // So an existing settings.json can be imported without hand-editing it.
        var bare = System.Text.Json.JsonSerializer.Serialize(new HttpSpySettings { ListenPort = 4321 });
        var (loaded, rules) = SettingsStore.ImportBundle(bare);

        Assert.Equal(4321, loaded.ListenPort);
        Assert.Empty(rules);
    }

    [Fact]
    public void Garbage_is_rejected_with_a_clear_error()
    {
        Assert.ThrowsAny<Exception>(() => SettingsStore.ImportBundle("not json at all"));
    }
}

/// <summary>Coverage for the derived transfer-rate figure shown in the Speed column.</summary>
public class SpeedTests
{
    [Fact]
    public void Speed_is_wire_bytes_over_elapsed_time()
    {
        var s = new HttpSession
        {
            ResponseBody = new byte[1000],
            EncodedBodySize = 500,   // compressed on the wire
        };
        s.EndTime = s.StartTime.AddMilliseconds(1000);

        // The wire size is what actually crossed the network.
        Assert.Equal(500, s.SpeedBytesPerSecond, precision: 0);
    }

    [Fact]
    public void A_zero_duration_yields_zero_rather_than_infinity()
    {
        var s = new HttpSession { ResponseBody = new byte[1000] };
        s.EndTime = s.StartTime;

        Assert.Equal(0, s.SpeedBytesPerSecond);
    }
}
