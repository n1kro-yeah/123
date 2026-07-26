using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using HttpSpy.Core.Analysis;
using HttpSpy.Core.Localization;
using HttpSpy.Core.Models;

namespace HttpSpy.App.Converters;

/// <summary>
/// Brushes shared by the converters. Cached and frozen: the grid asks for a
/// brush per visible cell on every refresh, and allocating a fresh
/// SolidColorBrush each time is pure churn on the UI thread.
/// </summary>
internal static class Palette
{
    public static readonly IBrush Transparent = Brushes.Transparent;

    public static readonly IBrush Success = Freeze(0x2E, 0xA0, 0x43);
    public static readonly IBrush Redirect = Freeze(0x8B, 0x5C, 0xF6);
    public static readonly IBrush ClientError = Freeze(0xE1, 0x6F, 0x24);
    public static readonly IBrush ServerError = Freeze(0xE5, 0x48, 0x4A);
    public static readonly IBrush Informational = Freeze(0x39, 0xA0, 0xC5);
    public static readonly IBrush Neutral = Freeze(0x8B, 0x94, 0x9E);

    public static readonly IBrush SuccessSoft = Freeze(0x2E, 0xA0, 0x43, 0x28);
    public static readonly IBrush RedirectSoft = Freeze(0x8B, 0x5C, 0xF6, 0x28);
    public static readonly IBrush ClientErrorSoft = Freeze(0xE1, 0x6F, 0x24, 0x2E);
    public static readonly IBrush ServerErrorSoft = Freeze(0xE5, 0x48, 0x4A, 0x2E);
    public static readonly IBrush InformationalSoft = Freeze(0x39, 0xA0, 0xC5, 0x28);
    public static readonly IBrush NeutralSoft = Freeze(0x8B, 0x94, 0x9E, 0x24);

    public static readonly IBrush Critical = Freeze(0xCF, 0x22, 0x2E);
    public static readonly IBrush High = Freeze(0xE1, 0x6F, 0x24);
    public static readonly IBrush Medium = Freeze(0xD4, 0xA7, 0x2C);
    public static readonly IBrush Low = Freeze(0x54, 0xAE, 0xFF);
    public static readonly IBrush Info = Freeze(0x8B, 0x94, 0x9E);

    public static readonly IBrush CriticalSoft = Freeze(0xCF, 0x22, 0x2E, 0x2E);
    public static readonly IBrush HighSoft = Freeze(0xE1, 0x6F, 0x24, 0x2E);
    public static readonly IBrush MediumSoft = Freeze(0xD4, 0xA7, 0x2C, 0x2E);
    public static readonly IBrush LowSoft = Freeze(0x54, 0xAE, 0xFF, 0x28);
    public static readonly IBrush InfoSoft = Freeze(0x8B, 0x94, 0x9E, 0x24);

    private static IBrush Freeze(byte r, byte g, byte b, byte a = 0xFF)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.ToImmutable();
        return brush.ToImmutable();
    }
}

/// <summary>
/// Maps an ARGB uint highlight colour to a row-background brush. Highlight rules
/// write <see cref="HttpSession.HighlightColor"/>; before this converter was
/// bound in the grid the whole highlighting feature had no visible effect.
/// </summary>
public sealed class HighlightToBrushConverter : IValueConverter
{
    public static readonly HighlightToBrushConverter Instance = new();

    // Rule colours are authored as opaque pastels; as a row background they need
    // to sit behind the text, so they are re-published at a low alpha.
    private readonly Dictionary<uint, IBrush> _cache = new();

    /// <summary>
    /// Ceiling on row-tint opacity. Highlight rules commonly match a large share
    /// of a capture — 4xx responses are routine while debugging — so a heavy fill
    /// turns the whole grid into a wall of colour and fights the text for
    /// attention. A tint is enough to make a row findable while scrolling.
    /// </summary>
    private const byte MaxTintAlpha = 0x2E;

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not uint argb || argb == 0) return Palette.Transparent;

        lock (_cache)
        {
            if (_cache.TryGetValue(argb, out var cached)) return cached;

            var color = Color.FromUInt32(argb);
            var soft = Color.FromArgb(Math.Min(color.A, MaxTintAlpha), color.R, color.G, color.B);
            var brush = new SolidColorBrush(soft).ToImmutable();

            // Bound the cache: a regex highlight rule could in principle produce
            // many distinct colours over a long capture.
            if (_cache.Count > 256) _cache.Clear();
            _cache[argb] = brush;
            return brush;
        }
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// Maps an HTTP status code to a foreground brush. Pass <c>soft</c> as the
/// converter parameter for the translucent background variant used by pills.
/// </summary>
public sealed class StatusToBrushConverter : IValueConverter
{
    public static readonly StatusToBrushConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        int code = value switch
        {
            int i => i,
            long l => (int)l,
            _ => 0,
        };

