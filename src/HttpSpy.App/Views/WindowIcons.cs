using System;
using Avalonia.Controls;
using Avalonia.Platform;

namespace HttpSpy.App.Views;

/// <summary>Provides the shared HttpSpy window icon for code-created dialogs.</summary>
public static class WindowIcons
{
    private static WindowIcon? _icon;

    public static WindowIcon Shared =>
        _icon ??= new WindowIcon(AssetLoader.Open(new Uri("avares://HttpSpy/Assets/httpspy.ico")));

    /// <summary>Sets the HttpSpy icon on the given window (ignores any load failure).</summary>
    public static void Apply(Window window)
    {
        try { window.Icon = Shared; }
        catch { /* asset missing in design-time / tests */ }
    }
}
