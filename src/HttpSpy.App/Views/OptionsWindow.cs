using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using HttpSpy.App.ViewModels;

namespace HttpSpy.App.Views;

/// <summary>
/// Settings dialog covering network simulation (bandwidth throttling + latency),
/// upstream proxy chaining and TLS passthrough hosts. Edits are written back to
/// the main view model and persisted on Apply.
/// </summary>
public sealed class OptionsWindow : Window
{
    public OptionsWindow(MainWindowViewModel vm)
    {
        Title = "Options";
        Width = 520;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        WindowIcons.Apply(this);

        var enableHttp2 = new CheckBox { Content = "Decode HTTP/2 and gRPC (ALPN \"h2\")", IsChecked = vm.EnableHttp2 };
        var transparent = new CheckBox
        {
            Content = "Transparent capture without system proxy (Windows + WinDivert, admin)",
            IsChecked = vm.TransparentCapture,
        };
        var throttleEnabled = new CheckBox { Content = "Enable network simulation (throttling)", IsChecked = vm.ThrottleEnabled };
        var kbps = new NumericUpDown { Value = vm.ThrottleKbps, Minimum = 0, Maximum = 1_000_000, Increment = 50, FormatString = "0" };
        var latency = new NumericUpDown { Value = vm.ExtraLatencyMs, Minimum = 0, Maximum = 60_000, Increment = 25, FormatString = "0" };
        var upstream = new TextBox { Text = vm.UpstreamProxy, Watermark = "host:port (leave empty for direct)" };
        var passthrough = new TextBox
        {
            Text = vm.PassthroughHosts,
            AcceptsReturn = true,
            Height = 96,
            Watermark = "one host per line — these are tunneled without HTTPS decryption",
            FontFamily = new FontFamily("Cascadia Mono,Consolas,monospace"),
        };

        var presets = new ComboBox { ItemsSource = vm.ThrottlePresets, SelectedIndex = 0, MinWidth = 180 };
        presets.SelectionChanged += (_, _) =>
        {
            switch (presets.SelectedIndex)
            {
                case 1: kbps.Value = 50; latency.Value = 500; throttleEnabled.IsChecked = true; break;
                case 2: kbps.Value = 250; latency.Value = 300; throttleEnabled.IsChecked = true; break;
                case 3: kbps.Value = 750; latency.Value = 100; throttleEnabled.IsChecked = true; break;
                case 4: kbps.Value = 2000; latency.Value = 40; throttleEnabled.IsChecked = true; break;
                case 5: kbps.Value = 30000; latency.Value = 5; throttleEnabled.IsChecked = true; break;
            }
        };

        var apply = new Button { Content = "Apply", MinWidth = 90, IsDefault = true };
        apply.Click += (_, _) =>
        {
            vm.EnableHttp2 = enableHttp2.IsChecked == true;
            vm.TransparentCapture = transparent.IsChecked == true;
            vm.ThrottleEnabled = throttleEnabled.IsChecked == true;
            vm.ThrottleKbps = (int)(kbps.Value ?? 0);
            vm.ExtraLatencyMs = (int)(latency.Value ?? 0);
            vm.UpstreamProxy = upstream.Text ?? "";
            vm.PassthroughHosts = passthrough.Text ?? "";
            vm.ApplyOptions();
            Close();
        };
        var cancel = new Button { Content = "Cancel", MinWidth = 90, IsCancel = true };
        cancel.Click += (_, _) => Close();

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Children = { cancel, apply },
        };

        var panel = new StackPanel
        {
            Margin = new Avalonia.Thickness(16),
            Spacing = 8,
            Children =
            {
                new TextBlock { Text = "Protocols", FontWeight = FontWeight.Bold, FontSize = 15 },
                enableHttp2,
                transparent,
                new TextBlock
                {
                    Text = "Transparent mode redirects outbound :80/:443 via the WinDivert driver — " +
                           "requires running as Administrator with WinDivert.dll + WinDivert64.sys present. " +
                           "Applied on next Start.",
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                    FontSize = 11,
                    Opacity = 0.7,
                },
                new Separator(),
                new TextBlock { Text = "Network simulation", FontWeight = FontWeight.Bold, FontSize = 15 },
                throttleEnabled,
                Labeled("Preset", presets),
                Labeled("Bandwidth (kbps, 0 = unlimited)", kbps),
                Labeled("Extra latency per response (ms)", latency),
                new Separator(),
                new TextBlock { Text = "Connection", FontWeight = FontWeight.Bold, FontSize = 15 },
                Labeled("Upstream proxy", upstream),
                new TextBlock { Text = "TLS passthrough hosts" },
                passthrough,
                buttons,
            },
        };

        Content = panel;
    }

    private static Control Labeled(string label, Control control) => new StackPanel
    {
        Spacing = 2,
        Children = { new TextBlock { Text = label }, control },
    };
}
