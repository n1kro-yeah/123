using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using HttpSpy.App.ViewModels;

namespace HttpSpy.App.Views;

public partial class StructureView : UserControl
{
    public StructureView() => InitializeComponent();

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private StructureViewModel? Model => DataContext as StructureViewModel;

    /// <summary>Double-clicking a branch jumps to its first request in the grid.</summary>
    private void Node_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is Control { DataContext: StructureNodeViewModel node })
            Model?.OpenNodeCommand.Execute(node);
    }

    private void Connection_DoubleTapped(object? sender, TappedEventArgs e)
    {
        // The same template serves connections and their streams; only a stream
        // maps to a single session worth revealing.
        if (sender is Control { DataContext: ConnectionStreamViewModel stream })
            Model?.OpenStreamCommand.Execute(stream);
    }
}
