using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using HttpSpy.Core.Models;

namespace HttpSpy.Core.Rules;

/// <summary>How a rule's URL pattern is interpreted.</summary>
public enum MatchMode
{
    Contains,
    Wildcard,
    Regex,
    Exact
}

/// <summary>
/// A single user-defined traffic rule. The same type backs every feature in
/// the Rules tab (Auto-Reply, HTTP Modifier, breakpoints, highlighting,
/// blocking, redirecting) — the <see cref="Action"/> selects the behaviour.
/// </summary>
public sealed class Rule
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public bool Enabled { get; set; } = true;
    public string Name { get; set; } = "New rule";
    public RuleAction Action { get; set; } = RuleAction.None;

    // ---- Matching ------------------------------------------------------------
    public MatchMode UrlMatchMode { get; set; } = MatchMode.Wildcard;
    public string UrlPattern { get; set; } = "*";
    public string? MethodFilter { get; set; }
    public int? StatusFilter { get; set; }

    // ---- Action parameters ---------------------------------------------------
    /// <summary>For Redirect: the replacement URL. For AutoReply: a file path or inline marker.</summary>
    public string? RedirectUrl { get; set; }

    /// <summary>For AutoReply: the status code/body/headers to return.</summary>
    public int AutoReplyStatus { get; set; } = 200;
    public string AutoReplyReason { get; set; } = "OK";
    public string AutoReplyContentType { get; set; } = "text/plain";
    public string AutoReplyBody { get; set; } = string.Empty;

    /// <summary>For ModifyRequest/ModifyResponse: header set/remove operations.</summary>
    public List<HeaderEdit> HeaderEdits { get; set; } = new();

    /// <summary>For ModifyRequest/ModifyResponse: replace the whole body when set.</summary>
    public string? ReplacementBody { get; set; }

    /// <summary>
    /// For ModifyRequest/ModifyResponse: regex find/replace operations applied to
    /// the raw header block and/or the body (HTTP Debugger "HTTP Modifier" analog).
    /// Capture groups (<c>$1</c>) and the escapes <c>\r \n \t</c> are supported;
    /// <c>Content-Length</c> is recalculated automatically.
    /// </summary>
    public List<ModifierRule> ModifierRules { get; set; } = new();

    /// <summary>
    /// Optional extra regex match rules evaluated against the raw header block
    /// (case-insensitive). When present, all of them must match for the rule to
    /// fire — mirrors HTTP Debugger's multi-line "Match Rules" box.
    /// </summary>
    public List<string> HeaderMatchRegexes { get; set; } = new();

    /// <summary>For Delay: artificial latency in milliseconds.</summary>
    public int DelayMs { get; set; }

    /// <summary>For MapLocal: path to a local file served as the response body.</summary>
    public string? MapLocalPath { get; set; }

    // ---- Endpoint redirect (TCP/IP Redirector analog) ------------------------
    /// <summary>For RedirectEndpoint: the upstream host to connect to instead.</summary>
    public string? RedirectHost { get; set; }
    /// <summary>For RedirectEndpoint: the upstream port to connect to instead (0 = keep).</summary>
    public int RedirectPort { get; set; }
    /// <summary>For RedirectEndpoint: rewrite the Host header to the new endpoint.</summary>
    public bool RewriteHostHeader { get; set; }

    /// <summary>For Breakpoint: which phase(s) to pause on.</summary>
    public BreakpointPhase BreakpointPhase { get; set; } = BreakpointPhase.Both;

    // ---- Highlighting (Standard + RegExp rules) ------------------------------
    /// <summary>For Highlight: ARGB color.</summary>
    public uint HighlightColor { get; set; } = 0xFFFFF2CC;
    /// <summary>For Highlight (Standard): the column the rule is evaluated against.</summary>
    public HighlightColumn HighlightColumn { get; set; } = HighlightColumn.Status;
    /// <summary>For Highlight (Standard): the comparison operator.</summary>
    public HighlightOperator HighlightOperator { get; set; } = HighlightOperator.IsBigger;
    /// <summary>For Highlight (Standard): the comparison value (text or number).</summary>
    public string HighlightValue { get; set; } = "399";
    /// <summary>For Highlight (Standard, IsBetween): the upper bound.</summary>
    public string HighlightValue2 { get; set; } = string.Empty;

    /// <summary>For Bookmark: an optional comment attached to matching items.</summary>
    public string? BookmarkComment { get; set; }

    /// <summary>How many transactions this rule has fired on. Updated concurrently.</summary>
    [JsonIgnore]
    public long HitCount
    {
        get => Interlocked.Read(ref _hitCount);
        set => Interlocked.Exchange(ref _hitCount, value);
    }

    private long _hitCount;

    /// <summary>Atomically records one more hit and returns the new total.</summary>
    public long RecordHit() => Interlocked.Increment(ref _hitCount);

    /// <summary>
    /// The last regex compilation error for this rule's URL pattern, or null when
    /// the pattern is valid. Surfaced in the rule editor so a typo is visible
    /// instead of silently matching nothing.
    /// </summary>
    [JsonIgnore]
    public string? PatternError { get; private set; }

    /// <summary>
    /// Ceiling on how long a single user-supplied pattern may run. Without it a
    /// catastrophically backtracking pattern would stall a proxy worker — and
    /// with it, the client connection it is serving.
    /// </summary>
    internal static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);

    // Written as one unit under the lock and published as a single reference, so a
    // concurrent reader can never observe a regex paired with the wrong pattern.
    private sealed record CompiledPattern(string Source, Regex? Regex, string? Error);

    private volatile CompiledPattern? _compiled;
    private readonly object _compileGate = new();

    public bool Matches(string method, string url, int status)
    {
        if (!Enabled) return false;
        if (!string.IsNullOrEmpty(MethodFilter) &&
            !string.Equals(MethodFilter, method, StringComparison.OrdinalIgnoreCase))
            return false;
        if (StatusFilter.HasValue && status != 0 && StatusFilter.Value != status)
            return false;
        return UrlMatches(url);
    }

    public bool UrlMatches(string url)
    {
        if (string.IsNullOrEmpty(UrlPattern) || UrlPattern == "*") return true;
        switch (UrlMatchMode)
        {
            case MatchMode.Contains:
                return url.Contains(UrlPattern, StringComparison.OrdinalIgnoreCase);
            case MatchMode.Exact:
                return string.Equals(url, UrlPattern, StringComparison.OrdinalIgnoreCase);
            case MatchMode.Regex:
                return IsRegexMatch(UrlPattern, url);
            case MatchMode.Wildcard:
            default:
                return IsRegexMatch(WildcardToRegex(UrlPattern), url);
        }
    }

    /// <summary>
    /// Matches with a compiled, cached regex. An invalid pattern or a match that
    /// blows the timeout is recorded on <see cref="PatternError"/> and treated as
    /// "no match" — a bad rule must never take the proxy down with it.
    /// </summary>
    private bool IsRegexMatch(string pattern, string input)
    {
        var compiled = GetCompiled(pattern);
        if (compiled.Regex is null) return false;
        try
        {
            return compiled.Regex.IsMatch(input);
        }
        catch (RegexMatchTimeoutException)
        {
            PatternError = $"Pattern timed out after {RegexTimeout.TotalMilliseconds:F0} ms; rule skipped.";
            return false;
        }
    }

    private CompiledPattern GetCompiled(string pattern)
    {
        var current = _compiled;
        if (current is not null && current.Source == pattern) return current;

        lock (_compileGate)
        {
            current = _compiled;
            if (current is not null && current.Source == pattern) return current;

            CompiledPattern built;
            try
            {
                built = new CompiledPattern(pattern,
                    new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.Compiled, RegexTimeout), null);
            }
            catch (ArgumentException ex)
            {
                built = new CompiledPattern(pattern, null, ex.Message);
            }

            PatternError = built.Error;
            _compiled = built;
            return built;
        }
    }

    /// <summary>Validates the current URL pattern, returning the error message if any.</summary>
    public string? ValidatePattern()
    {
        if (string.IsNullOrEmpty(UrlPattern) || UrlPattern == "*") { PatternError = null; return null; }
        if (UrlMatchMode is MatchMode.Contains or MatchMode.Exact) { PatternError = null; return null; }
        var pattern = UrlMatchMode == MatchMode.Regex ? UrlPattern : WildcardToRegex(UrlPattern);
        return GetCompiled(pattern).Error;
    }

    public static string WildcardToRegex(string wildcard)
    {
        var escaped = Regex.Escape(wildcard).Replace("\\*", ".*").Replace("\\?", ".");
        return "^" + escaped + "$";
    }

    public Rule Clone()
    {
        var clone = (Rule)MemberwiseClone();
        clone.Id = Guid.NewGuid();
        clone.HeaderEdits = HeaderEdits.Select(h => h.Clone()).ToList();
        clone.ModifierRules = ModifierRules.Select(m => m.Clone()).ToList();
        clone.HeaderMatchRegexes = new List<string>(HeaderMatchRegexes);
        clone._compiled = null;
        clone._hitCount = 0;
        return clone;
    }

    /// <summary>
    /// Evaluates the optional <see cref="HeaderMatchRegexes"/> (all must match)
    /// against a raw header block. Returns true when there are no extra rules.
    /// </summary>
    public bool HeaderRegexesMatch(string rawHeaderBlock)
    {
        if (HeaderMatchRegexes.Count == 0) return true;
        foreach (var pattern in HeaderMatchRegexes)
        {
            if (string.IsNullOrWhiteSpace(pattern)) continue;
            try
            {
                if (!Regex.IsMatch(rawHeaderBlock, pattern,
                        RegexOptions.IgnoreCase | RegexOptions.Multiline, RegexTimeout))
                    return false;
            }
            catch (ArgumentException) { return false; }
            catch (RegexMatchTimeoutException) { return false; }
        }
        return true;
    }
}

/// <summary>A single header mutation applied by a Modify rule.</summary>
public sealed class HeaderEdit
{
    public enum Op { Set, Remove }

    public Op Operation { get; set; } = Op.Set;
    public string Name { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;

    public HeaderEdit Clone() => new() { Operation = Operation, Name = Name, Value = Value };
}

/// <summary>
/// One regex find/replace operation of the HTTP Modifier. The replacement
/// supports .NET substitution groups (<c>$1</c>) plus the literal escapes
/// <c>\r \n \t</c>.
/// </summary>
public sealed class ModifierRule
{
    public ModifierTarget Target { get; set; } = ModifierTarget.RequestHeaders;
    public string Find { get; set; } = string.Empty;
    public string Replace { get; set; } = string.Empty;

    public ModifierRule Clone() => new() { Target = Target, Find = Find, Replace = Replace };
}
