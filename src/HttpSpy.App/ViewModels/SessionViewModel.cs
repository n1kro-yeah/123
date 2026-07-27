using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using HttpSpy.App.Converters;
using HttpSpy.Core.Models;

namespace HttpSpy.App.ViewModels;

/// <summary>A thin observable wrapper around a captured <see cref="HttpSession"/> for the grid.</summary>
public sealed class SessionViewModel : ObservableObject
{
    public SessionViewModel(HttpSession model) => Model = model;

    public HttpSession Model { get; }

    /// <summary>
    /// Fades out shortly after the row arrives. During a live capture rows scroll
    /// past faster than the eye can follow; a brief tint is what makes "this one
    /// just landed" legible without stopping the capture.
    /// </summary>
    private bool _isNew;

    public bool IsNew
    {
        get => _isNew;
        set
        {
            if (_isNew == value) return;
            _isNew = value;
            OnPropertyChanged(nameof(IsNew));
            OnPropertyChanged(nameof(RowBackground));
        }
    }

    /// <summary>
    /// The row fill: the arrival tint while it lasts, then whatever the
    /// highlight rules say. One property rather than two layers, because a
    /// DataGridRow has a single background to give.
    /// </summary>
    public IBrush RowBackground => IsNew
        ? Palette.ArrivalTint
        : (IBrush?)HighlightToBrushConverter.Instance.Convert(
              HighlightColor, typeof(IBrush), null, System.Globalization.CultureInfo.InvariantCulture)
          ?? Palette.Transparent;

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

    /// <summary>Effective transfer rate, for the Speed column.</summary>
    public double SpeedBytesPerSecond => Model.SpeedBytesPerSecond;

    /// <summary>Which backend answered — useful behind a load balancer or CDN.</summary>
    public string RemoteAddress => Model.RemoteAddress;

    /// <summary>Transport connection identity, shown as "connection · stream".</summary>
    public string ConnectionLabel => Model.StreamId > 0
        ? $"{Model.ConnectionId}·{Model.StreamId}"
        : Model.ConnectionId > 0
            ? Model.ConnectionId.ToString()
            : string.Empty;
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
            if (Model.Imported) flags += "⤓";
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
        OnPropertyChanged(nameof(RowBackground));
        OnPropertyChanged(nameof(Bookmarked));
        OnPropertyChanged(nameof(BookmarkGlyph));
        OnPropertyChanged(nameof(Flags));
        OnPropertyChanged(nameof(IsTruncated));
        OnPropertyChanged(nameof(RowTooltip));
        OnPropertyChanged(nameof(Error));
        OnPropertyChanged(nameof(IsError));
        OnPropertyChanged(nameof(Comment));
        OnPropertyChanged(nameof(ProcessName));
        OnPropertyChanged(nameof(SpeedBytesPerSecond));
        OnPropertyChanged(nameof(RemoteAddress));
        OnPropertyChanged(nameof(ConnectionLabel));
        OnPropertyChanged(nameof(Url));
        OnPropertyChanged(nameof(PathAndQuery));
    }
}
