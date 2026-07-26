using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HttpSpy.Core.Models;

namespace HttpSpy.Core.Export;

/// <summary>Which reader recognised the input.</summary>
public enum ImportFormat
{
    Unknown,
    Har,
    Curl,
    HttpFile,
    HttpSpyCapture
}

/// <summary>
/// What an import produced: the sessions themselves, the format that was
/// recognised, and anything that could not be represented faithfully.
/// </summary>
/// <param name="Sessions">The reconstructed transactions, in file order.</param>
/// <param name="Format">Which reader handled the input.</param>
/// <param name="Warnings">Lossy or skipped details, worth showing but not fatal.</param>
public sealed record ImportResult(
    IReadOnlyList<HttpSession> Sessions,
    ImportFormat Format,
    IReadOnlyList<string> Warnings)
{
    public static ImportResult Empty(ImportFormat format = ImportFormat.Unknown) =>
        new(Array.Empty<HttpSession>(), format, Array.Empty<string>());
}

/// <summary>
/// Reads traffic captured elsewhere back into HttpSpy.
///
/// Export has always been one-way, which meant the analyzer, the structure tree
/// and the dashboard could only ever look at traffic this process captured
/// itself. A HAR from browser devtools, a cURL command pasted from a bug report
/// or a <c>.http</c> file from a repository are all traffic worth inspecting with
/// the same tools.
/// </summary>
public static class TrafficImporter
{
    /// <summary>File-picker filters covering everything <see cref="Import"/> understands.</summary>
    public static readonly (string Label, string Extension)[] FileFilters =
    {
        ("HTTP Archive", "har"),
        ("HttpSpy capture", "hsc"),
        ("Request file", "http"),
        ("Request file", "rest"),
        ("Text", "txt"),
    };

    /// <summary>Reads a file, choosing the reader by content rather than by extension.</summary>
    public static ImportResult ImportFile(string path)
    {
        var text = File.ReadAllText(path);
        var result = Import(text);

        // A .http file with a single request and no leading verb is
        // indistinguishable from prose; fall back on the extension there.
        if (result.Format == ImportFormat.Unknown &&
            Path.GetExtension(path).ToLowerInvariant() is ".http" or ".rest")
            return ImportHttpFile(text);

        return result;
    }

    /// <summary>Detects the format of <paramref name="text"/> and parses it.</summary>
    public static ImportResult Import(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return ImportResult.Empty();

        var trimmed = text.TrimStart();

        if (trimmed.StartsWith('{'))
        {
            // Both HAR and our own capture files are JSON; tell them apart by shape.
            if (LooksLikeHar(trimmed)) return ImportHar(text);
            return ImportResult.Empty(ImportFormat.HttpSpyCapture);
        }

        if (LooksLikeCurl(trimmed)) return ImportCurl(text);
        if (LooksLikeHttpFile(trimmed)) return ImportHttpFile(text);

        return ImportResult.Empty();
    }

    private static bool LooksLikeHar(string text)
    {
        try
        {
            var node = JsonNode.Parse(text);
            return node?["log"]?["entries"] is JsonArray;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool LooksLikeCurl(string text) =>
        text.StartsWith("curl ", StringComparison.OrdinalIgnoreCase) ||
        text.StartsWith("curl\t", StringComparison.OrdinalIgnoreCase) ||
        text.StartsWith("curl\n", StringComparison.OrdinalIgnoreCase);

    private static readonly string[] Verbs =
    {
        "GET ", "POST ", "PUT ", "PATCH ", "DELETE ", "HEAD ", "OPTIONS ", "TRACE ", "CONNECT ",
    };

    private static bool LooksLikeHttpFile(string text)
    {
        var first = FirstMeaningfulLine(text);
        return first is not null &&
               (first.StartsWith("###", StringComparison.Ordinal) ||
                Verbs.Any(v => first.StartsWith(v, StringComparison.OrdinalIgnoreCase)));
    }

    private static string? FirstMeaningfulLine(string text)
    {
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#') && !line.StartsWith("###")) continue;
            return line;
        }
        return null;
    }

    // ================= HAR =====================================================

    /// <summary>
    /// Reads a HAR 1.2 log. Everything the format carries and HttpSpy models is
    /// preserved: headers, bodies (including base64 content), timings, the origin
    /// server address and the non-standard process hints we ourselves emit.
    /// </summary>
    public static ImportResult ImportHar(string json)
    {
        var warnings = new List<string>();
        var sessions = new List<HttpSession>();

        JsonNode? root;
        try { root = JsonNode.Parse(json); }
        catch (JsonException ex) { return new ImportResult(sessions, ImportFormat.Har, new[] { $"Not valid JSON: {ex.Message}" }); }

        if (root?["log"]?["entries"] is not JsonArray entries)
            return new ImportResult(sessions, ImportFormat.Har, new[] { "No log.entries array — this is not a HAR file." });

        string? creator = root["log"]?["creator"]?["name"]?.GetValue<string>();
        long connectionCounter = 0;
        var connectionsByServer = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < entries.Count; i++)
        {
            try
            {
                var session = HarEntry(entries[i], creator, connectionsByServer, ref connectionCounter, warnings);
                if (session is not null) sessions.Add(session);
            }
            catch (Exception ex)
            {
                warnings.Add($"Entry {i + 1} skipped: {ex.Message}");
            }
        }

        if (sessions.Count == 0 && warnings.Count == 0)
            warnings.Add("The HAR contained no entries.");

        return new ImportResult(sessions, ImportFormat.Har, warnings);
    }

