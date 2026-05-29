using CommunityToolkit.Mvvm.ComponentModel;
using HttpSpy.Core.Models;

namespace HttpSpy.App.ViewModels;

/// <summary>A thin observable wrapper around a captured <see cref="HttpSession"/> for the grid.</summary>
public sealed class SessionViewModel : ObservableObject
{
    public SessionViewModel(HttpSession model) => Model = model;

    public HttpSession Model { get; }

    public int Index => Model.Index;
    public string Method => Model.Method;
    public SessionKind Kind => Model.Kind;
    public bool IsTls => Model.IsTls;
    public int StatusCode => Model.StatusCode;
    public string StatusDisplay => Model.StatusDisplay;
    public string Host => Model.Host;
    public string PathAndQuery => string.IsNullOrEmpty(Model.QueryString) ? Model.Path : $"{Model.Path}?{Model.QueryString}";
    public string Url => Model.FullUrl;
    public string ContentType => Model.ResponseContentTypeShort;
    public long Size => Model.ResponseBodySize;
    public double DurationMs => Model.DurationMs;
    public string ProcessName => Model.ProcessName;
    public int ProcessId => Model.ProcessId;
    public string StartTime => Model.StartTime.ToString("HH:mm:ss.fff");
    public bool Bookmarked => Model.Bookmarked;
    public uint HighlightColor => Model.HighlightColor;
    public string? Error => Model.Error;
    public bool IsError => Model.IsError;
    public string? Comment => Model.Comment;

    public string SchemeGlyph => Kind switch
    {
        SessionKind.Https => "HTTPS",
        SessionKind.WebSocket => "WS",
        SessionKind.ServerSentEvents => "SSE",
        SessionKind.Tunnel => "TUN",
        _ => "HTTP"
    };

    /// <summary>Raises change notifications for the dynamic columns after the model updates.</summary>
    public void Refresh()
    {
        OnPropertyChanged(nameof(StatusCode));
        OnPropertyChanged(nameof(StatusDisplay));
        OnPropertyChanged(nameof(ContentType));
        OnPropertyChanged(nameof(Size));
        OnPropertyChanged(nameof(DurationMs));
        OnPropertyChanged(nameof(Kind));
        OnPropertyChanged(nameof(SchemeGlyph));
        OnPropertyChanged(nameof(HighlightColor));
        OnPropertyChanged(nameof(Bookmarked));
        OnPropertyChanged(nameof(Error));
        OnPropertyChanged(nameof(IsError));
        OnPropertyChanged(nameof(Comment));
        OnPropertyChanged(nameof(ProcessName));
        OnPropertyChanged(nameof(Url));
        OnPropertyChanged(nameof(PathAndQuery));
    }
}
