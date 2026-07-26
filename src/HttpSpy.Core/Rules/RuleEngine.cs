using System.IO;
using System.Text;
using HttpSpy.Core.Models;

namespace HttpSpy.Core.Rules;

/// <summary>The decision returned by the rule engine for an outgoing request.</summary>
public sealed class RequestDecision
{
    public bool Block { get; set; }
    public string? RedirectUrl { get; set; }
    public int DelayMs { get; set; }
    public bool Breakpoint { get; set; }

    /// <summary>When set, short-circuit the request and return this canned response.</summary>
    public AutoReply? AutoReply { get; set; }

    // ---- Endpoint redirect (TCP/IP Redirector analog) ------------------------
    /// <summary>When set, connect to this host instead of the original one.</summary>
    public string? ConnectHost { get; set; }
    /// <summary>When &gt; 0, connect to this port instead of the original one.</summary>
    public int ConnectPort { get; set; }
    /// <summary>Whether the Host header should be rewritten to the redirected endpoint.</summary>
    public bool RewriteHost { get; set; }
}

/// <summary>A canned response produced by an Auto-Reply rule.</summary>
public sealed class AutoReply
{
    public int Status { get; set; } = 200;
    public string Reason { get; set; } = "OK";
    public string ContentType { get; set; } = "text/plain";
    public byte[] Body { get; set; } = Array.Empty<byte>();
}

/// <summary>
/// Holds the active rule set and applies it at the request and response phases.
/// Thread-safe for concurrent reads while the UI mutates the list.
/// </summary>
public sealed class RuleEngine
{
    private readonly object _gate = new();
    private List<Rule> _rules = new();

    public IReadOnlyList<Rule> Rules
    {
        get { lock (_gate) return _rules.ToList(); }
    }

    public void SetRules(IEnumerable<Rule> rules)
    {
        lock (_gate) _rules = rules.ToList();
    }

    public void Add(Rule rule) { lock (_gate) _rules.Add(rule); }

    public void Remove(Guid id) { lock (_gate) _rules.RemoveAll(r => r.Id == id); }

    private List<Rule> Snapshot() { lock (_gate) return _rules.Where(r => r.Enabled).ToList(); }

    /// <summary>Evaluates rules that apply before the request is sent upstream.</summary>
    public RequestDecision EvaluateRequest(HttpSession session)
    {
        var decision = new RequestDecision();
        foreach (var rule in Snapshot())
        {
            if (!rule.Matches(session.Method, session.FullUrl, 0)) continue;
            rule.RecordHit(); // atomic: many connections evaluate rules concurrently
            switch (rule.Action)
            {
                case RuleAction.Block:
                    decision.Block = true;
                    return decision;
                case RuleAction.Redirect:
                    if (!string.IsNullOrEmpty(rule.RedirectUrl)) decision.RedirectUrl = rule.RedirectUrl;
                    break;
                case RuleAction.Delay:
                    decision.DelayMs = Math.Max(decision.DelayMs, rule.DelayMs);
                    break;
                case RuleAction.Breakpoint:
                    if (rule.BreakpointPhase is BreakpointPhase.BeforeRequest or BreakpointPhase.Both)
                        decision.Breakpoint = true;
                    break;
                case RuleAction.AutoReply:
                    decision.AutoReply = new AutoReply
                    {
                        Status = rule.AutoReplyStatus,
                        Reason = rule.AutoReplyReason,
                        ContentType = rule.AutoReplyContentType,
                        Body = Encoding.UTF8.GetBytes(rule.AutoReplyBody)
                    };
                    return decision;
                case RuleAction.MapLocal:
                    decision.AutoReply = ReadMappedFile(rule.MapLocalPath);
                    return decision;
                case RuleAction.RedirectEndpoint:
                    if (!string.IsNullOrEmpty(rule.RedirectHost))
                        decision.ConnectHost = rule.RedirectHost;
                    if (rule.RedirectPort > 0)
                        decision.ConnectPort = rule.RedirectPort;
                    decision.RewriteHost = rule.RewriteHostHeader;
                    break;
                case RuleAction.Bookmark:
                    if (rule.HeaderRegexesMatch(HttpModifier.RawHeaderBlock(session.RequestHeaders)))
                    {
                        session.Bookmarked = true;
                        if (!string.IsNullOrEmpty(rule.BookmarkComment))
                            session.Comment = rule.BookmarkComment;
                    }
                    break;
                case RuleAction.ModifyRequest:
                    if (!rule.HeaderRegexesMatch(HttpModifier.RawHeaderBlock(session.RequestHeaders)))
                        break;
                    ApplyHeaderEdits(session.RequestHeaders, rule.HeaderEdits);
                    HttpModifier.Apply(rule, session, responsePhase: false);
                    if (rule.ReplacementBody is not null)
                    {
                        session.RequestBody = Encoding.UTF8.GetBytes(rule.ReplacementBody);
                        if (!IsChunked(session.RequestHeaders))
                            session.RequestHeaders.Set("Content-Length", session.RequestBody.Length.ToString());
                    }
                    break;
            }
        }
        return decision;
    }

