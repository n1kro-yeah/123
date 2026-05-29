using System.Text;
using HttpSpy.Core.Models;

namespace HttpSpy.Core.Export;

/// <summary>Exports a single session as a raw HTTP text representation.</summary>
public static class RawExporter
{
    public static string ExportRaw(HttpSession session)
    {
        var sb = new StringBuilder();
        sb.AppendLine("=== REQUEST ===");
        sb.Append(session.Method).Append(' ').Append(session.FullUrl).Append(' ').AppendLine(session.HttpVersion);
        foreach (var h in session.RequestHeaders) sb.Append(h.Name).Append(": ").AppendLine(h.Value);
        sb.AppendLine();
        if (session.RequestBody.Length > 0) sb.AppendLine(session.RequestBodyText);

        sb.AppendLine();
        sb.AppendLine("=== RESPONSE ===");
        sb.Append(session.ResponseHttpVersion).Append(' ').Append(session.StatusCode).Append(' ').AppendLine(session.StatusText);
        foreach (var h in session.ResponseHeaders) sb.Append(h.Name).Append(": ").AppendLine(h.Value);
        sb.AppendLine();
        if (session.ResponseBody.Length > 0) sb.AppendLine(session.ResponseBodyText);

        return sb.ToString();
    }
}
