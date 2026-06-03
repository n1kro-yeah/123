using System;
using System.Collections.Generic;
using Avalonia.Media;

namespace HttpSpy.App.Controls;

/// <summary>The semantic role of a highlighted token (drives its colour).</summary>
public enum TokenKind
{
    Plain,
    Key,
    String,
    Number,
    Keyword,     // true / false / null
    Punctuation,
    TagName,
    AttrName,
    AttrValue,
    Comment,
    Cdata,
}

/// <summary>A run of text plus the role it plays, produced by the tokenizers.</summary>
public readonly record struct SyntaxToken(string Text, TokenKind Kind);

/// <summary>
/// Lightweight, dependency-free syntax tokenizers for the payload viewers.
/// Produces colourised token streams for JSON and XML/HTML so the inspector
/// reads like a real editor (Charles / Proxyman / HTTP Toolkit) instead of a
/// plain monospace dump. The tokenizers are intentionally tolerant — they
/// never throw on malformed input, they just fall back to plain text.
/// </summary>
public static class SyntaxHighlighter
{
    /// <summary>Caps highlighting work so huge payloads never freeze the UI.</summary>
    public const int MaxHighlightChars = 200_000;

    public static IReadOnlyList<SyntaxToken> Tokenize(string? text, SyntaxLanguage language)
    {
        if (string.IsNullOrEmpty(text)) return Array.Empty<SyntaxToken>();
        if (text.Length > MaxHighlightChars || language == SyntaxLanguage.None)
            return new[] { new SyntaxToken(text, TokenKind.Plain) };

        try
        {
            return language switch
            {
                SyntaxLanguage.Json => TokenizeJson(text),
                SyntaxLanguage.Xml => TokenizeXml(text),
                _ => new[] { new SyntaxToken(text, TokenKind.Plain) },
            };
        }
        catch
        {
            return new[] { new SyntaxToken(text, TokenKind.Plain) };
        }
    }

    // ---- JSON ----------------------------------------------------------------
    private static List<SyntaxToken> TokenizeJson(string s)
    {
        var tokens = new List<SyntaxToken>();
        int i = 0, n = s.Length;
        while (i < n)
        {
            char c = s[i];
            if (c == '"')
            {
                int start = i++;
                while (i < n)
                {
                    if (s[i] == '\\') { i += 2; continue; }
                    if (s[i] == '"') { i++; break; }
                    i++;
                }
                string str = s.Substring(start, Math.Min(i, n) - start);
                // A string immediately followed by ':' is a property key.
                int j = i;
                while (j < n && (s[j] == ' ' || s[j] == '\t')) j++;
                bool isKey = j < n && s[j] == ':';
                tokens.Add(new SyntaxToken(str, isKey ? TokenKind.Key : TokenKind.String));
            }
            else if (c == '-' || char.IsDigit(c))
            {
                int start = i++;
                while (i < n && (char.IsDigit(s[i]) || s[i] is '.' or 'e' or 'E' or '+' or '-')) i++;
                tokens.Add(new SyntaxToken(s.Substring(start, i - start), TokenKind.Number));
            }
            else if (char.IsLetter(c))
            {
                int start = i++;
                while (i < n && char.IsLetter(s[i])) i++;
                string word = s.Substring(start, i - start);
                tokens.Add(new SyntaxToken(word,
                    word is "true" or "false" or "null" ? TokenKind.Keyword : TokenKind.Plain));
            }
            else if (c is '{' or '}' or '[' or ']' or ':' or ',')
            {
                tokens.Add(new SyntaxToken(c.ToString(), TokenKind.Punctuation));
                i++;
            }
            else
            {
                int start = i++;
                while (i < n && s[i] is ' ' or '\t' or '\r' or '\n') i++;
                tokens.Add(new SyntaxToken(s.Substring(start, i - start), TokenKind.Plain));
            }
        }
        return Coalesce(tokens);
    }

    // ---- XML / HTML ----------------------------------------------------------
    private static List<SyntaxToken> TokenizeXml(string s)
    {
        var tokens = new List<SyntaxToken>();
        int i = 0, n = s.Length;
        while (i < n)
        {
            char c = s[i];
            if (c == '<')
            {
                // Comment <!-- ... -->
                if (i + 3 < n && s[i + 1] == '!' && s[i + 2] == '-' && s[i + 3] == '-')
                {
                    int end = s.IndexOf("-->", i + 4, StringComparison.Ordinal);
                    end = end < 0 ? n : end + 3;
                    tokens.Add(new SyntaxToken(s.Substring(i, end - i), TokenKind.Comment));
                    i = end;
                    continue;
                }
                // CDATA
                if (i + 8 < n && s.AsSpan(i, 9).SequenceEqual("<![CDATA["))
                {
                    int end = s.IndexOf("]]>", i + 9, StringComparison.Ordinal);
                    end = end < 0 ? n : end + 3;
                    tokens.Add(new SyntaxToken(s.Substring(i, end - i), TokenKind.Cdata));
                    i = end;
                    continue;
                }
                int tagEnd = s.IndexOf('>', i);
                if (tagEnd < 0) tagEnd = n - 1;
                TokenizeTag(s, i, tagEnd + 1, tokens);
                i = tagEnd + 1;
            }
            else
            {
                int start = i;
                while (i < n && s[i] != '<') i++;
                tokens.Add(new SyntaxToken(s.Substring(start, i - start), TokenKind.Plain));
            }
        }
        return Coalesce(tokens);
    }

