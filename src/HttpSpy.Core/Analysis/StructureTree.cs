using System.Text;
using HttpSpy.Core.Models;

namespace HttpSpy.Core.Analysis;

// =============================================================================
//  HttpSpy — Structure and connection trees
// -----------------------------------------------------------------------------
//  The session grid answers "what happened, in order". These trees answer the
//  two questions the grid cannot:
//
//    * Structure tree — "what does this site actually consist of?"  Requests are
//      folded into a host → path hierarchy, and every node carries the totals of
//      its whole subtree, so one glance shows which area of an application is
//      responsible for the bytes, the time, or the errors.
//
//    * Connection tree — "how was this multiplexed?"  Transactions are grouped by
//      the transport connection they travelled on and, for HTTP/2, by stream.
//      This is what makes connection reuse, head-of-line blocking and h2
//      multiplexing visible rather than a matter of faith.
//
//  Both are pure functions over a snapshot of sessions: no engine coupling, no
//  UI types, safe to build on a background thread.
// =============================================================================

/// <summary>What a tree node stands for.</summary>
public enum StructureNodeKind
{
    /// <summary>The synthetic root holding every host.</summary>
    Root,
    /// <summary>A host (authority), e.g. <c>api.example.com</c>.</summary>
    Host,
    /// <summary>An intermediate path segment, e.g. <c>/v1</c>.</summary>
    Folder,
    /// <summary>A leaf that at least one request targeted directly.</summary>
    Endpoint,
}

/// <summary>
/// Aggregated totals for a node and everything beneath it. Kept as a separate
/// type so a node can expose both its own and its subtree's figures without
/// duplicating a dozen properties.
/// </summary>
public sealed class TrafficTotals
{
    public int Requests { get; private set; }
    public long BytesSent { get; private set; }
    public long BytesReceived { get; private set; }

    /// <summary>Decoded response body bytes, i.e. what the application saw.</summary>
    public long BodyBytes { get; private set; }

    public int Errors { get; private set; }
    public int Warnings { get; private set; }
    public double TotalMs { get; private set; }
    public double SlowestMs { get; private set; }

    /// <summary>Mean wall-clock duration across the requests counted here.</summary>
    public double AverageMs => Requests == 0 ? 0 : TotalMs / Requests;

    /// <summary>All bytes that crossed the wire in either direction.</summary>
    public long TotalBytes => BytesSent + BytesReceived;

    public void Add(HttpSession s)
    {
        Requests++;
        BytesSent += s.BytesSent;
        BytesReceived += s.BytesReceived;
        BodyBytes += s.ResponseBodySize;

        if (s.Error is not null || s.StatusCode >= 500) Errors++;
        else if (s.StatusCode >= 400) Warnings++;

        double ms = s.DurationMs;
        if (ms > 0)
        {
            TotalMs += ms;
            if (ms > SlowestMs) SlowestMs = ms;
        }
    }

    public void Absorb(TrafficTotals other)
    {
        Requests += other.Requests;
        BytesSent += other.BytesSent;
        BytesReceived += other.BytesReceived;
        BodyBytes += other.BodyBytes;
        Errors += other.Errors;
        Warnings += other.Warnings;
        TotalMs += other.TotalMs;
        if (other.SlowestMs > SlowestMs) SlowestMs = other.SlowestMs;
    }
}

/// <summary>A node in the host/path structure tree.</summary>
public sealed class StructureNode
{
    public required string Name { get; init; }
    public required StructureNodeKind Kind { get; init; }

    /// <summary>Full path from the root, e.g. <c>api.example.com/v1/users</c>.</summary>
    public required string FullPath { get; init; }

    public StructureNode? Parent { get; set; }
    public List<StructureNode> Children { get; } = new();

    /// <summary>Totals for requests that targeted this node itself.</summary>
    public TrafficTotals Own { get; } = new();

    /// <summary>Totals for this node plus every descendant.</summary>
    public TrafficTotals Subtree { get; } = new();

    /// <summary>Sessions that targeted this node directly, newest last.</summary>
    public List<HttpSession> Sessions { get; } = new();

    /// <summary>Distinct HTTP methods seen on this node, for the label.</summary>
    public SortedSet<string> Methods { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Distinct response media types seen on this node.</summary>
    public SortedSet<string> ContentTypes { get; } = new(StringComparer.OrdinalIgnoreCase);

    public bool HasChildren => Children.Count > 0;

    /// <summary>Depth below the root; hosts are 0.</summary>
    public int Depth => Parent is null or { Kind: StructureNodeKind.Root } ? 0 : Parent.Depth + 1;

    /// <summary>
    /// The label shown in the tree: the segment plus a request count, and for a
    /// leaf the methods that were used against it.
    /// </summary>
    public string Display
    {
        get
        {
            var sb = new StringBuilder(Name);
            if (Kind == StructureNodeKind.Endpoint && Methods.Count > 0)
                sb.Append("   [").Append(string.Join('/', Methods)).Append(']');
            return sb.ToString();
        }
    }

    public override string ToString() => $"{FullPath} ({Subtree.Requests} req)";
}

/// <summary>A single HTTP/2 stream, or one HTTP/1.x transaction on a connection.</summary>
public sealed class ConnectionStreamNode
{
    public required HttpSession Session { get; init; }

