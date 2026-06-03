using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using HttpSpy.App.ViewModels;
using HttpSpy.Core;

namespace HttpSpy.App.Views;

/// <summary>
/// The display-filters editor (HTTP Debugger "Set Filters" analog). Each row is
/// a rule that either shows-only or hides sessions matching a field pattern
/// (substring or regex). Rules persist between runs.
/// </summary>
public sealed class FiltersWindow : Window
{
    public FiltersWindow(MainWindowViewModel vm)
    {
        Title = "Filters";
        Width = 680;
        Height = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        WindowIcons.Apply(this);

        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("60,150,60,*,70,40"), Margin = new Avalonia.Thickness(0, 0, 0, 4) };
        AddHeader(header, 0, "On");
        AddHeader(header, 1, "Field");
        AddHeader(header, 2, "Hide");
        AddHeader(header, 3, "Pattern");
        AddHeader(header, 4, "Regex");
        AddHeader(header, 5, "");

        var list = new ItemsControl
        {
            ItemsSource = vm.Filters,
            ItemTemplate = new FuncDataTemplate<DisplayFilter>((_, _) => BuildRow(vm)),
        };

        var add = new Button { Content = "Add filter", MinWidth = 90 };
        add.Click += (_, _) => vm.AddFilterCommand.Execute(null);
        var apply = new Button { Content = "Apply", MinWidth = 90, IsDefault = true };
        apply.Click += (_, _) => vm.ApplyFilters();
        var close = new Button { Content = "Close", MinWidth = 90, IsCancel = true };
        close.Click += (_, _) => { vm.ApplyFilters(); Close(); };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Avalonia.Thickness(0, 8, 0, 0),
            Children = { add, apply, close },
        };

        var hint = new TextBlock
        {
            Text = "Unchecked \"Hide\" = show only matching. With several show-only rows, a session is kept if it matches any of them. Field: URL/Host/Method/Status/ContentType/Process/AnyHeader/Body.",
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.Parse("#FFB0C4DE")),
            Margin = new Avalonia.Thickness(0, 0, 0, 6),
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
        var tb = new TextBlock { Text = text, FontWeight = FontWeight.Bold };
        Grid.SetColumn(tb, col);
        grid.Children.Add(tb);
    }

    private static Control BuildRow(MainWindowViewModel vm)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("60,150,60,*,70,40"), Margin = new Avalonia.Thickness(0, 2, 0, 2) };

        var enabled = new CheckBox();
        enabled.Bind(CheckBox.IsCheckedProperty, new Binding(nameof(DisplayFilter.Enabled)) { Mode = BindingMode.TwoWay });
        Grid.SetColumn(enabled, 0);

        var field = new ComboBox { ItemsSource = vm.FilterFields, Width = 140 };
        field.Bind(ComboBox.SelectedItemProperty, new Binding(nameof(DisplayFilter.Field)) { Mode = BindingMode.TwoWay });
        Grid.SetColumn(field, 1);

        var hide = new CheckBox();
        hide.Bind(CheckBox.IsCheckedProperty, new Binding(nameof(DisplayFilter.Hide)) { Mode = BindingMode.TwoWay });
        Grid.SetColumn(hide, 2);

        var pattern = new TextBox { Watermark = "pattern…" };
        pattern.Bind(TextBox.TextProperty, new Binding(nameof(DisplayFilter.Pattern)) { Mode = BindingMode.TwoWay });
        Grid.SetColumn(pattern, 3);

        var regex = new CheckBox();
        regex.Bind(CheckBox.IsCheckedProperty, new Binding(nameof(DisplayFilter.UseRegex)) { Mode = BindingMode.TwoWay });
        Grid.SetColumn(regex, 4);

        var del = new Button { Content = "✕", Padding = new Avalonia.Thickness(8, 2) };
        Grid.SetColumn(del, 5);

        row.Children.Add(enabled);
        row.Children.Add(field);
        row.Children.Add(hide);
        row.Children.Add(pattern);
        row.Children.Add(regex);
        row.Children.Add(del);

        del.Click += (_, _) =>
        {
            if (row.DataContext is DisplayFilter f) vm.RemoveFilterCommand.Execute(f);
        };
        return row;
    }
}
