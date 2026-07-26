using System.Text;
using HttpSpy.Core.Analysis;
using HttpSpy.Core.Models;
using Xunit;

namespace HttpSpy.Tests;

/// <summary>Coverage for the traffic-analysis rule engine.</summary>
public class TrafficAnalyzerTests
{
    private static readonly TrafficAnalyzer Analyzer = new();

    /// <summary>Builds a plausible transaction; each test perturbs one aspect.</summary>
    private static HttpSession Session(
        string url = "https://example.com/",
        string method = "GET",
        int status = 200,
        string contentType = "text/html; charset=utf-8",
        string body = "<html><body>hi</body></html>",
        (string Name, string Value)[]? requestHeaders = null,
        (string Name, string Value)[]? responseHeaders = null,
        double durationMs = 40)
    {
        var uri = new Uri(url);
        var s = new HttpSession
        {
            Method = method,
            Url = url,
            Scheme = uri.Scheme,
            Host = uri.Host,
            RemotePort = uri.Port,
            Path = uri.AbsolutePath,
            QueryString = uri.Query.TrimStart('?'),
            IsTls = uri.Scheme == "https",
            Kind = uri.Scheme == "https" ? SessionKind.Https : SessionKind.Http,
            StatusCode = status,
            StatusText = status == 200 ? "OK" : "",
            ResponseBody = Encoding.UTF8.GetBytes(body),
            State = SessionState.Completed,
        };
        s.Timings.TotalMs = durationMs;
        s.EndTime = s.StartTime.AddMilliseconds(durationMs);

        if (!string.IsNullOrEmpty(contentType)) s.ResponseHeaders.Add("Content-Type", contentType);
        foreach (var (n, v) in requestHeaders ?? Array.Empty<(string, string)>()) s.RequestHeaders.Add(n, v);
        foreach (var (n, v) in responseHeaders ?? Array.Empty<(string, string)>()) s.ResponseHeaders.Add(n, v);
        return s;
    }

    private static AnalysisReport Analyze(params HttpSession[] sessions) =>
        Analyzer.Analyze(sessions, new AnalysisOptions());

    private static bool Has(AnalysisReport report, string ruleId) =>
        report.Findings.Any(f => f.RuleId == ruleId);

    // ---- Security ------------------------------------------------------------

    [Fact]
    public void Credentials_over_plaintext_http_are_critical()
    {
        var report = Analyze(Session(
            url: "http://example.com/login",
            method: "POST",
            requestHeaders: new[] { ("Authorization", "Bearer abc.def.ghi") }));

        var finding = Assert.Single(report.Findings.Where(f => f.RuleId == "SEC001"));
        Assert.Equal(FindingSeverity.Critical, finding.Severity);
    }

    [Fact]
    public void Basic_auth_is_reported_and_the_credential_is_redacted()
    {
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes("alice:hunter2"));
        var report = Analyze(Session(
            url: "https://example.com/",
            requestHeaders: new[] { ("Authorization", "Basic " + encoded) }));

