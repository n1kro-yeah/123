using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace HttpSpy.App.Views;

/// <summary>A small modal message / confirmation dialog built in code (no XAML).</summary>
public sealed class MessageWindow : Window
{
    private MessageWindow(string title, string message, bool confirm)
    {
        Title = title;
        Width = 460;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        CanResize = false;

        var text = new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Avalonia.Thickness(16),
        };

        var ok = new Button { Content = confirm ? "OK" : "Close", MinWidth = 80, IsDefault = true };
        ok.Click += (_, _) => Close(true);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Margin = new Avalonia.Thickness(16, 0, 16, 16),
        };

        if (confirm)
        {
            var cancel = new Button { Content = "Cancel", MinWidth = 80, IsCancel = true };
            cancel.Click += (_, _) => Close(false);
            buttons.Children.Add(cancel);
        }
        buttons.Children.Add(ok);

        Content = new DockPanel
        {
            Children =
            {
                Apply(buttons, DockPanel.DockProperty, Dock.Bottom),
                text,
            }
        };
    }

    private static Control Apply(Control control, Avalonia.AvaloniaProperty property, object value)
    {
        control.SetValue(property, value);
        return control;
    }

    public static async Task<bool> ShowAsync(Window owner, string title, string message, bool confirm)
    {
        var window = new MessageWindow(title, message, confirm);
        var result = await window.ShowDialog<bool>(owner);
        return result;
    }
}
