using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HttpSpy.Core.Analysis;
using HttpSpy.Core.Models;

namespace HttpSpy.App.ViewModels;

/// <summary>A structure-tree node prepared for display.</summary>
public sealed class StructureNodeViewModel
{
    public StructureNodeViewModel(StructureNode node, long maxBytes)
    {
        Node = node;
        Children = new ObservableCollection<StructureNodeViewModel>(
            node.Children.Select(c => new StructureNodeViewModel(c, maxBytes)));

        // Bar width is relative to the heaviest node in the whole tree, so the
        // visual weight of a branch is comparable across the view.
        BarWidth = maxBytes <= 0 ? 0 : Math.Max(2, node.Subtree.TotalBytes / (double)maxBytes * 130);
    }

    public StructureNode Node { get; }
    public ObservableCollection<StructureNodeViewModel> Children { get; }

    public string Display => Node.Display;
    public double BarWidth { get; }

    public string Glyph => Node.Kind switch
    {
        StructureNodeKind.Root => "◈",
        StructureNodeKind.Host => "◉",
        StructureNodeKind.Folder => "▸",
        _ => "·",
    };

    public int Requests => Node.Subtree.Requests;
    public long Bytes => Node.Subtree.TotalBytes;

    public string RequestsText => Node.Subtree.Requests.ToString("N0");
    public string BytesText => Converters.ByteSizeConverter.Format(Node.Subtree.TotalBytes);
    public string AverageText => Node.Subtree.Requests == 0
        ? "—"
        : Converters.DurationConverter.Format(Node.Subtree.AverageMs);

    public bool HasProblems => Node.Subtree.Errors > 0 || Node.Subtree.Warnings > 0;

    public string ProblemsText => Node.Subtree switch
    {
        { Errors: 0, Warnings: 0 } => string.Empty,
        { Errors: > 0, Warnings: > 0 } t => $"{t.Errors} err · {t.Warnings} 4xx",
        { Errors: > 0 } t => $"{t.Errors} err",
        var t => $"{t.Warnings} 4xx",
    };

    /// <summary>The first session on this node, used to jump into the grid.</summary>
    public HttpSession? FirstSession =>
        Node.Sessions.Count > 0 ? Node.Sessions[0] : FirstDescendantSession(Node);

    private static HttpSession? FirstDescendantSession(StructureNode node)
    {
        foreach (var child in node.Children)
        {
            if (child.Sessions.Count > 0) return child.Sessions[0];
            var found = FirstDescendantSession(child);
            if (found is not null) return found;
        }
        return null;
    }

    public string Tooltip
    {
        get
        {
            var t = Node.Subtree;
            var sb = new System.Text.StringBuilder();
            sb.Append(string.IsNullOrEmpty(Node.FullPath) ? "All hosts" : Node.FullPath).AppendLine();
            sb.Append("Requests: ").Append(t.Requests.ToString("N0")).AppendLine();
            sb.Append("Sent: ").Append(Converters.ByteSizeConverter.Format(t.BytesSent))
              .Append("   Received: ").Append(Converters.ByteSizeConverter.Format(t.BytesReceived)).AppendLine();
            sb.Append("Average: ").Append(Converters.DurationConverter.Format(t.AverageMs))
              .Append("   Slowest: ").Append(Converters.DurationConverter.Format(t.SlowestMs));
            if (t.Errors > 0 || t.Warnings > 0)
                sb.AppendLine().Append("Errors: ").Append(t.Errors).Append("   4xx: ").Append(t.Warnings);
            if (Node.ContentTypes.Count > 0)
                sb.AppendLine().Append("Types: ").Append(string.Join(", ", Node.ContentTypes.Take(6)));
            return sb.ToString();
        }
    }
}

/// <summary>One connection in the connection tree.</summary>
public sealed class ConnectionNodeViewModel
{
    public ConnectionNodeViewModel(ConnectionNode node)
    {
        Node = node;
        Children = new ObservableCollection<ConnectionStreamViewModel>(
            node.Streams.Select(s => new ConnectionStreamViewModel(s)));
    }

    public ConnectionNode Node { get; }
    public ObservableCollection<ConnectionStreamViewModel> Children { get; }

    public string Display => Node.Display;
    public string Glyph => Node.HasConcurrentStreams ? "⇉" : Node.IsMultiplexed ? "⇄" : "→";

    public string Detail
    {
        get
        {
            var sb = new System.Text.StringBuilder();
            if (!string.IsNullOrEmpty(Node.RemoteAddress)) sb.Append(Node.RemoteAddress).Append("   ");
            if (!string.IsNullOrEmpty(Node.ProcessName))
                sb.Append(Node.ProcessName).Append(" (").Append(Node.ProcessId).Append(")").Append("   ");
            sb.Append(Converters.ByteSizeConverter.Format(Node.Totals.TotalBytes));
            if (Node.SpanMs > 0) sb.Append("   over ").Append(Converters.DurationConverter.Format(Node.SpanMs));
            return sb.ToString();
        }
    }

