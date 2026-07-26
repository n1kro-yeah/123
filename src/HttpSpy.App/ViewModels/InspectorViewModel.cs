using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using HttpSpy.Core.Export;
using HttpSpy.Core.Models;
using HttpSpy.Core.Proxy.Grpc;
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

    /// <summary>Syntax language used to colourise the request body viewer.</summary>
    public Controls.SyntaxLanguage RequestBodyLanguage => LanguageFor(_session?.RequestBodyKind);
    public string RequestRaw => _requestRaw ??= _session is null ? "" : BuildRaw(true);
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

    /// <summary>Syntax language used to colourise the response body viewer.</summary>
    public Controls.SyntaxLanguage ResponseBodyLanguage => LanguageFor(_session?.ResponseBodyKind);

    private static Controls.SyntaxLanguage LanguageFor(BodyContentType? kind) => kind switch
    {
        BodyContentType.Json => Controls.SyntaxLanguage.Json,
        BodyContentType.Xml or BodyContentType.Html => Controls.SyntaxLanguage.Xml,
        _ => Controls.SyntaxLanguage.None,
    };

    public string ResponseBodyRaw => _session?.ResponseBodyText ?? "";

    // Hex dumps and raw views are ~4x and ~1x the body size respectively. Building
    // them eagerly for every selection froze the UI on large payloads, so they are
    // computed on first access and cached until the selection changes.
    private string? _responseHex;
    private string? _requestHex;
    private string? _responseRaw;
    private string? _requestRaw;

    public string ResponseHex =>
        _responseHex ??= _session is null ? "" : BodyFormatter.HexDump(_session.ResponseBody);

    public string RequestHex =>
        _requestHex ??= _session is null ? "" : BodyFormatter.HexDump(_session.RequestBody);

    public string ResponseRaw => _responseRaw ??= _session is null ? "" : BuildRaw(false);
    public bool HasResponseBody => _session is { ResponseBody.Length: > 0 };

    /// <summary>Warns when the captured body is only a prefix of what crossed the wire.</summary>
    public bool IsResponseTruncated => _session?.ResponseBodyTruncated == true;
    public bool IsRequestTruncated => _session?.RequestBodyTruncated == true;

    public string TruncationNotice => _session is null
        ? ""
        : "This body exceeded the buffered-body limit and was captured only in part. " +
          "Raise it under Tools ▸ Options if you need the whole payload.";

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

    // ---- Summary -------------------------------------------------------------
    /// <summary>
    /// A consolidated overview (HTTP Debugger "Summary" pane): sizes, transfer
    /// speed, and compression ratio derived from Content-Encoding/Content-Length.
    /// </summary>
    public string SummaryText
    {
        get
        {
            if (_session is null) return "";
            var s = _session;
            long decoded = s.ResponseBody.LongLength;
            // Prefer the wire size recorded before decompression; fall back to the
            // live Content-Encoding/Content-Length headers (e.g. loaded sessions).
            string? enc = s.OriginalContentEncoding ?? s.ResponseHeaders["Content-Encoding"];
            long wire = s.EncodedBodySize > 0 ? s.EncodedBodySize : ParseLong(s.ResponseHeaders["Content-Length"]);
            if (wire <= 0) wire = decoded;

            double seconds = s.DurationMs > 0 ? s.DurationMs / 1000.0 : 0;
            string speed = seconds > 0
                ? $"{decoded / seconds / 1024.0:F1} KB/s"
                : "—";

            string compression;
            if (!string.IsNullOrEmpty(enc) && !enc.Equals("identity", StringComparison.OrdinalIgnoreCase)
                && decoded > 0 && wire > 0 && wire < decoded)
            {
                double ratio = (1.0 - (double)wire / decoded) * 100.0;
                compression = $"{enc} — {ratio:F1}% smaller ({wire:N0} → {decoded:N0} bytes)";
            }
            else compression = string.IsNullOrEmpty(enc) ? "none" : enc;

            return $"URL:          {s.FullUrl}\n" +
                   $"Method:       {s.Method}    Status: {s.StatusCode} {s.StatusText}\n" +
                   $"Protocol:     {s.HttpVersion} → {s.ResponseHttpVersion}\n" +
                   $"Process:      {s.ProcessName} (PID {s.ProcessId})\n" +
                   $"Content type: {s.ResponseContentTypeShort}\n\n" +
                   $"Request size:  {s.BytesSent:N0} bytes\n" +
                   $"Response size: {s.BytesReceived:N0} bytes (body {decoded:N0})\n" +
                   $"Duration:      {s.DurationMs:F1} ms\n" +
                   $"Speed:         {speed}\n" +
                   $"Compression:   {compression}";
        }
    }

    private static long ParseLong(string? s) => long.TryParse(s, out var v) ? v : 0;

    // ---- Timing waterfall ----------------------------------------------------
    public ObservableCollection<TimingBar> TimingBars { get; } = new();

    private void RebuildTimingBars()
    {
        TimingBars.Clear();
        if (_session is null) return;
        var t = _session.Timings;
        var phases = new (string Label, double Ms, string Color)[]
        {
            ("DNS", t.DnsMs, "#9575CD"),
            ("Connect", t.ConnectMs, "#4FC3F7"),
            ("TLS", t.TlsMs, "#4DB6AC"),
            ("Send", t.SendMs, "#81C784"),
            ("Wait", t.WaitMs, "#FFB74D"),
            ("Receive", t.ReceiveMs, "#E57373"),
        };
        double total = phases.Where(p => p.Ms > 0).Sum(p => p.Ms);
        if (total <= 0) total = t.TotalMs > 0 ? t.TotalMs : 1;
        const double scale = 520.0;
        foreach (var p in phases)
        {
            if (p.Ms <= 0) continue;
            TimingBars.Add(new TimingBar
            {
                Label = p.Label,
                Width = Math.Max(2.0, p.Ms / total * scale),
                Color = p.Color,
                ValueText = $"{p.Ms:F1} ms",
            });
        }
    }

    // ---- JSON tree -----------------------------------------------------------
    public ObservableCollection<JsonTreeNode> ResponseJsonTree { get; } = new();
    public bool IsJsonResponse => _session?.ResponseBodyKind == BodyContentType.Json;

    private void RebuildJsonTree()
    {
        ResponseJsonTree.Clear();
        if (_session is null || _session.ResponseBodyKind != BodyContentType.Json) return;
        foreach (var node in JsonTreeNode.Parse(_session.ResponseBodyText))
            ResponseJsonTree.Add(node);
    }

    // ---- gRPC ----------------------------------------------------------------
    /// <summary>True when this transaction carries gRPC (application/grpc) payloads.</summary>
    public bool IsGrpc =>
        _session is not null &&
        (GrpcDecoder.IsGrpc(_session.RequestHeaders["Content-Type"]) ||
         GrpcDecoder.IsGrpc(_session.ResponseHeaders["Content-Type"]));

    public string GrpcRequestText => DecodeGrpc(
        _session?.RequestBody, _session?.RequestHeaders["grpc-encoding"]);

    public string GrpcResponseText => DecodeGrpc(
        _session?.ResponseBody, _session?.ResponseHeaders["grpc-encoding"]);

    private static string DecodeGrpc(byte[]? body, string? encoding)
    {
        if (body is null || body.Length == 0) return "(empty)";
        try { return GrpcDecoder.RenderAll(GrpcDecoder.Decode(body, encoding)); }
        catch (Exception ex) { return $"(failed to decode gRPC: {ex.Message})"; }
    }

    // ---- WebSocket / SSE -----------------------------------------------------
    public ObservableCollection<WebSocketFrame> WebSocketFrames { get; } = new();
    public ObservableCollection<ServerSentEvent> ServerSentEvents { get; } = new();
    public bool IsWebSocket => _session?.Kind == SessionKind.WebSocket;
    public bool IsSse => _session?.Kind == SessionKind.ServerSentEvents;

    // ---- Code generation -----------------------------------------------------
    // Sourced from the generator itself so the picker cannot drift out of sync
    // with the Language enum it indexes into.
    public string[] CodeLanguages { get; } = CodeGenerator.LanguageNames;

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
        WebSocketFrames.Clear();
        ServerSentEvents.Clear();
        ImagePreview = null;

        // Invalidate the lazily-built heavy views for the previous selection.
        _responseHex = _requestHex = _responseRaw = _requestRaw = null;

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
            RebuildJsonTree();
            RebuildTimingBars();
            SyncStreaming();
        }
        else
        {
            ResponseJsonTree.Clear();
            TimingBars.Clear();
        }

        foreach (var name in DynamicProperties) OnPropertyChanged(name);
    }

    /// <summary>
    /// Mirrors the session's streaming collections into the observable ones the
    /// grids bind to. The model returns immutable snapshots (frames are appended
    /// from proxy worker threads), and the retention cap can drop old entries — so
    /// when the source has shrunk or diverged, rebuild rather than append.
    /// </summary>
    private void SyncStreaming()
    {
        if (_session is null) return;

        var frames = _session.WebSocketFrames;
        if (frames.Count < WebSocketFrames.Count)
        {
            WebSocketFrames.Clear();
            foreach (var f in frames) WebSocketFrames.Add(f);
        }
        else
        {
            for (int i = WebSocketFrames.Count; i < frames.Count; i++) WebSocketFrames.Add(frames[i]);
        }

        var events = _session.ServerSentEvents;
        if (events.Count < ServerSentEvents.Count)
        {
            ServerSentEvents.Clear();
            foreach (var e in events) ServerSentEvents.Add(e);
        }
        else
        {
            for (int i = ServerSentEvents.Count; i < events.Count; i++) ServerSentEvents.Add(events[i]);
        }
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
        nameof(IsJsonResponse), nameof(SummaryText),
        nameof(IsGrpc), nameof(GrpcRequestText), nameof(GrpcResponseText),
        nameof(RequestBodyLanguage), nameof(ResponseBodyLanguage),
        nameof(IsRequestTruncated), nameof(IsResponseTruncated), nameof(TruncationNotice),
    };
}
