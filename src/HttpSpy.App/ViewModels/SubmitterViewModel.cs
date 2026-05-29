using System;
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
    private readonly Submitter _submitter = new();

    public string[] Methods { get; } = { "GET", "POST", "PUT", "PATCH", "DELETE", "HEAD", "OPTIONS" };

    [ObservableProperty] private string _method = "GET";
    [ObservableProperty] private string _url = "https://httpbin.org/get";
    [ObservableProperty] private string _headersText = "User-Agent: HttpSpy/1.0\nAccept: */*";
    [ObservableProperty] private string _bodyText = "";

    [ObservableProperty] private string _responseStatus = "";
    [ObservableProperty] private string _responseHeaders = "";
    [ObservableProperty] private string _responseBody = "";
    [ObservableProperty] private bool _isSending;
    [ObservableProperty] private string _elapsed = "";

    /// <summary>Raised when a request is sent so the parent can record it in the grid.</summary>
    public event Action<HttpSession>? RequestSent;

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

            var session = await _submitter.SendAsync(spec);
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
        }
        catch (Exception ex)
        {
            ResponseStatus = $"ERROR: {ex.Message}";
        }
        finally
        {
            IsSending = false;
        }
    }
}
