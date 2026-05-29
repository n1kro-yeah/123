using System.Globalization;
using System.Text;
using HttpSpy.Core.Models;

namespace HttpSpy.Core.Export;

/// <summary>Exports session list metadata (no bodies) to CSV/TSV for spreadsheets.</summary>
public static class CsvExporter
{
    public static void Export(IEnumerable<HttpSession> sessions, string path, string delimiter = ",")
    {
        var sb = new StringBuilder();
        var columns = new[]
        {
            "#", "Method", "Status", "Host", "URL", "ContentType", "BodySize",
            "DurationMs", "ProcessName", "ProcessId", "Scheme", "StartTime", "Error"
        };
        sb.AppendLine(string.Join(delimiter, columns));

        foreach (var s in sessions)
        {
            sb.AppendLine(string.Join(delimiter,
                Escape(s.Index.ToString(), delimiter),
                Escape(s.Method, delimiter),
                Escape(s.StatusDisplay, delimiter),
                Escape(s.Host, delimiter),
                Escape(s.FullUrl, delimiter),
                Escape(s.ResponseContentTypeShort, delimiter),
                Escape(s.ResponseBodySize.ToString(), delimiter),
                Escape(s.DurationMs.ToString("F1", CultureInfo.InvariantCulture), delimiter),
                Escape(s.ProcessName, delimiter),
                Escape(s.ProcessId.ToString(), delimiter),
                Escape(s.Scheme, delimiter),
                Escape(s.StartTime.ToString("O"), delimiter),
                Escape(s.Error ?? "", delimiter)));
        }

        File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
    }

    private static string Escape(string value, string delimiter)
    {
        bool needsQuote = value.Contains(delimiter) || value.Contains('"') || value.Contains('\n');
        return needsQuote ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
    }
}
