using System;
using System.Text;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using HttpSpy.Core.Util;

namespace HttpSpy.App.Views;

/// <summary>
/// A small developer utility (HTTP Debugger "Converter" analog): URL, Base64 and
/// Hex encode/decode plus a JSON minify/prettify. Input is on top, the result
/// appears below when a transform button is clicked.
/// </summary>
public sealed class ConverterWindow : Window
{
    private static readonly FontFamily Mono = new("Cascadia Mono,Consolas,monospace");

    public ConverterWindow()
    {
        Title = "Converter";
        Width = 640;
        Height = 460;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        WindowIcons.Apply(this);

        var input = new TextBox { AcceptsReturn = true, Height = 150, FontFamily = Mono, Watermark = "input text…" };
        var output = new TextBox { AcceptsReturn = true, Height = 150, FontFamily = Mono, IsReadOnly = true };

        void Run(Func<string, string> transform)
        {
            try { output.Text = transform(input.Text ?? string.Empty); }
            catch (Exception ex) { output.Text = "Error: " + ex.Message; }
        }

        var row1 = Row(
            Btn("URL encode", () => Run(UrlCodec.Encode)),
            Btn("URL decode", () => Run(UrlCodec.Decode)),
            Btn("Base64 encode", () => Run(s => Convert.ToBase64String(Encoding.UTF8.GetBytes(s)))),
            Btn("Base64 decode", () => Run(s => Encoding.UTF8.GetString(Convert.FromBase64String(s.Trim())))));

        var row2 = Row(
            Btn("Hex encode", () => Run(s => Convert.ToHexString(Encoding.UTF8.GetBytes(s)))),
            Btn("Hex decode", () => Run(s => Encoding.UTF8.GetString(Convert.FromHexString(s.Trim())))),
            Btn("JSON prettify", () => Run(BodyFormatter.PrettyJson)),
            Btn("JSON minify", () => Run(MinifyJson)));

        var swap = Btn("⇅ Move result to input", () => { input.Text = output.Text; output.Text = ""; });
        var close = new Button { Content = "Close", MinWidth = 90, IsCancel = true };
        close.Click += (_, _) => Close();

        var panel = new StackPanel
        {
            Margin = new Avalonia.Thickness(12),
            Spacing = 8,
            Children =
            {
                new TextBlock { Text = "Input", FontWeight = FontWeight.Bold },
                input,
                row1,
                row2,
                new TextBlock { Text = "Result", FontWeight = FontWeight.Bold },
                output,
                Row(swap, close),
            },
        };
        Content = panel;
    }

    private static string MinifyJson(string s)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(s);
        return System.Text.Json.JsonSerializer.Serialize(doc.RootElement,
            new System.Text.Json.JsonSerializerOptions { WriteIndented = false });
    }

    private static Button Btn(string text, Action onClick)
    {
        var b = new Button { Content = text, MinWidth = 120 };
        b.Click += (_, _) => onClick();
        return b;
    }

    private static StackPanel Row(params Control[] children)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        foreach (var c in children) sp.Children.Add(c);
        return sp;
    }
}