        bool soft = parameter as string == "soft";
        return code switch
        {
            >= 500 => soft ? Palette.ServerErrorSoft : Palette.ServerError,
            >= 400 => soft ? Palette.ClientErrorSoft : Palette.ClientError,
            >= 300 => soft ? Palette.RedirectSoft : Palette.Redirect,
            >= 200 => soft ? Palette.SuccessSoft : Palette.Success,
            > 0 => soft ? Palette.InformationalSoft : Palette.Informational,
            _ => soft ? Palette.NeutralSoft : Palette.Neutral,
        };
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Maps an analysis severity to its accent brush (or its soft fill).</summary>
public sealed class SeverityToBrushConverter : IValueConverter
{
    public static readonly SeverityToBrushConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool soft = parameter as string == "soft";
        return value switch
        {
            FindingSeverity.Critical => soft ? Palette.CriticalSoft : Palette.Critical,
            FindingSeverity.High => soft ? Palette.HighSoft : Palette.High,
            FindingSeverity.Medium => soft ? Palette.MediumSoft : Palette.Medium,
            FindingSeverity.Low => soft ? Palette.LowSoft : Palette.Low,
            _ => soft ? Palette.InfoSoft : Palette.Info,
        };
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Maps a 0–100 health score to a traffic-light brush.</summary>
public sealed class ScoreToBrushConverter : IValueConverter
{
    public static readonly ScoreToBrushConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        int score = value switch { int i => i, double d => (int)d, _ => 100 };
        return score switch
        {
            >= 85 => Palette.Success,
            >= 60 => Palette.Medium,
            _ => Palette.Critical,
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
            double d => (long)d,
            _ => 0
        };
        return Format(bytes);
    }

    public static string Format(long bytes)
    {
        if (bytes < 0) return "—";
        if (bytes == 0) return "0 B";
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double size = bytes;
        int unit = 0;
        while (size >= 1024 && unit < units.Length - 1) { size /= 1024; unit++; }
        return unit == 0 ? $"{bytes} B" : $"{size:0.#} {units[unit]}";
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Formats a millisecond duration compactly (µs / ms / s).</summary>
public sealed class DurationConverter : IValueConverter
{
    public static readonly DurationConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        double ms = value switch { double d => d, int i => i, long l => l, _ => -1 };
        return Format(ms);
    }

    public static string Format(double ms)
    {
        if (ms < 0) return "—";
        if (ms < 1) return $"{ms * 1000:F0} µs";
        if (ms < 1000) return $"{ms:F0} ms";
        return $"{ms / 1000:F2} s";
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

/// <summary>Tints a diagnostic log line: errors red, warnings amber, "listening" green.</summary>
public sealed class LogLineToBrushConverter : IValueConverter
{
    public static readonly LogLineToBrushConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string s) return Palette.Neutral;

        if (s.Contains("fail", StringComparison.OrdinalIgnoreCase) ||
            s.Contains("error", StringComparison.OrdinalIgnoreCase) ||
            s.Contains("refused", StringComparison.OrdinalIgnoreCase) ||
            s.Contains("exception", StringComparison.OrdinalIgnoreCase))
            return Palette.ServerError;

        if (s.Contains("warn", StringComparison.OrdinalIgnoreCase) ||
            s.Contains("timeout", StringComparison.OrdinalIgnoreCase) ||
            s.Contains("truncat", StringComparison.OrdinalIgnoreCase) ||
            s.Contains("retry", StringComparison.OrdinalIgnoreCase) ||
            s.Contains("released", StringComparison.OrdinalIgnoreCase))
            return Palette.Medium;

        if (s.Contains("listening", StringComparison.OrdinalIgnoreCase) ||
            s.Contains("started", StringComparison.OrdinalIgnoreCase) ||
            s.Contains("active", StringComparison.OrdinalIgnoreCase) ||
            s.Contains("stopped", StringComparison.OrdinalIgnoreCase))
            return Palette.Success;

        return Palette.Neutral;
    }

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

/// <summary>True when a collection or count is non-empty.</summary>
public sealed class CountToBoolConverter : IValueConverter
{
    public static readonly CountToBoolConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool any = value switch
        {
            int i => i > 0,
            long l => l > 0,
            System.Collections.ICollection c => c.Count > 0,
            null => false,
            _ => true,
        };
        return parameter as string == "invert" ? !any : any;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// Maps a boolean onto a two-item ComboBox index, inverted: index 0 = true.
/// Used by the capture-filter editor, where "Drop" (Exclude = true) reads much
/// better as the first entry than a bare checkbox labelled "Exclude".
/// </summary>
public sealed class InvertedBoolIndexConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? 0 : 1;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is int i && i == 0;
}

/// <summary>
/// Displays the quick-filter sentinel values ("All types", "All hosts", …) in
/// the interface language while leaving the underlying strings alone.
///
/// Those sentinels double as the filter logic's "no filter" markers and are
/// compared by value throughout the view model, so translating the values
/// themselves would silently disable the filters. Only the label is localized.
/// </summary>
public sealed class QuickFilterLabelConverter : IValueConverter
{
    private static readonly Dictionary<string, string> Keys = new(StringComparer.Ordinal)
    {
        ["All types"] = "Filter.AllTypes",
        ["All hosts"] = "Filter.AllHosts",
        ["All processes"] = "Filter.AllProcesses",
        ["All"] = "Filter.All",
    };

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string text) return value;
        return Keys.TryGetValue(text, out var key) ? Loc.Current[key] : text;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