    /// <summary>HTTP/2 stream id, or 0 for HTTP/1.x.</summary>
    public int StreamId => Session.StreamId;

    /// <summary>Position within the connection, 1-based.</summary>
    public int Sequence => Session.ConnectionSequence;

    public string Label => StreamId > 0
        ? $"stream {StreamId}   {Session.Method} {Session.Path}"
        : $"#{Sequence}   {Session.Method} {Session.Path}";
}

/// <summary>One transport connection and everything carried over it.</summary>
public sealed class ConnectionNode
{
    public required long ConnectionId { get; init; }
    public required string Host { get; init; }
    public string ProcessName { get; init; } = string.Empty;
    public int ProcessId { get; init; }
    public string RemoteAddress { get; init; } = string.Empty;
    public string Protocol { get; init; } = "HTTP/1.1";
    public bool IsTls { get; init; }
    public DateTime OpenedAt { get; init; }

    public List<ConnectionStreamNode> Streams { get; } = new();
    public TrafficTotals Totals { get; } = new();

    /// <summary>True when more than one transaction shared this connection.</summary>
    public bool IsMultiplexed => Streams.Count > 1;

    /// <summary>
    /// True when transactions on this connection actually overlapped in time —
    /// real h2 multiplexing, as opposed to sequential keep-alive reuse.
    /// </summary>
    public bool HasConcurrentStreams { get; set; }

    /// <summary>Wall-clock span from the first request to the last response.</summary>
    public double SpanMs { get; set; }

    public string Display
    {
        get
        {
            var sb = new StringBuilder();
            sb.Append(IsTls ? "🔒 " : string.Empty).Append(Host);
            sb.Append("   ").Append(Protocol);
            sb.Append("   ").Append(Streams.Count).Append(Streams.Count == 1 ? " request" : " requests");
            if (HasConcurrentStreams) sb.Append("  · multiplexed");
            else if (IsMultiplexed) sb.Append("  · reused");
            return sb.ToString();
        }
    }
}

/// <summary>Builds the structure and connection trees from a capture.</summary>
public static class StructureTreeBuilder
{
    /// <summary>
    /// Folds sessions into a host → path hierarchy. Numeric and UUID-looking path
    /// segments are collapsed so <c>/users/1</c> and <c>/users/2</c> land on one
    /// endpoint node instead of producing a node per identifier — otherwise a
    /// REST API renders as an unreadable fan of thousands of leaves.
    /// </summary>
    public static StructureNode Build(IEnumerable<HttpSession> sessions, bool collapseIdentifiers = true)
    {
        var root = new StructureNode
        {
            Name = "All hosts",
            Kind = StructureNodeKind.Root,
            FullPath = string.Empty,
        };

        var hosts = new Dictionary<string, StructureNode>(StringComparer.OrdinalIgnoreCase);

        foreach (var session in sessions)
        {
            if (session is null) continue;

            string host = string.IsNullOrEmpty(session.Host) ? "(unknown host)" : session.Host;
            if (!hosts.TryGetValue(host, out var hostNode))
            {
                hostNode = new StructureNode
                {
                    Name = host,
                    Kind = StructureNodeKind.Host,
                    FullPath = host,
                    Parent = root,
                };
                hosts[host] = hostNode;
                root.Children.Add(hostNode);
            }

            var target = Descend(hostNode, session, collapseIdentifiers);
            target.Own.Add(session);
            target.Sessions.Add(session);
            target.Methods.Add(session.Method);
            var media = session.ResponseContentTypeShort;
            if (!string.IsNullOrEmpty(media)) target.ContentTypes.Add(media);
        }

        Rollup(root);
        Sort(root);
        return root;
    }

    /// <summary>Walks (creating as needed) the path nodes down to a session's endpoint.</summary>
    private static StructureNode Descend(StructureNode hostNode, HttpSession session, bool collapse)
    {
        var segments = SplitPath(session.Path, collapse);
        var current = hostNode;

        for (int i = 0; i < segments.Count; i++)
        {
            bool last = i == segments.Count - 1;
            string name = segments[i];
            string fullPath = current.FullPath + "/" + name;

            // A node that was created as a folder becomes an endpoint too if a
            // request targets it directly; the kind reflects the deepest use.
            var child = current.Children.FirstOrDefault(c =>
                string.Equals(c.Name, name, StringComparison.Ordinal));

            if (child is null)
            {
                child = new StructureNode
                {
                    Name = name,
                    Kind = last ? StructureNodeKind.Endpoint : StructureNodeKind.Folder,
                    FullPath = fullPath,
                    Parent = current,
                };
                current.Children.Add(child);
            }
            current = child;
        }

        return current;
    }

