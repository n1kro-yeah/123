using System;
using System.Text;
using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace HttpSpy.App.Views;

/// <summary>
/// Tests a regular expression against sample text (HTTP Debugger's "RegExp
/// Tester"). Every rule in HttpSpy that accepts a pattern — URL matching, the
/// HTTP Modifier, header match rules, highlight rules, display and capture
/// filters — uses .NET regex semantics, and a pattern that silently matches
/// nothing is the single most common way to author a rule that appears broken.
/// This shows what a pattern actually does before it is armed.
/// </summary>
public sealed class RegexTesterWindow : Window
{
    private static readonly FontFamily Mono = new("Cascadia Mono,Consolas,monospace");

    /// <summary>Same ceiling the rule engine applies, so results are representative.</summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(250);

    private readonly TextBox _pattern;
    private readonly TextBox _input;
    private readonly TextBox _replacement;
    private readonly TextBlock _status;
    private readonly TextBox _results;
    private readonly CheckBox _ignoreCase;
    private readonly CheckBox _multiline;
    private readonly CheckBox _singleline;

    public RegexTesterWindow()
    {
        Title = "Regular expression tester";
        Width = 720;
        Height = 560;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        WindowIcons.Apply(this);

        _pattern = new TextBox { FontFamily = Mono, Watermark = @"^Server:\s*(.+)$" };
        _input = new TextBox
        {
            AcceptsReturn = true,
            Height = 140,
            FontFamily = Mono,
            Watermark = "Paste a header block, a URL, or a body to test against…",
        };
        _replacement = new TextBox { FontFamily = Mono, Watermark = @"Server: HttpSpy   (supports $1, \r \n \t)" };
        _status = new TextBlock { Margin = new Avalonia.Thickness(0, 4) };
        _results = new TextBox { AcceptsReturn = true, IsReadOnly = true, FontFamily = Mono, MinHeight = 150 };

        _ignoreCase = new CheckBox { Content = "Ignore case", IsChecked = true };
        _multiline = new CheckBox { Content = "Multiline (^ $ per line)", IsChecked = true };
        _singleline = new CheckBox { Content = "Singleline (. matches newline)" };

        // Live feedback: the point of the tool is immediacy.
        _pattern.TextChanged += (_, _) => Evaluate();
        _input.TextChanged += (_, _) => Evaluate();
        _replacement.TextChanged += (_, _) => Evaluate();
        _ignoreCase.IsCheckedChanged += (_, _) => Evaluate();
        _multiline.IsCheckedChanged += (_, _) => Evaluate();
        _singleline.IsCheckedChanged += (_, _) => Evaluate();

        Content = new ScrollViewer
        {
            Padding = new Avalonia.Thickness(14),
            Content = new StackPanel
            {
                Spacing = 6,
                Children =
                {
                    Label("Pattern"),
                    _pattern,
                    new WrapPanel
                    {
                        Children = { _ignoreCase, Gap(), _multiline, Gap(), _singleline },
                    },
                    Label("Sample text"),
                    _input,
                    Label("Replacement (optional — previews what the HTTP Modifier would produce)"),
                    _replacement,
                    _status,
                    Label("Matches"),
                    _results,
                },
            },
        };

        Evaluate();
    }

    private static TextBlock Label(string text) => new()
    {
        Text = text,
        Classes = { "fieldLabel" },
    };

    private static Control Gap() => new Border { Width = 14 };

    private RegexOptions BuildOptions()
    {
        var options = RegexOptions.CultureInvariant;
        if (_ignoreCase.IsChecked == true) options |= RegexOptions.IgnoreCase;
        if (_multiline.IsChecked == true) options |= RegexOptions.Multiline;
        if (_singleline.IsChecked == true) options |= RegexOptions.Singleline;
        return options;
    }

    private void Evaluate()
    {
        var pattern = _pattern.Text ?? string.Empty;
        var input = _input.Text ?? string.Empty;

        if (pattern.Length == 0)
        {
            SetStatus("Enter a pattern to test.", neutral: true);
            _results.Text = string.Empty;
            return;
        }

        Regex regex;
        try
        {
            regex = new Regex(pattern, BuildOptions(), Timeout);
        }
        catch (ArgumentException ex)
        {
            // This is the whole reason the tool exists: show the parse error.
            SetStatus("Invalid pattern — " + ex.Message, neutral: false);
            _results.Text = string.Empty;
            return;
        }

        MatchCollection matches;
        try
        {
            matches = regex.Matches(input);
        }
        catch (RegexMatchTimeoutException)
        {
            SetStatus($"Pattern timed out after {Timeout.TotalMilliseconds:F0} ms — it would be skipped at " +
                      "capture time. Simplify the backtracking.", neutral: false);
            _results.Text = string.Empty;
            return;
        }

        var sb = new StringBuilder();
        int n = 0;
        foreach (Match match in matches)
        {
            sb.Append('#').Append(++n)
              .Append("  at ").Append(match.Index).Append(", length ").Append(match.Length)
              .AppendLine()
              .Append("      ").AppendLine(Escape(match.Value));

            // Group 0 is the whole match, already shown above.
            for (int g = 1; g < match.Groups.Count; g++)
            {
                var group = match.Groups[g];
                if (!group.Success) continue;
                var name = group.Name == g.ToString() ? $"${g}" : $"${{{group.Name}}}";
                sb.Append("      ").Append(name).Append(" = ").AppendLine(Escape(group.Value));
            }
        }

        if (n == 0)
        {
            SetStatus("Valid pattern, but no matches in the sample text.", neutral: true);
            _results.Text = string.Empty;
            return;
        }

        SetStatus($"Valid pattern — {n} match(es).", neutral: true, success: true);

        var replacement = _replacement.Text;
        if (!string.IsNullOrEmpty(replacement))
        {
            try
            {
                // Mirror the HTTP Modifier, which honours \r \n \t in replacements.
                var translated = HttpSpy.Core.Rules.HttpModifier.TranslateEscapes(replacement);
                sb.AppendLine().AppendLine("--- Result after replacement ---")
                  .AppendLine(regex.Replace(input, translated));
            }
            catch (Exception ex)
            {
                sb.AppendLine().Append("Replacement failed: ").AppendLine(ex.Message);
            }
        }

        _results.Text = sb.ToString();
    }

    /// <summary>Makes control characters visible so an unexpected newline is obvious.</summary>
    private static string Escape(string value) =>
        value.Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");

    private void SetStatus(string text, bool neutral, bool success = false)
    {
        _status.Text = text;
        _status.Classes.Clear();
        _status.Classes.Add(neutral ? (success ? "success" : "muted") : "danger");
    }
}
