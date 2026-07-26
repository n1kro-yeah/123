using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using HttpSpy.Core.Analysis;
using HttpSpy.Core.Models;
using Xunit;

namespace HttpSpy.Tests;

/// <summary>
/// The inspector's Find box only ever looked inside the transaction already
/// open, and the grid filter only matches displayed columns. These cover the
/// engine that answers "which of these requests mentions this string".
/// </summary>
public class ContentSearchTests
{
    private static HttpSession Session(string url, string requestBody = "", string responseBody = "",
        (string Name, string Value)[]? requestHeaders = null,
        (string Name, string Value)[]? responseHeaders = null)
    {
        var s = new HttpSession
        {
            Method = "GET",
            Url = url,
            Scheme = url.StartsWith("https") ? "https" : "http",
            Host = new Uri(url).Host,
            Path = new Uri(url).AbsolutePath,
            QueryString = new Uri(url).Query.TrimStart('?'),
            StatusCode = 200,
            RequestBody = Encoding.UTF8.GetBytes(requestBody),
            ResponseBody = Encoding.UTF8.GetBytes(responseBody),
        };
        foreach (var (name, value) in requestHeaders ?? Array.Empty<(string, string)>())
            s.RequestHeaders.Add(name, value);
        foreach (var (name, value) in responseHeaders ?? Array.Empty<(string, string)>())
            s.ResponseHeaders.Add(name, value);
        if (responseBody.Length > 0) s.ResponseHeaders.Add("Content-Type", "text/plain; charset=utf-8");
        return s;
    }

    private static List<HttpSession> Corpus() => new()
    {
        Session("https://api.example.com/login",
            requestBody: "{\"user\":\"bob\",\"token\":\"SECRET123\"}",
            responseBody: "{\"ok\":true}",
            requestHeaders: new[] { ("Authorization", "Bearer SECRET123") }),
        Session("https://cdn.example.com/app.js",
            responseBody: "function hello(){ return 'world'; }",
            responseHeaders: new[] { ("Cache-Control", "max-age=3600") }),
        Session("https://api.example.com/profile?id=42",
            responseBody: "{\"id\":42,\"name\":\"bob\"}"),
    };

    private static SearchQuery Query(string text, SearchScope scope = SearchScope.Everything) =>
        new() { Text = text, Scope = scope };

    [Fact]
    public void Finds_a_token_across_headers_and_bodies()
    {
        var results = ContentSearch.Run(Corpus(), Query("SECRET123"));

        Assert.Equal(3, results.SessionsSearched);
        Assert.Equal(1, results.SessionsMatched);
        Assert.Equal(2, results.Hits.Count); // the header and the request body
        Assert.Contains(results.Hits, h => h.Location == SearchLocation.RequestHeader);
        Assert.Contains(results.Hits, h => h.Location == SearchLocation.RequestBody);
    }

    [Fact]
    public void Search_is_case_insensitive_by_default()
    {
        Assert.NotEmpty(ContentSearch.Run(Corpus(), Query("secret123")).Hits);
    }

    [Fact]
    public void Match_case_is_honoured_when_asked_for()
    {
        var query = Query("secret123");
        query.CaseSensitive = true;
        Assert.Empty(ContentSearch.Run(Corpus(), query).Hits);
    }

    [Fact]
    public void Scope_limits_where_the_search_looks()
    {
        var hits = ContentSearch.Run(Corpus(), Query("SECRET123", SearchScope.RequestHeaders)).Hits;
        Assert.All(hits, h => Assert.Equal(SearchLocation.RequestHeader, h.Location));
    }

    [Fact]
    public void Url_scope_matches_the_query_string_too()
    {
        var hits = ContentSearch.Run(Corpus(), Query("id=42", SearchScope.Url)).Hits;
        var hit = Assert.Single(hits);
        Assert.Equal(SearchLocation.Url, hit.Location);
        Assert.Contains("profile", hit.Preview);
    }

    [Fact]
    public void Plain_text_queries_are_not_treated_as_regex()
    {
        // "{\"ok\":true}" contains characters that would blow up as a pattern.
        var hits = ContentSearch.Run(Corpus(), Query("{\"ok\":true}")).Hits;
        Assert.Single(hits);
    }

