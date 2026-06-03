using System;
using System.Collections.Generic;
using System.Text;

namespace HttpSpy.Core.Util;

/// <summary>The request fields recovered from a <c>curl</c> command line.</summary>
public sealed class CurlRequest
{
    public string Method { get; set; } = "GET";
    public string Url { get; set; } = "";
    public List<(string Name, string Value)> Headers { get; } = new();
    public string Body { get; set; } = "";
}

/// <summary>
/// Parses a <c>curl</c> command line (as produced by browser "Copy as cURL"
/// and most API tools) into a structured request. Supports the flags people
/// actually paste: -X/--request, -H/--header, -d/--data[-raw|-binary|-urlencode],
/// --json, -G/--get, -u/--user, -b/--cookie, --compressed, -A/--user-agent,
/// -e/--referer, line-continuation backslashes and single/double quoting.
/// </summary>
public static class CurlParser
{
    public static CurlRequest Parse(string command)
    {
        var req = new CurlRequest();
        var tokens = Tokenize(command);
        bool methodExplicit = false;
        bool getFlag = false;
        var dataParts = new List<string>();
        string contentType = "";

        for (int i = 0; i < tokens.Count; i++)
        {
            string t = tokens[i];
            if (t.Length == 0) continue;
            if (string.Equals(t, "curl", StringComparison.OrdinalIgnoreCase)) continue;

            string? Next() => i + 1 < tokens.Count ? tokens[++i] : null;

            switch (t)
            {
                case "-X":
                case "--request":
                    var m = Next();
                    if (m is not null) { req.Method = m.ToUpperInvariant(); methodExplicit = true; }
                    break;

                case "-H":
                case "--header":
                    AddHeader(req, Next(), ref contentType);
                    break;

                case "-A":
                case "--user-agent":
                    var ua = Next();
                    if (ua is not null) req.Headers.Add(("User-Agent", ua));
                    break;

                case "-e":
                case "--referer":
                    var rf = Next();
                    if (rf is not null) req.Headers.Add(("Referer", rf));
                    break;

                case "-b":
                case "--cookie":
                    var ck = Next();
                    if (ck is not null && ck.Contains('=')) req.Headers.Add(("Cookie", ck));
                    break;

                case "-u":
                case "--user":
                    var cred = Next();
                    if (cred is not null)
                        req.Headers.Add(("Authorization",
                            "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(cred))));
                    break;

                case "-d":
                case "--data":
                case "--data-raw":
                case "--data-binary":
                case "--data-ascii":
                    var d = Next();
                    if (d is not null) dataParts.Add(d);
                    break;

                case "--data-urlencode":
                    var due = Next();
                    if (due is not null) dataParts.Add(due);
                    break;

                case "--json":
                    var j = Next();
                    if (j is not null) { dataParts.Add(j); if (contentType.Length == 0) contentType = "application/json"; }
                    break;

                case "-G":
                case "--get":
                    getFlag = true;
                    break;

                case "--url":
                    var u = Next();
                    if (u is not null) req.Url = u;
                    break;

                case "--compressed":
                case "-s": case "--silent":
                case "-L": case "--location":
                case "-k": case "--insecure":
                case "-i": case "--include":
                case "-v": case "--verbose":
                case "-#": case "--progress-bar":
                case "-S": case "--show-error":
                    break; // no-ops for request reconstruction

                default:
                    if (t.StartsWith("-", StringComparison.Ordinal))
                    {
                        // Unknown flag — skip its value if the next token isn't a flag/url.
                        if (i + 1 < tokens.Count && !tokens[i + 1].StartsWith("-", StringComparison.Ordinal)
                            && !LooksLikeUrl(tokens[i + 1]))
                            i++;
                    }
                    else if (req.Url.Length == 0)
                    {
                        req.Url = t;
                    }
                    break;
            }
        }

        if (dataParts.Count > 0)
        {
            req.Body = string.Join("&", dataParts);
            if (!methodExplicit && !getFlag) req.Method = "POST";
            if (contentType.Length == 0) contentType = "application/x-www-form-urlencoded";
            if (!HasHeader(req, "Content-Type"))
                req.Headers.Add(("Content-Type", contentType));
        }

        if (getFlag && dataParts.Count > 0)
        {
            // -G moves data into the query string.
            string sep = req.Url.Contains('?') ? "&" : "?";
            req.Url += sep + req.Body;
            req.Body = "";
            req.Method = methodExplicit ? req.Method : "GET";
        }

        return req;
    }

    private static bool HasHeader(CurlRequest req, string name)
    {
        foreach (var (n, _) in req.Headers)
            if (string.Equals(n, name, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static void AddHeader(CurlRequest req, string? raw, ref string contentType)
    {
        if (string.IsNullOrEmpty(raw)) return;
        int colon = raw.IndexOf(':');
        if (colon <= 0) return;
        string name = raw[..colon].Trim();
        string value = raw[(colon + 1)..].Trim();
        if (string.Equals(name, "Content-Type", StringComparison.OrdinalIgnoreCase)) contentType = value;
        req.Headers.Add((name, value));
    }

    private static bool LooksLikeUrl(string s) =>
        s.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
        s.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    /// <summary>Shell-style tokenizer: honours quotes and line-continuation backslashes.</summary>
    private static List<string> Tokenize(string input)
    {
        var tokens = new List<string>();
        var sb = new StringBuilder();
        bool inToken = false;
        char quote = '\0';

        for (int i = 0; i < input.Length; i++)
        {
            char c = input[i];
            if (quote != '\0')
            {
                if (c == quote) { quote = '\0'; }
                else if (c == '\\' && quote == '"' && i + 1 < input.Length)
                {
                    char nx = input[i + 1];
                    if (nx is '"' or '\\' or '$' or '`') { sb.Append(nx); i++; }
                    else sb.Append(c);
                }
                else sb.Append(c);
                continue;
            }

            switch (c)
            {
                case '\'':
                case '"':
                    quote = c; inToken = true; break;
                case '\\':
                    // Line continuation or escaped char.
                    if (i + 1 < input.Length && (input[i + 1] == '\n' || input[i + 1] == '\r')) { /* skip */ }
                    else if (i + 1 < input.Length) { sb.Append(input[i + 1]); inToken = true; i++; }
                    break;
                case ' ':
                case '\t':
                case '\r':
                case '\n':
                    if (inToken) { tokens.Add(sb.ToString()); sb.Clear(); inToken = false; }
                    break;
                default:
                    sb.Append(c); inToken = true; break;
            }
        }
        if (inToken || sb.Length > 0) tokens.Add(sb.ToString());
        return tokens;
    }
}
