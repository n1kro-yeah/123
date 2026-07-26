using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using HttpSpy.Core.Models;

namespace HttpSpy.Core.Analysis;

// =============================================================================
//  HttpSpy — Traffic Analyzer
// -----------------------------------------------------------------------------
//  A capture tells you what happened. This tells you what is *wrong* with it.
//
//  The analyzer walks every captured transaction through a set of independent
//  checks — security headers, cookie flags, credential leakage, caching,
//  payload weight, latency, protocol hygiene, API shape — and emits structured
//  findings with a severity, the evidence that triggered them, and the concrete
//  change that would fix them. Findings from many sessions are then collapsed
//  into one entry per (rule, subject) pair so a problem that shows up on 400
//  requests is reported once, with a count, rather than 400 times.
//
//  Design notes
//  ------------
//  * Everything here is pure: it reads a snapshot of sessions and returns a
//    report. No I/O, no engine coupling, no UI types — which is what makes it
//    directly unit-testable and safe to run on a background thread.
//  * Checks are data, not control flow. Each one is an IAnalysisRule with an
//    id, a category and a default severity, so the UI can group, filter and
//    explain them without knowing what any individual check does.
//  * Rules that need to reason across requests (duplicate calls, N+1 patterns,
//    per-host TLS posture) implement ICorrelationRule and see the whole capture
//    once, after the per-session pass.
//  * Nothing here ever throws on malformed input. A capture is attacker-shaped
//    data by definition — every parse is defensive and every regex is bounded.
// =============================================================================

#region Model

/// <summary>Broad grouping used to organise findings in the report.</summary>
public enum FindingCategory
{
    Security,
    Privacy,
    Performance,
    Caching,
    Correctness,
    Compatibility,
    ApiDesign,
}

/// <summary>How much a finding matters. Ordered so higher is worse.</summary>
public enum FindingSeverity
{
    Info = 0,
    Low = 1,
    Medium = 2,
    High = 3,
    Critical = 4,
}

/// <summary>
/// One issue detected on one subject (a URL, a host, or the capture as a whole).
/// </summary>
public sealed class Finding
{
    public required string RuleId { get; init; }
    public required string Title { get; init; }
    public required FindingCategory Category { get; init; }
    public required FindingSeverity Severity { get; init; }

    /// <summary>What the finding is about — a URL, a host, a cookie name.</summary>
    public required string Subject { get; init; }

    /// <summary>One sentence explaining what was observed.</summary>
    public required string Detail { get; init; }

    /// <summary>The concrete change that resolves it.</summary>
    public string Remediation { get; init; } = string.Empty;

    /// <summary>The literal header/value/body excerpt that triggered the rule.</summary>
    public string? Evidence { get; init; }

    /// <summary>Session indices this finding was observed on (capped for display).</summary>
    public List<int> SessionIndices { get; } = new();

    /// <summary>How many transactions exhibited this exact finding.</summary>
    public int Occurrences { get; set; } = 1;

    /// <summary>A stable key used to merge identical findings across sessions.</summary>
    public string MergeKey => $"{RuleId}{Subject}";

    public override string ToString() =>
        $"[{Severity}] {Title} — {Subject}" + (Occurrences > 1 ? $" (x{Occurrences})" : "");
}

/// <summary>Per-category rollup shown in the report header.</summary>
public sealed class CategorySummary
{
    public required FindingCategory Category { get; init; }
    public int Findings { get; set; }
    public int Critical { get; set; }
    public int High { get; set; }
    public int Medium { get; set; }
    public int Low { get; set; }
    public int Info { get; set; }

    /// <summary>0–100; 100 means nothing was found in this category.</summary>
    public int Score { get; set; } = 100;
}

/// <summary>The complete result of an analysis run.</summary>
public sealed class AnalysisReport
{
    public DateTime GeneratedAt { get; init; } = DateTime.Now;
    public int SessionsAnalyzed { get; init; }
    public TimeSpan Duration { get; set; }

    public List<Finding> Findings { get; } = new();
    public List<CategorySummary> Categories { get; } = new();

    /// <summary>Aggregate 0–100 health score across every category.</summary>
    public int OverallScore { get; set; } = 100;

    /// <summary>A one-line verdict derived from <see cref="OverallScore"/>.</summary>
    public string Verdict => OverallScore switch
    {
        >= 95 => "Excellent — nothing significant found",
        >= 85 => "Good — a few things worth tightening",
        >= 70 => "Fair — several real issues to address",
        >= 50 => "Poor — multiple serious problems",
        _ => "Critical — this traffic needs attention now",
    };

    public int CountOf(FindingSeverity severity) => Findings.Count(f => f.Severity == severity);

    /// <summary>Total transactions implicated, counting repeats.</summary>
    public int TotalOccurrences => Findings.Sum(f => f.Occurrences);
}

/// <summary>Knobs that change what the analyzer looks for and how loudly.</summary>
public sealed class AnalysisOptions
{
    /// <summary>Responses slower than this are flagged. Milliseconds.</summary>
    public double SlowResponseMs { get; set; } = 1500;

    /// <summary>Responses slower than this are flagged more severely. Milliseconds.</summary>
    public double VerySlowResponseMs { get; set; } = 5000;

    /// <summary>Text responses above this size should have been compressed. Bytes.</summary>
    public long LargeUncompressedBytes { get; set; } = 32 * 1024;

    /// <summary>Any response above this size is called out regardless of type. Bytes.</summary>
    public long HugePayloadBytes { get; set; } = 5 * 1024 * 1024;

    /// <summary>Identical GETs beyond this count in one capture look like a caching miss.</summary>
    public int DuplicateRequestThreshold { get; set; } = 3;

    /// <summary>Requests to one host+path-shape beyond this look like an N+1 pattern.</summary>
    public int ChattyEndpointThreshold { get; set; } = 12;

    /// <summary>Skip checks that only make sense for first-party traffic.</summary>
    public HashSet<string> IgnoredHosts { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Categories to run. Empty means "all of them".</summary>
    public HashSet<FindingCategory> EnabledCategories { get; } = new();

    /// <summary>Findings below this severity are dropped from the report.</summary>
    public FindingSeverity MinimumSeverity { get; set; } = FindingSeverity.Info;

    /// <summary>Cap on session indices recorded per merged finding.</summary>
    public int MaxSessionIndicesPerFinding { get; set; } = 25;

    /// <summary>Don't scan bodies larger than this for secrets/PII. Bytes.</summary>
    public long MaxBodyScanBytes { get; set; } = 1024 * 1024;
}

#endregion

#region Rule contracts

/// <summary>Everything a rule needs about one transaction, parsed once.</summary>
public sealed class SessionContext
{
    public required HttpSession Session { get; init; }
    public required AnalysisOptions Options { get; init; }

    /// <summary>Parsed request URI, or null when the URL could not be parsed.</summary>
    public Uri? Uri { get; init; }

    public string Host => Session.Host;
    public bool IsTls => Session.IsTls;
    public int Status => Session.StatusCode;

    /// <summary>Response media type without parameters, lower-cased.</summary>
    public required string MediaType { get; init; }

    /// <summary>True when the response body is textual and worth scanning.</summary>
    public required bool IsTextResponse { get; init; }

    /// <summary>True when this looks like a JSON/GraphQL API call rather than a page asset.</summary>
    public required bool IsApiCall { get; init; }

    /// <summary>True for static assets (scripts, styles, images, fonts).</summary>
    public required bool IsStaticAsset { get; init; }

    /// <summary>True when this transaction never produced a response.</summary>
    public bool Failed => Session.Error is not null || Session.StatusCode == 0;

    private string? _responseText;

    /// <summary>Response body as text, decoded once and capped for scanning.</summary>
    public string ResponseText
    {
        get
        {
            if (_responseText is not null) return _responseText;
            if (!IsTextResponse || Session.ResponseBody.LongLength > Options.MaxBodyScanBytes)
                return _responseText = string.Empty;
            return _responseText = Session.ResponseBodyText;
        }
    }

    private string? _requestText;

    public string RequestText
    {
        get
        {
            if (_requestText is not null) return _requestText;
            if (Session.RequestBody.LongLength > Options.MaxBodyScanBytes)
                return _requestText = string.Empty;
            return _requestText = Session.RequestBodyText;
        }
    }

    public string? RequestHeader(string name) => Session.RequestHeaders[name];
    public string? ResponseHeader(string name) => Session.ResponseHeaders[name];
    public bool HasResponseHeader(string name) => Session.ResponseHeaders.Contains(name);
}

/// <summary>A check that looks at one transaction at a time.</summary>
public interface IAnalysisRule
{
    string Id { get; }
    string Title { get; }
    FindingCategory Category { get; }
    FindingSeverity DefaultSeverity { get; }

    /// <summary>Cheap pre-filter; skip the rule entirely when this is false.</summary>
    bool AppliesTo(SessionContext ctx) => true;

    IEnumerable<Finding> Inspect(SessionContext ctx);
}

/// <summary>A check that needs to see the whole capture at once.</summary>
public interface ICorrelationRule
{
    string Id { get; }
    string Title { get; }
    FindingCategory Category { get; }

    IEnumerable<Finding> Correlate(IReadOnlyList<SessionContext> all, AnalysisOptions options);
}

#endregion

/// <summary>
/// Runs the full rule set over a capture and produces an <see cref="AnalysisReport"/>.
/// Thread-safe and reusable: the rule instances hold no per-run state.
/// </summary>
public sealed class TrafficAnalyzer
{
    private readonly IReadOnlyList<IAnalysisRule> _rules;
    private readonly IReadOnlyList<ICorrelationRule> _correlationRules;

    public TrafficAnalyzer(IEnumerable<IAnalysisRule>? rules = null,
        IEnumerable<ICorrelationRule>? correlationRules = null)
    {
        _rules = (rules ?? DefaultRules()).ToList();
        _correlationRules = (correlationRules ?? DefaultCorrelationRules()).ToList();
    }

    /// <summary>Every per-session rule shipped with HttpSpy.</summary>
    public static IEnumerable<IAnalysisRule> DefaultRules() => new IAnalysisRule[]
    {
        // ---- Security ----
        new PlaintextTransportRule(),
        new BasicAuthOverHttpRule(),
        new MissingHstsRule(),
        new MissingContentTypeOptionsRule(),
        new MissingFrameOptionsRule(),
        new MissingContentSecurityPolicyRule(),
        new WeakContentSecurityPolicyRule(),
        new PermissiveCorsRule(),
        new CorsCredentialsWildcardRule(),
        new InsecureCookieRule(),
        new ServerBannerRule(),
        new DirectoryListingRule(),
        new StackTraceLeakRule(),
        new MissingReferrerPolicyRule(),
        new DangerousMethodRule(),

        // ---- Privacy ----
        new CredentialsInUrlRule(),
        new SecretInResponseRule(),
        new PiiInQueryStringRule(),
        new TokenInRefererRule(),

        // ---- Performance ----
        new SlowResponseRule(),
        new UncompressedTextRule(),
        new HugePayloadRule(),
        new UnoptimizedImageRule(),
        new RedirectChainHintRule(),
        new BlockingCookieBloatRule(),

        // ---- Caching ----
        new NoCacheHeadersRule(),
        new StaticAssetNotCacheableRule(),
        new PrivateDataCachedPubliclyRule(),

        // ---- Correctness ----
        new ContentLengthMismatchRule(),
        new WrongContentTypeRule(),
        new MalformedJsonRule(),
        new ServerErrorRule(),
        new ClientErrorRule(),
        new EmptySuccessBodyRule(),
        new TransportFailureRule(),

        // ---- Compatibility ----
        new LegacyProtocolRule(),
        new DeprecatedHeaderRule(),
        new MissingCharsetRule(),

        // ---- API design ----
        new ErrorStatusWithOkBodyRule(),
        new UnversionedApiRule(),
        new NonStandardStatusRule(),
    };

    /// <summary>Every cross-session rule shipped with HttpSpy.</summary>
    public static IEnumerable<ICorrelationRule> DefaultCorrelationRules() => new ICorrelationRule[]
    {
        new DuplicateRequestRule(),
        new ChattyEndpointRule(),
        new MixedContentRule(),
        new InconsistentTlsRule(),
        new RepeatedFailureRule(),
        new SessionFixationRule(),
    };

    /// <summary>Analyses a capture and returns a merged, scored report.</summary>
    public AnalysisReport Analyze(IEnumerable<HttpSession> sessions, AnalysisOptions? options = null,
        CancellationToken ct = default)
    {
        options ??= new AnalysisOptions();
        var started = DateTime.UtcNow;

        var contexts = sessions
            .Where(s => s is not null)
            .Where(s => !options.IgnoredHosts.Contains(s.Host))
            .Select(s => BuildContext(s, options))
            .ToList();

        var report = new AnalysisReport { SessionsAnalyzed = contexts.Count };

        // Findings are merged as they are produced so a capture with tens of
        // thousands of sessions never materialises tens of thousands of objects.
        var merged = new Dictionary<string, Finding>(StringComparer.Ordinal);

        foreach (var ctx in contexts)
        {
            ct.ThrowIfCancellationRequested();
            foreach (var rule in _rules)
            {
                if (!IsEnabled(rule.Category, options)) continue;
                if (!rule.AppliesTo(ctx)) continue;

                foreach (var finding in SafeInspect(rule, ctx))
                    Merge(merged, finding, ctx.Session.Index, options);
            }
        }

        foreach (var rule in _correlationRules)
        {
            ct.ThrowIfCancellationRequested();
            if (!IsEnabled(rule.Category, options)) continue;
            foreach (var finding in SafeCorrelate(rule, contexts, options))
                Merge(merged, finding, null, options);
        }

        report.Findings.AddRange(merged.Values
            .Where(f => f.Severity >= options.MinimumSeverity)
            .OrderByDescending(f => f.Severity)
            .ThenByDescending(f => f.Occurrences)
            .ThenBy(f => f.RuleId, StringComparer.Ordinal)
            .ThenBy(f => f.Subject, StringComparer.Ordinal));

        Score(report);
        report.Duration = DateTime.UtcNow - started;
        return report;
    }

    private static bool IsEnabled(FindingCategory category, AnalysisOptions options) =>
        options.EnabledCategories.Count == 0 || options.EnabledCategories.Contains(category);

