using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HttpSpy.Core.Models;

namespace HttpSpy.Core.Export;

/// <summary>Exports captured sessions to the HTTP Archive (HAR) 1.2 format.</summary>
public static class HarExporter
{
    public static string Export(IEnumerable<HttpSession> sessions)
    {
        var entries = new JsonArray();
        foreach (var s in sessions) entries.Add(EntryNode(s));

        var har = new JsonObject
        {
            ["log"] = new JsonObject
            {
                ["version"] = "1.2",
                ["creator"] = new JsonObject { ["name"] = "HttpSpy", ["version"] = "1.0" },
                // Several HAR viewers (Chrome DevTools among them) expect the
                // optional "pages" array to be present even when it is empty.
                ["pages"] = new JsonArray(),
                ["entries"] = entries
            }
        };
        return har.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    public static void ExportToFile(IEnumerable<HttpSession> sessions, string path) =>
        File.WriteAllText(path, Export(sessions), Encoding.UTF8);

    private static JsonObject EntryNode(HttpSession s)
    {
        var entry = new JsonObject
        {
            ["startedDateTime"] = s.StartTime.ToUniversalTime().ToString("O"),
            ["time"] = s.DurationMs,
            ["request"] = RequestNode(s),
            ["response"] = ResponseNode(s),
            ["cache"] = new JsonObject(),
            ["timings"] = TimingsNode(s.Timings),
            ["serverIPAddress"] = s.RemoteAddress,
            ["_processId"] = s.ProcessId,
            ["_processName"] = s.ProcessName,
        };
        return entry;
    }

    private static JsonObject RequestNode(HttpSession s)
    {
        var node = new JsonObject
        {
            ["method"] = s.Method,
            ["url"] = s.FullUrl,
            ["httpVersion"] = s.HttpVersion,
            ["cookies"] = CookiesArray(s.RequestCookies()),
            ["headers"] = HeadersArray(s.RequestHeaders),
            ["queryString"] = QueryArray(s.QueryParameters()),
            ["headersSize"] = -1,
            ["bodySize"] = s.RequestBodySize,
        };

        // HAR 1.2 has no encoding field on postData, so a binary request body can
        // only be represented as (lossy) text. Emit the member only when there is
        // something to say — a null postData trips up strict readers.
        if (s.RequestBody.Length > 0)
        {
            node["postData"] = new JsonObject
            {
                ["mimeType"] = s.RequestHeaders["Content-Type"] ?? "application/octet-stream",
                ["text"] = IsBinary(s.RequestBodyKind)
                    ? Convert.ToBase64String(s.RequestBody)
                    : s.RequestBodyText,
                // Non-standard hint so round-tripping through HttpSpy stays lossless.
                ["comment"] = IsBinary(s.RequestBodyKind) ? "base64" : null,
            };
        }
        return node;
    }

    private static JsonObject ResponseNode(HttpSession s)
    {
        bool binary = IsBinary(s.ResponseBodyKind);
        var content = new JsonObject
        {
            ["size"] = s.ResponseBodySize,
            ["mimeType"] = s.ResponseContentTypeShort,
            // HAR 1.2 §content: "encoding" declares how "text" is armoured. Without
            // it, image/font/binary payloads were being mangled by UTF-8 decoding.
            ["text"] = binary ? Convert.ToBase64String(s.ResponseBody) : s.ResponseBodyText,
        };
        if (binary) content["encoding"] = "base64";
        if (s.EncodedBodySize > 0 && s.EncodedBodySize < s.ResponseBodySize)
            content["compression"] = s.ResponseBodySize - s.EncodedBodySize;

        return new JsonObject
        {
            ["status"] = s.StatusCode,
            ["statusText"] = s.StatusText,
            ["httpVersion"] = s.ResponseHttpVersion,
            ["cookies"] = SetCookieArray(s.ResponseSetCookies()),
            ["headers"] = HeadersArray(s.ResponseHeaders),
            ["content"] = content,
            ["redirectURL"] = s.ResponseHeaders["Location"] ?? "",
            ["headersSize"] = -1,
            ["bodySize"] = s.EncodedBodySize > 0 ? s.EncodedBodySize : s.ResponseBodySize,
        };
    }

    private static bool IsBinary(BodyContentType kind) =>
        kind is BodyContentType.Image or BodyContentType.Font or BodyContentType.Binary;

    private static JsonObject TimingsNode(SessionTimings t) => new()
    {
        ["blocked"] = t.BlockedMs,
        ["dns"] = t.DnsMs,
        ["connect"] = t.ConnectMs,
        ["ssl"] = t.TlsMs,
        ["send"] = t.SendMs,
        ["wait"] = t.WaitMs,
        ["receive"] = t.ReceiveMs,
    };

    private static JsonArray HeadersArray(HeaderCollection headers)
    {
        var arr = new JsonArray();
        foreach (var h in headers)
            arr.Add(new JsonObject { ["name"] = h.Name, ["value"] = h.Value });
        return arr;
    }

    private static JsonArray CookiesArray(IEnumerable<KeyValuePair<string, string>> cookies)
    {
        var arr = new JsonArray();
        foreach (var c in cookies)
            arr.Add(new JsonObject { ["name"] = c.Key, ["value"] = c.Value });
        return arr;
    }

    private static JsonArray SetCookieArray(IEnumerable<string> setCookies)
    {
        var arr = new JsonArray();
        foreach (var sc in setCookies)
        {
            var parts = sc.Split('=', 2);
            arr.Add(new JsonObject { ["name"] = parts[0].Trim(), ["value"] = parts.Length > 1 ? parts[1] : "" });
        }
        return arr;
    }

    private static JsonArray QueryArray(IEnumerable<KeyValuePair<string, string>> queries)
    {
        var arr = new JsonArray();
        foreach (var q in queries)
            arr.Add(new JsonObject { ["name"] = q.Key, ["value"] = q.Value });
        return arr;
    }
}