    /// <summary>
    /// Splits a URL path into display segments. The root path renders as a single
    /// "/" node so a site's landing page has somewhere to live.
    /// </summary>
    private static List<string> SplitPath(string path, bool collapse)
    {
        var raw = (path ?? "/").Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (raw.Length == 0) return new List<string> { "/" };

        var result = new List<string>(raw.Length);
        foreach (var segment in raw)
            result.Add(collapse ? CollapseSegment(segment) : segment);
        return result;
    }

    /// <summary>Replaces identifier-shaped segments with a placeholder.</summary>
    internal static string CollapseSegment(string segment)
    {
        if (segment.Length == 0) return segment;
        if (segment.All(char.IsDigit)) return "{id}";
        if (Guid.TryParse(segment, out _)) return "{uuid}";
        if (segment.Length >= 16 && segment.All(Uri.IsHexDigit)) return "{hash}";
        return segment;
    }

    /// <summary>Propagates each node's own totals up through its ancestors.</summary>
    private static void Rollup(StructureNode node)
    {
        node.Subtree.Absorb(node.Own);
        foreach (var child in node.Children)
        {
            Rollup(child);
            node.Subtree.Absorb(child.Subtree);
        }
    }

    /// <summary>Orders children by traffic volume so the heaviest branch is first.</summary>
    private static void Sort(StructureNode node)
    {
        node.Children.Sort((a, b) =>
        {
            int byBytes = b.Subtree.TotalBytes.CompareTo(a.Subtree.TotalBytes);
            if (byBytes != 0) return byBytes;
            int byCount = b.Subtree.Requests.CompareTo(a.Subtree.Requests);
            if (byCount != 0) return byCount;
            return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        });
        foreach (var child in node.Children) Sort(child);
    }

    /// <summary>
    /// Groups sessions by the transport connection they used. Connections that
    /// carried overlapping transactions are marked as genuinely multiplexed,
    /// which distinguishes HTTP/2 from sequential keep-alive reuse.
    /// </summary>
    public static List<ConnectionNode> BuildConnections(IEnumerable<HttpSession> sessions)
    {
        var byConnection = new Dictionary<long, ConnectionNode>();

        foreach (var session in sessions)
        {
            if (session is null) continue;
            // Sessions loaded from an older capture, or produced by the Submitter,
            // have no connection identity; give each its own bucket rather than
            // collapsing them all into a bogus "connection 0".
            long key = session.ConnectionId != 0 ? session.ConnectionId : -session.Index;

            if (!byConnection.TryGetValue(key, out var node))
            {
                node = new ConnectionNode
                {
                    ConnectionId = session.ConnectionId,
                    Host = string.IsNullOrEmpty(session.Host) ? "(unknown host)" : session.Host,
                    ProcessName = session.ProcessName,
                    ProcessId = session.ProcessId,
                    RemoteAddress = session.RemoteAddress,
                    Protocol = session.HttpVersion,
                    IsTls = session.IsTls,
                    OpenedAt = session.StartTime,
                };
                byConnection[key] = node;
            }

            node.Streams.Add(new ConnectionStreamNode { Session = session });
            node.Totals.Add(session);
        }

        foreach (var node in byConnection.Values)
        {
            node.Streams.Sort((a, b) =>
            {
                if (a.StreamId != b.StreamId && a.StreamId > 0 && b.StreamId > 0)
                    return a.StreamId.CompareTo(b.StreamId);
                return a.Sequence.CompareTo(b.Sequence);
            });

            node.HasConcurrentStreams = HasOverlap(node.Streams);
            node.SpanMs = Span(node.Streams);
        }

        return byConnection.Values
            .OrderByDescending(c => c.Streams.Count)
            .ThenBy(c => c.OpenedAt)
            .ToList();
    }

    /// <summary>True when any two transactions on the connection overlapped in time.</summary>
    private static bool HasOverlap(List<ConnectionStreamNode> streams)
    {
        if (streams.Count < 2) return false;

        var intervals = streams
            .Select(s => (Start: s.Session.StartTime, End: s.Session.EndTime ?? s.Session.StartTime))
            .OrderBy(i => i.Start)
            .ToList();

        for (int i = 1; i < intervals.Count; i++)
            if (intervals[i].Start < intervals[i - 1].End) return true;

        return false;
    }

    private static double Span(List<ConnectionStreamNode> streams)
    {
        if (streams.Count == 0) return 0;
        var first = streams.Min(s => s.Session.StartTime);
        var last = streams.Max(s => s.Session.EndTime ?? s.Session.StartTime);
        return (last - first).TotalMilliseconds;
    }

    /// <summary>Depth-first enumeration of a subtree, the node itself first.</summary>
    public static IEnumerable<StructureNode> Flatten(StructureNode node)
    {
        yield return node;
        foreach (var child in node.Children)
            foreach (var descendant in Flatten(child))
                yield return descendant;
    }
}