    /// <summary>
    /// A rule that throws must not take the whole report down with it — the input
    /// is untrusted network data, so defensive isolation is worth the try/catch.
    /// </summary>
    private static IEnumerable<Finding> SafeInspect(IAnalysisRule rule, SessionContext ctx)
    {
        try { return rule.Inspect(ctx).ToList(); }
        catch (Exception ex) when (ex is not OperationCanceledException) { return Array.Empty<Finding>(); }
    }

    private static IEnumerable<Finding> SafeCorrelate(ICorrelationRule rule,
        IReadOnlyList<SessionContext> all, AnalysisOptions options)
    {
        try { return rule.Correlate(all, options).ToList(); }
        catch (Exception ex) when (ex is not OperationCanceledException) { return Array.Empty<Finding>(); }
    }

    private static void Merge(Dictionary<string, Finding> merged, Finding finding, int? sessionIndex,
        AnalysisOptions options)
    {
        if (merged.TryGetValue(finding.MergeKey, out var existing))
        {
            existing.Occurrences += finding.Occurrences;
            if (sessionIndex is { } idx && existing.SessionIndices.Count < options.MaxSessionIndicesPerFinding)
                existing.SessionIndices.Add(idx);
            return;
        }

        if (sessionIndex is { } first) finding.SessionIndices.Add(first);
        merged[finding.MergeKey] = finding;
    }

    /// <summary>
    /// Turns findings into 0–100 scores. Each finding subtracts a weight scaled by
    /// how many transactions it affected, with diminishing returns so one systemic
    /// issue cannot single-handedly zero out the score.
    /// </summary>
    private static void Score(AnalysisReport report)
    {
        foreach (FindingCategory category in Enum.GetValues<FindingCategory>())
        {
            var inCategory = report.Findings.Where(f => f.Category == category).ToList();
            var summary = new CategorySummary { Category = category, Findings = inCategory.Count };

            double penalty = 0;
            foreach (var f in inCategory)
            {
                switch (f.Severity)
                {
                    case FindingSeverity.Critical: summary.Critical++; break;
                    case FindingSeverity.High: summary.High++; break;
                    case FindingSeverity.Medium: summary.Medium++; break;
                    case FindingSeverity.Low: summary.Low++; break;
                    default: summary.Info++; break;
                }
                penalty += Weight(f.Severity) * RepeatFactor(f.Occurrences);
            }

            summary.Score = (int)Math.Round(Math.Clamp(100 - penalty, 0, 100));
            report.Categories.Add(summary);
        }

        // The overall score is the mean of the categories that actually had
        // something to say, so a clean capture is not diluted by empty categories.
        var scored = report.Categories.Where(c => c.Findings > 0).ToList();
        report.OverallScore = scored.Count == 0 ? 100 : (int)Math.Round(scored.Average(c => c.Score));
    }

    private static double Weight(FindingSeverity severity) => severity switch
    {
        FindingSeverity.Critical => 25,
        FindingSeverity.High => 12,
        FindingSeverity.Medium => 5,
        FindingSeverity.Low => 2,
        _ => 0.5,
    };

    /// <summary>Logarithmic so 100 occurrences hurt more than 1, but not 100x more.</summary>
    private static double RepeatFactor(int occurrences) =>
        occurrences <= 1 ? 1.0 : 1.0 + Math.Log10(occurrences);

    private static SessionContext BuildContext(HttpSession s, AnalysisOptions options)
    {
        Uri? uri = null;
        try { Uri.TryCreate(s.FullUrl, UriKind.Absolute, out uri); }
        catch (UriFormatException) { /* leave null */ }

        string media = s.ResponseContentTypeShort.ToLowerInvariant();
        var kind = s.ResponseBodyKind;
        bool text = kind is BodyContentType.Text or BodyContentType.Json or BodyContentType.Xml
            or BodyContentType.Html or BodyContentType.Css or BodyContentType.JavaScript
            or BodyContentType.Form;

        return new SessionContext
        {
            Session = s,
            Options = options,
            Uri = uri,
            MediaType = media,
            IsTextResponse = text,
            IsApiCall = LooksLikeApi(s, media),
            IsStaticAsset = LooksLikeStaticAsset(s, media),
        };
    }

    private static bool LooksLikeApi(HttpSession s, string media)
    {
        if (media.Contains("json") || media.Contains("grpc") || media.Contains("graphql")) return true;
        var path = s.Path.ToLowerInvariant();
        return path.Contains("/api/") || path.Contains("/graphql") || path.Contains("/rpc/") ||
               path.StartsWith("/v1/") || path.StartsWith("/v2/") || path.StartsWith("/v3/");
    }

    private static bool LooksLikeStaticAsset(HttpSession s, string media)
    {
        if (media.StartsWith("image/") || media.StartsWith("font/") ||
            media.Contains("javascript") || media.Contains("css")) return true;
        var path = s.Path.ToLowerInvariant();
        return StaticExtensions.Any(ext => path.EndsWith(ext, StringComparison.Ordinal));
    }

    private static readonly string[] StaticExtensions =
    {
        ".js", ".mjs", ".css", ".png", ".jpg", ".jpeg", ".gif", ".webp", ".avif", ".svg",
        ".ico", ".woff", ".woff2", ".ttf", ".otf", ".eot", ".map",
    };
}

#region Shared helpers

/// <summary>Small utilities shared by the rules; kept internal and side-effect free.</summary>
internal static class RuleHelpers
{
    /// <summary>Every regex here runs against untrusted input, so all are bounded.</summary>
    internal static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(150);

    /// <summary>Shortens a value for display in a finding.</summary>
    public static string Excerpt(string? value, int max = 160)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        var single = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return single.Length <= max ? single : single[..max] + "…";
    }

    /// <summary>Masks all but the first and last few characters of a secret.</summary>
    public static string Redact(string value)
    {
        if (value.Length <= 8) return new string('•', value.Length);
        return value[..3] + new string('•', Math.Min(12, value.Length - 6)) + value[^3..];
    }

    /// <summary>The URL without its query string — the unit most rules group by.</summary>
    public static string PathKey(SessionContext ctx)
    {
        if (ctx.Uri is not null) return $"{ctx.Uri.Scheme}://{ctx.Uri.Authority}{ctx.Uri.AbsolutePath}";
        return string.IsNullOrEmpty(ctx.Session.Host)
            ? ctx.Session.Path
            : $"{ctx.Session.Scheme}://{ctx.Session.Host}{ctx.Session.Path}";
    }

    /// <summary>
    /// Collapses numeric and UUID path segments so <c>/users/1</c> and
    /// <c>/users/2</c> group as one endpoint shape.
    /// </summary>
    public static string EndpointShape(SessionContext ctx)
    {
        var path = ctx.Uri?.AbsolutePath ?? ctx.Session.Path;
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(NormalizeSegment);
        return $"{ctx.Session.Host}/{string.Join('/', segments)}";
    }

    private static string NormalizeSegment(string segment)
    {
        if (segment.Length == 0) return segment;
        if (segment.All(char.IsDigit)) return "{id}";
        if (Guid.TryParse(segment, out _)) return "{uuid}";
        // Long hex blobs are almost always hashes or opaque ids.
        if (segment.Length >= 16 && segment.All(Uri.IsHexDigit)) return "{hash}";
        return segment;
    }

    public static bool IsSuccess(int status) => status is >= 200 and < 300;

    public static bool IsRedirect(int status) => status is 301 or 302 or 303 or 307 or 308;

    /// <summary>Parses the directives of a Cache-Control header into a set.</summary>
    public static HashSet<string> CacheDirectives(string? header)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(header)) return set;
        foreach (var raw in header.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int eq = raw.IndexOf('=');
            set.Add(eq > 0 ? raw[..eq].Trim() : raw);
        }
        return set;
    }

    /// <summary>Reads a numeric Cache-Control directive such as <c>max-age=600</c>.</summary>
    public static long? CacheDirectiveValue(string? header, string directive)
    {
        if (string.IsNullOrEmpty(header)) return null;
        foreach (var raw in header.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int eq = raw.IndexOf('=');
            if (eq <= 0) continue;
            if (!raw[..eq].Trim().Equals(directive, StringComparison.OrdinalIgnoreCase)) continue;
            if (long.TryParse(raw[(eq + 1)..].Trim().Trim('"'), out var value)) return value;
        }
        return null;
    }

    /// <summary>Splits a Set-Cookie value into its name and its attribute set.</summary>
    public static (string Name, string Value, HashSet<string> Attributes) ParseSetCookie(string raw)
    {
        var attributes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var parts = raw.Split(';');
        var pair = parts[0].Split('=', 2);
        string name = pair[0].Trim();
        string value = pair.Length > 1 ? pair[1].Trim() : string.Empty;

        for (int i = 1; i < parts.Length; i++)
        {
            var attr = parts[i].Trim();
            if (attr.Length == 0) continue;
            int eq = attr.IndexOf('=');
            attributes.Add(eq > 0 ? attr[..eq].Trim() : attr);
            if (eq > 0) attributes.Add(attr); // keep "SameSite=None" addressable too
        }
        return (name, value, attributes);
    }

    /// <summary>True when a Set-Cookie carries a SameSite attribute of any value.</summary>
    public static bool HasSameSite(HashSet<string> attributes) => attributes.Contains("SameSite");

    /// <summary>Reads the SameSite value, or null when the attribute is absent.</summary>
    public static string? SameSiteValue(HashSet<string> attributes)
    {
        foreach (var a in attributes)
        {
            int eq = a.IndexOf('=');
            if (eq > 0 && a[..eq].Trim().Equals("SameSite", StringComparison.OrdinalIgnoreCase))
                return a[(eq + 1)..].Trim();
        }
        return null;
    }

    /// <summary>Names that suggest a value is a session or auth credential.</summary>
    public static bool LooksLikeAuthCookie(string name) =>
        AuthCookieHints.Any(hint => name.Contains(hint, StringComparison.OrdinalIgnoreCase));

    private static readonly string[] AuthCookieHints =
        { "sess", "auth", "token", "jwt", "login", "sid", "csrf", "xsrf", "identity" };

    public static bool Matches(Regex regex, string input)
    {
        try { return regex.IsMatch(input); }
        catch (RegexMatchTimeoutException) { return false; }
    }

    public static IEnumerable<Match> MatchesOf(Regex regex, string input)
    {
        MatchCollection matches;
        try { matches = regex.Matches(input); }
        catch (RegexMatchTimeoutException) { yield break; }
        foreach (Match m in matches) yield return m;
    }

    public static Regex Compile(string pattern, RegexOptions extra = RegexOptions.None) =>
        new(pattern, RegexOptions.Compiled | RegexOptions.CultureInvariant | extra, RegexTimeout);
}

#endregion

#region Security rules

/// <summary>Flags credentials and sensitive endpoints served over plaintext HTTP.</summary>
public sealed class PlaintextTransportRule : IAnalysisRule
{
    public string Id => "SEC001";
    public string Title => "Sensitive request sent over plaintext HTTP";
    public FindingCategory Category => FindingCategory.Security;
    public FindingSeverity DefaultSeverity => FindingSeverity.High;

    public bool AppliesTo(SessionContext ctx) => !ctx.IsTls && ctx.Session.Kind != SessionKind.Tunnel;

    public IEnumerable<Finding> Inspect(SessionContext ctx)
    {
        bool hasCredential = ctx.RequestHeader("Authorization") is not null ||
                             ctx.RequestHeader("Cookie") is not null ||
                             ctx.Session.Method is "POST" or "PUT" or "PATCH";

        if (!hasCredential) yield break;

        yield return new Finding
        {
            RuleId = Id,
            Title = Title,
            Category = Category,
            Severity = ctx.RequestHeader("Authorization") is not null
                ? FindingSeverity.Critical
                : DefaultSeverity,
            Subject = RuleHelpers.PathKey(ctx),
            Detail = $"A {ctx.Session.Method} request carrying credentials or a body was sent unencrypted, " +
                     "so anyone on the network path can read and alter it.",
            Remediation = "Serve this endpoint over HTTPS and redirect HTTP to HTTPS with HSTS enabled.",
            Evidence = $"{ctx.Session.Method} {RuleHelpers.Excerpt(ctx.Session.FullUrl)}",
        };
    }
}

/// <summary>HTTP Basic authentication is base64, not encryption.</summary>
public sealed class BasicAuthOverHttpRule : IAnalysisRule
{
    public string Id => "SEC002";
    public string Title => "HTTP Basic credentials exposed";
    public FindingCategory Category => FindingCategory.Security;
    public FindingSeverity DefaultSeverity => FindingSeverity.Critical;

    public IEnumerable<Finding> Inspect(SessionContext ctx)
    {
        var auth = ctx.RequestHeader("Authorization");
        if (auth is null || !auth.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase)) yield break;

        string user = "unknown";
        try
        {
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(auth[6..].Trim()));
            int colon = decoded.IndexOf(':');
            if (colon > 0) user = decoded[..colon];
        }
        catch (FormatException) { /* not valid base64 — report it anyway */ }

        yield return new Finding
        {
            RuleId = Id,
            Title = Title,
            Category = Category,
            Severity = ctx.IsTls ? FindingSeverity.Medium : FindingSeverity.Critical,
            Subject = RuleHelpers.PathKey(ctx),
            Detail = ctx.IsTls
                ? $"Basic authentication sends the password on every request (user '{user}'). It is only as " +
                  "safe as the transport and is replayable if a single request leaks."
                : $"Basic credentials for user '{user}' were sent over plaintext HTTP — they are trivially recoverable.",
            Remediation = "Replace Basic auth with a bearer token or session cookie scoped to the endpoint, " +
                          "and never send it over plaintext HTTP.",
            Evidence = $"Authorization: Basic {RuleHelpers.Redact(auth[6..].Trim())}",
        };
    }
}

/// <summary>HTTPS responses should pin clients to HTTPS.</summary>
public sealed class MissingHstsRule : IAnalysisRule
{
    public string Id => "SEC003";
    public string Title => "Strict-Transport-Security header missing";
    public FindingCategory Category => FindingCategory.Security;
    public FindingSeverity DefaultSeverity => FindingSeverity.Medium;

    public bool AppliesTo(SessionContext ctx) =>
        ctx.IsTls && RuleHelpers.IsSuccess(ctx.Status) && ctx.MediaType.Contains("html");