    public string Tooltip => Node.HasConcurrentStreams
        ? "Requests on this connection overlapped in time — genuine HTTP/2 multiplexing."
        : Node.IsMultiplexed
            ? "The connection was reused for several sequential requests (keep-alive)."
            : "A single request used this connection.";
}

/// <summary>One stream / transaction inside a connection.</summary>
public sealed class ConnectionStreamViewModel
{
    public ConnectionStreamViewModel(ConnectionStreamNode node) => Node = node;

    public ConnectionStreamNode Node { get; }
    public HttpSession Session => Node.Session;

    public string Display => Node.Label;
    public string StatusText => Session.StatusCode > 0 ? Session.StatusCode.ToString() : "—";
    public int StatusCode => Session.StatusCode;
    public string SizeText => Converters.ByteSizeConverter.Format(Session.ResponseBodySize);
    public string TimeText => Converters.DurationConverter.Format(Session.DurationMs);

    /// <summary>Empty leaf collection so one TreeView template can serve both levels.</summary>
    public ObservableCollection<ConnectionStreamViewModel> Children { get; } = new();
}

/// <summary>
/// Drives the Structure tab: a host/path hierarchy of the capture and a
/// connection/stream view showing how it was multiplexed.
/// </summary>
public sealed partial class StructureViewModel : ViewModelBase
{
    /// <summary>Supplies the sessions to fold; set by the main view model.</summary>
    public Func<IReadOnlyList<HttpSession>>? SessionSource { get; set; }

    /// <summary>Raised when the user picks a node, to reveal it in the capture grid.</summary>
    public event Action<HttpSession>? NavigateToSessionRequested;

    public ObservableCollection<StructureNodeViewModel> Roots { get; } = new();
    public ObservableCollection<ConnectionNodeViewModel> Connections { get; } = new();

    public string[] Modes { get; } = { "Site structure", "Connections" };

    [ObservableProperty] private int _modeIndex;
    [ObservableProperty] private bool _collapseIdentifiers = true;
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private bool _hasContent;

    public bool IsStructureMode => ModeIndex == 0;
    public bool IsConnectionMode => ModeIndex == 1;

    partial void OnModeIndexChanged(int value)
    {
        OnPropertyChanged(nameof(IsStructureMode));
        OnPropertyChanged(nameof(IsConnectionMode));
        Rebuild();
    }

    partial void OnCollapseIdentifiersChanged(bool value) => Rebuild();

    /// <summary>Recomputes both trees from the current capture.</summary>
    [RelayCommand]
    public void Rebuild()
    {
        var sessions = SessionSource?.Invoke() ?? Array.Empty<HttpSession>();

        Roots.Clear();
        Connections.Clear();

        if (sessions.Count == 0)
        {
            HasContent = false;
            Summary = "Nothing captured yet.";
            return;
        }

        if (IsStructureMode)
        {
            var root = StructureTreeBuilder.Build(sessions, CollapseIdentifiers);
            long max = root.Children.Count == 0 ? 0 : root.Children.Max(c => c.Subtree.TotalBytes);

            // The synthetic root is not shown; hosts are the top level.
            foreach (var host in root.Children)
                Roots.Add(new StructureNodeViewModel(host, max));

            int endpoints = StructureTreeBuilder.Flatten(root)
                .Count(n => n.Kind == StructureNodeKind.Endpoint);

            Summary = $"{root.Children.Count} host(s), {endpoints} distinct endpoint(s), " +
                      $"{root.Subtree.Requests:N0} request(s), " +
                      $"{Converters.ByteSizeConverter.Format(root.Subtree.TotalBytes)} transferred.";
        }
        else
        {
            var connections = StructureTreeBuilder.BuildConnections(sessions);
            foreach (var c in connections) Connections.Add(new ConnectionNodeViewModel(c));

            int reused = connections.Count(c => c.IsMultiplexed);
            int multiplexed = connections.Count(c => c.HasConcurrentStreams);
            Summary = $"{connections.Count} connection(s) carried {sessions.Count:N0} transaction(s) — " +
                      $"{reused} reused, {multiplexed} with genuinely concurrent streams.";
        }

        HasContent = Roots.Count > 0 || Connections.Count > 0;
    }

    /// <summary>Reveals the first session behind a structure node in the capture grid.</summary>
    [RelayCommand]
    private void OpenNode(StructureNodeViewModel? node)
    {
        if (node?.FirstSession is { } session) NavigateToSessionRequested?.Invoke(session);
    }

    /// <summary>Reveals a specific stream in the capture grid.</summary>
    [RelayCommand]
    private void OpenStream(ConnectionStreamViewModel? stream)
    {
        if (stream is not null) NavigateToSessionRequested?.Invoke(stream.Session);
    }
}
