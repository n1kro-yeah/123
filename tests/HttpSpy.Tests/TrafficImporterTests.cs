using System;
using System.IO;
using System.Linq;
using System.Text;
using HttpSpy.Core.Export;
using HttpSpy.Core.Models;
using Xunit;

namespace HttpSpy.Tests;

/// <summary>
/// Export was one-way, so the analyzer and the structure tree could only ever
/// see traffic this process captured. These cover reading a HAR from another
/// tool, a cURL command copied out of devtools, and a request file.
/// </summary>
public class TrafficImporterTests
{
    // ---- HAR -----------------------------------------------------------------

    private const string SampleHar = """
    {
      "log": {
        "version": "1.2",
        "creator": { "name": "WebInspector", "version": "537.36" },
        "entries": [
          {
            "startedDateTime": "2024-03-01T10:00:00.000Z",
            "time": 123.5,
            "request": {
              "method": "POST",
              "url": "https://api.example.com/v1/users?page=2",
              "httpVersion": "h2",
              "headers": [
                { "name": ":authority", "value": "api.example.com" },
                { "name": "content-type", "value": "application/json" },
                { "name": "authorization", "value": "Bearer abc" }
              ],
              "postData": { "mimeType": "application/json", "text": "{\"name\":\"bob\"}" }
            },
            "response": {
              "status": 201,
              "statusText": "Created",
              "httpVersion": "h2",
              "headers": [ { "name": "content-type", "value": "application/json" } ],
              "content": { "size": 17, "mimeType": "application/json", "text": "{\"id\":42}" }
            },
            "timings": { "blocked": 1, "dns": 2, "connect": 3, "ssl": 4, "send": 5, "wait": 100, "receive": 8.5 },
            "serverIPAddress": "93.184.216.34"
          }
        ]
      }
    }
    """;

    [Fact]
    public void Har_is_detected_and_parsed()
    {
        var result = TrafficImporter.Import(SampleHar);

        Assert.Equal(ImportFormat.Har, result.Format);
        var s = Assert.Single(result.Sessions);
        Assert.Equal("POST", s.Method);
        Assert.Equal("api.example.com", s.Host);
        Assert.Equal("/v1/users", s.Path);
        Assert.Equal("page=2", s.QueryString);
        Assert.Equal(201, s.StatusCode);
        Assert.Equal("Created", s.StatusText);
        Assert.True(s.IsTls);
        Assert.True(s.Imported);
    }

    [Fact]
    public void Har_normalises_the_many_spellings_of_http_2()
    {
        var s = Assert.Single(TrafficImporter.ImportHar(SampleHar).Sessions);
        Assert.Equal("HTTP/2", s.HttpVersion);
        Assert.Equal("HTTP/2", s.ResponseHttpVersion);
    }

    [Fact]
    public void Har_drops_h2_pseudo_headers()
    {
        // ":authority" is framing, not a header the user set; carrying it into
        // the grid would make every imported h2 request look malformed.
        var s = Assert.Single(TrafficImporter.ImportHar(SampleHar).Sessions);
        Assert.DoesNotContain(s.RequestHeaders, h => h.Name.StartsWith(':'));
        Assert.Equal("Bearer abc", s.RequestHeaders["authorization"]);
    }

    [Fact]
    public void Har_reads_bodies_and_timings()
    {
        var s = Assert.Single(TrafficImporter.ImportHar(SampleHar).Sessions);

        Assert.Equal("{\"name\":\"bob\"}", Encoding.UTF8.GetString(s.RequestBody));
        Assert.Equal("{\"id\":42}", Encoding.UTF8.GetString(s.ResponseBody));
        Assert.Equal(123.5, s.Timings.TotalMs);
        Assert.Equal(100, s.Timings.WaitMs);
        Assert.Equal(4, s.Timings.TlsMs);
        Assert.Equal("93.184.216.34", s.RemoteAddress);
    }