    public IEnumerable<Finding> Inspect(SessionContext ctx)
    {
        if (ctx.HasResponseHeader("Strict-Transport-Security")) yield break;

        yield return new Finding
        {
            RuleId = Id,
            Title = Title,
            Category = Category,
            Severity = DefaultSeverity,
            Subject = ctx.Host,
            Detail = "This HTTPS document does not send Strict-Transport-Security, so a browser will still " +
                     "try plaintext HTTP for the next visit and can be stripped down to it.",
            Remediation = "Send 'Strict-Transport-Security: max-age=31536000; includeSubDomains' on HTTPS responses.",
        };
    }
}

/// <summary>MIME sniffing turns an uploaded text file into an executable script.</summary>
public sealed class MissingContentTypeOptionsRule : IAnalysisRule
{
    public string Id => "SEC004";
    public string Title => "X-Content-Type-Options: nosniff missing";
    public FindingCategory Category => FindingCategory.Security;
    public FindingSeverity DefaultSeverity => FindingSeverity.Low;

    public bool AppliesTo(SessionContext ctx) =>
        RuleHelpers.IsSuccess(ctx.Status) &&
        (ctx.MediaType.Contains("html") || ctx.MediaType.Contains("javascript") || ctx.MediaType.Contains("json"));

    public IEnumerable<Finding> Inspect(SessionContext ctx)
    {
        var value = ctx.ResponseHeader("X-Content-Type-Options");
        if (value is not null && value.Contains("nosniff", StringComparison.OrdinalIgnoreCase)) yield break;

        yield return new Finding
        {
            RuleId = Id,
            Title = Title,
            Category = Category,
            Severity = DefaultSeverity,
            Subject = ctx.Host,
            Detail = "Without 'nosniff' a browser may ignore the declared Content-Type and execute the " +
                     "response as script.",
            Remediation = "Add 'X-Content-Type-Options: nosniff' to every response.",
            Evidence = value is null ? null : $"X-Content-Type-Options: {value}",
        };
    }
}

/// <summary>Clickjacking protection for framed documents.</summary>
public sealed class MissingFrameOptionsRule : IAnalysisRule
{
    public string Id => "SEC005";
    public string Title => "No clickjacking protection on an HTML document";
    public FindingCategory Category => FindingCategory.Security;
    public FindingSeverity DefaultSeverity => FindingSeverity.Medium;

    public bool AppliesTo(SessionContext ctx) =>
        RuleHelpers.IsSuccess(ctx.Status) && ctx.MediaType.Contains("html");

    public IEnumerable<Finding> Inspect(SessionContext ctx)
    {
        if (ctx.HasResponseHeader("X-Frame-Options")) yield break;

        var csp = ctx.ResponseHeader("Content-Security-Policy");
        if (csp is not null && csp.Contains("frame-ancestors", StringComparison.OrdinalIgnoreCase)) yield break;

        yield return new Finding
        {
            RuleId = Id,
            Title = Title,
            Category = Category,
            Severity = DefaultSeverity,
            Subject = ctx.Host,
            Detail = "The document sets neither X-Frame-Options nor a CSP frame-ancestors directive, so it can " +
                     "be embedded in a hostile page and clickjacked.",
            Remediation = "Send 'Content-Security-Policy: frame-ancestors 'self'' (and X-Frame-Options: DENY " +
                          "for older clients).",
        };
    }
}

/// <summary>The single most effective XSS mitigation is often simply absent.</summary>
public sealed class MissingContentSecurityPolicyRule : IAnalysisRule
{
    public string Id => "SEC006";
    public string Title => "Content-Security-Policy missing";
    public FindingCategory Category => FindingCategory.Security;
    public FindingSeverity DefaultSeverity => FindingSeverity.Medium;

    public bool AppliesTo(SessionContext ctx) =>
        RuleHelpers.IsSuccess(ctx.Status) && ctx.MediaType.Contains("html");

    public IEnumerable<Finding> Inspect(SessionContext ctx)
    {
        if (ctx.HasResponseHeader("Content-Security-Policy")) yield break;

        yield return new Finding
        {
            RuleId = Id,
            Title = Title,
            Category = Category,
            Severity = ctx.HasResponseHeader("Content-Security-Policy-Report-Only")
                ? FindingSeverity.Low
                : DefaultSeverity,
            Subject = ctx.Host,
            Detail = ctx.HasResponseHeader("Content-Security-Policy-Report-Only")
                ? "Only a report-only CSP is present, so violations are logged but nothing is actually blocked."
                : "No Content-Security-Policy is set, so any injected script executes with full page privileges.",
            Remediation = "Ship a CSP starting from \"default-src 'self'\" and tighten script-src with nonces or hashes.",
        };
    }
}

/// <summary>A CSP that allows unsafe-inline or a wildcard is close to no CSP at all.</summary>
public sealed class WeakContentSecurityPolicyRule : IAnalysisRule
{
    public string Id => "SEC007";
    public string Title => "Content-Security-Policy is permissive enough to bypass";
    public FindingCategory Category => FindingCategory.Security;
    public FindingSeverity DefaultSeverity => FindingSeverity.Medium;

    public bool AppliesTo(SessionContext ctx) => ctx.HasResponseHeader("Content-Security-Policy");

    public IEnumerable<Finding> Inspect(SessionContext ctx)
    {
        var csp = ctx.ResponseHeader("Content-Security-Policy")!;
        var problems = new List<string>();

        if (csp.Contains("'unsafe-inline'", StringComparison.OrdinalIgnoreCase))
            problems.Add("'unsafe-inline' permits injected inline scripts");
        if (csp.Contains("'unsafe-eval'", StringComparison.OrdinalIgnoreCase))
            problems.Add("'unsafe-eval' permits eval() and string-to-code conversion");
        if (Regex.IsMatch(csp, @"(script-src|default-src)[^;]*\*(\s|;|$)",
                RegexOptions.IgnoreCase, RuleHelpers.RegexTimeout))
            problems.Add("a wildcard source allows script from any origin");
        if (csp.Contains("data:", StringComparison.OrdinalIgnoreCase) &&
            csp.Contains("script-src", StringComparison.OrdinalIgnoreCase))
            problems.Add("data: URIs are allowed as a script source");

        if (problems.Count == 0) yield break;

        yield return new Finding
        {
            RuleId = Id,
            Title = Title,
            Category = Category,
            Severity = problems.Count >= 2 ? FindingSeverity.High : DefaultSeverity,
            Subject = ctx.Host,
            Detail = "The policy is present but weak: " + string.Join("; ", problems) + ".",
            Remediation = "Remove 'unsafe-inline'/'unsafe-eval' and replace wildcards with explicit origins " +
                          "or per-request nonces.",
            Evidence = RuleHelpers.Excerpt(csp, 220),
        };
    }
}

/// <summary>A wildcard CORS origin on an authenticated API is a data leak.</summary>
public sealed class PermissiveCorsRule : IAnalysisRule
{
    public string Id => "SEC008";
    public string Title => "CORS allows any origin";
    public FindingCategory Category => FindingCategory.Security;
    public FindingSeverity DefaultSeverity => FindingSeverity.Medium;

    public IEnumerable<Finding> Inspect(SessionContext ctx)
    {
        var allow = ctx.ResponseHeader("Access-Control-Allow-Origin");
        if (allow is null || allow.Trim() != "*") yield break;

        // A wildcard on public static content is fine; on an authenticated API it is not.
        bool sensitive = ctx.RequestHeader("Authorization") is not null ||
                         ctx.RequestHeader("Cookie") is not null ||
                         ctx.IsApiCall;
        if (!sensitive) yield break;

        yield return new Finding
        {
            RuleId = Id,
            Title = Title,
            Category = Category,
            Severity = ctx.RequestHeader("Authorization") is not null ? FindingSeverity.High : DefaultSeverity,
            Subject = RuleHelpers.PathKey(ctx),
            Detail = "An authenticated or API response is readable by script from any origin.",
            Remediation = "Echo back only origins from an allow-list instead of '*'.",
            Evidence = "Access-Control-Allow-Origin: *",
        };
    }
}

/// <summary>Wildcard origin plus credentials is the classic CORS misconfiguration.</summary>
public sealed class CorsCredentialsWildcardRule : IAnalysisRule
{
    public string Id => "SEC009";
    public string Title => "CORS reflects the request origin while allowing credentials";
    public FindingCategory Category => FindingCategory.Security;
    public FindingSeverity DefaultSeverity => FindingSeverity.High;

    public bool AppliesTo(SessionContext ctx) => ctx.HasResponseHeader("Access-Control-Allow-Credentials");

    public IEnumerable<Finding> Inspect(SessionContext ctx)
    {
        var credentials = ctx.ResponseHeader("Access-Control-Allow-Credentials");
        if (credentials is null || !credentials.Trim().Equals("true", StringComparison.OrdinalIgnoreCase))
            yield break;

        var allow = ctx.ResponseHeader("Access-Control-Allow-Origin");
        var origin = ctx.RequestHeader("Origin");
        if (allow is null) yield break;

        // Reflecting whatever Origin arrived is equivalent to allowing everyone.
        bool reflects = origin is not null && allow.Trim().Equals(origin.Trim(), StringComparison.OrdinalIgnoreCase);
        bool wildcard = allow.Trim() == "*";
        if (!reflects && !wildcard) yield break;

        yield return new Finding
        {
            RuleId = Id,
            Title = Title,
            Category = Category,
            Severity = FindingSeverity.Critical,
            Subject = RuleHelpers.PathKey(ctx),
            Detail = wildcard
                ? "Allow-Origin '*' with Allow-Credentials: true is rejected by browsers but signals an " +
                  "intent that, once fixed by reflection, would expose authenticated data cross-origin."
                : "The server echoes the caller's Origin and allows credentials, so any site can read this " +
                  "user's authenticated responses.",
            Remediation = "Validate Origin against a fixed allow-list before echoing it, and only then set " +
                          "Access-Control-Allow-Credentials.",
            Evidence = $"Access-Control-Allow-Origin: {RuleHelpers.Excerpt(allow, 80)} + Allow-Credentials: true",
        };
    }
}

/// <summary>Session cookies without Secure / HttpOnly / SameSite.</summary>
public sealed class InsecureCookieRule : IAnalysisRule
{
    public string Id => "SEC010";
    public string Title => "Cookie set without full protection flags";
    public FindingCategory Category => FindingCategory.Security;
    public FindingSeverity DefaultSeverity => FindingSeverity.Medium;

    public bool AppliesTo(SessionContext ctx) => ctx.Session.ResponseHeaders.Contains("Set-Cookie");

    public IEnumerable<Finding> Inspect(SessionContext ctx)
    {
        foreach (var raw in ctx.Session.ResponseSetCookies())
        {
            var (name, _, attributes) = RuleHelpers.ParseSetCookie(raw);
            if (name.Length == 0) continue;

            var missing = new List<string>();
            if (!attributes.Contains("Secure") && ctx.IsTls) missing.Add("Secure");
            if (!attributes.Contains("HttpOnly")) missing.Add("HttpOnly");
            if (!RuleHelpers.HasSameSite(attributes)) missing.Add("SameSite");
            if (missing.Count == 0) continue;

            bool auth = RuleHelpers.LooksLikeAuthCookie(name);
            var severity = auth
                ? (missing.Contains("HttpOnly") ? FindingSeverity.High : FindingSeverity.Medium)
                : FindingSeverity.Low;

            yield return new Finding
            {
                RuleId = Id,
                Title = Title,
                Category = Category,
                Severity = severity,
                Subject = $"{ctx.Host} · {name}",
                Detail = auth
                    ? $"Session-like cookie '{name}' is missing {string.Join(", ", missing)}, so it is " +
                      "reachable from script or replayable cross-site."
                    : $"Cookie '{name}' is missing {string.Join(", ", missing)}.",
                Remediation = "Set 'Secure; HttpOnly; SameSite=Lax' (or Strict) on cookies that carry session state.",
                Evidence = RuleHelpers.Excerpt(raw, 140),
            };
        }
    }
}

/// <summary>SameSite=None without Secure is dropped by modern browsers.</summary>
public sealed class SessionFixationRule : ICorrelationRule
{
    public string Id => "SEC011";
    public string Title => "Session cookie re-issued without a login boundary";
    public FindingCategory Category => FindingCategory.Security;

    public IEnumerable<Finding> Correlate(IReadOnlyList<SessionContext> all, AnalysisOptions options)
    {
        // Track how many distinct values each session-like cookie was assigned.
        var values = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var ctx in all)
        {
            foreach (var raw in ctx.Session.ResponseSetCookies())
            {
                var (name, value, _) = RuleHelpers.ParseSetCookie(raw);
                if (!RuleHelpers.LooksLikeAuthCookie(name) || value.Length < 8) continue;
                var key = $"{ctx.Host}{name}";
                if (!values.TryGetValue(key, out var set)) values[key] = set = new HashSet<string>(StringComparer.Ordinal);
                set.Add(value);
            }
        }

        foreach (var (key, set) in values)
        {
            if (set.Count < 4) continue;
            var parts = key.Split('');
            yield return new Finding
            {
                RuleId = Id,
                Title = Title,
                Category = Category,
                Severity = FindingSeverity.Low,
                Subject = $"{parts[0]} · {parts[1]}",
                Detail = $"The session cookie '{parts[1]}' was assigned {set.Count} different values during this " +
                         "capture. Frequent re-issue is normal after login, but churn on ordinary requests " +
                         "usually means session state is being reset or is not sticky.",
                Remediation = "Rotate session identifiers at privilege boundaries (login, elevation) rather than " +
                              "on every response.",
                Occurrences = set.Count,
            };
        }
    }
}

/// <summary>Version banners hand attackers a shortcut to known CVEs.</summary>
public sealed class ServerBannerRule : IAnalysisRule
{
    public string Id => "SEC012";
    public string Title => "Server software and version disclosed";
    public FindingCategory Category => FindingCategory.Security;
    public FindingSeverity DefaultSeverity => FindingSeverity.Low;

    private static readonly string[] BannerHeaders =
        { "Server", "X-Powered-By", "X-AspNet-Version", "X-AspNetMvc-Version", "X-Generator", "X-Runtime" };

    private static readonly Regex VersionPattern = RuleHelpers.Compile(@"\d+\.\d+");

