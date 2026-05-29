using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace HttpSpy.App.Views;

public partial class SubmitterView : UserControl
{
    public SubmitterView() => InitializeComponent();

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
