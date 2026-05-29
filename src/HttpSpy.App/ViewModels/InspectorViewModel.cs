using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using HttpSpy.Core.Export;
using HttpSpy.Core.Models;
using HttpSpy.Core.Util;

namespace HttpSpy.App.ViewModels;

/// <summary>Presents the request/response details of the currently selected session.</summary>
public sealed class InspectorViewModel : ViewModelBase
{
    private HttpSession? _session;
    private CodeGenerator.Language _codeLanguage = CodeGenerator.Language.Curl;

    public HttpSession? Session
    {
        get => _session;
        set
        {
            if (SetProperty(ref _session, value)) RefreshAll();
        }
    }

    // ---- Request -------------------------------------------------------------
    public ObservableCollection<HttpHeader> RequestHeaders { get; } = new();
    public ObservableCollection<NameValue> QueryParameters { get; } = new();
    public ObservableCollection<NameValue> RequestCookies { get; } = new();
    public ObservableCollection<NameValue> FormFields { get; } = new();

    public string RequestLine => _session is null ? "" : $"{_session.Method} {_session.FullUrl} {_session.HttpVersion}";
    public string RequestBodyText => _session?.RequestBodyText ?? "";
    public string RequestRaw => _session is null ? "" : BuildRaw(true);
    public bool HasRequestBody => _session is { RequestBody.Length: > 0 };

    // ---- Response ------------------------------------------------------------
    public ObservableCollection<HttpHeader> ResponseHeaders { get; } = new();
    public ObservableCollection<NameValue> ResponseCookies { get; } = new();

    public string StatusLine => _session is null ? "" :
        $"{_session.ResponseHttpVersion} {_session.StatusCode} {_session.StatusText}";

    public string ResponseBodyFormatted
    {
        get
        {
            if (_session is null) return "";
            var text = _session.ResponseBodyText;
            return _session.ResponseBodyKind switch
            {
                BodyContentType.Json => BodyFormatter.PrettyJson(text),
                BodyContentType.Xml or BodyContentType.Html => BodyFormatter.PrettyXml(text),
                _ => text
            };
        }
    }

    public string ResponseBodyRaw => _session?.ResponseBodyText ?? "";
    public string ResponseHex => _session is null ? "" : BodyFormatter.HexDump(_session.ResponseBody);
    public string RequestHex => _session is null ? "" : BodyFormatter.HexDump(_session.RequestBody);
    public string ResponseRaw => _session is null ? "" : BuildRaw(false);
    public bool HasResponseBody => _session is { ResponseBody.Length: > 0 };

    // ---- Image preview -------------------------------------------------------
    private Bitmap? _imagePreview;
    public Bitmap? ImagePreview { get => _imagePreview; private set => SetProperty(ref _imagePreview, value); }
    public bool HasImagePreview => ImagePreview is not null;

    // ---- Timings -------------------------------------------------------------
    public string TimingsText
    {
        get
        {
            if (_session is null) return "";
            var t = _session.Timings;
            return $"Total:    {t.TotalMs:F1} ms\n" +
                   $"DNS:      {Fmt(t.DnsMs)}\n" +
                   $"Connect:  {Fmt(t.ConnectMs)}\n" +
                   $"TLS:      {Fmt(t.TlsMs)}\n" +
                   $"Send:     {Fmt(t.SendMs)}\n" +
                   $"Wait:     {Fmt(t.WaitMs)}\n" +
                   $"Receive:  {Fmt(t.ReceiveMs)}\n\n" +
                   $"Started:  {_session.StartTime:HH:mm:ss.fff}\n" +
                   $"Ended:    {_session.EndTime:HH:mm:ss.fff}\n" +
                   $"Bytes in: {_session.BytesReceived}\n" +
                   $"Bytes out:{_session.BytesSent}";
        }
    }

    private static string Fmt(double v) => v < 0 ? "—" : $"{v:F1} ms";

    // ---- WebSocket / SSE -----------------------------------------------------
    public ObservableCollection<WebSocketFrame> WebSocketFrames { get; } = new();
    public ObservableCollection<ServerSentEvent> ServerSentEvents { get; } = new();
    public bool IsWebSocket => _session?.Kind == SessionKind.WebSocket;
    public bool IsSse => _session?.Kind == SessionKind.ServerSentEvents;

