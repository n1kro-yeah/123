using System.Text.RegularExpressions;
using HttpSpy.Core.Models;

namespace HttpSpy.Core.Proxy;

/// <summary>What a capture filter matches against.</summary>
public enum CaptureFilterField
{
    Host,
    Url,
    Process,
    Method,
}

/// <summary>
/// A rule that decides whether a transaction is recorded at all.
/// </summary>
/// <remarks>
/// This is deliberately different from a <c>DisplayFilter</c>. A display filter
/// hides rows that have already been captured — the bodies are still in memory.
/// A capture filter drops the transaction before it is ever handed to the UI, so
/// noise (telemetry beacons, an unrelated browser, a polling endpoint) costs
/// nothing to ignore. That is what makes a long unattended capture practical.
/// The traffic itself is still proxied normally; only the recording is skipped.
/// </remarks>
public sealed class CaptureFilter
{
    public bool Enabled { get; set; } = true;

    public CaptureFilterField Field { get; set; } = CaptureFilterField.Host;

    /// <summary>true = drop matches; false = record only matches.</summary>
    public bool Exclude { get; set; } = true;

    public string Pattern { get; set; } = string.Empty;

    public bool UseRegex { get; set; }

    public CaptureFilter Clone() => new()
    {
        Enabled = Enabled,
        Field = Field,
        Exclude = Exclude,
        Pattern = Pattern,
        UseRegex = UseRegex,
    };

    internal bool Matches(HttpSession session)
    {
        if (!Enabled || string.IsNullOrWhiteSpace(Pattern)) return false;

        string value = Field switch
        {
            CaptureFilterField.Url => session.FullUrl,
            CaptureFilterField.Process => session.ProcessName,
            CaptureFilterField.Method => session.Method,
            _ => session.Host,
        };
        if (string.IsNullOrEmpty(value)) return false;

        if (!UseRegex)
            return value.Contains(Pattern, StringComparison.OrdinalIgnoreCase);

        try
        {
            return Regex.IsMatch(value, Pattern,
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                TimeSpan.FromMilliseconds(100));
        }
        catch (ArgumentException) { return false; }        // invalid user pattern
        catch (RegexMatchTimeoutException) { return false; }
    }

    public override string ToString() =>
        $"{(Exclude ? "Drop" : "Keep")} {Field} {(UseRegex ? "~" : "contains")} {Pattern}";
}

/// <summary>Evaluates the configured capture filters for a transaction.</summary>
public static class CaptureFilterSet
{
    /// <summary>
    /// Decides whether a session should be recorded. Any matching "drop" rule
    /// excludes it; when at least one "keep only" rule is active the session must
    /// match one of them.
    /// </summary>
    public static bool ShouldRecord(IReadOnlyList<CaptureFilter> filters, HttpSession session)
    {
        if (filters.Count == 0) return true;

        bool anyInclude = false;
        bool matchedInclude = false;

        foreach (var filter in filters)
        {
            if (!filter.Enabled || string.IsNullOrWhiteSpace(filter.Pattern)) continue;

            if (filter.Exclude)
            {
                if (filter.Matches(session)) return false;
            }
            else
            {
                anyInclude = true;
                matchedInclude |= filter.Matches(session);
            }
        }

        return !anyInclude || matchedInclude;
    }
}
