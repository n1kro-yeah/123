using System.Text;
using System.Text.RegularExpressions;
using HttpSpy.Core.Models;

namespace HttpSpy.Core.Rules;

/// <summary>
/// Implements HTTP Debugger's "HTTP Modifier": regex find/replace applied to the
/// raw header block and/or the message body, on the fly. Capture groups
/// (<c>$1</c>, <c>${name}</c>) work in the replacement, the literal escapes
/// <c>\r \n \t</c> are honoured, and <c>Content-Length</c> is recalculated after
/// the body changes. Bodies are treated as Latin-1 so the transform is
/// byte-preserving for non-UTF-8 / binary payloads.
/// </summary>
public static class HttpModifier
{
    private static readonly Encoding Bytes = Encoding.Latin1;

    /// <summary>Serializes a header collection to a raw <c>Name: Value\r\n</c> block.</summary>
    public static string RawHeaderBlock(HeaderCollection headers)
    {
        var sb = new StringBuilder();
        foreach (var h in headers.Items)
            sb.Append(h.Name).Append(": ").Append(h.Value).Append("\r\n");
        return sb.ToString();
    }

    /// <summary>
    /// Applies all modifier rules of a single <see cref="Rule"/> to one direction
    /// of a session. Returns true when anything changed.
    /// </summary>
    public static bool Apply(Rule rule, HttpSession session, bool responsePhase)
    {
        bool changed = false;
        foreach (var mod in rule.ModifierRules)
        {
            if (string.IsNullOrEmpty(mod.Find)) continue;
            switch (mod.Target)
            {
                case ModifierTarget.RequestHeaders when !responsePhase:
                    changed |= RewriteHeaders(session.RequestHeaders, mod);
                    break;
                case ModifierTarget.RequestBody when !responsePhase:
                    if (RewriteBody(session.RequestBody, mod, out var newReq))
                    {
                        session.RequestBody = newReq;
                        SyncContentLength(session.RequestHeaders, newReq.Length);
                        changed = true;
                    }
                    break;
                case ModifierTarget.ResponseHeaders when responsePhase:
                    changed |= RewriteHeaders(session.ResponseHeaders, mod);
                    break;
                case ModifierTarget.ResponseBody when responsePhase:
                    if (RewriteBody(session.ResponseBody, mod, out var newResp))
                    {
                        session.ResponseBody = newResp;
                        SyncContentLength(session.ResponseHeaders, newResp.Length);
                        changed = true;
                    }
                    break;
            }
        }
        return changed;
    }

    private static bool RewriteHeaders(HeaderCollection headers, ModifierRule mod)
    {
        string block = RawHeaderBlock(headers);
        string replacement = TranslateEscapes(mod.Replace);
        string updated;
        try
        {
            updated = Regex.Replace(block, mod.Find, replacement,
                RegexOptions.IgnoreCase | RegexOptions.Multiline, Rule.RegexTimeout);
        }
        catch (ArgumentException) { return false; }        // invalid user pattern
        catch (RegexMatchTimeoutException) { return false; } // pathological backtracking
        if (updated == block) return false;
        ReparseHeaders(headers, updated);
        return true;
    }

    private static bool RewriteBody(byte[] body, ModifierRule mod, out byte[] result)
    {
        result = body;
        if (body.Length == 0) return false;
        string text = Bytes.GetString(body);
        string replacement = TranslateEscapes(mod.Replace);
        string updated;
        try
        {
            updated = Regex.Replace(text, mod.Find, replacement,
                RegexOptions.IgnoreCase | RegexOptions.Singleline, Rule.RegexTimeout);
        }
        catch (ArgumentException) { return false; }
        catch (RegexMatchTimeoutException) { return false; }
        if (updated == text) return false;
        result = Bytes.GetBytes(updated);
        return true;
    }

    /// <summary>Replaces the in-collection headers with those parsed from a raw block.</summary>
    private static void ReparseHeaders(HeaderCollection headers, string block)
    {
        headers.Clear();
        foreach (var raw in block.Split("\r\n"))
        {
            if (raw.Length == 0) continue;
            int colon = raw.IndexOf(':');
            if (colon <= 0) continue;
            headers.Add(raw[..colon].Trim(), raw[(colon + 1)..].Trim());
        }
    }

    private static void SyncContentLength(HeaderCollection headers, int length)
    {
        // Only adjust when the message is not chunked; chunked bodies have no
        // Content-Length and the wire serializer recomputes framing itself.
        if (string.Equals(headers["Transfer-Encoding"], "chunked", StringComparison.OrdinalIgnoreCase))
            return;
        headers.Set("Content-Length", length.ToString());
    }

    /// <summary>Turns the literal escapes <c>\r \n \t</c> in a replacement into control chars.</summary>
    internal static string TranslateEscapes(string value)
    {
        if (value.IndexOf('\\') < 0) return value;
        var sb = new StringBuilder(value.Length);
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            if (c == '\\' && i + 1 < value.Length)
            {
                char n = value[++i];
                sb.Append(n switch { 'r' => '\r', 'n' => '\n', 't' => '\t', '\\' => '\\', _ => n });
            }
            else sb.Append(c);
        }
        return sb.ToString();
    }
}
