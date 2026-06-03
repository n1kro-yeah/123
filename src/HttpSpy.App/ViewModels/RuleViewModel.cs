using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using HttpSpy.Core.Models;
using HttpSpy.Core.Rules;

namespace HttpSpy.App.ViewModels;

/// <summary>Editable wrapper around a <see cref="Rule"/> for the Rules tab.</summary>
public sealed class RuleViewModel : ObservableObject
{
    public RuleViewModel(Rule rule)
    {
        Rule = rule;
        SyncModifierTextFromRule();
    }

    public RuleViewModel() : this(new Rule()) { }

    public Rule Rule { get; }

    public string[] Actions { get; } =
    {
        "None", "Block", "Redirect", "Breakpoint", "AutoReply", "ModifyRequest", "ModifyResponse",
        "Delay", "Highlight", "MapLocal", "RedirectEndpoint", "Bookmark"
    };

    public string[] MatchModes { get; } = { "Contains", "Wildcard", "Regex", "Exact" };

    public string[] HighlightColumns { get; } =
    {
        "Url", "Host", "Method", "Status", "ContentType", "Process",
        "RequestSize", "ResponseSize", "Duration", "Speed"
    };

    public string[] HighlightOperators { get; } =
    {
        "Contains", "IsSame", "StartsWith", "EndsWith", "IsEqual", "IsLess", "IsBigger", "IsBetween"
    };

    public bool Enabled
    {
        get => Rule.Enabled;
        set { Rule.Enabled = value; OnPropertyChanged(); }
    }

    public string Name
    {
        get => Rule.Name;
        set { Rule.Name = value; OnPropertyChanged(); }
    }

