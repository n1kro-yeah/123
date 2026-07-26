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

    /// <summary>
    /// Status code alone, for the coloured pill. The reason phrase is shown
    /// separately so the pill stays a fixed, scannable width.
    /// </summary>
    public string StatusCodeText => StatusCode > 0 ? StatusCode.ToString() : Model.Error is null ? "…" : "ERR";

    /// <summary>Bookmark marker rendered in its own narrow column.</summary>
    public string BookmarkGlyph => Bookmarked ? "★" : string.Empty;

    /// <summary>Marks bodies that were capped by the buffered-body limit.</summary>
    public bool IsTruncated => Model.RequestBodyTruncated || Model.ResponseBodyTruncated;

    /// <summary>Compact per-row annotations: truncation, replay, comment.</summary>
    public string Flags
    {
        get
        {
            var flags = string.Empty;
            if (Model.IsReplay) flags += "↻";
            if (IsTruncated) flags += "✂";
            if (!string.IsNullOrEmpty(Model.Comment)) flags += "💬";
            return flags;
        }
    }

    /// <summary>Tooltip text for the row, assembled once per refresh.</summary>
    public string RowTooltip
    {
        get
        {
            var sb = new System.Text.StringBuilder();
            sb.Append(Method).Append(' ').AppendLine(Url);
            if (StatusCode > 0) sb.Append("Status: ").AppendLine(StatusDisplay);
            if (Model.Error is not null) sb.Append("Error: ").AppendLine(Model.Error);
            sb.Append("Started: ").AppendLine(StartTime);
            if (!string.IsNullOrEmpty(ProcessName))
                sb.Append("Process: ").Append(ProcessName).Append(" (").Append(ProcessId).AppendLine(")");
            if (IsTruncated) sb.AppendLine("Body was truncated at the buffered-body limit.");
            if (!string.IsNullOrEmpty(Comment)) sb.Append("Note: ").AppendLine(Comment);
            return sb.ToString().TrimEnd();
        }
    }

    public string SchemeGlyph => Kind switch
    {
        SessionKind.WebSocket => "WS",
        SessionKind.ServerSentEvents => "SSE",
        SessionKind.Tunnel => "TUN",
        SessionKind.Https => Model.HttpVersion.Contains('2') ? "H2" : "HTTPS",
        _ => Model.HttpVersion.Contains('2') ? "H2C" : "HTTP"
    };

    /// <summary>Raises change notifications for the dynamic columns after the model updates.</summary>
    public void Refresh()
    {
        OnPropertyChanged(nameof(StatusCode));
        OnPropertyChanged(nameof(StatusDisplay));
        OnPropertyChanged(nameof(StatusCodeText));
        OnPropertyChanged(nameof(ContentType));
        OnPropertyChanged(nameof(Size));
        OnPropertyChanged(nameof(DurationMs));
        OnPropertyChanged(nameof(Kind));
        OnPropertyChanged(nameof(SchemeGlyph));
        OnPropertyChanged(nameof(HighlightColor));
        OnPropertyChanged(nameof(Bookmarked));
        OnPropertyChanged(nameof(BookmarkGlyph));
        OnPropertyChanged(nameof(Flags));
        OnPropertyChanged(nameof(IsTruncated));
        OnPropertyChanged(nameof(RowTooltip));
        OnPropertyChanged(nameof(Error));
        OnPropertyChanged(nameof(IsError));
        OnPropertyChanged(nameof(Comment));
        OnPropertyChanged(nameof(ProcessName));
        OnPropertyChanged(nameof(Url));
        OnPropertyChanged(nameof(PathAndQuery));
    }
}
