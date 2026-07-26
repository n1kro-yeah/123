using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace HttpSpy.Core.Util;

/// <summary>Pretty-printing and hex-dump helpers used by the body viewers.</summary>
public static class BodyFormatter
{
    public static string PrettyJson(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions { WriteIndented = true });
        }
        catch
        {
            return json;
        }
    }

    public static string PrettyXml(string xml)
    {
        try
        {
            var doc = XDocument.Parse(xml);
            var sb = new StringBuilder();
            using var writer = XmlWriter.Create(sb, new XmlWriterSettings
            {
                Indent = true,
                IndentChars = "  ",
                OmitXmlDeclaration = false,
            });
            doc.Save(writer);
            return sb.ToString();
        }
        catch
        {
            return xml;
        }
    }

    /// <summary>
    /// How many bytes a hex dump renders before stopping. A dump is roughly 4x
    /// the size of its input, so dumping a 32 MB body would build a ~130 MB
    /// string on the UI thread — far past the point where anyone would read it.
    /// </summary>
    public const int MaxHexDumpBytes = 512 * 1024;

    /// <summary>Classic two-column hex dump (offset, hex bytes, ascii gutter).</summary>
    public static string HexDump(byte[] data, int bytesPerLine = 16) =>
        HexDump(data, bytesPerLine, MaxHexDumpBytes);

    /// <summary>Hex dump of at most <paramref name="maxBytes"/> bytes.</summary>
    public static string HexDump(byte[] data, int bytesPerLine, int maxBytes)
    {
        if (data.Length == 0) return string.Empty;
        if (bytesPerLine <= 0) bytesPerLine = 16;

        int limit = maxBytes > 0 ? Math.Min(data.Length, maxBytes) : data.Length;
        var sb = new StringBuilder(limit * 4 + 128);
        for (int offset = 0; offset < limit; offset += bytesPerLine)
        {
            sb.Append(offset.ToString("X8")).Append("  ");
            var ascii = new StringBuilder(bytesPerLine);
            for (int i = 0; i < bytesPerLine; i++)
            {
                if (offset + i < limit)
                {
                    byte b = data[offset + i];
                    sb.Append(b.ToString("X2")).Append(' ');
                    ascii.Append(b is >= 0x20 and < 0x7F ? (char)b : '.');
                }
                else
                {
                    sb.Append("   ");
                }
                if (i == 7) sb.Append(' ');
            }
            sb.Append(' ').Append(ascii).Append('\n');
        }

        if (limit < data.Length)
            sb.Append('\n')
              .Append($"… {data.Length - limit:N0} more byte(s) not shown ")
              .Append($"({data.Length:N0} total; hex view is capped at {limit:N0} bytes).")
              .Append('\n');

        return sb.ToString();
    }
}