    private static void TokenizeTag(string s, int start, int end, List<SyntaxToken> tokens)
    {
        int i = start;
        // Opening punctuation: '<', '</', '<?'
        int p = i + 1;
        while (p < end && (s[p] == '/' || s[p] == '?' || s[p] == '!')) p++;
        tokens.Add(new SyntaxToken(s.Substring(i, p - i), TokenKind.Punctuation));
        i = p;

        // Tag name
        int nameStart = i;
        while (i < end && (char.IsLetterOrDigit(s[i]) || s[i] is '-' or '_' or ':' or '.')) i++;
        if (i > nameStart) tokens.Add(new SyntaxToken(s.Substring(nameStart, i - nameStart), TokenKind.TagName));

        // Attributes
        while (i < end)
        {
            char c = s[i];
            if (c == '"' || c == '\'')
            {
                char quote = c;
                int vs = i++;
                while (i < end && s[i] != quote) i++;
                if (i < end) i++;
                tokens.Add(new SyntaxToken(s.Substring(vs, i - vs), TokenKind.AttrValue));
            }
            else if (char.IsLetter(c) || c == '_')
            {
                int as_ = i;
                while (i < end && (char.IsLetterOrDigit(s[i]) || s[i] is '-' or '_' or ':' or '.')) i++;
                tokens.Add(new SyntaxToken(s.Substring(as_, i - as_), TokenKind.AttrName));
            }
            else
            {
                tokens.Add(new SyntaxToken(c.ToString(), TokenKind.Punctuation));
                i++;
            }
        }
    }

    /// <summary>Merges adjacent tokens of the same kind to cut down inline count.</summary>
    private static List<SyntaxToken> Coalesce(List<SyntaxToken> tokens)
    {
        var merged = new List<SyntaxToken>(tokens.Count);
        foreach (var t in tokens)
        {
            if (merged.Count > 0 && merged[^1].Kind == t.Kind && t.Kind is TokenKind.Plain)
                merged[^1] = new SyntaxToken(merged[^1].Text + t.Text, t.Kind);
            else
                merged.Add(t);
        }
        return merged;
    }

    /// <summary>Maps a token role to a brush, picking a palette per theme.</summary>
    public static IBrush BrushFor(TokenKind kind, bool dark)
    {
        return dark ? DarkPalette(kind) : LightPalette(kind);
    }

    private static IBrush DarkPalette(TokenKind kind) => kind switch
    {
        TokenKind.Key => Brush(0xFF9CDCFE),
        TokenKind.String or TokenKind.AttrValue => Brush(0xFFCE9178),
        TokenKind.Number => Brush(0xFFB5CEA8),
        TokenKind.Keyword => Brush(0xFF569CD6),
        TokenKind.TagName => Brush(0xFF4EC9B0),
        TokenKind.AttrName => Brush(0xFF9CDCFE),
        TokenKind.Comment => Brush(0xFF6A9955),
        TokenKind.Cdata => Brush(0xFFC586C0),
        TokenKind.Punctuation => Brush(0xFF808080),
        _ => Brush(0xFFD4D4D4),
    };

    private static IBrush LightPalette(TokenKind kind) => kind switch
    {
        TokenKind.Key => Brush(0xFF0451A5),
        TokenKind.String or TokenKind.AttrValue => Brush(0xFFA31515),
        TokenKind.Number => Brush(0xFF098658),
        TokenKind.Keyword => Brush(0xFF0000FF),
        TokenKind.TagName => Brush(0xFF267F99),
        TokenKind.AttrName => Brush(0xFFE50000),
        TokenKind.Comment => Brush(0xFF008000),
        TokenKind.Cdata => Brush(0xFFAF00DB),
        TokenKind.Punctuation => Brush(0xFF6F6F6F),
        _ => Brush(0xFF1E1E1E),
    };

    private static readonly Dictionary<uint, IBrush> _brushCache = new();

    private static IBrush Brush(uint argb)
    {
        if (_brushCache.TryGetValue(argb, out var b)) return b;
        var brush = new SolidColorBrush(Color.FromUInt32(argb));
        _brushCache[argb] = brush;
        return brush;
    }
}

/// <summary>Languages the payload viewers know how to colourise.</summary>
public enum SyntaxLanguage
{
    None,
    Json,
    Xml,
}
