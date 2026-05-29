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
            rule.HitCount++;
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
                case RuleAction.ModifyRequest:
                    ApplyHeaderEdits(session.RequestHeaders, rule.HeaderEdits);
                    if (rule.ReplacementBody is not null)
                        session.RequestBody = Encoding.UTF8.GetBytes(rule.ReplacementBody);
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
            switch (rule.Action)
            {
                case RuleAction.ModifyResponse:
                    ApplyHeaderEdits(session.ResponseHeaders, rule.HeaderEdits);
                    if (rule.ReplacementBody is not null)
                        session.ResponseBody = Encoding.UTF8.GetBytes(rule.ReplacementBody);
                    changed = true;
                    break;
                case RuleAction.Highlight:
                    session.HighlightColor = rule.HighlightColor;
                    break;
                case RuleAction.Breakpoint:
                    if (rule.BreakpointPhase is BreakpointPhase.BeforeResponse or BreakpointPhase.Both)
                        breakpoint = true;
                    break;
            }
        }
        return changed;
    }

    private static void ApplyHeaderEdits(HeaderCollection headers, List<HeaderEdit> edits)
    {
        foreach (var edit in edits)
        {
            if (edit.Operation == HeaderEdit.Op.Remove) headers.Remove(edit.Name);
            else headers.Set(edit.Name, edit.Value);
        }
    }
}