    private static HttpSession? HarEntry(JsonNode? entry, string? creator,
        Dictionary<string, long> connections, ref long counter, List<string> warnings)
    {
        if (entry?["request"] is not JsonObject request) return null;

        string url = Str(request["url"]) ?? "";
        if (url.Length == 0) return null;

        var session = new HttpSession
        {
            Imported = true,
            Method = (Str(request["method"]) ?? "GET").ToUpperInvariant(),
            HttpVersion = Normalize(Str(request["httpVersion"])),
            State = SessionState.Completed,
        };

        ApplyUrl(session, url);

        foreach (var (name, value) in ReadHeaders(request["headers"]))
            session.RequestHeaders.Add(name, value);

        var postData = request["postData"];
        if (postData is not null)
        {
            var (bytes, note) = HarBody(postData, Str(postData["comment"]));
            session.RequestBody = bytes;
            if (note is not null) warnings.Add($"{session.Method} {session.Path}: {note}");
        }

        if (entry["response"] is JsonObject response)
        {
            session.StatusCode = (int)(Num(response["status"]) ?? 0);
            session.StatusText = Str(response["statusText"]) ?? "";
            session.ResponseHttpVersion = Normalize(Str(response["httpVersion"]));
            foreach (var (name, value) in ReadHeaders(response["headers"]))
                session.ResponseHeaders.Add(name, value);

            if (response["content"] is JsonObject content)
            {
                var (bytes, note) = HarBody(content, Str(content["encoding"]));
                session.ResponseBody = bytes;
                if (note is not null) warnings.Add($"{session.Method} {session.Path}: {note}");

                // A HAR records the decoded size; when the text was omitted (many
                // tools drop large bodies) say so rather than reporting an empty body.
                long declared = (long)(Num(content["size"]) ?? 0);
                if (declared > 0 && bytes.Length == 0)
                {
                    session.ResponseBodyTruncated = true;
                    warnings.Add($"{session.Method} {session.Path}: body of {declared} bytes was not stored in the HAR.");
                }
            }

            if (session.StatusCode == 0) session.State = SessionState.Aborted;
        }
        else
        {
            session.State = SessionState.Aborted;
        }

        session.StartTime = ParseDate(Str(entry["startedDateTime"]));
        double total = Num(entry["time"]) ?? -1;
        session.Timings.TotalMs = total;
        ReadTimings(entry["timings"], session.Timings);
        session.EndTime = total > 0 ? session.StartTime.AddMilliseconds(total) : session.StartTime;

        session.RemoteAddress = Str(entry["serverIPAddress"]) ?? "";
        session.ProcessName = Str(entry["_processName"]) ?? creator ?? "import";
        session.ProcessId = (int)(Num(entry["_processId"]) ?? 0);
        session.BytesSent = session.RequestBody.LongLength;
        session.BytesReceived = session.ResponseBody.LongLength;

        // HAR has no connection identity, but grouping by origin server still
        // makes the connection view meaningful for an imported capture.
        string key = session.RemoteAddress.Length > 0 ? session.RemoteAddress : session.Host;
        if (!connections.TryGetValue(key, out long id))
        {
            id = ++counter;
            connections[key] = id;
        }
        session.ConnectionId = id;

        return session;
    }