    public int ActionIndex
    {
        get => (int)Rule.Action;
        set
        {
            Rule.Action = (RuleAction)value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ActionLabel));
            // The modifier target (request vs response) follows the action.
            RebuildModifierRules();
        }
    }

    public string ActionLabel => Rule.Action.ToString();

    public int MatchModeIndex
    {
        get => (int)Rule.UrlMatchMode;
        set { Rule.UrlMatchMode = (MatchMode)value; OnPropertyChanged(); }
    }

    public string UrlPattern
    {
        get => Rule.UrlPattern;
        set { Rule.UrlPattern = value; OnPropertyChanged(); }
    }

    public string? MethodFilter
    {
        get => Rule.MethodFilter;
        set { Rule.MethodFilter = value; OnPropertyChanged(); }
    }

    public string? RedirectUrl
    {
        get => Rule.RedirectUrl;
        set { Rule.RedirectUrl = value; OnPropertyChanged(); }
    }

    public int AutoReplyStatus
    {
        get => Rule.AutoReplyStatus;
        set { Rule.AutoReplyStatus = value; OnPropertyChanged(); }
    }

    public string AutoReplyContentType
    {
        get => Rule.AutoReplyContentType;
        set { Rule.AutoReplyContentType = value; OnPropertyChanged(); }
    }

    public string AutoReplyBody
    {
        get => Rule.AutoReplyBody;
        set { Rule.AutoReplyBody = value; OnPropertyChanged(); }
    }

    public int DelayMs
    {
        get => Rule.DelayMs;
        set { Rule.DelayMs = value; OnPropertyChanged(); }
    }

    public string? MapLocalPath
    {
        get => Rule.MapLocalPath;
        set { Rule.MapLocalPath = value; OnPropertyChanged(); }
    }

    // ---- HTTP Modifier (regex find/replace) ----------------------------------
    private string _headerFindReplaceText = string.Empty;
    private string _bodyFindReplaceText = string.Empty;

    /// <summary>One <c>find =&gt; replace</c> per line, applied to the raw header block.</summary>
    public string HeaderFindReplaceText
    {
        get => _headerFindReplaceText;
        set { _headerFindReplaceText = value; OnPropertyChanged(); RebuildModifierRules(); }
    }

    /// <summary>One <c>find =&gt; replace</c> per line, applied to the message body.</summary>
    public string BodyFindReplaceText
    {
        get => _bodyFindReplaceText;
        set { _bodyFindReplaceText = value; OnPropertyChanged(); RebuildModifierRules(); }
    }

    /// <summary>One regex per line — all must match the header block for the rule to fire.</summary>
    public string MatchRegexText
    {
        get => string.Join('\n', Rule.HeaderMatchRegexes);
        set
        {
            Rule.HeaderMatchRegexes = (value ?? string.Empty)
                .Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
            OnPropertyChanged();
        }
    }

    // ---- Endpoint redirect (TCP/IP Redirector analog) ------------------------
    public string? RedirectHost
    {
        get => Rule.RedirectHost;
        set { Rule.RedirectHost = value; OnPropertyChanged(); }
    }

    public int RedirectPort
    {
        get => Rule.RedirectPort;
        set { Rule.RedirectPort = value; OnPropertyChanged(); }
    }

    public bool RewriteHostHeader
    {
        get => Rule.RewriteHostHeader;
        set { Rule.RewriteHostHeader = value; OnPropertyChanged(); }
    }

    // ---- Highlighting --------------------------------------------------------
    public int HighlightColumnIndex
    {
        get => (int)Rule.HighlightColumn;
        set { Rule.HighlightColumn = (HighlightColumn)value; OnPropertyChanged(); }
    }

    public int HighlightOperatorIndex
    {
        get => (int)Rule.HighlightOperator;
        set { Rule.HighlightOperator = (HighlightOperator)value; OnPropertyChanged(); }
    }

    public string HighlightValue
    {
        get => Rule.HighlightValue;
        set { Rule.HighlightValue = value; OnPropertyChanged(); }
    }

    public string HighlightValue2
    {
        get => Rule.HighlightValue2;
        set { Rule.HighlightValue2 = value; OnPropertyChanged(); }
    }

    /// <summary>The highlight colour as a #AARRGGBB hex string for the editor.</summary>
    public string HighlightColorHex
    {
        get => "#" + Rule.HighlightColor.ToString("X8");
        set
        {
            var s = (value ?? string.Empty).TrimStart('#');
            if (uint.TryParse(s, System.Globalization.NumberStyles.HexNumber, null, out var argb))
            {
                Rule.HighlightColor = argb;
                OnPropertyChanged();
            }
        }
    }

    public string? BookmarkComment
    {
        get => Rule.BookmarkComment;
        set { Rule.BookmarkComment = value; OnPropertyChanged(); }
    }

    /// <summary>Rebuilds <see cref="Rule.ModifierRules"/> from the two text editors.</summary>
    private void RebuildModifierRules()
    {
        bool response = Rule.Action == RuleAction.ModifyResponse;
        var list = new List<ModifierRule>();
        foreach (var (text, body) in new[] { (_headerFindReplaceText, false), (_bodyFindReplaceText, true) })
        {
            if (string.IsNullOrEmpty(text)) continue;
            foreach (var line in text.Split('\n'))
            {
                var l = line.TrimEnd('\r');
                if (l.Trim().Length == 0) continue;
                int sep = l.IndexOf("=>", StringComparison.Ordinal);
                string find = sep >= 0 ? l[..sep].Trim() : l.Trim();
                string repl = sep >= 0 ? l[(sep + 2)..].Trim() : string.Empty;
                if (find.Length == 0) continue;
                var target = (response, body) switch
                {
                    (false, false) => ModifierTarget.RequestHeaders,
                    (false, true) => ModifierTarget.RequestBody,
                    (true, false) => ModifierTarget.ResponseHeaders,
                    (true, true) => ModifierTarget.ResponseBody,
                };
                list.Add(new ModifierRule { Target = target, Find = find, Replace = repl });
            }
        }
        Rule.ModifierRules = list;
    }

    /// <summary>Loads the modifier text editors from an existing rule (used after load).</summary>
    public void SyncModifierTextFromRule()
    {
        _headerFindReplaceText = string.Join('\n', Rule.ModifierRules
            .Where(m => m.Target is ModifierTarget.RequestHeaders or ModifierTarget.ResponseHeaders)
            .Select(m => $"{m.Find} => {m.Replace}"));
        _bodyFindReplaceText = string.Join('\n', Rule.ModifierRules
            .Where(m => m.Target is ModifierTarget.RequestBody or ModifierTarget.ResponseBody)
            .Select(m => $"{m.Find} => {m.Replace}"));
        OnPropertyChanged(nameof(HeaderFindReplaceText));
        OnPropertyChanged(nameof(BodyFindReplaceText));
    }

    public long HitCount => Rule.HitCount;

    public string Summary => $"{(Enabled ? "●" : "○")} {Name} — {Rule.Action} [{Rule.UrlMatchMode}: {Rule.UrlPattern}]";

    public void RefreshSummary()
    {
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(HitCount));
    }
}