    public IEnumerable<Finding> Inspect(SessionContext ctx)
    {
        foreach (var header in BannerHeaders)
        {
            var value = ctx.ResponseHeader(header);
            if (string.IsNullOrWhiteSpace(value)) continue;
            // "Server: nginx" is unremarkable; "Server: nginx/1.18.0" is a version.
            if (header == "Server" && !RuleHelpers.Matches(VersionPattern, value)) continue;

            yield return new Finding
            {
                RuleId = Id,
                Title = Title,
                Category = Category,
                Severity = DefaultSeverity,
                Subject = $"{ctx.Host} · {header}",
                Detail = $"Responses advertise '{RuleHelpers.Excerpt(value, 60)}', which tells an attacker " +
                         "exactly which published vulnerabilities to try first.",
                Remediation = $"Suppress or genericise the {header} header at the edge.",
                Evidence = $"{header}: {RuleHelpers.Excerpt(value, 100)}",
            };
        }
    }
}

/// <summary>Auto-generated index pages expose the filesystem layout.</summary>
public sealed class DirectoryListingRule : IAnalysisRule
{
    public string Id => "SEC013";
    public string Title => "Directory listing exposed";
    public FindingCategory Category => FindingCategory.Security;
    public FindingSeverity DefaultSeverity => FindingSeverity.Medium;

    private static readonly Regex ListingPattern = RuleHelpers.Compile(
        @"<title>\s*Index of /|<h1>\s*Index of /|\[To Parent Directory\]", RegexOptions.IgnoreCase);

    public bool AppliesTo(SessionContext ctx) =>
        RuleHelpers.IsSuccess(ctx.Status) && ctx.MediaType.Contains("html") && ctx.ResponseText.Length > 0;

    public IEnumerable<Finding> Inspect(SessionContext ctx)
    {
        if (!RuleHelpers.Matches(ListingPattern, ctx.ResponseText)) yield break;

        yield return new Finding
        {
            RuleId = Id,
            Title = Title,
            Category = Category,
            Severity = DefaultSeverity,
            Subject = RuleHelpers.PathKey(ctx),
            Detail = "The server returned an auto-generated directory index, revealing files that were never " +
                     "meant to be discoverable.",
            Remediation = "Disable directory indexing (autoindex off / Options -Indexes) for this path.",
        };
    }
}

/// <summary>Stack traces tell an attacker the framework, the paths and often the query.</summary>
public sealed class StackTraceLeakRule : IAnalysisRule
{
    public string Id => "SEC014";
    public string Title => "Server stack trace returned to the client";
    public FindingCategory Category => FindingCategory.Security;
    public FindingSeverity DefaultSeverity => FindingSeverity.High;

    private static readonly Regex[] TracePatterns =
    {
        RuleHelpers.Compile(@"at [\w\.]+\.[\w`<>]+\([^)]*\)\s+in\s+.+:line \d+"),   // .NET
        RuleHelpers.Compile(@"\bTraceback \(most recent call last\)"),               // Python
        RuleHelpers.Compile(@"\bat [\w$.]+\([\w$.]+\.java:\d+\)"),                   // Java
        RuleHelpers.Compile(@"^\s+at .+ \(.*:\d+:\d+\)$", RegexOptions.Multiline),   // Node
        RuleHelpers.Compile(@"\bFatal error: Uncaught \w+"),                         // PHP
        RuleHelpers.Compile(@"\bORA-\d{5}|\bSQLSTATE\[\w+\]"),                       // database
    };

    public bool AppliesTo(SessionContext ctx) => ctx.ResponseText.Length > 0;

    public IEnumerable<Finding> Inspect(SessionContext ctx)
    {
        foreach (var pattern in TracePatterns)
        {
            if (!RuleHelpers.Matches(pattern, ctx.ResponseText)) continue;

            var line = ctx.ResponseText
                .Split('\n')
                .FirstOrDefault(l => RuleHelpers.Matches(pattern, l)) ?? string.Empty;

            yield return new Finding
            {
                RuleId = Id,
                Title = Title,
                Category = Category,
                Severity = DefaultSeverity,
                Subject = RuleHelpers.PathKey(ctx),
                Detail = "The response body contains a server-side stack trace, disclosing internal file paths, " +
                         "framework versions and code structure.",
                Remediation = "Return a generic error page in production and log the trace server-side only.",
                Evidence = RuleHelpers.Excerpt(line, 180),
            };
            yield break; // one finding per session is enough
        }
    }
}

/// <summary>Full referrers leak internal URLs to third parties.</summary>
public sealed class MissingReferrerPolicyRule : IAnalysisRule
{
    public string Id => "SEC015";
    public string Title => "Referrer-Policy not set";
    public FindingCategory Category => FindingCategory.Security;
    public FindingSeverity DefaultSeverity => FindingSeverity.Info;

    public bool AppliesTo(SessionContext ctx) =>
        RuleHelpers.IsSuccess(ctx.Status) && ctx.MediaType.Contains("html");

    public IEnumerable<Finding> Inspect(SessionContext ctx)
    {
        if (ctx.HasResponseHeader("Referrer-Policy")) yield break;

        yield return new Finding
        {
            RuleId = Id,
            Title = Title,
            Category = Category,
            Severity = DefaultSeverity,
            Subject = ctx.Host,
            Detail = "Without a Referrer-Policy the full URL — including path and query — is sent to every " +
                     "third-party resource the page loads.",
            Remediation = "Send 'Referrer-Policy: strict-origin-when-cross-origin'.",
        };
    }
}

/// <summary>TRACE and friends should not be reachable.</summary>
public sealed class DangerousMethodRule : IAnalysisRule
{
    public string Id => "SEC016";
    public string Title => "Dangerous HTTP method accepted";
    public FindingCategory Category => FindingCategory.Security;
    public FindingSeverity DefaultSeverity => FindingSeverity.Medium;

    private static readonly HashSet<string> Dangerous = new(StringComparer.OrdinalIgnoreCase)
        { "TRACE", "TRACK", "CONNECT", "PUT", "DELETE", "PATCH" };

    private static readonly HashSet<string> AlwaysDangerous = new(StringComparer.OrdinalIgnoreCase)
        { "TRACE", "TRACK" };

    public bool AppliesTo(SessionContext ctx) => Dangerous.Contains(ctx.Session.Method);

    public IEnumerable<Finding> Inspect(SessionContext ctx)
    {
        // PUT/DELETE/PATCH are normal REST verbs; only flag them when the server
        // answered a request that carried no authentication at all.
        bool always = AlwaysDangerous.Contains(ctx.Session.Method);
        bool unauthenticated = ctx.RequestHeader("Authorization") is null &&
                               ctx.RequestHeader("Cookie") is null &&
                               ctx.RequestHeader("X-Api-Key") is null;

        if (!always && !(unauthenticated && RuleHelpers.IsSuccess(ctx.Status))) yield break;
        if (!RuleHelpers.IsSuccess(ctx.Status)) yield break;

        yield return new Finding
        {
            RuleId = Id,
            Title = Title,
            Category = Category,
            Severity = always ? FindingSeverity.Medium : FindingSeverity.High,
            Subject = RuleHelpers.PathKey(ctx),
            Detail = always
                ? $"The server accepted a {ctx.Session.Method} request ({ctx.Status}), which can be used for " +
                  "cross-site tracing and header reflection."
                : $"A {ctx.Session.Method} request with no credentials succeeded ({ctx.Status}) — this endpoint " +
                  "appears to allow unauthenticated writes.",
            Remediation = always
                ? "Disable TRACE/TRACK at the web server."
                : "Require authentication and authorization for state-changing methods.",
            Evidence = $"{ctx.Session.Method} {RuleHelpers.Excerpt(ctx.Session.FullUrl)} → {ctx.Status}",
        };
    }
}

#endregion

#region Privacy rules

/// <summary>Tokens and keys in query strings end up in logs, history and referrers.</summary>
public sealed class CredentialsInUrlRule : IAnalysisRule
{
    public string Id => "PRV001";
    public string Title => "Credential or token in the URL";
    public FindingCategory Category => FindingCategory.Privacy;
    public FindingSeverity DefaultSeverity => FindingSeverity.High;

    private static readonly string[] SensitiveParams =
    {
        "password", "passwd", "pwd", "secret", "token", "access_token", "id_token", "refresh_token",
        "apikey", "api_key", "api-key", "auth", "authorization", "session", "sessionid", "sig",
        "signature", "key", "client_secret", "private_key",
    };

    // The userinfo check below is independent of the query string, so this rule
    // must also run for a credential-bearing URL that has no query at all.
    public bool AppliesTo(SessionContext ctx) =>
        !string.IsNullOrEmpty(ctx.Session.QueryString) ||
        !string.IsNullOrEmpty(ctx.Uri?.UserInfo);

    public IEnumerable<Finding> Inspect(SessionContext ctx)
    {
        foreach (var (name, value) in ctx.Session.QueryParameters())
        {
            if (value.Length == 0) continue;
            if (!SensitiveParams.Any(p => name.Equals(p, StringComparison.OrdinalIgnoreCase))) continue;

            yield return new Finding
            {
                RuleId = Id,
                Title = Title,
                Category = Category,
                Severity = DefaultSeverity,
                Subject = $"{RuleHelpers.PathKey(ctx)} · {name}",
                Detail = $"The query parameter '{name}' carries a credential. URLs are written to server access " +
                         "logs, browser history, and the Referer header sent to third parties.",
                Remediation = "Move the credential into a request header or a POST body.",
                Evidence = $"{name}={RuleHelpers.Redact(value)}",
            };
        }

        // The deprecated user:pass@host form.
        if (ctx.Uri is not null && !string.IsNullOrEmpty(ctx.Uri.UserInfo))
        {
            yield return new Finding
            {
                RuleId = Id,
                Title = "Credentials embedded in the URL authority",
                Category = Category,
                Severity = FindingSeverity.Critical,
                Subject = RuleHelpers.PathKey(ctx),
                Detail = "The URL uses the deprecated user:password@host form, exposing the credential " +
                         "everywhere the URL is recorded.",
                Remediation = "Use an Authorization header instead of URL userinfo.",
                Evidence = RuleHelpers.Redact(ctx.Uri.UserInfo),
            };
        }
    }
}

/// <summary>High-signal secret formats appearing in a response body.</summary>
public sealed class SecretInResponseRule : IAnalysisRule
{
    public string Id => "PRV002";
    public string Title => "Secret material in a response body";
    public FindingCategory Category => FindingCategory.Privacy;
    public FindingSeverity DefaultSeverity => FindingSeverity.Critical;

    // Deliberately narrow, anchored patterns: a generic "long random string"
    // matcher produces far more noise than signal on real traffic.
    private static readonly (string Label, Regex Pattern, FindingSeverity Severity)[] Signatures =
    {
        ("AWS access key id", RuleHelpers.Compile(@"\b(AKIA|ASIA)[0-9A-Z]{16}\b"), FindingSeverity.Critical),
        ("GitHub token", RuleHelpers.Compile(@"\bgh[pousr]_[A-Za-z0-9]{36,}\b"), FindingSeverity.Critical),
        ("Slack token", RuleHelpers.Compile(@"\bxox[baprs]-[A-Za-z0-9-]{10,}\b"), FindingSeverity.Critical),
        ("Google API key", RuleHelpers.Compile(@"\bAIza[0-9A-Za-z\-_]{35}\b"), FindingSeverity.High),
        ("Stripe secret key", RuleHelpers.Compile(@"\bsk_(live|test)_[0-9A-Za-z]{16,}\b"), FindingSeverity.Critical),
        ("private key block", RuleHelpers.Compile(@"-----BEGIN (RSA |EC |OPENSSH |PGP )?PRIVATE KEY-----"),
            FindingSeverity.Critical),
        ("JSON web token", RuleHelpers.Compile(@"\beyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\b"),
            FindingSeverity.Medium),
        ("connection string password",
            RuleHelpers.Compile(@"(?i)\b(password|pwd)\s*=\s*[^;""'\s]{4,}"), FindingSeverity.High),
    };

    public bool AppliesTo(SessionContext ctx) => ctx.ResponseText.Length > 0;

    public IEnumerable<Finding> Inspect(SessionContext ctx)
    {
        foreach (var (label, pattern, severity) in Signatures)
        {
            var match = RuleHelpers.MatchesOf(pattern, ctx.ResponseText).FirstOrDefault();
            if (match is null || !match.Success) continue;

            yield return new Finding
            {
                RuleId = Id,
                Title = $"{label} found in a response body",
                Category = Category,
                Severity = severity,
                Subject = $"{RuleHelpers.PathKey(ctx)} · {label}",
                Detail = $"A value matching the {label} format was returned to the client. If this is a real " +
                         "credential it should be treated as compromised.",
                Remediation = "Remove the secret from the response, rotate it, and audit who could have read it.",
                Evidence = RuleHelpers.Redact(match.Value),
            };
        }
    }
}

/// <summary>Personal data in a query string is logged everywhere.</summary>
public sealed class PiiInQueryStringRule : IAnalysisRule
{
    public string Id => "PRV003";
    public string Title => "Personal data in the query string";
    public FindingCategory Category => FindingCategory.Privacy;
    public FindingSeverity DefaultSeverity => FindingSeverity.Medium;

    private static readonly Regex EmailPattern =
        RuleHelpers.Compile(@"^[^@\s]+@[^@\s]+\.[A-Za-z]{2,}$");

    private static readonly string[] PiiParamHints =
        { "email", "e-mail", "mail", "phone", "tel", "ssn", "dob", "birth", "address", "postcode", "zip" };

    public bool AppliesTo(SessionContext ctx) => !string.IsNullOrEmpty(ctx.Session.QueryString);

    public IEnumerable<Finding> Inspect(SessionContext ctx)
    {
        foreach (var (name, value) in ctx.Session.QueryParameters())
        {
            if (value.Length == 0) continue;

            bool byName = PiiParamHints.Any(h => name.Contains(h, StringComparison.OrdinalIgnoreCase));
            bool byShape = RuleHelpers.Matches(EmailPattern, value);
            if (!byName && !byShape) continue;

            yield return new Finding
            {
                RuleId = Id,
                Title = Title,
                Category = Category,
                Severity = DefaultSeverity,
                Subject = $"{RuleHelpers.PathKey(ctx)} · {name}",
                Detail = $"The query parameter '{name}' appears to carry personal data. Query strings are " +
                         "retained in access logs and shared with third parties via the Referer header.",
                Remediation = "Send personal data in a POST body, or reference it by an opaque identifier.",
                Evidence = $"{name}={RuleHelpers.Redact(value)}",
            };
        }
    }
}

