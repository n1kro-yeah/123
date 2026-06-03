using System;
using System.Collections.Generic;
using System.Text;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using HttpSpy.Core.Models;
using HttpSpy.Core.Util;

namespace HttpSpy.App.Views;

/// <summary>
/// A side-by-side line diff of two captured sessions (request line, headers and
/// body). Added lines are tinted green, removed lines red — the classic diff view.
/// </summary>
public sealed class CompareWindow : Window
{
    private sealed class DiffRow
    {
        public string Left = "";
        public string Right = "";
        public IBrush LeftBg = Brushes.Transparent;
        public IBrush RightBg = Brushes.Transparent;
    }

    private static readonly IBrush AddBg = new SolidColorBrush(Color.FromArgb(0x55, 0x2E, 0x7D, 0x32));
    private static readonly IBrush DelBg = new SolidColorBrush(Color.FromArgb(0x55, 0xC6, 0x28, 0x28));

    public CompareWindow(HttpSession left, HttpSession right)
    {
        Title = $"Compare  #{left.Index} ↔ #{right.Index}";
        Width = 1100;
        Height = 720;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        WindowIcons.Apply(this);

        var rows = BuildDiff(Describe(left), Describe(right));

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*") };

        var leftPanel = new StackPanel();
        var rightPanel = new StackPanel();
        foreach (var r in rows)
        {
            leftPanel.Children.Add(LineBlock(r.Left, r.LeftBg));
            rightPanel.Children.Add(LineBlock(r.Right, r.RightBg));
        }

        var leftScroll = new ScrollViewer { Content = leftPanel };
        var rightScroll = new ScrollViewer { Content = rightPanel };
        Grid.SetColumn(leftScroll, 0);
        Grid.SetColumn(rightScroll, 1);
        grid.Children.Add(leftScroll);
        grid.Children.Add(rightScroll);

        var header = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,*"),
            Margin = new Avalonia.Thickness(8, 6),
        };
        var lh = HeaderBlock($"#{left.Index}  {left.Method} {left.FullUrl}");
        var rh = HeaderBlock($"#{right.Index}  {right.Method} {right.FullUrl}");
        Grid.SetColumn(lh, 0);
        Grid.SetColumn(rh, 1);
        header.Children.Add(lh);
        header.Children.Add(rh);

        var root = new DockPanel();
        root.Children.Add(header);
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(grid);
        Content = root;
    }

    private static TextBlock HeaderBlock(string text) => new()
    {
        Text = text,
        FontWeight = FontWeight.Bold,
        TextTrimming = TextTrimming.CharacterEllipsis,
        Margin = new Avalonia.Thickness(4, 0),
    };

    private static Border LineBlock(string text, IBrush bg) => new()
    {
        Background = bg,
        Child = new TextBlock
        {
            Text = text.Length == 0 ? " " : text,
            FontFamily = new FontFamily("Cascadia Mono,Consolas,monospace"),
            FontSize = 12,
            TextWrapping = TextWrapping.NoWrap,
            Margin = new Avalonia.Thickness(4, 0),
        },
    };

    private static string Describe(HttpSession s)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{s.Method} {s.FullUrl} {s.HttpVersion}");
        foreach (var h in s.RequestHeaders) sb.AppendLine($"{h.Name}: {h.Value}");
        sb.AppendLine();
        if (s.RequestBody.Length > 0) sb.AppendLine(s.RequestBodyText);
        sb.AppendLine("──── RESPONSE ────");
        sb.AppendLine($"{s.ResponseHttpVersion} {s.StatusCode} {s.StatusText}");
        foreach (var h in s.ResponseHeaders) sb.AppendLine($"{h.Name}: {h.Value}");
        sb.AppendLine();
        var body = s.ResponseBodyKind == BodyContentType.Json
            ? BodyFormatter.PrettyJson(s.ResponseBodyText)
            : s.ResponseBodyText;
        sb.Append(body);
        return sb.ToString();
    }

    /// <summary>Classic LCS line diff producing aligned side-by-side rows.</summary>
    private static List<DiffRow> BuildDiff(string leftText, string rightText)
    {
        var a = leftText.Replace("\r\n", "\n").Split('\n');
        var b = rightText.Replace("\r\n", "\n").Split('\n');
        int n = a.Length, m = b.Length;

        var lcs = new int[n + 1, m + 1];
        for (int i = n - 1; i >= 0; i--)
            for (int j = m - 1; j >= 0; j--)
                lcs[i, j] = a[i] == b[j] ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);

        var rows = new List<DiffRow>();
        int x = 0, y = 0;
        while (x < n && y < m)
        {
            if (a[x] == b[y])
            {
                rows.Add(new DiffRow { Left = a[x], Right = b[y] });
                x++; y++;
            }
            else if (lcs[x + 1, y] >= lcs[x, y + 1])
            {
                rows.Add(new DiffRow { Left = a[x], LeftBg = DelBg });
                x++;
            }
            else
            {
                rows.Add(new DiffRow { Right = b[y], RightBg = AddBg });
                y++;
            }
        }
        while (x < n) rows.Add(new DiffRow { Left = a[x++], LeftBg = DelBg });
        while (y < m) rows.Add(new DiffRow { Right = b[y++], RightBg = AddBg });
        return rows;
    }
}
