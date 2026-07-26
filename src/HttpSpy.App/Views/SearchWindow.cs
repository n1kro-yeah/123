using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using HttpSpy.Core.Analysis;
using HttpSpy.Core.Models;

namespace HttpSpy.App.Views;

/// <summary>
/// Searches every captured transaction, not just the one open in the inspector.
///
/// The inspector's Find box looks inside a single transaction and the grid
/// filter only matches the columns it draws, so neither answers "which of these
/// four thousand requests carries this token" — which is where debugging usually
/// starts. Results are listed with the matching line, and activating one selects
/// that transaction in the grid.
/// </summary>
public sealed class SearchWindow : Window
{
    private static readonly FontFamily Mono = new("Cascadia Mono,Consolas,monospace");

    /// <summary>A result row, flattened for display.</summary>
    public sealed class Row
    {
        public required SearchHit Hit { get; init; }
        public string Index => "#" + Hit.Session.Index;
        public string Where => Hit.LineNumber > 0 ? $"{Hit.LocationLabel} :{Hit.LineNumber}" : Hit.LocationLabel;
        public string Url => Hit.Session.FullUrl;
        public string Preview => Hit.Preview;
    }

    private readonly Func<IReadOnlyList<HttpSession>> _source;
    private readonly Action<HttpSession> _navigate;

    private readonly TextBox _query;
    private readonly CheckBox _regex;
    private readonly CheckBox _caseSensitive;
    private readonly CheckBox _wholeWord;
    private readonly CheckBox _urls;
    private readonly CheckBox _headers;
    private readonly CheckBox _bodies;
    private readonly CheckBox _messages;
    private readonly TextBlock _status;
    private readonly ListBox _results;
    private readonly ObservableCollection<Row> _rows = new();

    private CancellationTokenSource? _running;

