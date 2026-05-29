using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using HttpSpy.Core.Models;

namespace HttpSpy.App.Converters;

/// <summary>Maps an ARGB uint highlight color to a brush (transparent when 0).</summary>
public sealed class HighlightToBrushConverter : IValueConverter
{
    public static readonly HighlightToBrushConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is uint argb && argb != 0)
            return new SolidColorBrush(Color.FromUInt32(argb));
        return Brushes.Transparent;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Maps an HTTP status code to a status-tinted brush for the grid.</summary>
public sealed class StatusToBrushConverter : IValueConverter
{
    public static readonly StatusToBrushConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        int code = value switch
        {
            int i => i,
            _ => 0
        };
        return code switch
        {
            >= 500 => new SolidColorBrush(Color.FromRgb(0xD3, 0x2F, 0x2F)),
            >= 400 => new SolidColorBrush(Color.FromRgb(0xE6, 0x7E, 0x22)),
            >= 300 => new SolidColorBrush(Color.FromRgb(0x8E, 0x44, 0xAD)),
            >= 200 => new SolidColorBrush(Color.FromRgb(0x27, 0xAE, 0x60)),
            > 0 => new SolidColorBrush(Color.FromRgb(0x29, 0x80, 0xB9)),
            _ => Brushes.Gray
        };
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Maps a SessionKind to a short scheme glyph.</summary>
public sealed class KindToGlyphConverter : IValueConverter
{
    public static readonly KindToGlyphConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is SessionKind k ? k switch
        {
            SessionKind.Https => "🔒",
            SessionKind.WebSocket => "🔌",
            SessionKind.ServerSentEvents => "📡",
            SessionKind.Tunnel => "⇄",
            _ => "🌐"
        } : "";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Formats a byte count as a human readable size.</summary>
public sealed class ByteSizeConverter : IValueConverter
{
    public static readonly ByteSizeConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        long bytes = value switch
        {
            long l => l,
            int i => i,
            _ => 0
        };
        return Format(bytes);
    }

    public static string Format(long bytes)
    {
        if (bytes <= 0) return "0";
        string[] units = { "B", "KB", "MB", "GB" };
        double size = bytes;
        int unit = 0;
        while (size >= 1024 && unit < units.Length - 1) { size /= 1024; unit++; }
        return unit == 0 ? $"{bytes} {units[unit]}" : $"{size:0.#} {units[unit]}";
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Selects one of two strings based on a boolean (e.g. button captions).</summary>
public sealed class BoolToTextConverter : IValueConverter
{
    public string TrueText { get; set; } = "";
    public string FalseText { get; set; } = "";

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? TrueText : FalseText;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>True when a string is non-empty (used for visibility bindings).</summary>
public sealed class StringNotEmptyConverter : IValueConverter
{
    public static readonly StringNotEmptyConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string s && !string.IsNullOrEmpty(s);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