    private static IEnumerable<(string Name, string Value)> ReadHeaders(JsonNode? node)
    {
        if (node is not JsonArray array) yield break;
        foreach (var item in array)
        {
            string? name = Str(item?["name"]);
            if (string.IsNullOrEmpty(name)) continue;
            // Chrome writes pseudo-headers (":authority") into HAR; they are part
            // of the h2 framing, not headers the user set, so drop them.
            if (name.StartsWith(':')) continue;
            yield return (name, Str(item?["value"]) ?? "");
        }
    }

    /// <summary>
    /// Decodes a HAR body. The spec only defines <c>encoding: "base64"</c> on
    /// response content; our own exporter also leaves a <c>comment: "base64"</c>
    /// on request bodies, since HAR has nowhere else to put it.
    /// </summary>
    private static (byte[] Bytes, string? Warning) HarBody(JsonNode? node, string? encoding)
    {
        string? text = Str(node?["text"]);
        if (string.IsNullOrEmpty(text)) return (Array.Empty<byte>(), null);

        if (string.Equals(encoding, "base64", StringComparison.OrdinalIgnoreCase))
        {
            try { return (Convert.FromBase64String(text), null); }
            catch (FormatException) { return (Encoding.UTF8.GetBytes(text), "body was marked base64 but is not valid base64."); }
        }
        return (Encoding.UTF8.GetBytes(text), null);
    }

    private static void ReadTimings(JsonNode? node, SessionTimings timings)
    {
        if (node is null) return;
        timings.BlockedMs = Num(node["blocked"]) ?? -1;
        timings.DnsMs = Num(node["dns"]) ?? -1;
        timings.ConnectMs = Num(node["connect"]) ?? -1;
        timings.TlsMs = Num(node["ssl"]) ?? -1;
        timings.SendMs = Num(node["send"]) ?? -1;
        timings.WaitMs = Num(node["wait"]) ?? -1;
        timings.ReceiveMs = Num(node["receive"]) ?? -1;
    }

    // ================= cURL ====================================================