    /// <summary>Evaluates rules that apply once the response head/body are known.</summary>
    public bool EvaluateResponse(HttpSession session, out bool breakpoint)
    {
        breakpoint = false;
        bool changed = false;
        foreach (var rule in Snapshot())
        {
            if (!rule.Matches(session.Method, session.FullUrl, session.StatusCode)) continue;

            // Response-only actions never reach EvaluateRequest's counter, so their
            // hit count stayed at zero and the rule looked dead in the editor.
            if (rule.Action is RuleAction.ModifyResponse or RuleAction.Highlight)
                rule.RecordHit();

            switch (rule.Action)
            {
                case RuleAction.ModifyResponse:
                    if (!rule.HeaderRegexesMatch(HttpModifier.RawHeaderBlock(session.ResponseHeaders)))
                        break;
                    ApplyHeaderEdits(session.ResponseHeaders, rule.HeaderEdits);
                    HttpModifier.Apply(rule, session, responsePhase: true);
                    if (rule.ReplacementBody is not null)
                    {
                        session.ResponseBody = Encoding.UTF8.GetBytes(rule.ReplacementBody);
                        if (!IsChunked(session.ResponseHeaders))
                            session.ResponseHeaders.Set("Content-Length", session.ResponseBody.Length.ToString());
                    }
                    changed = true;
                    break;
                case RuleAction.Highlight:
                    if (Highlighter.IsMatch(rule, session))
                        session.HighlightColor = rule.HighlightColor;
                    break;
                case RuleAction.Bookmark:
                    if (rule.HeaderRegexesMatch(HttpModifier.RawHeaderBlock(session.ResponseHeaders)))
                    {
                        session.Bookmarked = true;
                        if (!string.IsNullOrEmpty(rule.BookmarkComment))
                            session.Comment = rule.BookmarkComment;
                    }
                    break;
                case RuleAction.Breakpoint:
                    if (rule.BreakpointPhase is BreakpointPhase.BeforeResponse or BreakpointPhase.Both)
                        breakpoint = true;
                    break;
            }
        }
        return changed;
    }

    /// <summary>
    /// Serves a local file as a canned response for a Map Local rule. Every
    /// failure mode (missing path, locked file, permission denied) becomes an
    /// HTTP error rather than an exception — this runs on the proxy's request
    /// path, where a throw would abort an unrelated client connection.
    /// </summary>
    private static AutoReply ReadMappedFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return TextReply(500, "Map Local Misconfigured", "Map Local rule has no file path configured.");

        try
        {
            if (!File.Exists(path))
                return TextReply(404, "Not Found", $"Map Local file not found: {path}");

            return new AutoReply
            {
                Status = 200,
                Reason = "OK",
                ContentType = MimeForExtension(Path.GetExtension(path)),
                Body = File.ReadAllBytes(path),
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException
                                       or System.Security.SecurityException)
        {
            return TextReply(500, "Map Local Failed", $"Could not read {path}: {ex.Message}");
        }
    }

    private static AutoReply TextReply(int status, string reason, string message) => new()
    {
        Status = status,
        Reason = reason,
        ContentType = "text/plain; charset=utf-8",
        Body = Encoding.UTF8.GetBytes(message),
    };

    private static string MimeForExtension(string ext) => ext.ToLowerInvariant() switch
    {
        ".html" or ".htm" => "text/html; charset=utf-8",
        ".css" => "text/css; charset=utf-8",
        ".js" or ".mjs" => "application/javascript; charset=utf-8",
        ".json" => "application/json; charset=utf-8",
        ".xml" => "application/xml; charset=utf-8",
        ".txt" => "text/plain; charset=utf-8",
        ".csv" => "text/csv; charset=utf-8",
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".svg" => "image/svg+xml",
        ".webp" => "image/webp",
        ".ico" => "image/x-icon",
        ".woff" => "font/woff",
        ".woff2" => "font/woff2",
        ".pdf" => "application/pdf",
        ".wasm" => "application/wasm",
        _ => "application/octet-stream"
    };

    private static bool IsChunked(HeaderCollection headers) =>
        string.Equals(headers["Transfer-Encoding"], "chunked", StringComparison.OrdinalIgnoreCase);

    private static void ApplyHeaderEdits(HeaderCollection headers, List<HeaderEdit> edits)
    {
        foreach (var edit in edits)
        {
            if (edit.Operation == HeaderEdit.Op.Remove) headers.Remove(edit.Name);
            else headers.Set(edit.Name, edit.Value);
        }
    }
}