    [Fact]
    public void Regex_mode_works_when_requested()
    {
        var query = Query(@"""id"":\s*\d+");
        query.UseRegex = true;
        var hit = Assert.Single(ContentSearch.Run(Corpus(), query).Hits);
        Assert.Equal(SearchLocation.ResponseBody, hit.Location);
    }

    [Fact]
    public void An_invalid_pattern_is_reported_not_thrown()
    {
        var query = Query("([unclosed");
        query.UseRegex = true;
        var results = ContentSearch.Run(Corpus(), query);

        Assert.NotNull(results.Error);
        Assert.Empty(results.Hits);
    }

    [Fact]
    public void Whole_word_does_not_match_inside_a_longer_word()
    {
        var sessions = new[] { Session("http://x.test/", responseBody: "hello helloworld") };

        var loose = ContentSearch.Run(sessions, Query("hello"));
        Assert.Equal(2, loose.Hits.Count);

        var strict = Query("hello");
        strict.WholeWord = true;
        Assert.Single(ContentSearch.Run(sessions, strict).Hits);
    }

    [Fact]
    public void Hits_carry_the_line_number_within_the_body()
    {
        var sessions = new[] { Session("http://x.test/", responseBody: "alpha\nbeta\ngamma\nbeta") };
        var hits = ContentSearch.Run(sessions, Query("beta", SearchScope.ResponseBody)).Hits;

        Assert.Equal(new[] { 2, 4 }, hits.Select(h => h.LineNumber));
    }

    [Fact]
    public void The_preview_of_a_long_line_is_trimmed_around_the_match()
    {
        // A minified bundle is one enormous line; rendering it whole is useless.
        string body = new string('x', 5000) + "NEEDLE" + new string('y', 5000);
        var sessions = new[] { Session("http://x.test/app.js", responseBody: body) };

        var hit = Assert.Single(ContentSearch.Run(sessions, Query("NEEDLE", SearchScope.ResponseBody)).Hits);

        Assert.True(hit.Preview.Length < 400, $"preview was {hit.Preview.Length} chars");
        Assert.Contains("NEEDLE", hit.Preview);
        Assert.Equal("NEEDLE", hit.Preview.Substring(hit.MatchStart, hit.MatchLength));
    }

    [Fact]
    public void Short_lines_are_returned_whole_with_an_accurate_offset()
    {
        var sessions = new[] { Session("http://x.test/", responseBody: "prefix NEEDLE suffix") };
        var hit = Assert.Single(ContentSearch.Run(sessions, Query("NEEDLE", SearchScope.ResponseBody)).Hits);

        Assert.Equal("prefix NEEDLE suffix", hit.Preview);
        Assert.Equal("NEEDLE", hit.Preview.Substring(hit.MatchStart, hit.MatchLength));
    }

    [Fact]
    public void Per_session_hit_budget_is_respected()
    {
        var body = string.Join("\n", Enumerable.Repeat("match", 100));
        var sessions = new[] { Session("http://x.test/", responseBody: body) };

        var query = Query("match", SearchScope.ResponseBody);
        query.MaxHitsPerSession = 5;

        Assert.Equal(5, ContentSearch.Run(sessions, query).Hits.Count);
    }

    [Fact]
    public void The_total_ceiling_stops_the_search_and_says_so()
    {
        var sessions = Enumerable.Range(0, 50)
            .Select(i => Session($"http://x.test/{i}", responseBody: "match")).ToList();

        var query = Query("match", SearchScope.ResponseBody);
        query.MaxTotalHits = 10;

        var results = ContentSearch.Run(sessions, query);
        Assert.True(results.Truncated);
        Assert.Equal(10, results.Hits.Count);
        Assert.Contains("limit", ContentSearch.Describe(results));
    }

    [Fact]
    public void A_zero_width_pattern_does_not_produce_a_hit_per_character()
    {
        var sessions = new[] { Session("http://x.test/", responseBody: "abcdef") };
        var query = Query("x*", SearchScope.ResponseBody);
        query.UseRegex = true;

        Assert.Single(ContentSearch.Run(sessions, query).Hits);
    }

    [Fact]
    public void Websocket_text_frames_are_searched_but_binary_ones_are_not()
    {
        var s = Session("http://x.test/ws");
        s.AddWebSocketFrame(new WebSocketFrame
        {
            Opcode = WebSocketOpcode.Text,
            Payload = Encoding.UTF8.GetBytes("{\"event\":\"ping\"}"),
        });
        s.AddWebSocketFrame(new WebSocketFrame
        {
            Opcode = WebSocketOpcode.Binary,
            Payload = new byte[] { 1, 2, 3 },
        });

        Assert.Single(ContentSearch.Run(new[] { s }, Query("ping", SearchScope.Messages)).Hits);
        // "bytes" appears in the binary placeholder text, which must not be searched.
        Assert.Empty(ContentSearch.Run(new[] { s }, Query("bytes", SearchScope.Messages)).Hits);
    }

    [Fact]
    public void Server_sent_events_are_searched()
    {
        var s = Session("http://x.test/stream");
        s.AddServerSentEvent(new ServerSentEvent { EventName = "update", Data = "order 91 shipped" });

        var hit = Assert.Single(ContentSearch.Run(new[] { s }, Query("shipped", SearchScope.Messages)).Hits);
        Assert.Equal(SearchLocation.Message, hit.Location);
    }

    [Fact]
    public void An_empty_query_searches_nothing()
    {
        var results = ContentSearch.Run(Corpus(), Query(""));
        Assert.Empty(results.Hits);
        Assert.Equal(0, results.SessionsSearched);
    }

    [Fact]
    public void An_empty_scope_searches_nothing()
    {
        Assert.Empty(ContentSearch.Run(Corpus(), Query("bob", SearchScope.None)).Hits);
    }

    [Fact]
    public void Describe_summarises_the_outcome()
    {
        Assert.Contains("No matches", ContentSearch.Describe(ContentSearch.Run(Corpus(), Query("zzzz"))));
        Assert.Contains("2 of 3", ContentSearch.Describe(ContentSearch.Run(Corpus(), Query("bob"))));
    }

    [Fact]
    public void Cancellation_is_observed()
    {
        using var cts = new System.Threading.CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() =>
            ContentSearch.Run(Corpus(), Query("bob"), cts.Token));
    }
}
