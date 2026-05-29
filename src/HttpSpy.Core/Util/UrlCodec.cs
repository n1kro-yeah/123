using System.Text;

namespace HttpSpy.Core.Util;

/// <summary>URL / query-string helpers that preserve order and duplicates.</summary>
public static class UrlCodec
{
    public static IEnumerable<KeyValuePair<string, string>> ParseQuery(string? query)
    {
        if (string.IsNullOrEmpty(query)) yield break;
        if (query.StartsWith('?')) query = query[1..];
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var idx = pair.IndexOf('=');
            if (idx < 0)
                yield return new KeyValuePair<string, string>(Decode(pair), string.Empty);
            else
                yield return new KeyValuePair<string, string>(Decode(pair[..idx]), Decode(pair[(idx + 1)..]));
        }
    }

    public static string BuildQuery(IEnumerable<KeyValuePair<string, string>> pairs)
    {
        var sb = new StringBuilder();
        foreach (var p in pairs)
        {
            if (sb.Length > 0) sb.Append('&');
            sb.Append(Encode(p.Key));
            sb.Append('=');
            sb.Append(Encode(p.Value));
        }
        return sb.ToString();
    }

    public static string Decode(string s) => Uri.UnescapeDataString(s.Replace('+', ' '));

    public static string Encode(string s) => Uri.EscapeDataString(s);
}