    // ---- Code generation -----------------------------------------------------
    public string[] CodeLanguages { get; } = { "curl", "C#", "Python", "JavaScript" };

    private int _selectedCodeLanguageIndex;
    public int SelectedCodeLanguageIndex
    {
        get => _selectedCodeLanguageIndex;
        set
        {
            if (SetProperty(ref _selectedCodeLanguageIndex, value))
            {
                _codeLanguage = (CodeGenerator.Language)value;
                OnPropertyChanged(nameof(GeneratedCode));
            }
        }
    }

    public string GeneratedCode =>
        _session is null ? "" : CodeGenerator.Generate(_session, _codeLanguage);

    public void RefreshStreaming()
    {
        if (_session is null) return;
        SyncStreaming();
        OnPropertyChanged(nameof(ResponseBodyFormatted));
    }

    private void RefreshAll()
    {
        RequestHeaders.Clear();
        QueryParameters.Clear();
        RequestCookies.Clear();
        FormFields.Clear();
        ResponseHeaders.Clear();
        ResponseCookies.Clear();
        ImagePreview = null;

        if (_session is not null)
        {
            foreach (var h in _session.RequestHeaders) RequestHeaders.Add(h);
            foreach (var q in _session.QueryParameters()) QueryParameters.Add(new NameValue(q.Key, q.Value));
            foreach (var c in _session.RequestCookies()) RequestCookies.Add(new NameValue(c.Key, c.Value));
            foreach (var h in _session.ResponseHeaders) ResponseHeaders.Add(h);
            foreach (var sc in _session.ResponseSetCookies())
            {
                var parts = sc.Split('=', 2);
                ResponseCookies.Add(new NameValue(parts[0].Trim(), parts.Length > 1 ? parts[1] : ""));
            }

            if (_session.RequestBodyKind == BodyContentType.Form)
                foreach (var f in UrlCodec.ParseQuery(_session.RequestBodyText))
                    FormFields.Add(new NameValue(f.Key, f.Value));

            TryLoadImage();
            SyncStreaming();
        }

        foreach (var name in DynamicProperties) OnPropertyChanged(name);
    }

    private void SyncStreaming()
    {
        if (_session is null) return;
        while (WebSocketFrames.Count < _session.WebSocketFrames.Count)
            WebSocketFrames.Add(_session.WebSocketFrames[WebSocketFrames.Count]);
        while (ServerSentEvents.Count < _session.ServerSentEvents.Count)
            ServerSentEvents.Add(_session.ServerSentEvents[ServerSentEvents.Count]);
        if (WebSocketFrames.Count > _session.WebSocketFrames.Count) WebSocketFrames.Clear();
        if (ServerSentEvents.Count > _session.ServerSentEvents.Count) ServerSentEvents.Clear();
    }

    private void TryLoadImage()
    {
        if (_session is null || _session.ResponseBodyKind != BodyContentType.Image) return;
        try
        {
            using var ms = new MemoryStream(_session.ResponseBody);
            ImagePreview = new Bitmap(ms);
        }
        catch
        {
            ImagePreview = null;
        }
    }

    private string BuildRaw(bool request)
    {
        if (_session is null) return "";
        var sb = new System.Text.StringBuilder();
        if (request)
        {
            sb.AppendLine(RequestLine);
            foreach (var h in _session.RequestHeaders) sb.Append(h.Name).Append(": ").AppendLine(h.Value);
            sb.AppendLine();
            sb.Append(_session.RequestBodyText);
        }
        else
        {
            sb.AppendLine(StatusLine);
            foreach (var h in _session.ResponseHeaders) sb.Append(h.Name).Append(": ").AppendLine(h.Value);
            sb.AppendLine();
            sb.Append(_session.ResponseBodyText);
        }
        return sb.ToString();
    }

    private static readonly string[] DynamicProperties =
    {
        nameof(RequestLine), nameof(RequestBodyText), nameof(RequestRaw), nameof(HasRequestBody),
        nameof(RequestHex), nameof(StatusLine), nameof(ResponseBodyFormatted), nameof(ResponseBodyRaw),
        nameof(ResponseHex), nameof(ResponseRaw), nameof(HasResponseBody), nameof(TimingsText),
        nameof(IsWebSocket), nameof(IsSse), nameof(GeneratedCode), nameof(HasImagePreview),
    };
}
