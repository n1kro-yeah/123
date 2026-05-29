using System;
using CommunityToolkit.Mvvm.ComponentModel;
using HttpSpy.Core.Models;
using HttpSpy.Core.Proxy;

namespace HttpSpy.App.ViewModels;

/// <summary>Presents a transaction paused at a breakpoint and lets the user edit + resume it.</summary>
public sealed class BreakpointViewModel : ObservableObject
{
    public BreakpointViewModel(PausedTransaction paused)
    {
        Paused = paused;
        var s = paused.Session;
        IsRequestPhase = paused.Phase is BreakpointPhase.BeforeRequest;

        if (IsRequestPhase)
        {
            _headersText = DumpHeaders(s.RequestHeaders);
            _bodyText = s.RequestBodyText;
            _statusLine = $"{s.Method} {s.FullUrl}";
        }
        else
        {
            _headersText = DumpHeaders(s.ResponseHeaders);
            _bodyText = s.ResponseBodyText;
            _statusLine = $"{s.StatusCode} {s.StatusText}";
        }
    }

    public PausedTransaction Paused { get; }
    public bool IsRequestPhase { get; }
    public string PhaseLabel => IsRequestPhase ? "REQUEST paused" : "RESPONSE paused";

    private string _statusLine;
    public string StatusLine { get => _statusLine; set => SetProperty(ref _statusLine, value); }

    private string _headersText;
    public string HeadersText { get => _headersText; set => SetProperty(ref _headersText, value); }

    private string _bodyText;
    public string BodyText { get => _bodyText; set => SetProperty(ref _bodyText, value); }

    /// <summary>Applies the edited headers/body back to the session, then resumes.</summary>
    public void ContinueWithEdits()
    {
        var s = Paused.Session;
        var headers = IsRequestPhase ? s.RequestHeaders : s.ResponseHeaders;
        headers.Clear();
        foreach (var line in HeadersText.Split('\n'))
        {
            var trimmed = line.Trim();
            int colon = trimmed.IndexOf(':');
            if (colon <= 0) continue;
            headers.Add(trimmed[..colon].Trim(), trimmed[(colon + 1)..].Trim());
        }

        var bytes = System.Text.Encoding.UTF8.GetBytes(BodyText);
        if (IsRequestPhase) s.RequestBody = bytes; else s.ResponseBody = bytes;
        headers.Set("Content-Length", bytes.Length.ToString());

        Paused.Resume();
    }

    public void Abort() => Paused.Abort();

    private static string DumpHeaders(HeaderCollection headers)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var h in headers) sb.Append(h.Name).Append(": ").AppendLine(h.Value);
        return sb.ToString().TrimEnd();
    }
}