/// <summary>Tokens travelling in Referer headers reach whoever the page links to.</summary>
public sealed class TokenInRefererRule : IAnalysisRule
{
    public string Id => "PRV004";
    public string Title => "Referer header leaks a token to a third party";
    public FindingCategory Category => FindingCategory.Privacy;
    public FindingSeverity DefaultSeverity => FindingSeverity.Medium;

    private static readonly Regex TokenishQuery =
        RuleHelpers.Compile(@"[?&](token|access_token|code|session|sig|key|auth)=[^&]{8,}", RegexOptions.IgnoreCase);

    public bool AppliesTo(SessionContext ctx) => ctx.RequestHeader("Referer") is not null;

    public IEnumerable<Finding> Inspect(SessionContext ctx)
    {
        var referer = ctx.RequestHeader("Referer")!;
        if (!RuleHelpers.Matches(TokenishQuery, referer)) yield break;

        // Only interesting when the referrer crosses an origin boundary.
        string? refererHost = Uri.TryCreate(referer, UriKind.Absolute, out var r) ? r.Host : null;
        if (refererHost is null ||
            refererHost.Equals(ctx.Host, StringComparison.OrdinalIgnoreCase)) yield break;

        yield return new Finding
        {
            RuleId = Id,
            Title = Title,
            Category = Category,
            Severity = DefaultSeverity,
            Subject = $"{refererHost} → {ctx.Host}",
            Detail = $"A request to {ctx.Host} carried a Referer from {refererHost} whose query string contains " +
                     "what looks like a token or authorization code.",
            Remediation = "Set 'Referrer-Policy: strict-origin-when-cross-origin' and keep tokens out of URLs.",
            Evidence = RuleHelpers.Excerpt(referer, 140),
        };
    }
}

#endregion

#region Performance rules

/// <summary>Requests that took long enough for a user to notice.</summary>
public sealed class SlowResponseRule : IAnalysisRule
{
    public string Id => "PRF001";
    public string Title => "Slow response";
    public FindingCategory Category => FindingCategory.Performance;
    public FindingSeverity DefaultSeverity => FindingSeverity.Medium;

    public bool AppliesTo(SessionContext ctx) => ctx.Session.DurationMs > 0;

    public IEnumerable<Finding> Inspect(SessionContext ctx)
    {
        double ms = ctx.Session.DurationMs;
        if (ms < ctx.Options.SlowResponseMs) yield break;

        var t = ctx.Session.Timings;
        // Attribute the time so the finding says something actionable.
        string culprit = "overall";
        if (t.WaitMs > 0 && t.WaitMs > ms * 0.6) culprit = "server think time (time to first byte)";
        else if (t.ReceiveMs > 0 && t.ReceiveMs > ms * 0.6) culprit = "response download";
        else if (t.ConnectMs > 0 && t.ConnectMs > ms * 0.4) culprit = "connection setup";

        yield return new Finding
        {
            RuleId = Id,
            Title = Title,
            Category = Category,
            Severity = ms >= ctx.Options.VerySlowResponseMs ? FindingSeverity.High : DefaultSeverity,
            Subject = RuleHelpers.EndpointShape(ctx),
            Detail = $"Took {ms:F0} ms, dominated by {culprit}.",
            Remediation = culprit.StartsWith("server", StringComparison.Ordinal)
                ? "Profile the handler; look for slow queries or synchronous downstream calls."
                : "Reduce payload size, enable compression, or move the resource closer to the client.",
            Evidence = $"total {ms:F0} ms · connect {Fmt(t.ConnectMs)} · wait {Fmt(t.WaitMs)} · receive {Fmt(t.ReceiveMs)}",
        };
    }

    private static string Fmt(double v) => v < 0 ? "—" : $"{v:F0} ms";
}

/// <summary>Text that should have been gzip/br compressed but was not.</summary>
public sealed class UncompressedTextRule : IAnalysisRule
{
    public string Id => "PRF002";
    public string Title => "Large text response sent uncompressed";
    public FindingCategory Category => FindingCategory.Performance;
    public FindingSeverity DefaultSeverity => FindingSeverity.Medium;

    public bool AppliesTo(SessionContext ctx) =>
        ctx.IsTextResponse && RuleHelpers.IsSuccess(ctx.Status) &&
        ctx.Session.ResponseBodySize >= ctx.Options.LargeUncompressedBytes;

    public IEnumerable<Finding> Inspect(SessionContext ctx)
    {
        // OriginalContentEncoding is set by the proxy when it decoded the body,
        // so its absence means nothing was compressed on the wire.
        if (!string.IsNullOrEmpty(ctx.Session.OriginalContentEncoding)) yield break;
        if (ctx.HasResponseHeader("Content-Encoding")) yield break;

        // Only worth flagging when the client said it could accept compression.
        var accept = ctx.RequestHeader("Accept-Encoding");
        if (accept is null ||
            !(accept.Contains("gzip", StringComparison.OrdinalIgnoreCase) ||
              accept.Contains("br", StringComparison.OrdinalIgnoreCase))) yield break;

        long size = ctx.Session.ResponseBodySize;
        yield return new Finding
        {
            RuleId = Id,
            Title = Title,
            Category = Category,
            Severity = size >= ctx.Options.LargeUncompressedBytes * 8 ? FindingSeverity.High : DefaultSeverity,
            Subject = RuleHelpers.EndpointShape(ctx),
            Detail = $"{size:N0} bytes of {ctx.MediaType} were sent uncompressed even though the client " +
                     "advertised gzip/brotli support. Text of this kind typically compresses by 70–90%.",
            Remediation = "Enable gzip or brotli for text media types at the server or CDN.",
            Evidence = $"Accept-Encoding: {RuleHelpers.Excerpt(accept, 60)} · no Content-Encoding in the response",
        };
    }
}

/// <summary>Anything genuinely enormous, whatever its type.</summary>
public sealed class HugePayloadRule : IAnalysisRule
{
    public string Id => "PRF003";
    public string Title => "Very large response payload";
    public FindingCategory Category => FindingCategory.Performance;
    public FindingSeverity DefaultSeverity => FindingSeverity.Medium;

    public bool AppliesTo(SessionContext ctx) =>
        ctx.Session.ResponseBodySize >= ctx.Options.HugePayloadBytes;

    public IEnumerable<Finding> Inspect(SessionContext ctx)
    {
        long size = ctx.Session.ResponseBodySize;
        yield return new Finding
        {
            RuleId = Id,
            Title = Title,
            Category = Category,
            Severity = DefaultSeverity,
            Subject = RuleHelpers.EndpointShape(ctx),
            Detail = $"Returned {size / 1024.0 / 1024.0:F1} MB of {(ctx.MediaType.Length == 0 ? "data" : ctx.MediaType)} " +
                     "in a single response.",
            Remediation = ctx.IsApiCall
                ? "Paginate the endpoint or let callers select the fields they need."
                : "Split, stream, or lazily load this resource.",
        };
    }
}

/// <summary>Images in formats and sizes that modern pipelines would shrink.</summary>
public sealed class UnoptimizedImageRule : IAnalysisRule
{
    public string Id => "PRF004";
    public string Title => "Large image in a legacy format";
    public FindingCategory Category => FindingCategory.Performance;
    public FindingSeverity DefaultSeverity => FindingSeverity.Low;

    private const long LargeImageBytes = 200 * 1024;

    public bool AppliesTo(SessionContext ctx) =>
        ctx.MediaType.StartsWith("image/") && ctx.Session.ResponseBodySize >= LargeImageBytes;

    public IEnumerable<Finding> Inspect(SessionContext ctx)
    {
        // WebP/AVIF are already the modern formats; nothing to say about them.
        if (ctx.MediaType.Contains("webp") || ctx.MediaType.Contains("avif")) yield break;

        yield return new Finding
        {
            RuleId = Id,
            Title = Title,
            Category = Category,
            Severity = ctx.Session.ResponseBodySize >= LargeImageBytes * 5
                ? FindingSeverity.Medium
                : DefaultSeverity,
            Subject = RuleHelpers.PathKey(ctx),
            Detail = $"{ctx.Session.ResponseBodySize / 1024.0:F0} KB served as {ctx.MediaType}. " +
                     "WebP or AVIF typically cuts this by 25–50% at the same visual quality.",
            Remediation = "Serve WebP/AVIF with a <picture> fallback and size variants via srcset.",
        };
    }
}

/// <summary>A redirect that could have been avoided entirely.</summary>
public sealed class RedirectChainHintRule : IAnalysisRule
{
    public string Id => "PRF005";
    public string Title => "Avoidable redirect";
    public FindingCategory Category => FindingCategory.Performance;
    public FindingSeverity DefaultSeverity => FindingSeverity.Low;

    public bool AppliesTo(SessionContext ctx) => RuleHelpers.IsRedirect(ctx.Status);

    public IEnumerable<Finding> Inspect(SessionContext ctx)
    {
        var location = ctx.ResponseHeader("Location");
        if (string.IsNullOrEmpty(location)) yield break;

        string reason;
        if (!ctx.IsTls && location.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            reason = "an HTTP→HTTPS upgrade that HSTS preloading would make unnecessary";
        else if (Uri.TryCreate(location, UriKind.Absolute, out var target) &&
                 !target.Host.Equals(ctx.Host, StringComparison.OrdinalIgnoreCase))
            reason = $"a cross-host hop to {target.Host}";
        else if (location.TrimEnd('/') ==
                 (ctx.Uri?.AbsolutePath ?? ctx.Session.Path).TrimEnd('/'))
            reason = "a trailing-slash normalisation";
        else
            yield break;

        yield return new Finding
        {
            RuleId = Id,
            Title = Title,
            Category = Category,
            Severity = DefaultSeverity,
            Subject = RuleHelpers.PathKey(ctx),
            Detail = $"A {ctx.Status} redirect costs a full round trip and is caused here by {reason}.",
            Remediation = "Link directly to the final URL, and preload HSTS so browsers skip the plaintext hop.",
            Evidence = $"{ctx.Status} → {RuleHelpers.Excerpt(location, 120)}",
        };
    }
}

/// <summary>Cookies are sent on every request to the origin; large ones tax them all.</summary>
public sealed class BlockingCookieBloatRule : IAnalysisRule
{
    public string Id => "PRF006";
    public string Title => "Oversized cookie header on every request";
    public FindingCategory Category => FindingCategory.Performance;
    public FindingSeverity DefaultSeverity => FindingSeverity.Low;

    private const int LargeCookieBytes = 2048;

    public bool AppliesTo(SessionContext ctx) =>
        (ctx.RequestHeader("Cookie")?.Length ?? 0) >= LargeCookieBytes;

    public IEnumerable<Finding> Inspect(SessionContext ctx)
    {
        var cookie = ctx.RequestHeader("Cookie")!;
        yield return new Finding
        {
            RuleId = Id,
            Title = Title,
            Category = Category,
            Severity = cookie.Length >= LargeCookieBytes * 4 ? FindingSeverity.Medium : DefaultSeverity,
            Subject = ctx.Host,
            Detail = $"The Cookie header is {cookie.Length:N0} bytes and is re-sent with every single request " +
                     "to this origin, including static assets.",
            Remediation = "Trim unused cookies and serve static assets from a cookie-less domain or path.",
        };
    }
}

#endregion

#region Caching rules

/// <summary>Cacheable responses with no freshness information at all.</summary>
public sealed class NoCacheHeadersRule : IAnalysisRule
{
    public string Id => "CAC001";
    public string Title => "No caching directives on a cacheable response";
    public FindingCategory Category => FindingCategory.Caching;
    public FindingSeverity DefaultSeverity => FindingSeverity.Low;

    public bool AppliesTo(SessionContext ctx) =>
        ctx.Session.Method == "GET" && RuleHelpers.IsSuccess(ctx.Status) &&
        ctx.Session.ResponseBodySize > 0;

    public IEnumerable<Finding> Inspect(SessionContext ctx)
    {
        if (ctx.HasResponseHeader("Cache-Control") || ctx.HasResponseHeader("Expires") ||
            ctx.HasResponseHeader("ETag") || ctx.HasResponseHeader("Last-Modified")) yield break;

        yield return new Finding
        {
            RuleId = Id,
            Title = Title,
            Category = Category,
            Severity = ctx.IsStaticAsset ? FindingSeverity.Medium : DefaultSeverity,
            Subject = RuleHelpers.EndpointShape(ctx),
            Detail = "The response carries no Cache-Control, Expires, ETag or Last-Modified, so every client " +
                     "and proxy has to guess whether it may be reused.",
            Remediation = ctx.IsStaticAsset
                ? "Serve immutable, content-hashed assets with 'Cache-Control: public, max-age=31536000, immutable'."
                : "Set an explicit Cache-Control, even if it is 'no-store'.",
        };
    }
}

/// <summary>Fingerprinted assets that are nonetheless re-fetched constantly.</summary>
public sealed class StaticAssetNotCacheableRule : IAnalysisRule
{
    public string Id => "CAC002";
    public string Title => "Static asset is not cacheable for long";
    public FindingCategory Category => FindingCategory.Caching;
    public FindingSeverity DefaultSeverity => FindingSeverity.Low;

    private const long ShortMaxAgeSeconds = 3600;

    public bool AppliesTo(SessionContext ctx) =>
        ctx.IsStaticAsset && ctx.Session.Method == "GET" && RuleHelpers.IsSuccess(ctx.Status);

    public IEnumerable<Finding> Inspect(SessionContext ctx)
    {
        var cacheControl = ctx.ResponseHeader("Cache-Control");
        var directives = RuleHelpers.CacheDirectives(cacheControl);
        if (directives.Count == 0) yield break; // CAC001 already covers "nothing at all"

        if (directives.Contains("no-store") || directives.Contains("no-cache"))
        {
            yield return new Finding
            {
                RuleId = Id,
                Title = Title,
                Category = Category,
                Severity = FindingSeverity.Medium,
                Subject = RuleHelpers.EndpointShape(ctx),
                Detail = $"A static asset is served with '{RuleHelpers.Excerpt(cacheControl, 60)}', forcing a " +
                         "network round trip on every page load.",
                Remediation = "Content-hash the filename and cache it immutably instead of disabling caching.",
                Evidence = $"Cache-Control: {RuleHelpers.Excerpt(cacheControl, 80)}",
            };
            yield break;
        }

        var maxAge = RuleHelpers.CacheDirectiveValue(cacheControl, "max-age");
        if (maxAge is null || maxAge >= ShortMaxAgeSeconds) yield break;

        yield return new Finding
        {
            RuleId = Id,
            Title = Title,
            Category = Category,
            Severity = DefaultSeverity,
            Subject = RuleHelpers.EndpointShape(ctx),
            Detail = $"A static asset has max-age={maxAge}s, so it expires within the hour and is re-validated " +
                     "far more often than its content changes.",
            Remediation = "Version the URL and raise max-age to a year with 'immutable'.",
            Evidence = $"Cache-Control: {RuleHelpers.Excerpt(cacheControl, 80)}",
        };
    }
}

/// <summary>Authenticated responses that shared caches are allowed to store.</summary>
public sealed class PrivateDataCachedPubliclyRule : IAnalysisRule
{
    public string Id => "CAC003";
    public string Title => "Authenticated response marked publicly cacheable";
    public FindingCategory Category => FindingCategory.Caching;
    public FindingSeverity DefaultSeverity => FindingSeverity.High;

