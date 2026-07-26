using System.Net;
using System.Net.Sockets;
using HttpSpy.Core.Models;

namespace HttpSpy.Core.Proxy;

/// <summary>
/// Per-connection state shared by every transaction that travels over one client
/// socket: who opened it, where it came from, and a stable id.
/// </summary>
/// <remarks>
/// This exists so the UI can reconstruct which transactions shared a connection —
/// the HTTP/2 connection tree and the keep-alive reuse view both key off
/// <see cref="Id"/>. It also replaces the anonymous <c>(int pid, string name)</c>
/// tuple that used to be threaded through the pipeline, which had nowhere to put
/// the peer addresses.
/// </remarks>
public sealed class ClientConnection
{
    /// <summary>Unique for the lifetime of the process.</summary>
    public long Id { get; } = HttpSession.NextConnectionId();

    public int ProcessId { get; init; }
    public string ProcessName { get; init; } = string.Empty;

    /// <summary>Address the client connected from (usually loopback).</summary>
    public string ClientAddress { get; init; } = string.Empty;
    public int ClientPort { get; init; }

    /// <summary>
    /// Address of the origin server, filled in once the upstream connection is
    /// established. Reported per session so a load-balanced host shows which
    /// backend actually answered.
    /// </summary>
    public string RemoteAddress { get; set; } = string.Empty;

    /// <summary>When this connection was accepted.</summary>
    public DateTime OpenedAt { get; } = DateTime.Now;

    private int _sequence;

    /// <summary>Allocates the next 1-based position within this connection.</summary>
    public int NextSequence() => Interlocked.Increment(ref _sequence);

    /// <summary>How many transactions have been served on this connection.</summary>
    public int TransactionCount => Volatile.Read(ref _sequence);

    /// <summary>Builds the context for an accepted socket, resolving the owning process.</summary>
    public static ClientConnection Accept(TcpClient client, ProxyEngine engine)
    {
        string address = string.Empty;
        int port = 0;
        if (client.Client.RemoteEndPoint is IPEndPoint endpoint)
        {
            address = endpoint.Address.ToString();
            port = endpoint.Port;
        }

        var (pid, name) = engine.Options.ResolveProcess
            ? engine.ProcessResolver.Resolve(port)
            : (0, string.Empty);

        return new ClientConnection
        {
            ProcessId = pid,
            ProcessName = name,
            ClientAddress = address,
            ClientPort = port,
        };
    }

    /// <summary>Stamps a session with this connection's identity.</summary>
    public void Stamp(HttpSession session, int streamId = 0)
    {
        session.ConnectionId = Id;
        session.StreamId = streamId;
        session.ConnectionSequence = NextSequence();
        session.ProcessId = ProcessId;
        session.ProcessName = ProcessName;
        session.ClientAddress = ClientAddress;
        session.ClientPort = ClientPort;
        session.RemoteAddress = RemoteAddress;
    }
}
