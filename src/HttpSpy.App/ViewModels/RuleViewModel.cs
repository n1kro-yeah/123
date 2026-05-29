using CommunityToolkit.Mvvm.ComponentModel;
using HttpSpy.Core.Models;
using HttpSpy.Core.Rules;

namespace HttpSpy.App.ViewModels;

/// <summary>Editable wrapper around a <see cref="Rule"/> for the Rules tab.</summary>
public sealed class RuleViewModel : ObservableObject
{
    public RuleViewModel(Rule rule) => Rule = rule;

    public Rule Rule { get; }

    public string[] Actions { get; } =
        { "None", "Block", "Redirect", "Breakpoint", "AutoReply", "ModifyRequest", "ModifyResponse", "Delay", "Highlight" };

    public string[] MatchModes { get; } = { "Contains", "Wildcard", "Regex", "Exact" };

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
        set { Rule.Action = (RuleAction)value; OnPropertyChanged(); OnPropertyChanged(nameof(ActionLabel)); }
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

    public long HitCount => Rule.HitCount;

    public string Summary => $"{(Enabled ? "●" : "○")} {Name} — {Rule.Action} [{Rule.UrlMatchMode}: {Rule.UrlPattern}]";

    public void RefreshSummary()
    {
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(HitCount));
    }
}