    public bool AppliesTo(SessionContext ctx) =>
        RuleHelpers.IsSuccess(ctx.Status) &&
        (ctx.RequestHeader("Authorization") is not null || ctx.RequestHeader("Cookie") is not null);

    public IEnumerable<Finding> Inspect(SessionContext ctx)
    {
        var cacheControl = ctx.ResponseHeader("Cache-Control");
        var directives = RuleHelpers.CacheDirectives(cacheControl);

        bool publiclyCacheable = directives.Contains("public");
        bool safe = directives.Contains("private") || directives.Contains("no-store");
        if (!publiclyCacheable || safe) yield break;

        // A response that varies correctly on the credential is fine.
        var vary = ctx.ResponseHeader("Vary") ?? string.Empty;
        if (vary.Contains("Authorization", StringComparison.OrdinalIgnoreCase) ||
            vary.Contains("Cookie", StringComparison.OrdinalIgnoreCase)) yield break;

        yield return new Finding
        {
            RuleId = Id,
            Title = Title,
            Category = Category,
            Severity = DefaultSeverity,
            Subject = RuleHelpers.EndpointShape(ctx),
            Detail = "A response to an authenticated request is marked 'public' and does not Vary on the " +
                     "credential, so a shared cache or CDN can serve one user's data to another.",
            Remediation = "Use 'Cache-Control: private' (or no-store) for per-user responses, or add " +
                          "'Vary: Authorization, Cookie'.",
            Evidence = $"Cache-Control: {RuleHelpers.Excerpt(cacheControl, 80)}" +
                       (vary.Length > 0 ? $" · Vary: {RuleHelpers.Excerpt(vary, 60)}" : " · no Vary"),
        };
    }
}

#endregion

#region Correctness rules

/// <summary>Declared Content-Length that disagrees with the body actually received.</summary>
public sealed class ContentLengthMismatchRule : IAnalysisRule
{
    public string Id => "COR001";
    public string Title => "Content-Length disagrees with the body";
    public FindingCategory Category => FindingCategory.Correctness;
    public FindingSeverity DefaultSeverity => FindingSeverity.Medium;

    public bool AppliesTo(SessionContext ctx) =>
        // Only meaningful when nothing rewrote the body between wire and capture.
        !ctx.Session.ResponseBodyTruncated &&
        string.IsNullOrEmpty(ctx.Session.OriginalContentEncoding) &&
        ctx.HasResponseHeader("Content-Length");

    public IEnumerable<Finding> Inspect(SessionContext ctx)
    {
        var raw = ctx.ResponseHeader("Content-Length");
        if (!long.TryParse(raw, out var declared)) yield break;

        long actual = ctx.Session.ResponseBodySize;
        if (declared == actual) yield break;

        // A body-less status legitimately declares the length a GET would return.
        if (ctx.Status is 204 or 304 || ctx.Session.Method == "HEAD") yield break;

        yield return new Finding
        {
            RuleId = Id,
            Title = Title,
            Category = Category,
            Severity = DefaultSeverity,
            Subject = RuleHelpers.EndpointShape(ctx),
            Detail = $"The response declared Content-Length: {declared:N0} but {actual:N0} bytes arrived. " +
                     "Clients may truncate the body or hang waiting for the rest.",
            Remediation = "Recompute Content-Length after any middleware rewrites the body, or use chunked framing.",
            Evidence = $"declared {declared:N0} · received {actual:N0}",
        };
    }
}

/// <summary>A body whose bytes plainly are not what the Content-Type claims.</summary>
public sealed class WrongContentTypeRule : IAnalysisRule
{
    public string Id => "COR002";
    public string Title => "Content-Type does not match the body";
    public FindingCategory Category => FindingCategory.Correctness;
    public FindingSeverity DefaultSeverity => FindingSeverity.Medium;

    public bool AppliesTo(SessionContext ctx) =>
        RuleHelpers.IsSuccess(ctx.Status) && ctx.Session.ResponseBodySize > 0 &&
        ctx.MediaType.Length > 0;

    public IEnumerable<Finding> Inspect(SessionContext ctx)
    {
        var body = ctx.Session.ResponseBody;
        string? actual = SniffMediaType(body);
        if (actual is null) yield break;

        bool consistent = ctx.MediaType.Contains(actual, StringComparison.OrdinalIgnoreCase) ||
                          (actual == "text" && ctx.IsTextResponse);
        if (consistent) yield break;

        yield return new Finding
        {
            RuleId = Id,
            Title = Title,
            Category = Category,
            Severity = DefaultSeverity,
            Subject = RuleHelpers.EndpointShape(ctx),
            Detail = $"The response is labelled '{ctx.MediaType}' but its first bytes look like {actual}. " +
                     "Clients that trust the label will fail to parse it.",
            Remediation = "Set the Content-Type the handler actually produces.",
            Evidence = $"declared {ctx.MediaType} · sniffed {actual}",
        };
    }

    /// <summary>Magic-number sniffing for the few formats worth being sure about.</summary>
    private static string? SniffMediaType(byte[] body)
    {
        if (body.Length < 4) return null;

        if (body[0] == 0x89 && body[1] == 'P' && body[2] == 'N' && body[3] == 'G') return "png";
        if (body[0] == 0xFF && body[1] == 0xD8 && body[2] == 0xFF) return "jpeg";
        if (body[0] == 'G' && body[1] == 'I' && body[2] == 'F') return "gif";
        if (body[0] == '%' && body[1] == 'P' && body[2] == 'D' && body[3] == 'F') return "pdf";
        if (body[0] == 'P' && body[1] == 'K' && body[2] == 0x03 && body[3] == 0x04) return "zip";
        if (body[0] == 0x1F && body[1] == 0x8B) return "gzip";
        if (body[0] == 'w' && body[1] == 'O' && body[2] == 'F' && body[3] == 'F') return "font";
        return null;
    }
}

/// <summary>A response that says it is JSON but does not parse.</summary>
public sealed class MalformedJsonRule : IAnalysisRule
{
    public string Id => "COR003";
    public string Title => "Response claims to be JSON but does not parse";
    public FindingCategory Category => FindingCategory.Correctness;
    public FindingSeverity DefaultSeverity => FindingSeverity.High;

    public bool AppliesTo(SessionContext ctx) =>
        ctx.MediaType.Contains("json") && ctx.Session.ResponseBodySize > 0 &&
        ctx.ResponseText.Length > 0;

    public IEnumerable<Finding> Inspect(SessionContext ctx)
    {
        var text = ctx.ResponseText;
        string? error = null;
        try
        {
            using var _ = JsonDocument.Parse(text, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
            });
        }
        catch (JsonException ex)
        {
            error = ex.Message;
        }

        if (error is null) yield break;

        yield return new Finding
        {
            RuleId = Id,
            Title = Title,
            Category = Category,
            Severity = DefaultSeverity,
            Subject = RuleHelpers.EndpointShape(ctx),
            Detail = $"Content-Type is '{ctx.MediaType}' but the body is not valid JSON: {error}",
            Remediation = "Fix the serializer, or label the response with the media type it actually returns.",
            Evidence = RuleHelpers.Excerpt(text, 160),
        };
    }
}

/// <summary>5xx responses grouped by endpoint.</summary>
public sealed class ServerErrorRule : IAnalysisRule
{
    public string Id => "COR004";
    public string Title => "Server error response";
    public FindingCategory Category => FindingCategory.Correctness;
    public FindingSeverity DefaultSeverity => FindingSeverity.High;

    public bool AppliesTo(SessionContext ctx) => ctx.Status >= 500;

    public IEnumerable<Finding> Inspect(SessionContext ctx)
    {
        yield return new Finding
        {
            RuleId = Id,
            Title = Title,
            Category = Category,
            Severity = DefaultSeverity,
            Subject = $"{RuleHelpers.EndpointShape(ctx)} → {ctx.Status}",
            Detail = $"The server returned {ctx.Status} {ctx.Session.StatusText}.",
            Remediation = "Check the server logs for this endpoint; a 5xx is always a server-side defect.",
            Evidence = RuleHelpers.Excerpt(ctx.ResponseText, 160),
        };
    }
}

/// <summary>4xx responses that suggest a broken integration rather than user error.</summary>
public sealed class ClientErrorRule : IAnalysisRule
{
    public string Id => "COR005";
    public string Title => "Client error response";
    public FindingCategory Category => FindingCategory.Correctness;
    public FindingSeverity DefaultSeverity => FindingSeverity.Low;

    public bool AppliesTo(SessionContext ctx) => ctx.Status is >= 400 and < 500;

    public IEnumerable<Finding> Inspect(SessionContext ctx)
    {
        // 401/403 on an unauthenticated request is the protocol working correctly.
        if (ctx.Status is 401 or 403 &&
            ctx.RequestHeader("Authorization") is null && ctx.RequestHeader("Cookie") is null) yield break;

        var severity = ctx.Status switch
        {
            404 when ctx.IsStaticAsset => FindingSeverity.Medium, // a broken asset reference
            401 or 403 => FindingSeverity.Medium,                 // credentials present but rejected
            429 => FindingSeverity.Medium,                        // being rate limited
            _ => DefaultSeverity,
        };

        yield return new Finding
        {
            RuleId = Id,
            Title = Title,
            Category = Category,
            Severity = severity,
            Subject = $"{RuleHelpers.EndpointShape(ctx)} → {ctx.Status}",
            Detail = ctx.Status switch
            {
                404 when ctx.IsStaticAsset => "A referenced static asset is missing, so a page is loading without it.",
                401 or 403 => "Credentials were sent but rejected, which usually means an expired or mis-scoped token.",
                429 => "The client is being rate limited.",
                _ => $"The server returned {ctx.Status} {ctx.Session.StatusText}.",
            },
            Remediation = ctx.Status == 429
                ? "Honour Retry-After and add client-side backoff."
                : "Verify the request the client is constructing against what the endpoint expects.",
            Evidence = RuleHelpers.Excerpt(ctx.ResponseText, 140),
        };
    }
}

/// <summary>A 200 with nothing in it is usually a bug on one side or the other.</summary>
public sealed class EmptySuccessBodyRule : IAnalysisRule
{
    public string Id => "COR006";
    public string Title => "Successful response with an empty body";
    public FindingCategory Category => FindingCategory.Correctness;
    public FindingSeverity DefaultSeverity => FindingSeverity.Low;

    public bool AppliesTo(SessionContext ctx) =>
        ctx.Status == 200 && ctx.Session.ResponseBodySize == 0 &&
        ctx.Session.Method is "GET" or "POST" &&
        ctx.Session.Kind is not (SessionKind.WebSocket or SessionKind.ServerSentEvents or SessionKind.Tunnel);

    public IEnumerable<Finding> Inspect(SessionContext ctx)
    {
        yield return new Finding
        {
            RuleId = Id,
            Title = Title,
            Category = Category,
            Severity = DefaultSeverity,
            Subject = RuleHelpers.EndpointShape(ctx),
            Detail = "A 200 OK carried no body. If there is genuinely nothing to return, 204 No Content states " +
                     "that explicitly and saves the client from parsing an empty payload.",
            Remediation = "Return 204 for intentionally empty responses; otherwise investigate why the body is missing.",
        };
    }
}

/// <summary>Connections that never completed.</summary>
public sealed class TransportFailureRule : IAnalysisRule
{
    public string Id => "COR007";
    public string Title => "Request failed at the transport layer";
    public FindingCategory Category => FindingCategory.Correctness;
    public FindingSeverity DefaultSeverity => FindingSeverity.High;

    public bool AppliesTo(SessionContext ctx) => ctx.Failed;

    public IEnumerable<Finding> Inspect(SessionContext ctx)
    {
        var error = ctx.Session.Error ?? "no response was received";
        yield return new Finding
        {
            RuleId = Id,
            Title = Title,
            Category = Category,
            Severity = DefaultSeverity,
            Subject = $"{ctx.Host}: {RuleHelpers.Excerpt(error, 60)}",
            Detail = $"The transaction never produced a response: {RuleHelpers.Excerpt(error, 200)}",
            Remediation = "Check DNS, connectivity, TLS trust and any upstream proxy configuration for this host.",
            Evidence = $"{ctx.Session.Method} {RuleHelpers.Excerpt(ctx.Session.FullUrl, 120)}",
        };
    }
}

#endregion

#region Compatibility rules

/// <summary>HTTP/1.0 and other stale protocol choices.</summary>
public sealed class LegacyProtocolRule : IAnalysisRule
{
    public string Id => "CMP001";
    public string Title => "Legacy HTTP version in use";
    public FindingCategory Category => FindingCategory.Compatibility;
    public FindingSeverity DefaultSeverity => FindingSeverity.Low;

    public bool AppliesTo(SessionContext ctx) =>
        ctx.Session.ResponseHttpVersion.Contains("1.0", StringComparison.Ordinal);

    public IEnumerable<Finding> Inspect(SessionContext ctx)
    {
        yield return new Finding
        {
            RuleId = Id,
            Title = Title,
            Category = Category,
            Severity = DefaultSeverity,
            Subject = ctx.Host,
            Detail = "The origin answered with HTTP/1.0, which has no persistent connections by default and no " +
                     "chunked transfer encoding.",
            Remediation = "Upgrade or reconfigure the origin to speak at least HTTP/1.1.",
            Evidence = $"response: {ctx.Session.ResponseHttpVersion}",
        };
    }
}

/// <summary>Headers that modern browsers ignore or that actively cause harm.</summary>
public sealed class DeprecatedHeaderRule : IAnalysisRule
{
    public string Id => "CMP002";
    public string Title => "Deprecated response header";
    public FindingCategory Category => FindingCategory.Compatibility;
    public FindingSeverity DefaultSeverity => FindingSeverity.Info;

