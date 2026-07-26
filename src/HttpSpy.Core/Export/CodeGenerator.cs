using System.Text;
using HttpSpy.Core.Models;

namespace HttpSpy.Core.Export;

/// <summary>Generates executable code snippets from a captured session.</summary>
public static class CodeGenerator
{
    public enum Language
    {
        Curl,
        CSharp,
        Python,
        JavaScript,
        PowerShell,
        HttpFile,
    }

    /// <summary>Display names, in <see cref="Language"/> order, for pickers.</summary>
    public static readonly string[] LanguageNames =
        { "cURL", "C#", "Python", "JavaScript", "PowerShell", ".http file" };

    /// <summary>
    /// Headers that describe how the body was framed on the wire. Reusing them in
    /// generated code produces requests that contradict the new body, so every
    /// generator drops them and lets its HTTP client recompute them.
    /// </summary>
    private static readonly HashSet<string> WireFramingHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Host", "Content-Length", "Connection", "Proxy-Connection", "Keep-Alive",
        "Transfer-Encoding", "Upgrade", "TE", "Trailer",
    };

    public static string Generate(HttpSession session, Language language) => language switch
    {
        Language.Curl => GenerateCurl(session),
        Language.CSharp => GenerateCSharp(session),
        Language.Python => GeneratePython(session),
        Language.JavaScript => GenerateJavaScript(session),
        Language.PowerShell => GeneratePowerShell(session),
        Language.HttpFile => GenerateHttpFile(session),
        _ => "// Unsupported language",
    };

    private static IEnumerable<HttpHeader> PortableHeaders(HttpSession s, bool keepContentType = true) =>
        s.RequestHeaders.Where(h =>
            !WireFramingHeaders.Contains(h.Name) &&
            (keepContentType || !h.Name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)));

    // ---- curl ----------------------------------------------------------------
    private static string GenerateCurl(HttpSession s)
    {
        var sb = new StringBuilder();
        sb.Append("curl");
        // HEAD needs -I, otherwise curl waits for a body that never arrives.
        if (s.Method.Equals("HEAD", StringComparison.OrdinalIgnoreCase)) sb.Append(" -I");
        else if (s.Method != "GET") sb.Append(" -X ").Append(s.Method);

        sb.Append(' ').Append(SingleQuote(s.FullUrl));
        foreach (var h in PortableHeaders(s))
            sb.Append(" \\\n  -H ").Append(SingleQuote($"{h.Name}: {h.Value}"));

        if (s.RequestBody.Length > 0)
            sb.Append(" \\\n  --data-raw ").Append(SingleQuote(s.RequestBodyText));

        if (!string.IsNullOrEmpty(s.OriginalContentEncoding) ||
            s.RequestHeaders["Accept-Encoding"] is not null)
            sb.Append(" \\\n  --compressed");

        return sb.ToString();
    }

    /// <summary>
    /// Wraps a value in POSIX single quotes. Inside them nothing is special, so
    /// the only escape needed is to close, emit a literal quote, and reopen.
    /// </summary>
    private static string SingleQuote(string value) => "'" + value.Replace("'", @"'\''") + "'";

    // ---- C# ------------------------------------------------------------------
    private static string GenerateCSharp(HttpSession s)
    {
        var sb = new StringBuilder();
        sb.AppendLine("using System.Net.Http;");
        sb.AppendLine();
        sb.AppendLine("using var client = new HttpClient();");
        sb.AppendLine($"using var request = new HttpRequestMessage({CSharpMethod(s.Method)}, {CSharpString(s.FullUrl)});");

        foreach (var h in PortableHeaders(s, keepContentType: false))
            sb.AppendLine($"request.Headers.TryAddWithoutValidation({CSharpString(h.Name)}, {CSharpString(h.Value)});");

        if (s.RequestBody.Length > 0)
        {
            var ct = s.RequestHeaders["Content-Type"] ?? "application/octet-stream";
            // Strip any charset parameter: StringContent appends its own.
            int semi = ct.IndexOf(';');
            var mediaType = (semi >= 0 ? ct[..semi] : ct).Trim();
            sb.AppendLine($"request.Content = new StringContent({CSharpVerbatim(s.RequestBodyText)}, " +
                          $"System.Text.Encoding.UTF8, {CSharpString(mediaType)});");
        }

        sb.AppendLine("var response = await client.SendAsync(request);");
        sb.AppendLine("var body = await response.Content.ReadAsStringAsync();");
        sb.AppendLine("Console.WriteLine($\"{(int)response.StatusCode} {response.ReasonPhrase}\");");
        sb.AppendLine("Console.WriteLine(body);");
        return sb.ToString();
    }

    /// <summary>
    /// Renders a C# regular string literal. Escapes backslashes, quotes and the
    /// control characters that cannot appear literally in a non-verbatim string.
    /// </summary>
    private static string CSharpString(string value)
    {
        var sb = new StringBuilder(value.Length + 2).Append('"');
        foreach (char c in value)
        {
            switch (c)
            {
                case '\\': sb.Append(@"\\"); break;
                case '"': sb.Append("\\\""); break;
                case '\r': sb.Append(@"\r"); break;
                case '\n': sb.Append(@"\n"); break;
                case '\t': sb.Append(@"\t"); break;
                case '\0': sb.Append(@"\0"); break;
                default: sb.Append(c); break;
            }
        }
        return sb.Append('"').ToString();
    }

    /// <summary>
    /// Renders a C# verbatim string literal for a multi-line body. In a verbatim
    /// literal a backslash is *not* an escape and a quote is doubled — escaping it
    /// like a regular string (as this used to) produced code that either failed to
    /// compile or silently changed the payload.
    /// </summary>
    private static string CSharpVerbatim(string value) => "@\"" + value.Replace("\"", "\"\"") + "\"";

    private static string CSharpMethod(string method) => method.ToUpperInvariant() switch
    {
        "GET" => "HttpMethod.Get",
        "POST" => "HttpMethod.Post",
        "PUT" => "HttpMethod.Put",
        "DELETE" => "HttpMethod.Delete",
        "HEAD" => "HttpMethod.Head",
        "OPTIONS" => "HttpMethod.Options",
        "PATCH" => "HttpMethod.Patch",
        "TRACE" => "HttpMethod.Trace",
        // Anything non-standard still needs to round-trip exactly.
        _ => $"new HttpMethod({CSharpString(method)})",
    };

    // ---- Python --------------------------------------------------------------
    private static string GeneratePython(HttpSession s)
    {
        var sb = new StringBuilder();
        sb.AppendLine("import requests");
        sb.AppendLine();
        sb.AppendLine("headers = {");
        foreach (var h in PortableHeaders(s))
            sb.AppendLine($"    {PythonString(h.Name)}: {PythonString(h.Value)},");
        sb.AppendLine("}");
        sb.AppendLine();

        string method = s.Method.ToLowerInvariant();
        bool standard = method is "get" or "post" or "put" or "patch" or "delete" or "head" or "options";
        string call = standard
            ? $"requests.{method}({PythonString(s.FullUrl)}, headers=headers"
            : $"requests.request({PythonString(s.Method)}, {PythonString(s.FullUrl)}, headers=headers";

        if (s.RequestBody.Length > 0)
        {
            sb.AppendLine($"data = {PythonString(s.RequestBodyText)}");
            sb.AppendLine();
            sb.AppendLine($"response = {call}, data=data.encode(\"utf-8\"))");
        }
        else
        {
            sb.AppendLine($"response = {call})");
        }

        sb.AppendLine("print(response.status_code)");
        sb.AppendLine("print(response.text[:2000])");
        return sb.ToString();
    }

    /// <summary>
    /// Renders a Python string literal. Uses a double-quoted single-line form with
    /// explicit escapes rather than a triple-quoted block, which breaks as soon as
    /// the body itself contains a quote run or a trailing backslash.
    /// </summary>
    private static string PythonString(string value)
    {
        var sb = new StringBuilder(value.Length + 2).Append('"');
        foreach (char c in value)
        {
            switch (c)
            {
                case '\\': sb.Append(@"\\"); break;
                case '"': sb.Append("\\\""); break;
                case '\r': sb.Append(@"\r"); break;
                case '\n': sb.Append(@"\n"); break;
                case '\t': sb.Append(@"\t"); break;
                default:
                    if (c < 0x20) sb.Append("\\x").Append(((int)c).ToString("x2"));
                    else sb.Append(c);
                    break;
            }
        }
        return sb.Append('"').ToString();
    }

    // ---- JavaScript ----------------------------------------------------------
    private static string GenerateJavaScript(HttpSession s)
    {
        var sb = new StringBuilder();
        sb.AppendLine("const headers = {");
        foreach (var h in PortableHeaders(s))
            sb.AppendLine($"  {JsString(h.Name)}: {JsString(h.Value)},");
        sb.AppendLine("};");
        sb.AppendLine();

        sb.AppendLine($"const response = await fetch({JsString(s.FullUrl)}, {{");
        sb.AppendLine($"  method: {JsString(s.Method)},");
        sb.AppendLine("  headers,");
        if (s.RequestBody.Length > 0)
            sb.AppendLine($"  body: {JsString(s.RequestBodyText)},");
        sb.AppendLine("});");
        sb.AppendLine("const body = await response.text();");
        sb.AppendLine("console.log(response.status, body.slice(0, 2000));");
        return sb.ToString();
    }

    /// <summary>
    /// Renders a JavaScript string literal. A template literal (the previous
    /// choice) would be broken by a backtick or a <c>${</c> sequence in the body.
    /// </summary>
    private static string JsString(string value)
    {
        var sb = new StringBuilder(value.Length + 2).Append('"');
        foreach (char c in value)
        {
            switch (c)
            {
                case '\\': sb.Append(@"\\"); break;
                case '"': sb.Append("\\\""); break;
                case '\r': sb.Append(@"\r"); break;
                case '\n': sb.Append(@"\n"); break;
                case '\t': sb.Append(@"\t"); break;
                // U+2028/U+2029 are literal line terminators in JS source.
                case '\u2028': sb.Append("\\u2028"); break;
                case '\u2029': sb.Append("\\u2029"); break;
                default:
                    if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                    else sb.Append(c);
                    break;
            }
        }
        return sb.Append('"').ToString();
    }

    // ---- PowerShell ----------------------------------------------------------
    private static string GeneratePowerShell(HttpSession s)
    {
        var sb = new StringBuilder();
        sb.AppendLine("$headers = @{");
        foreach (var h in PortableHeaders(s, keepContentType: false))
            sb.AppendLine($"    {PsString(h.Name)} = {PsString(h.Value)}");
        sb.AppendLine("}");
        sb.AppendLine();

        if (s.RequestBody.Length > 0)
        {
            sb.AppendLine($"$body = {PsString(s.RequestBodyText)}");
            sb.AppendLine();
        }

        sb.Append("$response = Invoke-WebRequest -Uri ").Append(PsString(s.FullUrl))
          .Append(" -Method ").Append(PsString(s.Method))
          .Append(" -Headers $headers");
        if (s.RequestBody.Length > 0)
        {
            var ct = s.RequestHeaders["Content-Type"];
            if (!string.IsNullOrEmpty(ct)) sb.Append(" -ContentType ").Append(PsString(ct));
            sb.Append(" -Body $body");
        }
        sb.AppendLine();
        sb.AppendLine("$response.StatusCode");
        sb.AppendLine("$response.Content");
        return sb.ToString();
    }

    /// <summary>PowerShell single-quoted literal: only the quote itself is special.</summary>
    private static string PsString(string value) => "'" + value.Replace("'", "''") + "'";

    // ---- .http (VS Code REST Client / JetBrains HTTP client) -----------------
    private static string GenerateHttpFile(HttpSession s)
    {
        var sb = new StringBuilder();
        sb.Append(s.Method).Append(' ').Append(s.FullUrl).Append(' ')
          .AppendLine(string.IsNullOrEmpty(s.HttpVersion) ? "HTTP/1.1" : s.HttpVersion);
        foreach (var h in s.RequestHeaders)
        {
            // Content-Length is recomputed by the client; everything else is
            // meaningful in a .http file, including Host.
            if (h.Name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)) continue;
            sb.Append(h.Name).Append(": ").AppendLine(h.Value);
        }
        if (s.RequestBody.Length > 0)
        {
            sb.AppendLine();
            sb.AppendLine(s.RequestBodyText);
        }
        return sb.ToString();
    }
}
