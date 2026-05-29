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

    /// <summary>Classic two-column hex dump (offset, hex bytes, ascii gutter).</summary>
    public static string HexDump(byte[] data, int bytesPerLine = 16)
    {
        if (data.Length == 0) return string.Empty;
        var sb = new StringBuilder();
        for (int offset = 0; offset < data.Length; offset += bytesPerLine)
        {
            sb.Append(offset.ToString("X8")).Append("  ");
            var ascii = new StringBuilder();
            for (int i = 0; i < bytesPerLine; i++)
            {
                if (offset + i < data.Length)
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
        return sb.ToString();
    }
}
