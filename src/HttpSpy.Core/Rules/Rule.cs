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

    /// <summary>For Delay: artificial latency in milliseconds.</summary>
    public int DelayMs { get; set; }

    /// <summary>For Breakpoint: which phase(s) to pause on.</summary>
    public BreakpointPhase BreakpointPhase { get; set; } = BreakpointPhase.Both;

    /// <summary>For Highlight: ARGB color.</summary>
    public uint HighlightColor { get; set; } = 0xFFFFF2CC;

    public long HitCount { get; set; }

    private Regex? _compiled;
    private string? _compiledFor;

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
                return GetRegex(UrlPattern).IsMatch(url);
            case MatchMode.Wildcard:
            default:
                return GetRegex(WildcardToRegex(UrlPattern)).IsMatch(url);
        }
    }

    private Regex GetRegex(string pattern)
    {
        if (_compiled is null || _compiledFor != pattern)
        {
            _compiled = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.Compiled);
            _compiledFor = pattern;
        }
        return _compiled;
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
        clone._compiled = null;
        return clone;
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
