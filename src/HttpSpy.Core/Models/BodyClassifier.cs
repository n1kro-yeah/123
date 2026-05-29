namespace HttpSpy.Core.Models;

/// <summary>Heuristics to map a Content-Type (and body bytes) to a viewer category.</summary>
public static class BodyClassifier
{
    public static BodyContentType Classify(string contentType, byte[] body)
    {
        if (body is null || body.Length == 0)
            return string.IsNullOrEmpty(contentType) ? BodyContentType.None : BodyContentType.Text;

        var ct = (contentType ?? string.Empty).ToLowerInvariant();

        if (ct.Contains("application/json") || ct.Contains("+json")) return BodyContentType.Json;
        if (ct.Contains("application/xml") || ct.Contains("text/xml") || ct.Contains("+xml")) return BodyContentType.Xml;
        if (ct.Contains("text/html")) return BodyContentType.Html;
        if (ct.Contains("text/css")) return BodyContentType.Css;
        if (ct.Contains("javascript") || ct.Contains("ecmascript")) return BodyContentType.JavaScript;
        if (ct.Contains("image/")) return BodyContentType.Image;
        if (ct.Contains("font/") || ct.Contains("application/font") || ct.Contains("woff")) return BodyContentType.Font;
        if (ct.Contains("application/x-www-form-urlencoded")) return BodyContentType.Form;
        if (ct.Contains("multipart/")) return BodyContentType.Multipart;
        if (ct.StartsWith("text/")) return BodyContentType.Text;

        // Fall back to content sniffing.
        if (LooksLikeText(body))
        {
            var sample = System.Text.Encoding.UTF8.GetString(body, 0, Math.Min(body.Length, 256)).TrimStart();
            if (sample.StartsWith("{") || sample.StartsWith("[")) return BodyContentType.Json;
            if (sample.StartsWith("<")) return BodyContentType.Xml;
            return BodyContentType.Text;
        }

        return BodyContentType.Binary;
    }

    public static bool LooksLikeText(byte[] data)
    {
        int sample = Math.Min(data.Length, 512);
        int suspicious = 0;
        for (int i = 0; i < sample; i++)
        {
            byte b = data[i];
            if (b == 0) return false;
            if (b < 0x09 || (b > 0x0D && b < 0x20)) suspicious++;
        }
        return suspicious <= sample / 32;
    }
}