    /// <summary>
    /// Reconstructs the request described by a cURL command line — the form
    /// browsers produce with "Copy as cURL" and the form bug reports are written
    /// in. Only the request exists, so the session is marked as having no response.
    /// </summary>
    public static ImportResult ImportCurl(string command)
    {
        var warnings = new List<string>();
        var tokens = Tokenize(command);
        if (tokens.Count == 0) return ImportResult.Empty(ImportFormat.Curl);

        if (string.Equals(tokens[0], "curl", StringComparison.OrdinalIgnoreCase)) tokens.RemoveAt(0);

        var session = new HttpSession { Imported = true, ProcessName = "curl", State = SessionState.Pending };
        string? url = null;
        string? method = null;
        var body = new StringBuilder();
        var formParts = new List<string>();
        bool head = false;

        for (int i = 0; i < tokens.Count; i++)
        {
            string token = tokens[i];
            string? Next() => i + 1 < tokens.Count ? tokens[++i] : null;

            switch (token)
            {
                case "-X" or "--request":
                    method = Next()?.ToUpperInvariant();
                    break;

                case "-H" or "--header":
                    AddCurlHeader(session, Next(), warnings);
                    break;

                case "-d" or "--data" or "--data-raw" or "--data-ascii" or "--data-binary" or "--data-urlencode":
                {
                    var value = Next();
                    if (value is null) break;
                    if (value.StartsWith('@'))
                    {
                        warnings.Add($"Body was read from the file {value[1..]}, which is not available here.");
                        break;
                    }
                    if (body.Length > 0) body.Append('&');
                    body.Append(value);
                    break;
                }

                case "-F" or "--form":
                {
                    var value = Next();
                    if (value is not null) formParts.Add(value);
                    break;
                }

                case "-b" or "--cookie":
                {
                    var value = Next();
                    if (!string.IsNullOrEmpty(value)) session.RequestHeaders.Set("Cookie", value);
                    break;
                }

                case "-A" or "--user-agent":
                {
                    var value = Next();
                    if (!string.IsNullOrEmpty(value)) session.RequestHeaders.Set("User-Agent", value);
                    break;
                }

                case "-e" or "--referer":
                {
                    var value = Next();
                    if (!string.IsNullOrEmpty(value)) session.RequestHeaders.Set("Referer", value);
                    break;
                }

                case "-u" or "--user":
                {
                    var value = Next();
                    if (!string.IsNullOrEmpty(value))
                        session.RequestHeaders.Set("Authorization",
                            "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(value)));
                    break;
                }

                case "--url":
                    url ??= Next();
                    break;

                case "-I" or "--head":
                    head = true;
                    break;

                case "--compressed":
                    if (session.RequestHeaders["Accept-Encoding"] is null)
                        session.RequestHeaders.Set("Accept-Encoding", "gzip, deflate, br");
                    break;

                // Switches that do not change the request we would replay.
                case "-s" or "--silent" or "-S" or "--show-error" or "-L" or "--location" or "-k" or
                     "--insecure" or "-v" or "--verbose" or "-i" or "--include" or "-f" or "--fail" or
                     "-g" or "--globoff" or "--no-progress-meter" or "--http1.1" or "--http2":
                    break;

                // Switches that consume an argument we do not model.
                case "-o" or "--output" or "-w" or "--write-out" or "--connect-timeout" or "-m" or
                     "--max-time" or "--retry" or "-x" or "--proxy" or "--resolve" or "--cacert" or
                     "-c" or "--cookie-jar":
                    Next();
                    break;

                default:
                    if (token.StartsWith('-'))
                    {
                        warnings.Add($"Ignored unsupported option {token}.");
                        break;
                    }
                    url ??= token;
                    break;
            }
        }

        if (url is null)
            return new ImportResult(Array.Empty<HttpSession>(), ImportFormat.Curl,
                warnings.Append("No URL found in the command.").ToList());

        if (formParts.Count > 0)
        {
            var (contentType, bytes) = BuildMultipart(formParts);
            session.RequestHeaders.Set("Content-Type", contentType);
            session.RequestBody = bytes;
        }
        else if (body.Length > 0)
        {
            session.RequestBody = Encoding.UTF8.GetBytes(body.ToString());
            if (session.RequestHeaders["Content-Type"] is null)
                session.RequestHeaders.Set("Content-Type", "application/x-www-form-urlencoded");
        }

        session.Method = method ?? (head ? "HEAD" : session.RequestBody.Length > 0 ? "POST" : "GET");
        ApplyUrl(session, url);
        session.BytesSent = session.RequestBody.LongLength;

        if (session.RequestHeaders["Host"] is null && session.Host.Length > 0)
            session.RequestHeaders.Set("Host", session.Host);

        return new ImportResult(new[] { session }, ImportFormat.Curl, warnings);
    }

    private static void AddCurlHeader(HttpSession session, string? raw, List<string> warnings)
    {
        if (string.IsNullOrWhiteSpace(raw)) return;
        int colon = raw.IndexOf(':');
        if (colon <= 0)
        {
            warnings.Add($"Ignored malformed header \"{raw}\".");
            return;
        }
        session.RequestHeaders.Add(raw[..colon].Trim(), raw[(colon + 1)..].Trim());
    }

    private static (string ContentType, byte[] Body) BuildMultipart(List<string> parts)
    {
        // A fixed boundary keeps imports reproducible; the parts are text-only
        // because the referenced files are not available to us anyway.
        const string boundary = "----HttpSpyImportBoundary";
        var sb = new StringBuilder();
        foreach (var part in parts)
        {
            int eq = part.IndexOf('=');
            string name = eq > 0 ? part[..eq] : part;
            string value = eq > 0 ? part[(eq + 1)..] : "";
            sb.Append("--").Append(boundary).Append("\r\n");
            sb.Append("Content-Disposition: form-data; name=\"").Append(name).Append("\"\r\n\r\n");
            sb.Append(value.StartsWith('@') ? $"<contents of {value[1..]}>" : value).Append("\r\n");
        }
        sb.Append("--").Append(boundary).Append("--\r\n");
        return ($"multipart/form-data; boundary={boundary}", Encoding.UTF8.GetBytes(sb.ToString()));
    }

    /// <summary>
    /// Splits a command line the way a POSIX shell would: single quotes are
    /// literal, double quotes allow escapes, a backslash before a newline
    /// continues the line, and <c>$'…'</c> decodes the usual escapes. Browsers
    /// emit all four forms.
    /// </summary>
    internal static List<string> Tokenize(string command)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        bool has = false;

        for (int i = 0; i < command.Length; i++)
        {
            char c = command[i];

            if (c == '\\' && i + 1 < command.Length && (command[i + 1] == '\n' || command[i + 1] == '\r'))
            {
                i++;
                if (i + 1 < command.Length && command[i] == '\r' && command[i + 1] == '\n') i++;
                continue;
            }

            if (char.IsWhiteSpace(c))
            {
                if (has) { tokens.Add(current.ToString()); current.Clear(); has = false; }
                continue;
            }

            if (c == '\'')
            {
                has = true;
                int end = command.IndexOf('\'', i + 1);
                if (end < 0) end = command.Length;
                current.Append(command, i + 1, end - i - 1);
                i = end;
                continue;
            }

            if (c == '$' && i + 1 < command.Length && command[i + 1] == '\'')
            {
                has = true;
                i = AppendDollarQuoted(command, i + 2, current);
                continue;
            }

            if (c == '"')
            {
                has = true;
                i = AppendDoubleQuoted(command, i + 1, current);
                continue;
            }

            if (c == '^' && i + 1 < command.Length && command[i + 1] == '\n')
            {
                // "Copy as cURL (cmd)" uses ^ as the continuation character.
                i++;
                continue;
            }

            has = true;
            current.Append(c);
        }

        if (has) tokens.Add(current.ToString());
        return tokens;
    }

