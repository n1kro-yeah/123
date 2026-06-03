using System.Globalization;
using HttpSpy.Core.Models;

namespace HttpSpy.Core.Rules;

/// <summary>
/// Evaluates HTTP Debugger style highlighting rules. A rule is either a
/// <em>Standard</em> rule (a column compared with an operator against a value)
/// or a <em>RegExp</em> rule (when <see cref="Rule.HeaderMatchRegexes"/> are
/// present and matched). Matching rules colour the grid row.
/// </summary>
public static class Highlighter
{
    /// <summary>Returns true when the highlight rule applies to the session.</summary>
    public static bool IsMatch(Rule rule, HttpSession session)
    {
        // Regex highlight rules (matched on the raw header block) take priority.
        if (rule.HeaderMatchRegexes.Count > 0)
        {
            string block = HttpModifier.RawHeaderBlock(session.RequestHeaders) +
                           HttpModifier.RawHeaderBlock(session.ResponseHeaders);
            if (!rule.HeaderRegexesMatch(block)) return false;
        }

        return EvaluateStandard(rule, session);
    }

    private static bool EvaluateStandard(Rule rule, HttpSession s)
    {
        if (IsNumericColumn(rule.HighlightColumn))
        {
            double actual = NumericValue(rule.HighlightColumn, s);
            double a = ParseNum(rule.HighlightValue);
            double b = ParseNum(rule.HighlightValue2);
            return rule.HighlightOperator switch
            {
                HighlightOperator.IsEqual => Math.Abs(actual - a) < 0.0001,
                HighlightOperator.IsLess => actual < a,
                HighlightOperator.IsBigger => actual > a,
                HighlightOperator.IsBetween => actual >= Math.Min(a, b) && actual <= Math.Max(a, b),
                // Text operators on a numeric column fall back to string compare.
                _ => TextCompare(rule, NumericValue(rule.HighlightColumn, s)
                        .ToString(CultureInfo.InvariantCulture)),
            };
        }

        return TextCompare(rule, TextValue(rule.HighlightColumn, s));
    }

    private static bool TextCompare(Rule rule, string actual)
    {
        string v = rule.HighlightValue ?? string.Empty;
        return rule.HighlightOperator switch
        {
            HighlightOperator.Contains => actual.Contains(v, StringComparison.OrdinalIgnoreCase),
            HighlightOperator.IsSame => string.Equals(actual, v, StringComparison.OrdinalIgnoreCase),
            HighlightOperator.StartsWith => actual.StartsWith(v, StringComparison.OrdinalIgnoreCase),
            HighlightOperator.EndsWith => actual.EndsWith(v, StringComparison.OrdinalIgnoreCase),
            // Numeric operators against text: compare lengths as a best effort.
            HighlightOperator.IsEqual => string.Equals(actual, v, StringComparison.OrdinalIgnoreCase),
            _ => false,
        };
    }

    private static bool IsNumericColumn(HighlightColumn c) => c is
        HighlightColumn.Status or HighlightColumn.RequestSize or HighlightColumn.ResponseSize
        or HighlightColumn.Duration or HighlightColumn.Speed;

    private static string TextValue(HighlightColumn c, HttpSession s) => c switch
    {
        HighlightColumn.Url => s.FullUrl,
        HighlightColumn.Host => s.Host,
        HighlightColumn.Method => s.Method,
        HighlightColumn.ContentType => s.ResponseContentTypeShort,
        HighlightColumn.Process => s.ProcessName,
        _ => string.Empty,
    };

    private static double NumericValue(HighlightColumn c, HttpSession s) => c switch
    {
        HighlightColumn.Status => s.StatusCode,
        HighlightColumn.RequestSize => s.RequestBodySize,
        HighlightColumn.ResponseSize => s.ResponseBodySize,
        HighlightColumn.Duration => s.DurationMs,
        HighlightColumn.Speed => s.DurationMs > 0
            ? s.ResponseBodySize / (s.DurationMs / 1000.0)
            : 0,
        _ => 0,
    };

    private static double ParseNum(string? s) =>
        double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var d) ? d : 0;
}
