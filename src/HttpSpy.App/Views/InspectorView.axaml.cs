using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;

namespace HttpSpy.App.Views;

public partial class InspectorView : UserControl
{
    public InspectorView() => InitializeComponent();

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void FindBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) FindNext();
    }

    private void FindNext_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => FindNext();

    /// <summary>Incremental "find next" over the response body, wrapping at the end.</summary>
    private void FindNext()
    {
        var findBox = this.FindControl<TextBox>("FindBox");
        var body = this.FindControl<SelectableTextBlock>("ResponseBodyBox");
        var scroll = this.FindControl<ScrollViewer>("BodyScroll");
        var status = this.FindControl<TextBlock>("FindStatus");
        if (findBox is null || body is null) return;

        var term = findBox.Text;
        var text = (DataContext as ViewModels.InspectorViewModel)?.ResponseBodyFormatted ?? string.Empty;
        if (string.IsNullOrEmpty(term) || string.IsNullOrEmpty(text))
        {
            if (status is not null) status.Text = "";
            return;
        }

        int start = Math.Max(body.SelectionStart, body.SelectionEnd);
        int idx = text.IndexOf(term, Math.Min(start, text.Length), StringComparison.OrdinalIgnoreCase);
        if (idx < 0) idx = text.IndexOf(term, 0, StringComparison.OrdinalIgnoreCase); // wrap

        if (idx < 0)
        {
            if (status is not null) status.Text = "no matches";
            return;
        }

        body.SelectionStart = idx;
        body.SelectionEnd = idx + term.Length;
        ScrollToOffset(scroll, body, text, idx);

        int total = CountOccurrences(text, term);
        if (status is not null) status.Text = $"{total} match(es)";
    }

    /// <summary>Scrolls the body viewer so the match line is roughly centred.</summary>
    private static void ScrollToOffset(ScrollViewer? scroll, SelectableTextBlock body, string text, int idx)
    {
        if (scroll is null) return;
        int totalLines = 1;
        for (int i = 0; i < text.Length; i++) if (text[i] == '\n') totalLines++;
        int line = 0;
        for (int i = 0; i < idx && i < text.Length; i++) if (text[i] == '\n') line++;

        double extent = scroll.Extent.Height;
        double viewport = scroll.Viewport.Height;
        if (extent <= viewport || totalLines <= 1) return;
        double y = (double)line / totalLines * extent - viewport / 2;
        y = Math.Clamp(y, 0, extent - viewport);
        scroll.Offset = scroll.Offset.WithY(y);
    }

    private static int CountOccurrences(string text, string term)
    {
        int count = 0, i = 0;
        while ((i = text.IndexOf(term, i, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            count++;
            i += term.Length;
        }
        return count;
    }
}
