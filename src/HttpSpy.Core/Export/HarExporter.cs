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
        return new JsonObject
        {
            ["method"] = s.Method,
            ["url"] = s.FullUrl,
            ["httpVersion"] = s.HttpVersion,
            ["cookies"] = CookiesArray(s.RequestCookies()),
            ["headers"] = HeadersArray(s.RequestHeaders),
            ["queryString"] = QueryArray(s.QueryParameters()),
            ["headersSize"] = -1,
            ["bodySize"] = s.RequestBodySize,
            ["postData"] = s.RequestBody.Length > 0 ? new JsonObject
            {
                ["mimeType"] = s.RequestHeaders["Content-Type"] ?? "application/octet-stream",
                ["text"] = s.RequestBodyText
            } : null,
        };
    }

    private static JsonObject ResponseNode(HttpSession s)
    {
        return new JsonObject
        {
            ["status"] = s.StatusCode,
            ["statusText"] = s.StatusText,
            ["httpVersion"] = s.ResponseHttpVersion,
            ["cookies"] = SetCookieArray(s.ResponseSetCookies()),
            ["headers"] = HeadersArray(s.ResponseHeaders),
            ["content"] = new JsonObject
            {
                ["size"] = s.ResponseBodySize,
                ["mimeType"] = s.ResponseContentTypeShort,
                ["text"] = s.ResponseBodyText,
            },
            ["redirectURL"] = s.ResponseHeaders["Location"] ?? "",
            ["headersSize"] = -1,
            ["bodySize"] = s.ResponseBodySize,
        };
    }

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
