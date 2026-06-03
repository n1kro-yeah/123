using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Xml;
using HttpSpy.Core.Models;

namespace HttpSpy.Core.Export;

/// <summary>A flat, serializer-friendly projection of a session's metadata.</summary>
public sealed class SessionRecord
{
    public int Index { get; set; }
    public string Method { get; set; } = "";
    public int Status { get; set; }
    public string Host { get; set; } = "";
    public string Url { get; set; } = "";
    public string ContentType { get; set; } = "";
    public long RequestSize { get; set; }
    public long ResponseSize { get; set; }
    public double DurationMs { get; set; }
    public string Process { get; set; } = "";
    public int ProcessId { get; set; }
    public string Scheme { get; set; } = "";
    public string StartTime { get; set; } = "";
    public string? Error { get; set; }

    public static SessionRecord From(HttpSession s) => new()
    {
        Index = s.Index,
        Method = s.Method,
        Status = s.StatusCode,
        Host = s.Host,
        Url = s.FullUrl,
        ContentType = s.ResponseContentTypeShort,
        RequestSize = s.RequestBodySize,
        ResponseSize = s.ResponseBodySize,
        DurationMs = s.DurationMs,
        Process = s.ProcessName,
        ProcessId = s.ProcessId,
        Scheme = s.Scheme,
        StartTime = s.StartTime.ToString("O"),
        Error = s.Error,
    };
}

/// <summary>Exports the session list as a JSON array of metadata records.</summary>
public static class JsonExporter
{
    public static void Export(IEnumerable<HttpSession> sessions, string path)
    {
        var records = sessions.Select(SessionRecord.From).ToList();
        File.WriteAllText(path,
            JsonSerializer.Serialize(records, new JsonSerializerOptions { WriteIndented = true }),
            Encoding.UTF8);
    }
}

/// <summary>Exports the session list as a simple XML document.</summary>
public static class XmlExporter
{
    public static void Export(IEnumerable<HttpSession> sessions, string path)
    {
        var settings = new XmlWriterSettings { Indent = true, Encoding = new UTF8Encoding(false) };
        using var w = XmlWriter.Create(path, settings);
        w.WriteStartDocument();
        w.WriteStartElement("Sessions");
        foreach (var s in sessions)
        {
            var r = SessionRecord.From(s);
            w.WriteStartElement("Session");
            w.WriteAttributeString("index", r.Index.ToString());
            w.WriteElementString("Method", r.Method);
            w.WriteElementString("Status", r.Status.ToString());
            w.WriteElementString("Host", r.Host);
            w.WriteElementString("Url", r.Url);
            w.WriteElementString("ContentType", r.ContentType);
            w.WriteElementString("RequestSize", r.RequestSize.ToString());
            w.WriteElementString("ResponseSize", r.ResponseSize.ToString());
            w.WriteElementString("DurationMs", r.DurationMs.ToString("F1", CultureInfo.InvariantCulture));
            w.WriteElementString("Process", r.Process);
            w.WriteElementString("ProcessId", r.ProcessId.ToString());
            w.WriteElementString("Scheme", r.Scheme);
            w.WriteElementString("StartTime", r.StartTime);
            if (!string.IsNullOrEmpty(r.Error)) w.WriteElementString("Error", r.Error);
            w.WriteEndElement();
        }
        w.WriteEndElement();
        w.WriteEndDocument();
    }
}

/// <summary>Exports a human-readable plain-text listing of the sessions.</summary>
public static class TxtExporter
{
    public static void Export(IEnumerable<HttpSession> sessions, string path)
    {
        var sb = new StringBuilder();
        foreach (var s in sessions)
        {
            sb.Append('#').Append(s.Index).Append("  ")
              .Append(s.Method).Append(' ')
              .Append(s.StatusCode).Append("  ")
              .Append(s.FullUrl).Append("  [")
              .Append(s.ResponseContentTypeShort).Append(", ")
              .Append(s.ResponseBodySize).Append(" B, ")
              .Append(s.DurationMs.ToString("F0", CultureInfo.InvariantCulture)).Append(" ms, ")
              .Append(s.ProcessName).Append(']');
            if (!string.IsNullOrEmpty(s.Error)) sb.Append("  ERROR: ").Append(s.Error);
            sb.AppendLine();
        }
        File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
    }
}

/// <summary>
/// Writes each session's full raw request+response to its own <c>.txt</c> file in
/// a target directory (HTTP Debugger "save to separate files").
/// </summary>
public static class SeparateFilesExporter
{
    public static int Export(IEnumerable<HttpSession> sessions, string directory)
    {
        Directory.CreateDirectory(directory);
        int count = 0;
        foreach (var s in sessions)
        {
            string safeHost = string.Concat((s.Host ?? "session").Split(Path.GetInvalidFileNameChars()));
            string name = $"{s.Index:D5}_{s.Method}_{safeHost}.txt";
            File.WriteAllText(Path.Combine(directory, name), RawExporter.ExportRaw(s), Encoding.UTF8);
            count++;
        }
        return count;
    }
}
