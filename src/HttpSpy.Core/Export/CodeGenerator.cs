using System.Text;
using HttpSpy.Core.Models;

namespace HttpSpy.Core.Export;

/// <summary>Generates executable code snippets from a captured session.</summary>
public static class CodeGenerator
{
    public enum Language { Curl, CSharp, Python, JavaScript }

    public static string Generate(HttpSession session, Language language) => language switch
    {
        Language.Curl => GenerateCurl(session),
        Language.CSharp => GenerateCSharp(session),
        Language.Python => GeneratePython(session),
        Language.JavaScript => GenerateJavaScript(session),
        _ => "// Unsupported language",
    };

    // ---- curl ----------------------------------------------------------------
    private static string GenerateCurl(HttpSession s)
    {
        var sb = new StringBuilder();
        sb.Append("curl");
        if (s.Method != "GET") sb.Append($" -X {s.Method}");
        sb.Append($" '{s.FullUrl}'");
        foreach (var h in s.RequestHeaders)
        {
            if (h.Name.Equals("Host", StringComparison.OrdinalIgnoreCase)) continue;
            sb.Append($" \\\n  -H '{h.Name}: {h.Value}'");
        }
        if (s.RequestBody.Length > 0)
        {
            var text = s.RequestBodyText;
            sb.Append($" \\\n  -d '{EscapeShell(text)}'");
        }
        return sb.ToString();
    }

    private static string EscapeShell(string s) => s.Replace("'", "'\\''");

    // ---- C# ------------------------------------------------------------------
    private static string GenerateCSharp(HttpSession s)
    {
        var sb = new StringBuilder();
        sb.AppendLine("using System.Net.Http;");
        sb.AppendLine();
        sb.AppendLine("using var client = new HttpClient();");
        sb.AppendLine($"var request = new HttpRequestMessage(HttpMethod.{Capitalize(s.Method)}, \"{s.FullUrl}\");");
        foreach (var h in s.RequestHeaders)
        {
            if (h.Name.Equals("Host", StringComparison.OrdinalIgnoreCase)) continue;
            if (h.Name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)) continue;
            if (h.Name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)) continue;
            sb.AppendLine($"request.Headers.TryAddWithoutValidation(\"{h.Name}\", \"{Escape(h.Value)}\");");
        }
        if (s.RequestBody.Length > 0)
        {
            var ct = s.RequestHeaders["Content-Type"] ?? "application/octet-stream";
            sb.AppendLine($"request.Content = new StringContent(@\"{Escape(s.RequestBodyText)}\", System.Text.Encoding.UTF8, \"{ct}\");");
        }
        sb.AppendLine("var response = await client.SendAsync(request);");
        sb.AppendLine("var body = await response.Content.ReadAsStringAsync();");
        sb.AppendLine("Console.WriteLine($\"{(int)response.StatusCode} {response.ReasonPhrase}\");");
        sb.AppendLine("Console.WriteLine(body);");
        return sb.ToString();
    }

    // ---- Python --------------------------------------------------------------
    private static string GeneratePython(HttpSession s)
    {
        var sb = new StringBuilder();
        sb.AppendLine("import requests");
        sb.AppendLine();
        sb.AppendLine("headers = {");
        foreach (var h in s.RequestHeaders)
        {
            if (h.Name.Equals("Host", StringComparison.OrdinalIgnoreCase)) continue;
            if (h.Name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)) continue;
            sb.AppendLine($"    \"{h.Name}\": \"{EscapePy(h.Value)}\",");
        }
        sb.AppendLine("}");
        sb.AppendLine();

        string method = s.Method.ToLowerInvariant();
        if (s.RequestBody.Length > 0)
        {
            sb.AppendLine($"data = \"\"\"{s.RequestBodyText}\"\"\"");
            sb.AppendLine();
            sb.AppendLine($"response = requests.{method}(\"{s.FullUrl}\", headers=headers, data=data)");
        }
        else
        {
            sb.AppendLine($"response = requests.{method}(\"{s.FullUrl}\", headers=headers)");
        }
        sb.AppendLine("print(response.status_code, response.text[:500])");
        return sb.ToString();
    }

    // ---- JavaScript ----------------------------------------------------------
    private static string GenerateJavaScript(HttpSession s)
    {
        var sb = new StringBuilder();
        sb.AppendLine("const headers = {");
        foreach (var h in s.RequestHeaders)
        {
            if (h.Name.Equals("Host", StringComparison.OrdinalIgnoreCase)) continue;
            if (h.Name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)) continue;
            sb.AppendLine($"  '{h.Name}': '{EscapeJs(h.Value)}',");
        }
        sb.AppendLine("};");
        sb.AppendLine();

        sb.AppendLine($"const response = await fetch('{s.FullUrl}', {{");
        sb.AppendLine($"  method: '{s.Method}',");
        sb.AppendLine("  headers,");
        if (s.RequestBody.Length > 0)
            sb.AppendLine($"  body: `{s.RequestBodyText}`,");
        sb.AppendLine("});");
        sb.AppendLine("const body = await response.text();");
        sb.AppendLine("console.log(response.status, body.slice(0, 500));");
        return sb.ToString();
    }

    private static string Capitalize(string s) =>
        s.Length == 0 ? s : char.ToUpper(s[0]) + s[1..].ToLower();

    private static string Escape(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");
    private static string EscapePy(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");
    private static string EscapeJs(string s) => s.Replace("\\", "\\\\").Replace("'", "\\'");
}
