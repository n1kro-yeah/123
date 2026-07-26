using System;
using Avalonia.Data;
using Avalonia.Markup.Xaml;
using HttpSpy.Core.Localization;

namespace HttpSpy.App.Localization;

/// <summary>
/// Resolves a catalogue key from XAML: <c>Content="{l:T Toolbar.Start}"</c>.
///
/// The type is named <c>TExtension</c> because Avalonia strips the "Extension"
/// suffix when resolving markup extensions, and <c>{l:T …}</c> is short enough
/// to sit inline without making the markup harder to read than the literal was.
///
/// It deliberately returns a <see cref="Binding"/> rather than a plain string.
/// Binding to the catalogue's indexer means switching language re-evaluates
/// every localized string already on screen — a restart-to-apply language
/// switch is the kind of thing people assume is broken.
/// </summary>
public sealed class TExtension : MarkupExtension
{
    public TExtension() { }

    public TExtension(string key) => Key = key;

    /// <summary>The catalogue key, e.g. <c>Tab.Capture</c>.</summary>
    public string Key { get; set; } = string.Empty;

    public override object ProvideValue(IServiceProvider serviceProvider) => new Binding
    {
        Source = Loc.Current,
        Path = $"[{Key}]",
        Mode = BindingMode.OneWay,
    };
}