        var finding = Assert.Single(report.Findings.Where(f => f.RuleId == "SEC002"));
        Assert.Contains("alice", finding.Detail);
        // The password must never be reproduced verbatim in a report a user shares.
        Assert.DoesNotContain("hunter2", finding.Evidence);
        Assert.DoesNotContain(encoded, finding.Evidence);
    }

    [Fact]
    public void Missing_security_headers_are_flagged_on_html_documents()
    {
        var report = Analyze(Session());

        Assert.True(Has(report, "SEC003"), "expected missing HSTS");
        Assert.True(Has(report, "SEC005"), "expected missing clickjacking protection");
        Assert.True(Has(report, "SEC006"), "expected missing CSP");
    }

    [Fact]
    public void A_well_configured_document_produces_no_security_header_findings()
    {
        var report = Analyze(Session(responseHeaders: new[]
        {
            ("Strict-Transport-Security", "max-age=31536000; includeSubDomains"),
            ("Content-Security-Policy", "default-src 'self'; frame-ancestors 'self'"),
            ("X-Content-Type-Options", "nosniff"),
            ("X-Frame-Options", "DENY"),
            ("Referrer-Policy", "strict-origin-when-cross-origin"),
        }));

        foreach (var id in new[] { "SEC003", "SEC004", "SEC005", "SEC006", "SEC015" })
            Assert.False(Has(report, id), $"{id} should not fire on a hardened response");
    }

    [Fact]
    public void Unsafe_inline_csp_is_reported_as_weak()
    {
        var report = Analyze(Session(responseHeaders: new[]
        {
            ("Content-Security-Policy", "default-src 'self'; script-src 'unsafe-inline' 'unsafe-eval'"),
        }));

        var finding = Assert.Single(report.Findings.Where(f => f.RuleId == "SEC007"));
        Assert.Equal(FindingSeverity.High, finding.Severity); // two problems present
    }

    [Fact]
    public void Reflected_origin_with_credentials_is_critical()
    {
        var report = Analyze(Session(
            url: "https://api.example.com/me",
            contentType: "application/json",
            body: "{}",
            requestHeaders: new[] { ("Origin", "https://evil.test"), ("Cookie", "sid=1") },
            responseHeaders: new[]
            {
                ("Access-Control-Allow-Origin", "https://evil.test"),
                ("Access-Control-Allow-Credentials", "true"),
            }));

        var finding = Assert.Single(report.Findings.Where(f => f.RuleId == "SEC009"));
        Assert.Equal(FindingSeverity.Critical, finding.Severity);
    }

    [Fact]
    public void Session_cookie_without_httponly_is_high_severity()
    {
        var report = Analyze(Session(responseHeaders: new[]
        {
            ("Set-Cookie", "sessionid=abc123; Path=/; Secure; SameSite=Lax"),
        }));

        var finding = Assert.Single(report.Findings.Where(f => f.RuleId == "SEC010"));
        Assert.Equal(FindingSeverity.High, finding.Severity);
        Assert.Contains("HttpOnly", finding.Detail);
    }

    [Fact]
    public void Fully_protected_cookie_is_not_flagged()
    {
        var report = Analyze(Session(responseHeaders: new[]
        {
            ("Set-Cookie", "sessionid=abc; Path=/; Secure; HttpOnly; SameSite=Strict"),
        }));

        Assert.False(Has(report, "SEC010"));
    }

    [Fact]
    public void Versioned_server_banner_is_reported_but_a_bare_one_is_not()
    {
        Assert.True(Has(Analyze(Session(responseHeaders: new[] { ("Server", "nginx/1.18.0") })), "SEC012"));
        Assert.False(Has(Analyze(Session(responseHeaders: new[] { ("Server", "nginx") })), "SEC012"));
    }

    [Fact]
    public void Stack_traces_in_the_body_are_reported()
    {
        var report = Analyze(Session(
            status: 500,
            contentType: "text/plain",
            body: "Traceback (most recent call last):\n  File \"/srv/app/views.py\", line 42"));

        Assert.True(Has(report, "SEC014"));
    }

    // ---- Privacy -------------------------------------------------------------

    [Fact]
    public void Token_in_the_query_string_is_reported_and_redacted()
    {
        var report = Analyze(Session(url: "https://example.com/cb?access_token=super-secret-value-1234"));

        var finding = Assert.Single(report.Findings.Where(f => f.RuleId == "PRV001"));
        Assert.DoesNotContain("super-secret-value-1234", finding.Evidence);
    }

    [Fact]
    public void Userinfo_in_the_url_is_critical()
    {
        var report = Analyze(Session(url: "https://alice:s3cr3t@example.com/"));

        Assert.Contains(report.Findings,
            f => f.RuleId == "PRV001" && f.Severity == FindingSeverity.Critical);
        Assert.DoesNotContain(report.Findings, f => f.Evidence?.Contains("s3cr3t") == true);
    }

    [Theory]
    [InlineData("AKIAIOSFODNN7EXAMPLE")]
    [InlineData("ghp_1234567890abcdefghijklmnopqrstuvwxyz")]
    [InlineData("sk_live_abcdefghijklmnop1234")]
    public void Known_secret_formats_in_a_response_are_detected(string secret)
    {
        var report = Analyze(Session(contentType: "application/json", body: $"{{\"key\":\"{secret}\"}}"));

        Assert.True(Has(report, "PRV002"), $"expected {secret} to be detected");
        Assert.DoesNotContain(report.Findings, f => f.Evidence == secret);
    }

    [Fact]
    public void Ordinary_json_does_not_trip_the_secret_scanner()
    {
        var report = Analyze(Session(contentType: "application/json",
            body: """{"id":42,"name":"widget","tags":["a","b"]}"""));

        Assert.False(Has(report, "PRV002"));
    }

    [Fact]
    public void Email_in_the_query_string_is_reported()
    {
        var report = Analyze(Session(url: "https://example.com/s?q=user%40example.com&email=user%40example.com"));
        Assert.True(Has(report, "PRV003"));
    }

    // ---- Performance ---------------------------------------------------------

    [Fact]
    public void Slow_responses_are_flagged_and_attributed_to_a_phase()
    {
        var s = Session(durationMs: 6000);
        s.Timings.WaitMs = 5800;
        s.Timings.ConnectMs = 20;
        s.Timings.ReceiveMs = 50;

        var report = Analyze(s);
        var finding = Assert.Single(report.Findings.Where(f => f.RuleId == "PRF001"));

        Assert.Equal(FindingSeverity.High, finding.Severity);
        Assert.Contains("server think time", finding.Detail);
    }

    [Fact]
    public void Fast_responses_are_not_flagged()
    {
        Assert.False(Has(Analyze(Session(durationMs: 30)), "PRF001"));
    }

    [Fact]
    public void Large_uncompressed_text_is_flagged_only_when_the_client_could_accept_compression()
    {
        var big = new string('x', 200_000);

        var willAccept = Session(body: big, requestHeaders: new[] { ("Accept-Encoding", "gzip, br") });
        Assert.True(Has(Analyze(willAccept), "PRF002"));

        var cannotAccept = Session(body: big);
        Assert.False(Has(Analyze(cannotAccept), "PRF002"));
    }

    [Fact]
    public void Already_compressed_responses_are_not_flagged()
    {
        var s = Session(body: new string('x', 200_000),
            requestHeaders: new[] { ("Accept-Encoding", "gzip") });
        s.OriginalContentEncoding = "gzip";

        Assert.False(Has(Analyze(s), "PRF002"));
    }

    [Fact]
    public void Repeated_identical_gets_are_correlated_into_one_finding()
    {
        var sessions = Enumerable.Range(0, 6).Select(_ => Session(url: "https://example.com/poll")).ToArray();

        var report = Analyzer.Analyze(sessions, new AnalysisOptions());
        var finding = Assert.Single(report.Findings.Where(f => f.RuleId == "PRF010"));

        Assert.Equal(6, finding.Occurrences);
    }

    [Fact]
    public void An_n_plus_one_pattern_is_detected()
    {
        var sessions = Enumerable.Range(1, 20)
            .Select(i => Session(url: $"https://api.example.com/v1/users/{i}",
                contentType: "application/json", body: "{}"))
            .ToArray();

        var report = Analyzer.Analyze(sessions, new AnalysisOptions());
        var finding = Assert.Single(report.Findings.Where(f => f.RuleId == "PRF011"));

        // The path shape collapses the numeric id so all 20 group together.
        Assert.Contains("{id}", finding.Subject);
        Assert.Equal(20, finding.Occurrences);
    }

    // ---- Caching -------------------------------------------------------------

    [Fact]
    public void Authenticated_response_marked_public_is_high_severity()
    {
        var report = Analyze(Session(
            url: "https://example.com/account",
            requestHeaders: new[] { ("Cookie", "sid=abc") },
            responseHeaders: new[] { ("Cache-Control", "public, max-age=600") }));

        var finding = Assert.Single(report.Findings.Where(f => f.RuleId == "CAC003"));
        Assert.Equal(FindingSeverity.High, finding.Severity);
    }

    [Fact]
    public void Varying_on_the_credential_makes_public_caching_acceptable()
    {
        var report = Analyze(Session(
            url: "https://example.com/account",
            requestHeaders: new[] { ("Cookie", "sid=abc") },
            responseHeaders: new[] { ("Cache-Control", "public, max-age=600"), ("Vary", "Cookie") }));

        Assert.False(Has(report, "CAC003"));
    }

    [Fact]
    public void Static_asset_with_no_cache_is_flagged()
    {
        var report = Analyze(Session(
            url: "https://cdn.example.com/app.js",
            contentType: "application/javascript",
            body: "console.log(1)",
            responseHeaders: new[] { ("Cache-Control", "no-store") }));

        Assert.True(Has(report, "CAC002"));
    }

    // ---- Correctness ---------------------------------------------------------

    [Fact]
    public void Content_length_mismatch_is_detected()
    {
        var report = Analyze(Session(body: "12345", responseHeaders: new[] { ("Content-Length", "999") }));
        Assert.True(Has(report, "COR001"));
    }

    [Fact]
    public void A_304_keeps_its_origin_content_length_without_being_flagged()
    {
        var report = Analyze(Session(status: 304, body: "",
            responseHeaders: new[] { ("Content-Length", "5000") }));

        Assert.False(Has(report, "COR001"));
    }

    [Fact]
    public void Mislabelled_png_is_detected_by_magic_number()
    {
        var s = Session(contentType: "application/json", body: "");
        s.ResponseBody = new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A };

        var report = Analyze(s);
        var finding = Assert.Single(report.Findings.Where(f => f.RuleId == "COR002"));
        Assert.Contains("png", finding.Detail);
    }

    [Fact]
    public void Malformed_json_is_reported()
    {
        var report = Analyze(Session(contentType: "application/json", body: "{ not json ]"));
        Assert.True(Has(report, "COR003"));
    }

    [Fact]
    public void Valid_json_is_not_reported()
    {
        var report = Analyze(Session(contentType: "application/json", body: """{"ok":true}"""));
        Assert.False(Has(report, "COR003"));
    }

    [Fact]
    public void Consistently_failing_endpoint_is_escalated_over_isolated_failures()
    {
        var sessions = Enumerable.Range(0, 5)
            .Select(_ => Session(url: "https://api.example.com/v1/broken", status: 503,
                contentType: "application/json", body: "{}"))
            .ToArray();

        var report = Analyzer.Analyze(sessions, new AnalysisOptions());
        var finding = Assert.Single(report.Findings.Where(f => f.RuleId == "COR010"));

        Assert.Equal(FindingSeverity.Critical, finding.Severity); // 100% failure rate
    }

    // ---- API design ----------------------------------------------------------

    [Fact]
    public void An_error_body_inside_a_200_is_reported()
    {
        var report = Analyze(Session(
            url: "https://api.example.com/v1/thing",
            contentType: "application/json",
            body: """{"error":"not found"}"""));

        Assert.True(Has(report, "API001"));
    }

    [Fact]
    public void Graphql_errors_in_a_200_are_only_informational()
    {
        var report = Analyze(Session(
            url: "https://api.example.com/graphql",
            method: "POST",
            contentType: "application/json",
            body: """{"data":null,"errors":[{"message":"boom"}]}"""));

        var finding = Assert.Single(report.Findings.Where(f => f.RuleId == "API001"));
        Assert.Equal(FindingSeverity.Info, finding.Severity);
    }

    // ---- Reporting and scoring ----------------------------------------------

    [Fact]
    public void A_clean_capture_scores_100()
    {
        var report = Analyze(Session(
            url: "https://cdn.example.com/app.abc123.js",
            contentType: "application/javascript; charset=utf-8",
            body: "console.log(1)",
            responseHeaders: new[]
            {
                ("Cache-Control", "public, max-age=31536000, immutable"),
                ("X-Content-Type-Options", "nosniff"),
                ("Strict-Transport-Security", "max-age=31536000"),
            }));

        Assert.Empty(report.Findings);
        Assert.Equal(100, report.OverallScore);
        Assert.Contains("Excellent", report.Verdict);
    }

    [Fact]
    public void Findings_are_ordered_by_severity()
    {
        var report = Analyze(
            Session(url: "http://example.com/login", method: "POST",
                requestHeaders: new[] { ("Authorization", "Bearer x") }),
            Session());

        var severities = report.Findings.Select(f => f.Severity).ToList();
        Assert.Equal(severities.OrderByDescending(s => s), severities);
    }

    [Fact]
    public void Identical_findings_across_sessions_merge_with_a_count()
    {
        var sessions = Enumerable.Range(0, 4).Select(_ => Session()).ToArray();
        var report = Analyzer.Analyze(sessions, new AnalysisOptions());

        var hsts = Assert.Single(report.Findings.Where(f => f.RuleId == "SEC003"));
        Assert.Equal(4, hsts.Occurrences);
        Assert.Equal(4, hsts.SessionIndices.Count);
    }

    [Fact]
    public void Category_filter_limits_which_rules_run()
    {
        var options = new AnalysisOptions();
        options.EnabledCategories.Add(FindingCategory.Security);

        var report = Analyzer.Analyze(new[] { Session(durationMs: 9000) }, options);

        Assert.All(report.Findings, f => Assert.Equal(FindingCategory.Security, f.Category));
    }

    [Fact]
    public void Minimum_severity_filters_the_report()
    {
        var options = new AnalysisOptions { MinimumSeverity = FindingSeverity.High };
        var report = Analyzer.Analyze(new[] { Session() }, options);

        Assert.All(report.Findings, f => Assert.True(f.Severity >= FindingSeverity.High));
    }

    [Fact]
    public void Ignored_hosts_are_skipped_entirely()
    {
        var options = new AnalysisOptions();
        options.IgnoredHosts.Add("example.com");

        var report = Analyzer.Analyze(new[] { Session() }, options);

        Assert.Equal(0, report.SessionsAnalyzed);
        Assert.Empty(report.Findings);
    }

    [Fact]
    public void An_empty_capture_analyses_cleanly()
    {
        var report = Analyzer.Analyze(Array.Empty<HttpSession>(), new AnalysisOptions());

        Assert.Equal(0, report.SessionsAnalyzed);
        Assert.Equal(100, report.OverallScore);
        Assert.Empty(report.Findings);
    }

    [Fact]
    public void Malformed_sessions_do_not_crash_the_analyzer()
    {
        // Deliberately degenerate input: no URL, no status, no headers, odd bytes.
        var broken = new HttpSession
        {
            Url = "not a url at all",
            Method = "",
            Host = "",
            Path = "",
            ResponseBody = new byte[] { 0xFF, 0xFE, 0x00, 0x01 },
        };

        var report = Analyzer.Analyze(new[] { broken }, new AnalysisOptions());
        Assert.Equal(1, report.SessionsAnalyzed);
    }

    [Fact]
    public void Analysis_honours_cancellation()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var sessions = Enumerable.Range(0, 100).Select(_ => Session()).ToArray();

        Assert.Throws<OperationCanceledException>(
            () => Analyzer.Analyze(sessions, new AnalysisOptions(), cts.Token));
    }

    // ---- Report writers ------------------------------------------------------

    [Fact]
    public void Text_report_contains_the_score_and_every_finding()
    {
        var report = Analyze(Session(url: "http://example.com/login", method: "POST",
            requestHeaders: new[] { ("Authorization", "Bearer x") }));

        var text = AnalysisReportWriter.ToText(report);

        Assert.Contains($"{report.OverallScore}/100", text);
        foreach (var f in report.Findings) Assert.Contains(f.Title, text);
    }

    [Fact]
    public void Json_report_is_valid_json_and_round_trips_the_score()
    {
        var report = Analyze(Session());
        var json = AnalysisReportWriter.ToJson(report);

        using var doc = System.Text.Json.JsonDocument.Parse(json);
        Assert.Equal(report.OverallScore, doc.RootElement.GetProperty("overallScore").GetInt32());
        Assert.Equal(report.Findings.Count, doc.RootElement.GetProperty("findings").GetArrayLength());
    }

    [Fact]
    public void Html_report_escapes_markup_from_captured_traffic()
    {
        // A finding's subject comes from the URL, which is attacker-controlled.
        var report = Analyze(Session(url: "http://example.com/<script>alert(1)</script>",
            method: "POST", requestHeaders: new[] { ("Authorization", "Bearer x") }));

        var html = AnalysisReportWriter.ToHtml(report);

        Assert.DoesNotContain("<script>alert(1)</script>", html);
        Assert.Contains("&lt;script&gt;", html);
    }

    [Fact]
    public void Html_report_is_self_contained()
    {
        var html = AnalysisReportWriter.ToHtml(Analyze(Session()));

        Assert.StartsWith("<!DOCTYPE html>", html);
        Assert.Contains("</html>", html);
        Assert.DoesNotContain("<script src=", html);
    }
}