    [Fact]
    public void Har_decodes_base64_content()
    {
        string body = Convert.ToBase64String(new byte[] { 0x89, 0x50, 0x4E, 0x47 });
        string har = $$"""
        { "log": { "entries": [ {
            "request": { "method": "GET", "url": "http://x/y.png", "headers": [] },
            "response": { "status": 200, "headers": [],
              "content": { "size": 4, "encoding": "base64", "text": "{{body}}" } }
        } ] } }
        """;

        var s = Assert.Single(TrafficImporter.ImportHar(har).Sessions);
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, s.ResponseBody);
    }

    [Fact]
    public void Har_flags_a_body_the_exporter_dropped()
    {
        // Devtools omits large bodies; reporting an empty body would be a lie.
        string har = """
        { "log": { "entries": [ {
            "request": { "method": "GET", "url": "http://x/big", "headers": [] },
            "response": { "status": 200, "headers": [], "content": { "size": 5000000, "mimeType": "video/mp4" } }
        } ] } }
        """;

        var result = TrafficImporter.ImportHar(har);
        var s = Assert.Single(result.Sessions);
        Assert.True(s.ResponseBodyTruncated);
        Assert.Contains(result.Warnings, w => w.Contains("5000000"));
    }

    [Fact]
    public void Har_survives_one_broken_entry()
    {
        string har = """
        { "log": { "entries": [
            { "request": { "method": "GET", "url": "http://good/one", "headers": [] },
              "response": { "status": 200, "headers": [] } },
            { "response": { "status": 500, "headers": [] } },
            { "request": { "method": "GET", "url": "http://good/two", "headers": [] },
              "response": { "status": 204, "headers": [] } }
        ] } }
        """;

        var result = TrafficImporter.ImportHar(har);
        Assert.Equal(2, result.Sessions.Count);
    }

    [Fact]
    public void Har_groups_entries_by_origin_into_connections()
    {
        string har = """
        { "log": { "entries": [
            { "request": { "method": "GET", "url": "http://a/1", "headers": [] },
              "response": { "status": 200, "headers": [] }, "serverIPAddress": "1.1.1.1" },
            { "request": { "method": "GET", "url": "http://a/2", "headers": [] },
              "response": { "status": 200, "headers": [] }, "serverIPAddress": "1.1.1.1" },
            { "request": { "method": "GET", "url": "http://b/1", "headers": [] },
              "response": { "status": 200, "headers": [] }, "serverIPAddress": "2.2.2.2" }
        ] } }
        """;

        var sessions = TrafficImporter.ImportHar(har).Sessions;
        Assert.Equal(sessions[0].ConnectionId, sessions[1].ConnectionId);
        Assert.NotEqual(sessions[0].ConnectionId, sessions[2].ConnectionId);
    }

    [Fact]
    public void Invalid_json_is_reported_not_thrown()
    {
        var result = TrafficImporter.ImportHar("{ not json");
        Assert.Empty(result.Sessions);
        Assert.NotEmpty(result.Warnings);
    }

    [Fact]
    public void A_capture_of_ours_round_trips_through_har()
    {
        var original = new HttpSession
        {
            Method = "PUT",
            Url = "https://example.com/api/item?id=7",
            Scheme = "https",
            Host = "example.com",
            Path = "/api/item",
            QueryString = "id=7",
            StatusCode = 200,
            StatusText = "OK",
            RequestBody = Encoding.UTF8.GetBytes("{\"a\":1}"),
            ResponseBody = Encoding.UTF8.GetBytes("{\"ok\":true}"),
        };
        original.RequestHeaders.Add("Content-Type", "application/json");
        original.ResponseHeaders.Add("Content-Type", "application/json");

        var reimported = Assert.Single(TrafficImporter.ImportHar(HarExporter.Export(new[] { original })).Sessions);

        Assert.Equal(original.Method, reimported.Method);
        Assert.Equal(original.Host, reimported.Host);
        Assert.Equal(original.Path, reimported.Path);
        Assert.Equal(original.QueryString, reimported.QueryString);
        Assert.Equal(original.StatusCode, reimported.StatusCode);
        Assert.Equal(original.RequestBody, reimported.RequestBody);
        Assert.Equal(original.ResponseBody, reimported.ResponseBody);
    }

    // ---- cURL ----------------------------------------------------------------

    [Fact]
    public void Curl_from_chrome_copy_as_curl_is_parsed()
    {
        const string command = """
        curl 'https://api.example.com/search?q=cats' \
          -H 'accept: application/json' \
          -H 'authorization: Bearer t0ken' \
          --data-raw '{"page":1}' \
          --compressed
        """;

        var result = TrafficImporter.Import(command);
        Assert.Equal(ImportFormat.Curl, result.Format);

        var s = Assert.Single(result.Sessions);
        Assert.Equal("POST", s.Method);          // a body implies POST when -X is absent
        Assert.Equal("api.example.com", s.Host);
        Assert.Equal("q=cats", s.QueryString);
        Assert.Equal("Bearer t0ken", s.RequestHeaders["authorization"]);
        Assert.Equal("{\"page\":1}", Encoding.UTF8.GetString(s.RequestBody));
        Assert.Contains("gzip", s.RequestHeaders["Accept-Encoding"]);
        Assert.True(s.Imported);
    }

    [Fact]
    public void Curl_honours_an_explicit_method()
    {
        var s = Assert.Single(TrafficImporter.ImportCurl("curl -X DELETE https://x.test/item/9").Sessions);
        Assert.Equal("DELETE", s.Method);
    }

    [Fact]
    public void Curl_defaults_to_get_without_a_body()
    {
        var s = Assert.Single(TrafficImporter.ImportCurl("curl https://x.test/").Sessions);
        Assert.Equal("GET", s.Method);
        Assert.Empty(s.RequestBody);
    }

    [Fact]
    public void Curl_maps_head_and_basic_auth()
    {
        var s = Assert.Single(TrafficImporter.ImportCurl("curl -I -u alice:secret https://x.test/").Sessions);
        Assert.Equal("HEAD", s.Method);
        Assert.Equal("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("alice:secret")),
            s.RequestHeaders["Authorization"]);
    }

    [Fact]
    public void Curl_joins_repeated_data_flags_the_way_curl_does()
    {
        var s = Assert.Single(TrafficImporter.ImportCurl("curl -d name=bob -d age=7 http://x.test/f").Sessions);
        Assert.Equal("name=bob&age=7", Encoding.UTF8.GetString(s.RequestBody));
        Assert.Equal("application/x-www-form-urlencoded", s.RequestHeaders["Content-Type"]);
    }

    [Fact]
    public void Curl_warns_instead_of_inventing_a_body_from_a_file()
    {
        var result = TrafficImporter.ImportCurl("curl -d @payload.json http://x.test/f");
        Assert.Empty(Assert.Single(result.Sessions).RequestBody);
        Assert.Contains(result.Warnings, w => w.Contains("payload.json"));
    }

    [Fact]
    public void Curl_reports_a_command_with_no_url()
    {
        var result = TrafficImporter.ImportCurl("curl -X POST -H 'a: b'");
        Assert.Empty(result.Sessions);
        Assert.Contains(result.Warnings, w => w.Contains("No URL"));
    }

    [Theory]
    [InlineData("curl \"https://x.test/a b\"", "https://x.test/a b")]
    [InlineData("curl 'https://x.test/it'\\''s'", "https://x.test/it")]
    [InlineData("curl $'https://x.test/\\u0061'", "https://x.test/a")]
    public void Curl_tokenizer_handles_every_quoting_style_browsers_emit(string command, string expectedPrefix)
    {
        var s = Assert.Single(TrafficImporter.ImportCurl(command).Sessions);
        Assert.StartsWith(expectedPrefix, s.Url);
    }

    [Fact]
    public void Curl_tokenizer_joins_backslash_continued_lines()
    {
        var tokens = TrafficImporter.Tokenize("curl \\\n  -X GET \\\n  http://x.test/");
        Assert.Equal(new[] { "curl", "-X", "GET", "http://x.test/" }, tokens);
    }

    [Fact]
    public void Curl_skips_switches_that_do_not_shape_the_request()
    {
        var result = TrafficImporter.ImportCurl(
            "curl -s -S -L -k --max-time 30 -o out.txt https://x.test/p");
        var s = Assert.Single(result.Sessions);
        Assert.Equal("https://x.test/p", s.Url);
        // -o consumed its argument rather than being mistaken for the URL.
        Assert.DoesNotContain("out.txt", s.Url);
    }

    // ---- .http files ---------------------------------------------------------

    private const string SampleHttpFile = """
    ### Fetch the user
    GET https://api.example.com/users/7 HTTP/1.1
    Accept: application/json

    ### Create one
    POST https://api.example.com/users
    Content-Type: application/json

    {
      "name": "bob"
    }
    """;

    [Fact]
    public void Http_file_splits_requests_on_the_separator()
    {
        var result = TrafficImporter.Import(SampleHttpFile);
        Assert.Equal(ImportFormat.HttpFile, result.Format);
        Assert.Equal(2, result.Sessions.Count);
    }

    [Fact]
    public void Http_file_reads_method_headers_and_body()
    {
        var sessions = TrafficImporter.ImportHttpFile(SampleHttpFile).Sessions;

        Assert.Equal("GET", sessions[0].Method);
        Assert.Equal("/users/7", sessions[0].Path);
        Assert.Equal("application/json", sessions[0].RequestHeaders["Accept"]);
        Assert.Empty(sessions[0].RequestBody);

        Assert.Equal("POST", sessions[1].Method);
        Assert.Contains("bob", Encoding.UTF8.GetString(sessions[1].RequestBody));
    }

    [Fact]
    public void Http_file_resolves_a_relative_target_against_the_host_header()
    {
        const string text = """
        GET /health
        Host: service.internal:8080
        """;

        var s = Assert.Single(TrafficImporter.ImportHttpFile(text).Sessions);
        Assert.Equal("service.internal", s.Host);
        Assert.Equal("/health", s.Path);
    }

    [Fact]
    public void Http_file_reports_a_line_that_is_not_a_request()
    {
        var result = TrafficImporter.ImportHttpFile("just some prose\n");
        Assert.Empty(result.Sessions);
        Assert.NotEmpty(result.Warnings);
    }

    // ---- Detection and files -------------------------------------------------

    [Fact]
    public void Unrecognised_text_is_reported_rather_than_guessed_at()
    {
        var result = TrafficImporter.Import("hello there");
        Assert.Equal(ImportFormat.Unknown, result.Format);
        Assert.Empty(result.Sessions);
    }

    [Fact]
    public void Empty_input_is_handled()
    {
        Assert.Empty(TrafficImporter.Import("   ").Sessions);
    }

    [Fact]
    public void Our_own_capture_json_is_not_mistaken_for_a_har()
    {
        var result = TrafficImporter.Import("{ \"version\": 2, \"sessions\": [] }");
        Assert.Equal(ImportFormat.HttpSpyCapture, result.Format);
    }

    [Fact]
    public void ImportFile_falls_back_to_the_extension_for_an_ambiguous_request_file()
    {
        var path = Path.Combine(Path.GetTempPath(), $"httpspy-{Guid.NewGuid():N}.http");
        try
        {
            // No leading verb on the first line, so content sniffing alone fails.
            File.WriteAllText(path, "# a comment\nGET https://x.test/ HTTP/1.1\n");
            var result = TrafficImporter.ImportFile(path);
            Assert.Equal(ImportFormat.HttpFile, result.Format);
            Assert.Single(result.Sessions);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ImportFile_reads_a_har_from_disk()
    {
        var path = Path.Combine(Path.GetTempPath(), $"httpspy-{Guid.NewGuid():N}.har");
        try
        {
            File.WriteAllText(path, SampleHar);
            var result = TrafficImporter.ImportFile(path);
            Assert.Equal(ImportFormat.Har, result.Format);
            Assert.Single(result.Sessions);
        }
        finally { File.Delete(path); }
    }
}