    private static readonly (string Header, string Why)[] Deprecated =
    {
        ("X-XSS-Protection", "browsers removed the XSS auditor; the header can introduce vulnerabilities of its own"),
        ("P3P", "the P3P protocol is obsolete and ignored by every current browser"),
        ("X-UA-Compatible", "only ever affected Internet Explorer, which is retired"),
        ("Public-Key-Pins", "HPKP was removed from browsers because it bricks sites on key rotation"),
        ("Expect-CT", "Certificate Transparency is now enforced unconditionally"),
    };

    public IEnumerable<Finding> Inspect(SessionContext ctx)
    {
        foreach (var (header, why) in Deprecated)
        {
            var value = ctx.ResponseHeader(header);
            if (value is null) continue;

            yield return new Finding
            {
                RuleId = Id,
                Title = Title,
                Category = Category,
                Severity = header == "Public-Key-Pins" ? FindingSeverity.Medium : DefaultSeverity,
                Subject = $"{ctx.Host} · {header}",
                Detail = $"The response sends {header}, but {why}.",
                Remediation = $"Remove the {header} header.",
                Evidence = $"{header}: {RuleHelpers.Excerpt(value, 80)}",
            };
        }
    }
}

/// <summary>Text without a charset is decoded by guesswork.</summary>
public sealed class MissingCharsetRule : IAnalysisRule
{
    public string Id => "CMP003";
    public string Title => "Text response without a charset";
    public FindingCategory Category => FindingCategory.Compatibility;
    public FindingSeverity DefaultSeverity => FindingSeverity.Low;

    public bool AppliesTo(SessionContext ctx) =>
        RuleHelpers.IsSuccess(ctx.Status) && ctx.Session.ResponseBodySize > 0 &&
        (ctx.MediaType.StartsWith("text/") || ctx.MediaType.Contains("javascript"));

    public IEnumerable<Finding> Inspect(SessionContext ctx)
    {
        var contentType = ctx.ResponseHeader("Content-Type") ?? string.Empty;
        if (contentType.Contains("charset=", StringComparison.OrdinalIgnoreCase)) yield break;

        // Only interesting when the bytes are not plain ASCII, since ASCII decodes
        // identically under every encoding a client might guess.
        if (ctx.Session.ResponseBody.All(b => b < 0x80)) yield break;

        yield return new Finding
        {
            RuleId = Id,
            Title = Title,
            Category = Category,
            Severity = DefaultSeverity,
            Subject = RuleHelpers.EndpointShape(ctx),
            Detail = $"'{ctx.MediaType}' contains non-ASCII bytes but declares no charset, so the client has to " +
                     "guess the encoding — the classic source of mojibake.",
            Remediation = "Append '; charset=utf-8' to the Content-Type.",
            Evidence = $"Content-Type: {RuleHelpers.Excerpt(contentType, 80)}",
        };
    }
}

#endregion

#region API design rules

/// <summary>An error payload delivered with a 200 status.</summary>
public sealed class ErrorStatusWithOkBodyRule : IAnalysisRule
{
    public string Id => "API001";
    public string Title => "Error reported inside a 200 OK";
    public FindingCategory Category => FindingCategory.ApiDesign;
    public FindingSeverity DefaultSeverity => FindingSeverity.Medium;

    private static readonly Regex ErrorShape = RuleHelpers.Compile(
        @"""(error|errors|errorMessage|error_description)""\s*:\s*(""[^""]+""|\{|\[)", RegexOptions.IgnoreCase);

    private static readonly Regex SuccessFalse = RuleHelpers.Compile(
        @"""(success|ok)""\s*:\s*false", RegexOptions.IgnoreCase);

    public bool AppliesTo(SessionContext ctx) =>
        ctx.Status == 200 && ctx.MediaType.Contains("json") && ctx.ResponseText.Length > 0;

    public IEnumerable<Finding> Inspect(SessionContext ctx)
    {
        bool looksLikeError = RuleHelpers.Matches(ErrorShape, ctx.ResponseText) ||
                              RuleHelpers.Matches(SuccessFalse, ctx.ResponseText);
        if (!looksLikeError) yield break;

        // GraphQL legitimately returns 200 with an errors array; note it, don't scold.
        bool graphql = ctx.Session.Path.Contains("graphql", StringComparison.OrdinalIgnoreCase);

        yield return new Finding
        {
            RuleId = Id,
            Title = Title,
            Category = Category,
            Severity = graphql ? FindingSeverity.Info : DefaultSeverity,
            Subject = RuleHelpers.EndpointShape(ctx),
            Detail = graphql
                ? "The GraphQL endpoint returned errors inside a 200 response. That is per the GraphQL spec, but " +
                  "it means HTTP-level monitoring will not see these failures."
                : "The body describes an error while the status line says 200 OK. Caches, retries, monitoring and " +
                  "client error handling all key off the status code and will treat this as a success.",
            Remediation = graphql
                ? "Track GraphQL error rates separately from HTTP status metrics."
                : "Return a 4xx or 5xx status that matches the error in the body.",
            Evidence = RuleHelpers.Excerpt(ctx.ResponseText, 160),
        };
    }
}

/// <summary>API endpoints with no version in the path or headers.</summary>
public sealed class UnversionedApiRule : IAnalysisRule
{
    public string Id => "API002";
    public string Title => "API endpoint carries no version";
    public FindingCategory Category => FindingCategory.ApiDesign;
    public FindingSeverity DefaultSeverity => FindingSeverity.Info;

    private static readonly Regex VersionInPath = RuleHelpers.Compile(@"/v\d+(\.\d+)?(/|$)", RegexOptions.IgnoreCase);

    public bool AppliesTo(SessionContext ctx) => ctx.IsApiCall && RuleHelpers.IsSuccess(ctx.Status);

    public IEnumerable<Finding> Inspect(SessionContext ctx)
    {
        var path = ctx.Uri?.AbsolutePath ?? ctx.Session.Path;
        if (RuleHelpers.Matches(VersionInPath, path)) yield break;

        // A version may live in a header or in the Accept media type instead.
        var accept = ctx.RequestHeader("Accept") ?? string.Empty;
        if (accept.Contains("version=", StringComparison.OrdinalIgnoreCase) ||
            accept.Contains(".v", StringComparison.OrdinalIgnoreCase)) yield break;
        if (ctx.Session.RequestHeaders.Any(h =>
                h.Name.Contains("version", StringComparison.OrdinalIgnoreCase))) yield break;

        yield return new Finding
        {
            RuleId = Id,
            Title = Title,
            Category = Category,
            Severity = DefaultSeverity,
            Subject = RuleHelpers.EndpointShape(ctx),
            Detail = "The endpoint looks like an API but exposes no version in its path, Accept header or a " +
                     "custom header, which leaves no way to ship a breaking change safely.",
            Remediation = "Introduce a version segment (/v1/…) or media-type versioning before the first breaking change.",
        };
    }
}

/// <summary>Status codes outside the registered ranges.</summary>
public sealed class NonStandardStatusRule : IAnalysisRule
{
    public string Id => "API003";
    public string Title => "Non-standard HTTP status code";
    public FindingCategory Category => FindingCategory.ApiDesign;
    public FindingSeverity DefaultSeverity => FindingSeverity.Low;

    // Codes in common use that are not in the IANA core registry but are well understood.
    private static readonly HashSet<int> KnownExtensions = new()
        { 418, 419, 420, 422, 423, 424, 425, 426, 428, 429, 431, 440, 444, 449, 451, 494, 495, 496, 497, 499,
          507, 508, 509, 510, 511, 520, 521, 522, 523, 524, 525, 526, 527, 530 };

    public bool AppliesTo(SessionContext ctx) => ctx.Status > 0;

    public IEnumerable<Finding> Inspect(SessionContext ctx)
    {
        int status = ctx.Status;
        if (status is >= 100 and < 600 && (status % 100) < 30) yield break; // ordinary codes
        if (KnownExtensions.Contains(status)) yield break;
        if (status is >= 100 and < 600 && IsRegistered(status)) yield break;

        yield return new Finding
        {
            RuleId = Id,
            Title = Title,
            Category = Category,
            Severity = status is < 100 or >= 600 ? FindingSeverity.Medium : DefaultSeverity,
            Subject = $"{ctx.Host} → {status}",
            Detail = $"The server returned status {status}, which is not a registered HTTP status code. " +
                     "Intermediaries and client libraries handle unknown codes by their class digit at best.",
            Remediation = "Use a registered status code and convey the specifics in the response body.",
            Evidence = $"{status} {RuleHelpers.Excerpt(ctx.Session.StatusText, 40)}",
        };
    }

    private static bool IsRegistered(int status) => status is
        100 or 101 or 102 or 103 or
        200 or 201 or 202 or 203 or 204 or 205 or 206 or 207 or 208 or 226 or
        300 or 301 or 302 or 303 or 304 or 305 or 307 or 308 or
        400 or 401 or 402 or 403 or 404 or 405 or 406 or 407 or 408 or 409 or 410 or 411 or 412 or 413 or
        414 or 415 or 416 or 417 or
        500 or 501 or 502 or 503 or 504 or 505 or 506;
}

#endregion

#region Correlation rules

/// <summary>The same GET issued over and over inside one capture.</summary>
public sealed class DuplicateRequestRule : ICorrelationRule
{
    public string Id => "PRF010";
    public string Title => "Identical request repeated";
    public FindingCategory Category => FindingCategory.Performance;

    public IEnumerable<Finding> Correlate(IReadOnlyList<SessionContext> all, AnalysisOptions options)
    {
        var groups = all
            .Where(c => c.Session.Method == "GET" && RuleHelpers.IsSuccess(c.Status))
            .GroupBy(c => c.Session.FullUrl, StringComparer.Ordinal)
            .Where(g => g.Count() >= options.DuplicateRequestThreshold);

        foreach (var group in groups)
        {
            var first = group.First();
            int count = group.Count();

            // Repeats are only wasteful when the response was cacheable and unchanged.
            bool cacheable = !RuleHelpers.CacheDirectives(first.ResponseHeader("Cache-Control"))
                .Overlaps(new[] { "no-store", "no-cache" });
            long wasted = group.Skip(1).Sum(c => c.Session.ResponseBodySize);

            var finding = new Finding
            {
                RuleId = Id,
                Title = Title,
                Category = Category,
                Severity = count >= options.DuplicateRequestThreshold * 4
                    ? FindingSeverity.Medium
                    : FindingSeverity.Low,
                Subject = RuleHelpers.Excerpt(group.Key, 120),
                Detail = $"The exact same URL was fetched {count} times, transferring {wasted:N0} redundant bytes." +
                         (cacheable
                             ? " The response is cacheable, so these round trips could have been avoided."
                             : " The response is marked non-cacheable, which is why the client keeps re-fetching it."),
                Remediation = cacheable
                    ? "Cache the response client-side, or deduplicate the in-flight requests."
                    : "Relax the caching directives if the data does not genuinely change per request.",
                Occurrences = count,
            };
            foreach (var idx in group.Take(options.MaxSessionIndicesPerFinding).Select(c => c.Session.Index))
                finding.SessionIndices.Add(idx);
            yield return finding;
        }
    }
}

/// <summary>Many calls to one endpoint shape — the classic N+1 signature.</summary>
public sealed class ChattyEndpointRule : ICorrelationRule
{
    public string Id => "PRF011";
    public string Title => "Endpoint called repeatedly (possible N+1)";
    public FindingCategory Category => FindingCategory.Performance;

    public IEnumerable<Finding> Correlate(IReadOnlyList<SessionContext> all, AnalysisOptions options)
    {
        var groups = all
            .Where(c => c.IsApiCall)
            .GroupBy(RuleHelpers.EndpointShape, StringComparer.Ordinal)
            .Where(g => g.Count() >= options.ChattyEndpointThreshold);

        foreach (var group in groups)
        {
            int count = group.Count();
            // Distinct URLs within one shape is what separates N+1 from a plain repeat.
            int distinct = group.Select(c => c.Session.FullUrl).Distinct(StringComparer.Ordinal).Count();
            if (distinct < options.ChattyEndpointThreshold) continue;

            double totalMs = group.Sum(c => c.Session.DurationMs);
            var finding = new Finding
            {
                RuleId = Id,
                Title = Title,
                Category = Category,
                Severity = count >= options.ChattyEndpointThreshold * 3
                    ? FindingSeverity.High
                    : FindingSeverity.Medium,
                Subject = group.Key,
                Detail = $"{count} requests to {distinct} distinct resources under one endpoint shape, costing " +
                         $"{totalMs:F0} ms in total. This is the shape of an N+1: a list is fetched, then each " +
                         "item is fetched individually.",
                Remediation = "Add a batch or include/expand parameter so one round trip returns the whole set.",
                Occurrences = count,
            };
            foreach (var idx in group.Take(options.MaxSessionIndicesPerFinding).Select(c => c.Session.Index))
                finding.SessionIndices.Add(idx);
            yield return finding;
        }
    }
}

/// <summary>HTTPS pages pulling in plaintext subresources.</summary>
public sealed class MixedContentRule : ICorrelationRule
{
    public string Id => "SEC020";
    public string Title => "Mixed content on a secure page";
    public FindingCategory Category => FindingCategory.Security;

    public IEnumerable<Finding> Correlate(IReadOnlyList<SessionContext> all, AnalysisOptions options)
    {
        // Hosts that served at least one HTTPS document in this capture.
        var secureHosts = all
            .Where(c => c.IsTls && c.MediaType.Contains("html"))
            .Select(c => c.Host)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (secureHosts.Count == 0) yield break;

        var insecure = all
            .Where(c => !c.IsTls && c.Session.Kind != SessionKind.Tunnel)
            .Where(c =>
            {
                var referer = c.RequestHeader("Referer");
                return referer is not null &&
                       Uri.TryCreate(referer, UriKind.Absolute, out var r) &&
                       r.Scheme == "https" && secureHosts.Contains(r.Host);
            })
            .GroupBy(c => c.Host, StringComparer.OrdinalIgnoreCase);

        foreach (var group in insecure)
        {
            int count = group.Count();
            var finding = new Finding
            {
                RuleId = Id,
                Title = Title,
                Category = Category,
                Severity = FindingSeverity.High,
                Subject = group.Key,
                Detail = $"{count} plaintext HTTP request(s) to {group.Key} were made from an HTTPS page. " +
                         "Browsers block active mixed content outright and the rest is tamperable in transit.",
                Remediation = "Load every subresource over HTTPS, and add 'upgrade-insecure-requests' to the CSP.",
                Occurrences = count,
            };
            foreach (var idx in group.Take(options.MaxSessionIndicesPerFinding).Select(c => c.Session.Index))
                finding.SessionIndices.Add(idx);
            yield return finding;
        }
    }
}

/// <summary>A host reachable over both HTTP and HTTPS with no redirect.</summary>
public sealed class InconsistentTlsRule : ICorrelationRule
{
    public string Id => "SEC021";
    public string Title => "Host served over both HTTP and HTTPS";
    public FindingCategory Category => FindingCategory.Security;

