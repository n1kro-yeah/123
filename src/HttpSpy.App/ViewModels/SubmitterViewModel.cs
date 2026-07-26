using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HttpSpy.Core;
using HttpSpy.Core.Models;
using HttpSpy.Core.Util;

namespace HttpSpy.App.ViewModels;

/// <summary>The "Submitter" / request builder: compose, edit, and send HTTP requests.</summary>
public sealed partial class SubmitterViewModel : ViewModelBase
{
    private Submitter _submitter = new();
    private CancellationTokenSource? _inFlight;

    public string[] Methods { get; } = { "GET", "POST", "PUT", "PATCH", "DELETE", "HEAD", "OPTIONS" };

    // ---- Quick selectors (HTTP Debugger's Submitter presets) -----------------
    /// <summary>Ready-made request shapes, so a common call is two clicks away.</summary>
    public string[] Presets { get; } =
    {
        "Preset…",
        "GET JSON API",
        "POST JSON body",
        "POST form data",
        "GraphQL query",
        "Bearer-authenticated GET",
        "CORS preflight (OPTIONS)",
        "Multipart upload",
    };

    public string[] UserAgents { get; } =
    {
        "User-Agent…",
        "HttpSpy/1.0",
        "Chrome (Windows)",
        "Safari (iPhone)",
        "curl/8.5.0",
        "Googlebot",
    };

    public string[] ContentTypes { get; } =
    {
        "Content-Type…",
        "application/json",
        "application/x-www-form-urlencoded",
        "multipart/form-data",
        "text/plain",
        "text/xml",
        "application/octet-stream",
    };

    [ObservableProperty] private int _presetIndex;
    [ObservableProperty] private int _userAgentIndex;
    [ObservableProperty] private int _contentTypeIndex;

    /// <summary>Request timeout in seconds; 0 restores the default.</summary>
    [ObservableProperty] private int _timeoutSeconds = 100;

    /// <summary>Ignore upstream certificate errors when replaying.</summary>
    [ObservableProperty] private bool _ignoreCertificateErrors = true;

    partial void OnTimeoutSecondsChanged(int value) => RebuildClient();
    partial void OnIgnoreCertificateErrorsChanged(bool value) => RebuildClient();

    /// <summary>
    /// The HttpClient's timeout and certificate policy are fixed at construction,
    /// so changing either means building a fresh one.
    /// </summary>
    private void RebuildClient()
    {
        var replacement = new Submitter(IgnoreCertificateErrors,
            TimeSpan.FromSeconds(TimeoutSeconds > 0 ? TimeoutSeconds : 100));
        var previous = _submitter;
        _submitter = replacement;
        previous.Dispose();
    }

    partial void OnPresetIndexChanged(int value)
    {
        if (value <= 0) return;
        ApplyPreset(Presets[value]);
        PresetIndex = 0; // act like a menu, not a persistent selection
    }

