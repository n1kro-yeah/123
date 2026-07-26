using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using HttpSpy.App.ViewModels;
using HttpSpy.Core.Proxy;

namespace HttpSpy.App.Views;

/// <summary>
/// The capture-filter editor (HTTP Debugger "Filter Rules" analog).
/// </summary>
/// <remarks>
/// Deliberately a separate dialog from <see cref="FiltersWindow"/>, because the
/// two do genuinely different things and conflating them is a trap: a display
/// filter only hides rows that were already captured and still hold their bodies
/// in memory, whereas these rules stop a transaction being recorded at all. For
/// a capture left running for hours against a chatty application, that
/// distinction is the difference between a usable session and an exhausted heap.
/// </remarks>
public sealed class CaptureFiltersWindow : Window
{
    public CaptureFiltersWindow(MainWindowViewModel vm)
    {
        Title = "Capture filters";
        Width = 700;
        Height = 440;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        WindowIcons.Apply(this);

        var header = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("50,130,90,*,70,40"),
            Margin = new Avalonia.Thickness(0, 0, 0, 4),
        };
        AddHeader(header, 0, "On");
        AddHeader(header, 1, "Field");
        AddHeader(header, 2, "Action");
        AddHeader(header, 3, "Pattern");
        AddHeader(header, 4, "Regex");
        AddHeader(header, 5, "");

        var list = new ItemsControl
        {
            ItemsSource = vm.CaptureFilters,
            ItemTemplate = new FuncDataTemplate<CaptureFilter>((_, _) => BuildRow(vm)),
        };

        var add = new Button { Content = "Add rule", MinWidth = 90 };
        add.Click += (_, _) => vm.AddCaptureFilterCommand.Execute(null);

        var apply = new Button { Content = "Apply", MinWidth = 90, IsDefault = true };
        apply.Classes.Add("primary");
        apply.Click += (_, _) => vm.ApplyCaptureFilters();

        var close = new Button { Content = "Close", MinWidth = 90, IsCancel = true };
        close.Click += (_, _) => { vm.ApplyCaptureFilters(); Close(); };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Avalonia.Thickness(0, 8, 0, 0),
            Children = { add, apply, close },
        };

        var hint = new TextBlock
        {
            Text = "These rules decide what gets recorded, not merely what is shown. " +
                   "Traffic is still proxied normally — a dropped transaction simply never enters " +
                   "the session list, so it costs no memory. Use \"Drop\" to silence noise " +
                   "(telemetry, polling, an unrelated browser); use \"Keep only\" to record a single " +
                   "host or process. With several \"Keep only\" rules a transaction is recorded if it " +
                   "matches any of them.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Avalonia.Thickness(0, 0, 0, 8),
            Classes = { "fieldHint" },
        };

        var dock = new DockPanel { Margin = new Avalonia.Thickness(12) };
        DockPanel.SetDock(hint, Dock.Top);
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(buttons, Dock.Bottom);
        dock.Children.Add(hint);
        dock.Children.Add(header);
        dock.Children.Add(buttons);
        dock.Children.Add(new ScrollViewer { Content = list });
        Content = dock;
    }

    private static void AddHeader(Grid grid, int col, string text)
    {
        var tb = new TextBlock { Text = text, FontWeight = FontWeight.Bold, FontSize = 12 };
        Grid.SetColumn(tb, col);
        grid.Children.Add(tb);
    }

    private static readonly string[] Fields = { "Host", "Url", "Process", "Method" };
    private static readonly string[] Actions = { "Drop", "Keep only" };

    private static Control BuildRow(MainWindowViewModel vm)
    {
        var row = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("50,130,90,*,70,40"),
            Margin = new Avalonia.Thickness(0, 2),
        };

        var enabled = new CheckBox();
        enabled.Bind(CheckBox.IsCheckedProperty,
            new Binding(nameof(CaptureFilter.Enabled)) { Mode = BindingMode.TwoWay });
        Grid.SetColumn(enabled, 0);

        var field = new ComboBox { ItemsSource = Fields, Width = 120 };
        field.Bind(SelectingItemsControl.SelectedIndexProperty,
            new Binding(nameof(CaptureFilter.Field)) { Mode = BindingMode.TwoWay });
        Grid.SetColumn(field, 1);

        // Exclude is a bool on the model but reads far better as a named action.
        var action = new ComboBox { ItemsSource = Actions, Width = 84 };
        action.Bind(SelectingItemsControl.SelectedIndexProperty, new Binding(nameof(CaptureFilter.Exclude))
        {
            Mode = BindingMode.TwoWay,
            Converter = new Converters.InvertedBoolIndexConverter(),
        });
        Grid.SetColumn(action, 2);

        var pattern = new TextBox { Watermark = "pattern…" };
        pattern.Bind(TextBox.TextProperty,
            new Binding(nameof(CaptureFilter.Pattern)) { Mode = BindingMode.TwoWay });
        Grid.SetColumn(pattern, 3);

        var regex = new CheckBox();
        regex.Bind(CheckBox.IsCheckedProperty,
            new Binding(nameof(CaptureFilter.UseRegex)) { Mode = BindingMode.TwoWay });
        Grid.SetColumn(regex, 4);

        var del = new Button { Content = "✕", Padding = new Avalonia.Thickness(8, 2) };
        del.Classes.Add("ghost");
        Grid.SetColumn(del, 5);

        row.Children.Add(enabled);
        row.Children.Add(field);
        row.Children.Add(action);
        row.Children.Add(pattern);
        row.Children.Add(regex);
        row.Children.Add(del);

        del.Click += (_, _) =>
        {
            if (row.DataContext is CaptureFilter f) vm.RemoveCaptureFilterCommand.Execute(f);
        };
        return row;
    }
}