    public IEnumerable<Finding> Correlate(IReadOnlyList<SessionContext> all, AnalysisOptions options)
    {
        var byHost = all
            .Where(c => c.Session.Kind is SessionKind.Http or SessionKind.Https)
            .GroupBy(c => c.Host, StringComparer.OrdinalIgnoreCase);

        foreach (var group in byHost)
        {
            if (string.IsNullOrEmpty(group.Key)) continue;

            var plaintext = group.Where(c => !c.IsTls).ToList();
            bool hasTls = group.Any(c => c.IsTls);
            if (!hasTls || plaintext.Count == 0) continue;

            // A plaintext request that immediately redirects to HTTPS is correct behaviour.
            var notRedirected = plaintext
                .Where(c => !RuleHelpers.IsRedirect(c.Status) ||
                            !(c.ResponseHeader("Location")?.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                              ?? false))
                .ToList();
            if (notRedirected.Count == 0) continue;

            var finding = new Finding
            {
                RuleId = Id,
                Title = Title,
                Category = Category,
                Severity = FindingSeverity.Medium,
                Subject = group.Key,
                Detail = $"{notRedirected.Count} request(s) were served over plaintext HTTP by a host that also " +
                         "supports HTTPS, without redirecting to the secure origin.",
                Remediation = "Redirect all HTTP traffic to HTTPS with a 301 and enable HSTS.",
                Occurrences = notRedirected.Count,
            };
            foreach (var idx in notRedirected.Take(options.MaxSessionIndicesPerFinding).Select(c => c.Session.Index))
                finding.SessionIndices.Add(idx);
            yield return finding;
        }
    }
}

/// <summary>One endpoint failing over and over is a different problem than one failure.</summary>
public sealed class RepeatedFailureRule : ICorrelationRule
{
    public string Id => "COR010";
    public string Title => "Endpoint failing consistently";
    public FindingCategory Category => FindingCategory.Correctness;

    public IEnumerable<Finding> Correlate(IReadOnlyList<SessionContext> all, AnalysisOptions options)
    {
        var groups = all
            .GroupBy(RuleHelpers.EndpointShape, StringComparer.Ordinal)
            .Where(g => g.Count() >= 3);

        foreach (var group in groups)
        {
            var total = group.Count();
            var failures = group.Count(c => c.Failed || c.Status >= 500);
            if (failures == 0) continue;

            double rate = (double)failures / total;
            if (rate < 0.5) continue;

            var finding = new Finding
            {
                RuleId = Id,
                Title = Title,
                Category = Category,
                Severity = rate >= 0.9 ? FindingSeverity.Critical : FindingSeverity.High,
                Subject = group.Key,
                Detail = $"{failures} of {total} requests to this endpoint failed ({rate:P0}). A failure rate this " +
                         "high is a broken dependency, not intermittent noise.",
                Remediation = "Treat this endpoint as down: check the service, its dependencies and any recent deploy.",
                Occurrences = failures,
            };
            foreach (var idx in group.Where(c => c.Failed || c.Status >= 500)
                         .Take(options.MaxSessionIndicesPerFinding).Select(c => c.Session.Index))
                finding.SessionIndices.Add(idx);
            yield return finding;
        }
    }
}

#endregion

#region Reporting

/// <summary>Renders an <see cref="AnalysisReport"/> to shareable text formats.</summary>
public static class AnalysisReportWriter
{
    /// <summary>A plain-text report suitable for a terminal, a ticket or an email.</summary>
    public static string ToText(AnalysisReport report)
    {
        var sb = new StringBuilder();
        sb.AppendLine("HttpSpy — Traffic Analysis Report");
        sb.AppendLine(new string('=', 70));
        sb.AppendLine($"Generated : {report.GeneratedAt:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"Sessions  : {report.SessionsAnalyzed:N0}");
        sb.AppendLine($"Duration  : {report.Duration.TotalMilliseconds:F0} ms");
        sb.AppendLine($"Score     : {report.OverallScore}/100 — {report.Verdict}");
        sb.AppendLine();

        sb.AppendLine("Severity breakdown");
        sb.AppendLine(new string('-', 70));
        foreach (var severity in Enum.GetValues<FindingSeverity>().OrderByDescending(s => s))
            sb.AppendLine($"  {severity,-9} {report.CountOf(severity),5}");
        sb.AppendLine();

        sb.AppendLine("By category");
        sb.AppendLine(new string('-', 70));
        foreach (var c in report.Categories.Where(c => c.Findings > 0).OrderBy(c => c.Score))
            sb.AppendLine($"  {c.Category,-14} score {c.Score,3}/100   " +
                          $"{c.Findings,3} finding(s)   " +
                          $"crit {c.Critical}  high {c.High}  med {c.Medium}  low {c.Low}  info {c.Info}");
        if (report.Findings.Count == 0) sb.AppendLine("  (nothing found)");
        sb.AppendLine();

        sb.AppendLine("Findings");
        sb.AppendLine(new string('-', 70));
        if (report.Findings.Count == 0)
        {
            sb.AppendLine("  No issues detected in this capture.");
            return sb.ToString();
        }

        int n = 0;
        foreach (var f in report.Findings)
        {
            sb.AppendLine();
            sb.AppendLine($"{++n,3}. [{f.Severity}] {f.Title}   ({f.RuleId}, {f.Category})");
            sb.AppendLine($"     Subject     : {f.Subject}");
            sb.AppendLine($"     Observed    : {f.Detail}");
            if (!string.IsNullOrEmpty(f.Evidence))
                sb.AppendLine($"     Evidence    : {f.Evidence}");
            if (!string.IsNullOrEmpty(f.Remediation))
                sb.AppendLine($"     Fix         : {f.Remediation}");
            if (f.Occurrences > 1)
                sb.AppendLine($"     Occurrences : {f.Occurrences:N0}");
            if (f.SessionIndices.Count > 0)
                sb.AppendLine($"     Sessions    : #{string.Join(", #", f.SessionIndices.Take(12))}" +
                              (f.SessionIndices.Count > 12 ? " …" : ""));
        }
        return sb.ToString();
    }

    /// <summary>A machine-readable report for CI pipelines and dashboards.</summary>
    public static string ToJson(AnalysisReport report) => JsonSerializer.Serialize(new
    {
        generatedAt = report.GeneratedAt.ToString("O", CultureInfo.InvariantCulture),
        sessionsAnalyzed = report.SessionsAnalyzed,
        durationMs = report.Duration.TotalMilliseconds,
        overallScore = report.OverallScore,
        verdict = report.Verdict,
        categories = report.Categories.Select(c => new
        {
            category = c.Category.ToString(),
            score = c.Score,
            findings = c.Findings,
            critical = c.Critical,
            high = c.High,
            medium = c.Medium,
            low = c.Low,
            info = c.Info,
        }),
        findings = report.Findings.Select(f => new
        {
            ruleId = f.RuleId,
            title = f.Title,
            category = f.Category.ToString(),
            severity = f.Severity.ToString(),
            subject = f.Subject,
            detail = f.Detail,
            evidence = f.Evidence,
            remediation = f.Remediation,
            occurrences = f.Occurrences,
            sessions = f.SessionIndices,
        }),
    }, new JsonSerializerOptions { WriteIndented = true });

    /// <summary>A self-contained HTML report, styled for both light and dark viewers.</summary>
    public static string ToHtml(AnalysisReport report)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<!DOCTYPE html><html><head><meta charset=\"utf-8\">");
        sb.AppendLine("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
        sb.AppendLine("<title>HttpSpy — Traffic Analysis</title><style>");
        sb.AppendLine(HtmlStyle);
        sb.AppendLine("</style></head><body>");

        sb.AppendLine("<header>");
        sb.AppendLine("<h1>HttpSpy — Traffic Analysis</h1>");
        sb.AppendLine($"<p class=\"meta\">{Escape(report.GeneratedAt.ToString("yyyy-MM-dd HH:mm:ss"))} · " +
                      $"{report.SessionsAnalyzed:N0} sessions · {report.Duration.TotalMilliseconds:F0} ms</p>");
        sb.AppendLine($"<div class=\"score s{ScoreBand(report.OverallScore)}\">" +
                      $"<span class=\"num\">{report.OverallScore}</span><span class=\"den\">/100</span>" +
                      $"<p>{Escape(report.Verdict)}</p></div>");
        sb.AppendLine("</header>");

        sb.AppendLine("<section><h2>Categories</h2><table><thead><tr>" +
                      "<th>Category</th><th>Score</th><th>Findings</th>" +
                      "<th>Critical</th><th>High</th><th>Medium</th><th>Low</th><th>Info</th>" +
                      "</tr></thead><tbody>");
        foreach (var c in report.Categories.Where(c => c.Findings > 0).OrderBy(c => c.Score))
        {
            sb.AppendLine($"<tr><td>{Escape(c.Category.ToString())}</td>" +
                          $"<td class=\"s{ScoreBand(c.Score)}\">{c.Score}</td><td>{c.Findings}</td>" +
                          $"<td>{c.Critical}</td><td>{c.High}</td><td>{c.Medium}</td>" +
                          $"<td>{c.Low}</td><td>{c.Info}</td></tr>");
        }
        sb.AppendLine("</tbody></table></section>");

        sb.AppendLine("<section><h2>Findings</h2>");
        if (report.Findings.Count == 0)
        {
            sb.AppendLine("<p class=\"empty\">No issues detected in this capture.</p>");
        }
        else
        {
            foreach (var f in report.Findings)
            {
                sb.AppendLine($"<article class=\"finding sev-{f.Severity.ToString().ToLowerInvariant()}\">");
                sb.AppendLine($"<h3><span class=\"badge\">{Escape(f.Severity.ToString())}</span> " +
                              $"{Escape(f.Title)}</h3>");
                sb.AppendLine($"<p class=\"subject\">{Escape(f.Subject)}</p>");
                sb.AppendLine($"<p>{Escape(f.Detail)}</p>");
                if (!string.IsNullOrEmpty(f.Evidence))
                    sb.AppendLine($"<pre>{Escape(f.Evidence)}</pre>");
                if (!string.IsNullOrEmpty(f.Remediation))
                    sb.AppendLine($"<p class=\"fix\"><strong>Fix:</strong> {Escape(f.Remediation)}</p>");
                sb.AppendLine($"<p class=\"meta\">{Escape(f.RuleId)} · {Escape(f.Category.ToString())}" +
                              (f.Occurrences > 1 ? $" · {f.Occurrences:N0} occurrences" : "") + "</p>");
                sb.AppendLine("</article>");
            }
        }
        sb.AppendLine("</section></body></html>");
        return sb.ToString();
    }

    private static int ScoreBand(int score) => score >= 85 ? 1 : score >= 60 ? 2 : 3;

    private static string Escape(string? value) => string.IsNullOrEmpty(value)
        ? string.Empty
        : value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");

    private const string HtmlStyle = """
        :root { color-scheme: light dark;
                --bg:#ffffff; --fg:#1a1d21; --muted:#5c6773; --line:#e2e6ea; --card:#f6f8fa; }
        @media (prefers-color-scheme: dark) {
          :root { --bg:#0d1117; --fg:#e6edf3; --muted:#8b949e; --line:#30363d; --card:#161b22; }
        }
        body { background:var(--bg); color:var(--fg); margin:0; padding:2rem;
               font:15px/1.6 -apple-system, "Segoe UI", Roboto, sans-serif; }
        h1 { margin:0 0 .25rem; font-size:1.6rem; }
        h2 { margin:2rem 0 .75rem; font-size:1.2rem; border-bottom:1px solid var(--line); padding-bottom:.4rem; }
        h3 { margin:0 0 .4rem; font-size:1rem; }
        .meta { color:var(--muted); font-size:.85rem; margin:.2rem 0; }
        .score { display:inline-block; margin:1rem 0; padding:1rem 1.5rem; border-radius:12px;
                 background:var(--card); border:1px solid var(--line); }
        .score .num { font-size:2.6rem; font-weight:700; }
        .score .den { color:var(--muted); }
        .score p { margin:.2rem 0 0; color:var(--muted); }
        .s1 { color:#1a7f37; } .s2 { color:#9a6700; } .s3 { color:#cf222e; }
        table { border-collapse:collapse; width:100%; max-width:900px; }
        th, td { text-align:left; padding:.45rem .8rem; border-bottom:1px solid var(--line); }
        th { color:var(--muted); font-weight:600; font-size:.85rem; }
        .finding { background:var(--card); border:1px solid var(--line); border-left-width:4px;
                   border-radius:8px; padding:1rem 1.2rem; margin:.75rem 0; }
        .sev-critical { border-left-color:#cf222e; } .sev-high { border-left-color:#e16f24; }
        .sev-medium { border-left-color:#d4a72c; }  .sev-low { border-left-color:#54aeff; }
        .sev-info { border-left-color:#8b949e; }
        .badge { display:inline-block; padding:.1rem .5rem; border-radius:999px; font-size:.72rem;
                 font-weight:700; letter-spacing:.03em; text-transform:uppercase;
                 background:var(--line); color:var(--fg); vertical-align:middle; margin-right:.4rem; }
        .subject { font-family:ui-monospace, Menlo, Consolas, monospace; font-size:.85rem; color:var(--muted);
                   word-break:break-all; margin:.1rem 0 .5rem; }
        pre { background:var(--bg); border:1px solid var(--line); border-radius:6px; padding:.6rem .8rem;
              overflow-x:auto; font-size:.82rem; margin:.5rem 0; }
        .fix { border-left:3px solid #1a7f37; padding-left:.7rem; margin:.6rem 0; }
        .empty { color:var(--muted); font-style:italic; }
        """;
}

#endregion