    partial void OnUserAgentIndexChanged(int value)
    {
        if (value <= 0) return;
        SetHeader("User-Agent", UserAgents[value] switch
        {
            "Chrome (Windows)" =>
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) " +
                "Chrome/126.0.0.0 Safari/537.36",
            "Safari (iPhone)" =>
                "Mozilla/5.0 (iPhone; CPU iPhone OS 17_5 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) " +
                "Version/17.5 Mobile/15E148 Safari/604.1",
            "Googlebot" => "Mozilla/5.0 (compatible; Googlebot/2.1; +http://www.google.com/bot.html)",
            var other => other,
        });
        UserAgentIndex = 0;
    }

    partial void OnContentTypeIndexChanged(int value)
    {
        if (value <= 0) return;
        SetHeader("Content-Type", ContentTypes[value]);
        ContentTypeIndex = 0;
    }

    /// <summary>Replaces (or adds) one header in the free-text header editor.</summary>
    private void SetHeader(string name, string value)
    {
        var lines = (HeadersText ?? string.Empty)
            .Split('\n')
            .Select(l => l.TrimEnd('\r'))
            .Where(l => !l.TrimStart().StartsWith(name + ":", StringComparison.OrdinalIgnoreCase))
            .Where(l => l.Trim().Length > 0)
            .ToList();
        lines.Add($"{name}: {value}");
        HeadersText = string.Join('\n', lines);
    }

    private void ApplyPreset(string preset)
    {
        switch (preset)
        {
            case "GET JSON API":
                Method = "GET";
                HeadersText = "Accept: application/json\nUser-Agent: HttpSpy/1.0";
                BodyText = "";
                break;
            case "POST JSON body":
                Method = "POST";
                HeadersText = "Content-Type: application/json\nAccept: application/json";
                BodyText = "{\n  \"name\": \"example\",\n  \"value\": 1\n}";
                break;
            case "POST form data":
                Method = "POST";
                HeadersText = "Content-Type: application/x-www-form-urlencoded";
                BodyText = "field=value&other=123";
                break;
            case "GraphQL query":
                Method = "POST";
                HeadersText = "Content-Type: application/json\nAccept: application/json";
                BodyText = "{\n  \"query\": \"{ __typename }\",\n  \"variables\": {}\n}";
                break;
            case "Bearer-authenticated GET":
                Method = "GET";
                HeadersText = "Authorization: Bearer REPLACE_ME\nAccept: application/json";
                BodyText = "";
                break;
            case "CORS preflight (OPTIONS)":
                Method = "OPTIONS";
                HeadersText = "Origin: https://example.com\n" +
                              "Access-Control-Request-Method: POST\n" +
                              "Access-Control-Request-Headers: content-type, authorization";
                BodyText = "";
                break;
            case "Multipart upload":
                Method = "POST";
                HeadersText = "Content-Type: multipart/form-data; boundary=----HttpSpyBoundary";
                BodyText = "------HttpSpyBoundary\r\n" +
                           "Content-Disposition: form-data; name=\"file\"; filename=\"test.txt\"\r\n" +
                           "Content-Type: text/plain\r\n\r\n" +
                           "hello\r\n" +
                           "------HttpSpyBoundary--";
                break;
        }
        ResponseStatus = $"Loaded preset: {preset}";
    }

    [ObservableProperty] private string _method = "GET";
    [ObservableProperty] private string _url = "https://httpbin.org/get";
    [ObservableProperty] private string _headersText = "User-Agent: HttpSpy/1.0\nAccept: */*";
    [ObservableProperty] private string _bodyText = "";

    [ObservableProperty] private string _responseStatus = "";
    [ObservableProperty] private string _responseHeaders = "";
    [ObservableProperty] private string _responseBody = "";
    [ObservableProperty] private bool _isSending;
    [ObservableProperty] private string _elapsed = "";

    /// <summary>cURL command to import into the composer.</summary>
    [ObservableProperty] private string _curlImport = "";

    /// <summary>Syntax language used to colourise the response body viewer.</summary>
    [ObservableProperty] private Controls.SyntaxLanguage _responseBodyLanguage = Controls.SyntaxLanguage.None;

    /// <summary>Raised when a request is sent so the parent can record it in the grid.</summary>
    public event Action<HttpSession>? RequestSent;

    /// <summary>Parses a pasted <c>curl</c> command and fills the composer fields.</summary>
    [RelayCommand]
    private void ImportCurl()
    {
        if (string.IsNullOrWhiteSpace(CurlImport)) return;
        var req = CurlParser.Parse(CurlImport);
        if (req.Url.Length == 0) { ResponseStatus = "cURL: no URL found"; return; }

        Method = req.Method;
        Url = req.Url;
        var sb = new System.Text.StringBuilder();
        foreach (var (name, value) in req.Headers) sb.Append(name).Append(": ").AppendLine(value);
        HeadersText = sb.ToString().TrimEnd();
        BodyText = req.Body;
        ResponseStatus = $"Imported cURL → {Method} {Url}";
    }

    public void LoadFrom(HttpSession session)
    {
        Method = session.Method;
        Url = session.FullUrl;
        var sb = new System.Text.StringBuilder();
        foreach (var h in session.RequestHeaders)
        {
            if (h.Name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)) continue;
            sb.Append(h.Name).Append(": ").AppendLine(h.Value);
        }
        HeadersText = sb.ToString().TrimEnd();
        BodyText = session.RequestBodyText;
    }

    /// <summary>Cancels a request that is taking too long to come back.</summary>
    [RelayCommand]
    private void CancelSend()
    {
        _inFlight?.Cancel();
        ResponseStatus = "Cancelled";
    }

    [RelayCommand]
    private async Task SendAsync()
    {
        if (IsSending) return;
        IsSending = true;
        ResponseStatus = "Sending…";
        ResponseHeaders = "";
        ResponseBody = "";
        try
        {
            var spec = new RequestSpec { Method = Method, Url = Url, Body = BodyText };
            foreach (var line in HeadersText.Split('\n'))
            {
                var trimmed = line.Trim();
                if (trimmed.Length == 0) continue;
                int colon = trimmed.IndexOf(':');
                if (colon <= 0) continue;
                spec.Headers.Add(new HttpHeader(trimmed[..colon].Trim(), trimmed[(colon + 1)..].Trim()));
            }

            using var cts = new CancellationTokenSource();
            _inFlight = cts;

            var session = await _submitter.SendAsync(spec, cts.Token);
            RequestSent?.Invoke(session);

            ResponseStatus = session.Error is null
                ? $"{session.StatusCode} {session.StatusText}"
                : $"ERROR: {session.Error}";
            Elapsed = $"{session.DurationMs:F0} ms";

            var hb = new System.Text.StringBuilder();
            foreach (var h in session.ResponseHeaders) hb.Append(h.Name).Append(": ").AppendLine(h.Value);
            ResponseHeaders = hb.ToString();

            var body = session.ResponseBodyText;
            ResponseBody = session.ResponseBodyKind switch
            {
                BodyContentType.Json => BodyFormatter.PrettyJson(body),
                BodyContentType.Xml or BodyContentType.Html => BodyFormatter.PrettyXml(body),
                _ => body
            };
            ResponseBodyLanguage = session.ResponseBodyKind switch
            {
                BodyContentType.Json => Controls.SyntaxLanguage.Json,
                BodyContentType.Xml or BodyContentType.Html => Controls.SyntaxLanguage.Xml,
                _ => Controls.SyntaxLanguage.None,
            };
        }
        catch (Exception ex)
        {
            ResponseStatus = $"ERROR: {ex.Message}";
        }
        finally
        {
            _inFlight = null;
            IsSending = false;
        }
    }

    /// <summary>Serializes the composed request so it can be saved and replayed later.</summary>
    public string ToPortableText()
    {
        var sb = new System.Text.StringBuilder();
        sb.Append(Method).Append(' ').Append(Url).AppendLine(" HTTP/1.1");
        foreach (var line in (HeadersText ?? string.Empty).Split('\n'))
        {
            var trimmed = line.TrimEnd('\r');
            if (trimmed.Trim().Length > 0) sb.AppendLine(trimmed);
        }
        if (!string.IsNullOrEmpty(BodyText)) sb.AppendLine().Append(BodyText);
        return sb.ToString();
    }

    /// <summary>Loads a request previously saved by <see cref="ToPortableText"/>.</summary>
    public void LoadPortableText(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        if (lines.Length == 0) return;

        var request = lines[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (request.Length >= 2) { Method = request[0]; Url = request[1]; }

        var headers = new System.Text.StringBuilder();
        int i = 1;
        for (; i < lines.Length; i++)
        {
            if (lines[i].Trim().Length == 0) { i++; break; }
            headers.AppendLine(lines[i]);
        }
        HeadersText = headers.ToString().TrimEnd();
        BodyText = i < lines.Length ? string.Join('\n', lines[i..]) : string.Empty;
        ResponseStatus = "Request loaded";
    }
}
