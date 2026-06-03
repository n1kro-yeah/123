using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Styling;

namespace HttpSpy.App.Controls;

/// <summary>
/// Attached behaviour that colourises a <see cref="SelectableTextBlock"/> by
/// filling its <c>Inlines</c> with per-token coloured <see cref="Run"/>s.
/// Bind <see cref="SourceTextProperty"/> to the payload and
/// <see cref="LanguageProperty"/> to the content kind; the text stays fully
/// selectable/copyable while reading like a real code editor. Re-colours
/// automatically when the application theme flips between dark and light.
/// </summary>
public static class HighlightBehavior
{
    public static readonly AttachedProperty<string?> SourceTextProperty =
        AvaloniaProperty.RegisterAttached<SelectableTextBlock, string?>(
            "SourceText", typeof(HighlightBehavior));

    public static readonly AttachedProperty<SyntaxLanguage> LanguageProperty =
        AvaloniaProperty.RegisterAttached<SelectableTextBlock, SyntaxLanguage>(
            "Language", typeof(HighlightBehavior), SyntaxLanguage.None);

    public static void SetSourceText(SelectableTextBlock o, string? v) => o.SetValue(SourceTextProperty, v);
    public static string? GetSourceText(SelectableTextBlock o) => o.GetValue(SourceTextProperty);

    public static void SetLanguage(SelectableTextBlock o, SyntaxLanguage v) => o.SetValue(LanguageProperty, v);
    public static SyntaxLanguage GetLanguage(SelectableTextBlock o) => o.GetValue(LanguageProperty);

    static HighlightBehavior()
    {
        SourceTextProperty.Changed.AddClassHandler<SelectableTextBlock>((o, _) => Hook(o));
        LanguageProperty.Changed.AddClassHandler<SelectableTextBlock>((o, _) => Hook(o));
    }

    private static readonly AttachedProperty<bool> HookedProperty =
        AvaloniaProperty.RegisterAttached<SelectableTextBlock, bool>("Hooked", typeof(HighlightBehavior));

    private static void Hook(SelectableTextBlock tb)
    {
        if (!tb.GetValue(HookedProperty))
        {
            tb.SetValue(HookedProperty, true);
            tb.ActualThemeVariantChanged += (_, _) => Rebuild(tb);
        }
        Rebuild(tb);
    }

    private static void Rebuild(SelectableTextBlock tb)
    {
        var text = GetSourceText(tb);
        var lang = GetLanguage(tb);
        tb.Inlines?.Clear();
        if (string.IsNullOrEmpty(text)) return;

        bool dark = tb.ActualThemeVariant != ThemeVariant.Light;
        var tokens = SyntaxHighlighter.Tokenize(text, lang);

        // Plain / unhighlightable content: a single run keeps things cheap.
        if (tokens.Count == 1 && tokens[0].Kind == TokenKind.Plain)
        {
            tb.Inlines?.Add(new Run(tokens[0].Text));
            return;
        }

        foreach (var t in tokens)
        {
            var run = new Run(t.Text);
            if (t.Kind != TokenKind.Plain)
                run.Foreground = SyntaxHighlighter.BrushFor(t.Kind, dark);
            tb.Inlines?.Add(run);
        }
    }
}