    private static int AppendDoubleQuoted(string s, int start, StringBuilder sb)
    {
        for (int i = start; i < s.Length; i++)
        {
            if (s[i] == '\\' && i + 1 < s.Length)
            {
                char next = s[++i];
                sb.Append(next switch { 'n' => '\n', 'r' => '\r', 't' => '\t', _ => next });
                continue;
            }
            if (s[i] == '"') return i;
            sb.Append(s[i]);
        }
        return s.Length;
    }

    private static int AppendDollarQuoted(string s, int start, StringBuilder sb)
    {
        for (int i = start; i < s.Length; i++)
        {
            if (s[i] == '\\' && i + 1 < s.Length)
            {
                char next = s[++i];
                switch (next)
                {
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'u' when i + 4 < s.Length &&
                                  ushort.TryParse(s.AsSpan(i + 1, 4), NumberStyles.HexNumber,
                                      CultureInfo.InvariantCulture, out ushort code):
                        sb.Append((char)code);
                        i += 4;
                        break;
                    default: sb.Append(next); break;
                }
                continue;
            }
            if (s[i] == '\'') return i;
            sb.Append(s[i]);
        }
        return s.Length;
    }

    // ================= .http files =============================================

    /// <summary>
    /// Reads the request-file format used by VS Code REST Client, JetBrains HTTP
    /// Client and <c>curl --libcurl</c>-adjacent tooling: requests separated by
    /// <c>###</c>, each a request line, headers, a blank line and a body.
    /// </summary>
    public static ImportResult ImportHttpFile(string text)
    {
        var warnings = new List<string>();
        var sessions = new List<HttpSession>();

        foreach (var block in SplitRequests(text))
        {
            var session = ParseRequestBlock(block, warnings);
            if (session is not null) sessions.Add(session);
        }

        if (sessions.Count == 0)
            warnings.Add("No requests found. Each request needs a \"METHOD url\" line.");

        return new ImportResult(sessions, ImportFormat.HttpFile, warnings);
    }

    private static IEnumerable<string> SplitRequests(string text)
    {
        var current = new StringBuilder();
        foreach (var line in text.ReplaceLineEndings("\n").Split('\n'))
        {
            if (line.StartsWith("###", StringComparison.Ordinal))
            {
                if (current.Length > 0) yield return current.ToString();
                current.Clear();
                continue;
            }
            current.Append(line).Append('\n');
        }
        if (current.Length > 0) yield return current.ToString();
    }

    private static HttpSession? ParseRequestBlock(string block, List<string> warnings)
    {
        var lines = block.Split('\n');
        int i = 0;

        // Skip blank lines and comments before the request line.
        while (i < lines.Length &&
               (lines[i].Trim().Length == 0 ||
                lines[i].StartsWith("#", StringComparison.Ordinal) ||
                lines[i].StartsWith("//", StringComparison.Ordinal)))
            i++;

        if (i >= lines.Length) return null;

        var parts = lines[i].Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || !IsRequestLine(parts))
        {
            warnings.Add($"Skipped \"{lines[i].Trim()}\" — not a request line.");
            return null;
        }