    public SearchWindow(Func<IReadOnlyList<HttpSession>> source, Action<HttpSession> navigate)
    {
        _source = source;
        _navigate = navigate;

        Title = "Search all transactions";
        Width = 980;
        Height = 620;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        WindowIcons.Apply(this);

        _query = new TextBox { Watermark = "text to find in URLs, headers, bodies and messages", MinWidth = 360 };
        _query.KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) Run(); };

        _regex = new CheckBox { Content = "Regex" };
        _caseSensitive = new CheckBox { Content = "Match case" };
        _wholeWord = new CheckBox { Content = "Whole word" };

        _urls = new CheckBox { Content = "URLs", IsChecked = true };
        _headers = new CheckBox { Content = "Headers", IsChecked = true };
        _bodies = new CheckBox { Content = "Bodies", IsChecked = true };
        _messages = new CheckBox { Content = "WS / SSE", IsChecked = true };

        var search = new Button { Content = "Search", MinWidth = 96, IsDefault = true };
        search.Click += (_, _) => Run();

        _status = new TextBlock { Classes = { "muted" }, VerticalAlignment = VerticalAlignment.Center };

        _results = new ListBox
        {
            ItemsSource = _rows,
            ItemTemplate = new FuncDataTemplate<Row>((row, _) => BuildRow(row), supportsRecycling: true),
        };
        // Double-click and Enter both jump; the list is the point of the window.
        _results.DoubleTapped += (_, _) => Navigate();
        _results.KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) Navigate(); };

        var go = new Button { Content = "Go to transaction", MinWidth = 150 };
        go.Click += (_, _) => Navigate();

        var queryRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children = { _query, search },
        };

        var optionsRow = new WrapPanel
        {
            Children = { _regex, _caseSensitive, _wholeWord },
        };
        foreach (var child in optionsRow.Children) child.Margin = new Avalonia.Thickness(0, 0, 14, 0);

        var scopeRow = new WrapPanel
        {
            Children =
            {
                new TextBlock { Text = "Look in:", Classes = { "muted" }, Margin = new Avalonia.Thickness(0, 0, 10, 0),
                                VerticalAlignment = VerticalAlignment.Center },
                _urls, _headers, _bodies, _messages,
            },
        };
        foreach (var child in scopeRow.Children.Skip(1)) child.Margin = new Avalonia.Thickness(0, 0, 14, 0);

        Content = new DockPanel { Margin = new Avalonia.Thickness(14) }
            .With(panel =>
            {
                var header = new StackPanel { Spacing = 8, Children = { queryRow, optionsRow, scopeRow } };
                DockPanel.SetDock(header, Dock.Top);
                panel.Children.Add(header);

                var footer = new Grid
                {
                    ColumnDefinitions = new ColumnDefinitions("*,Auto"),
                    Margin = new Avalonia.Thickness(0, 10, 0, 0),
                };
                Grid.SetColumn(_status, 0);
                Grid.SetColumn(go, 1);
                footer.Children.Add(_status);
                footer.Children.Add(go);
                DockPanel.SetDock(footer, Dock.Bottom);
                panel.Children.Add(footer);

                var border = new Border
                {
                    Classes = { "panel" },
                    Margin = new Avalonia.Thickness(0, 10, 0, 0),
                    Child = _results,
                };
                panel.Children.Add(border);
            });
    }

    private static Control BuildRow(Row row) => new StackPanel
    {
        Spacing = 1,
        Children =
        {
            new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Children =
                {
                    new TextBlock { Text = row.Index, FontWeight = FontWeight.SemiBold, MinWidth = 52 },
                    new TextBlock { Text = row.Where, Classes = { "muted" }, MinWidth = 150 },
                    new TextBlock { Text = row.Url, TextTrimming = TextTrimming.CharacterEllipsis },
                },
            },
            new TextBlock
            {
                Text = row.Preview,
                FontFamily = Mono,
                FontSize = 11,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Avalonia.Thickness(60, 0, 0, 0),
            },
        },
    };

    private SearchScope Scope()
    {
        var scope = SearchScope.None;
        if (_urls.IsChecked == true) scope |= SearchScope.Url;
        if (_headers.IsChecked == true) scope |= SearchScope.Headers;
        if (_bodies.IsChecked == true) scope |= SearchScope.Bodies;
        if (_messages.IsChecked == true) scope |= SearchScope.Messages;
        return scope;
    }

    private async void Run()
    {
        // A search started while another is running replaces it; the older one is
        // cancelled so a large capture does not queue up work nobody wants.
        _running?.Cancel();
        var cts = new CancellationTokenSource();
        _running = cts;

        var text = _query.Text ?? "";
        if (text.Length == 0)
        {
            _rows.Clear();
            _status.Text = "Type something to search for.";
            return;
        }

        var scope = Scope();
        if (scope == SearchScope.None)
        {
            _rows.Clear();
            _status.Text = "Nothing selected to look in.";
            return;
        }

        var query = new SearchQuery
        {
            Text = text,
            UseRegex = _regex.IsChecked == true,
            CaseSensitive = _caseSensitive.IsChecked == true,
            WholeWord = _wholeWord.IsChecked == true,
            Scope = scope,
        };

        var sessions = _source();
        _status.Text = $"Searching {sessions.Count} transactions…";

        SearchResults results;
        try
        {
            results = await Task.Run(() => ContentSearch.Run(sessions, query, cts.Token), cts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            _status.Text = ex.Message;
            return;
        }

        if (cts.IsCancellationRequested) return;

        _rows.Clear();
        foreach (var hit in results.Hits) _rows.Add(new Row { Hit = hit });
        _status.Text = ContentSearch.Describe(results);
        if (_rows.Count > 0) _results.SelectedIndex = 0;
    }

    private void Navigate()
    {
        if (_results.SelectedItem is Row row) _navigate(row.Hit.Session);
    }
}

/// <summary>Small helper so a control can be configured inline while being constructed.</summary>
internal static class ControlBuilderExtensions
{
    public static T With<T>(this T control, Action<T> configure)
    {
        configure(control);
        return control;
    }
}
