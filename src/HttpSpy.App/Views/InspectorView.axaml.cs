using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace HttpSpy.App.Views;

public partial class InspectorView : UserControl
{
    public InspectorView() => InitializeComponent();

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