        var session = new HttpSession
        {
            Imported = true,
            Method = parts[0].ToUpperInvariant(),
            HttpVersion = parts.Length > 2 ? Normalize(parts[2]) : "HTTP/1.1",
            ProcessName = "import",
            State = SessionState.Pending,
        };
        i++;

        for (; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.Trim().Length == 0) { i++; break; }
            if (line.StartsWith("#", StringComparison.Ordinal) || line.StartsWith("//", StringComparison.Ordinal))
                continue;

            int colon = line.IndexOf(':');
            if (colon <= 0)
            {
                warnings.Add($"Ignored malformed header line \"{line.Trim()}\".");
                continue;
            }
            session.RequestHeaders.Add(line[..colon].Trim(), line[(colon + 1)..].Trim());
        }

        if (i < lines.Length)
        {
            var body = string.Join("\n", lines.Skip(i)).Trim('\n');
            if (body.Length > 0) session.RequestBody = Encoding.UTF8.GetBytes(body);
        }

        // A relative target needs the Host header to become an absolute URL.
        string target = parts[1];
        if (!target.Contains("://", StringComparison.Ordinal))
        {
            var host = session.RequestHeaders["Host"];
            target = host is null ? "http://localhost" + EnsureLeadingSlash(target)
                                  : $"http://{host}{EnsureLeadingSlash(target)}";
        }

        ApplyUrl(session, target);
        session.BytesSent = session.RequestBody.LongLength;
        return session;
    }

    /// <summary>
    /// Decides whether two tokens are really "METHOD target". Without this any
    /// three-word sentence in a text file parses as a request — the method is
    /// only ever letters, and the target is either absolute or rooted.
    /// </summary>
    private static bool IsRequestLine(string[] parts)
    {
        var method = parts[0];
        if (method.Length is 0 or > 20 || !method.All(char.IsAsciiLetter)) return false;

        var target = parts[1];
        return target.StartsWith('/') || target.Contains("://", StringComparison.Ordinal);
    }

    private static string EnsureLeadingSlash(string path) =>
        path.StartsWith('/') ? path : "/" + path;

    // ================= Shared helpers =========================================

    /// <summary>Fills scheme/host/path/query and the TLS + kind flags from a URL.</summary>
    internal static void ApplyUrl(HttpSession session, string url)
    {
        session.Url = url;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            // Keep whatever we were given rather than dropping the entry; the
            // grid can still show it and the user can see what failed.
            session.Host = url;
            session.Path = "/";
            return;
        }

        session.Scheme = uri.Scheme;
        session.Host = uri.IdnHost.Length > 0 ? uri.Host : uri.Host;
        session.Path = uri.AbsolutePath.Length > 0 ? uri.AbsolutePath : "/";
        session.QueryString = uri.Query.TrimStart('?');
        session.RemotePort = uri.Port;
        session.IsTls = string.Equals(uri.Scheme, "https", StringComparison.OrdinalIgnoreCase);
        session.Kind = session.IsTls ? SessionKind.Https : SessionKind.Http;
    }

    private static string Normalize(string? version)
    {
        if (string.IsNullOrWhiteSpace(version)) return "HTTP/1.1";
        var v = version.Trim();
        // HAR writers use "h2", "http/2.0" and "HTTP/2" interchangeably.
        if (v.Equals("h2", StringComparison.OrdinalIgnoreCase) ||
            v.Equals("http/2.0", StringComparison.OrdinalIgnoreCase)) return "HTTP/2";
        if (v.Equals("h3", StringComparison.OrdinalIgnoreCase) ||
            v.Equals("http/3.0", StringComparison.OrdinalIgnoreCase)) return "HTTP/3";
        return v.StartsWith("HTTP", StringComparison.OrdinalIgnoreCase) ? v.ToUpperInvariant() : v;
    }

    private static DateTime ParseDate(string? value) =>
        DateTime.TryParse(value, CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed.ToLocalTime()
            : DateTime.Now;

    private static string? Str(JsonNode? node)
    {
        try { return node?.GetValue<string>(); }
        catch (Exception) { return node?.ToString(); }
    }

    private static double? Num(JsonNode? node)
    {
        if (node is null) return null;
        try { return node.GetValue<double>(); }
        catch (Exception)
        {
            return double.TryParse(node.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out double d)
                ? d
                : null;
        }
    }
}
