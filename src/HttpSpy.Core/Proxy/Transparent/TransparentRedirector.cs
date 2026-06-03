using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Runtime.InteropServices;

namespace HttpSpy.Core.Proxy.Transparent;

/// <summary>
/// Optional Windows-only transparent capture using WinDivert, the HTTP Debugger
/// "no proxy configuration" analog. Outbound TCP to ports 80/443 is diverted to
/// the local transparent listener by rewriting the destination on the wire; the
/// original destination is remembered (keyed by the connection's source port) so
/// the proxy can recover the real host and perform MITM. Response packets from
/// the listener are rewritten back to appear to come from the origin.
///
/// EXPERIMENTAL: this path requires real Windows, Administrator rights and the
/// signed WinDivert driver (WinDivert.dll + WinDivert64.sys next to the exe).
/// It cannot be exercised in the Linux build/test environment, so it is disabled
/// by default and guarded behind <see cref="OperatingSystem.IsWindows"/>.
/// </summary>
public sealed class TransparentRedirector : IDisposable
{
    private readonly int _listenPort;
    private readonly ConcurrentDictionary<ushort, (uint Ip, ushort Port)> _originalDst = new();
    private IntPtr _handle = WinDivertInterop.InvalidHandle;
    private Thread? _thread;
    private volatile bool _running;

    public TransparentRedirector(int transparentListenPort) => _listenPort = transparentListenPort;

    /// <summary>Resolves the original destination for a connection by its source port.</summary>
    public bool TryGetOriginalDestination(int sourcePort, out IPAddress host, out int port)
    {
        if (_originalDst.TryGetValue((ushort)sourcePort, out var d))
        {
            host = new IPAddress(BitConverter.GetBytes(d.Ip));
            port = d.Port;
            return true;
        }
        host = IPAddress.None;
        port = 0;
        return false;
    }

    public void Start()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Transparent capture requires Windows + WinDivert.");

        // Divert outbound TCP to 80/443, plus the response leg coming from our
        // listener. Skip our own re-injected packets to avoid a capture loop.
        string filter =
            $"outbound and ip and tcp and not impostor and " +
            $"((tcp.DstPort == 80 or tcp.DstPort == 443) or tcp.SrcPort == {_listenPort})";

        _handle = WinDivertInterop.WinDivertOpen(filter, WinDivertInterop.Layer.Network, 0,
            WinDivertInterop.Flag.None);
        if (_handle == WinDivertInterop.InvalidHandle)
            throw new InvalidOperationException(
                $"WinDivertOpen failed (error {Marshal.GetLastWin32Error()}). Run as Administrator with the WinDivert driver installed.");

        _running = true;
        _thread = new Thread(CaptureLoop) { IsBackground = true, Name = "WinDivertRedirect" };
        _thread.Start();
    }

    private void CaptureLoop()
    {
        var packet = new byte[65535];
        var addr = default(WinDivertInterop.Address);
        while (_running)
        {
            if (!WinDivertInterop.WinDivertRecv(_handle, packet, (uint)packet.Length, out uint len, ref addr))
                continue;
            try { Rewrite(packet, (int)len, ref addr); }
            catch { /* malformed packet — pass through unchanged */ }
            WinDivertInterop.WinDivertHelperCalcChecksums(packet, len, ref addr, 0);
            WinDivertInterop.WinDivertSend(_handle, packet, len, out _, ref addr);
        }
    }

    private void Rewrite(byte[] packet, int len, ref WinDivertInterop.Address addr)
    {
        if (addr.IPv6 || len < 20) return;
        int ihl = (packet[0] & 0x0F) * 4;
        if (packet[9] != 6 || len < ihl + 20) return; // not TCP

        var ipSrc = packet.AsSpan(12, 4);
        var ipDst = packet.AsSpan(16, 4);
        ushort srcPort = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(ihl, 2));
        ushort dstPort = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(ihl + 2, 2));

        if (srcPort == _listenPort)
        {
            // Response leg (listener -> app): restore the origin's address/port so
            // the application's socket accepts the reply.
            if (_originalDst.TryGetValue(dstPort, out var origin))
            {
                BinaryPrimitives.WriteUInt32LittleEndian(ipSrc, origin.Ip);
                BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(ihl, 2), origin.Port);
            }
        }
        else if (dstPort is 80 or 443)
        {
            // Request leg (app -> server): remember the destination and redirect to
            // the local listener on this same host.
            uint dstIp = BinaryPrimitives.ReadUInt32LittleEndian(ipDst);
            _originalDst[srcPort] = (dstIp, dstPort);
            // Loop the packet back to this machine's own address on the listener port.
            ipSrc.CopyTo(ipDst);
            BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(ihl + 2, 2), (ushort)_listenPort);
        }
    }

    public void Dispose()
    {
        _running = false;
        if (_handle != WinDivertInterop.InvalidHandle && OperatingSystem.IsWindows())
        {
            WinDivertInterop.WinDivertClose(_handle);
            _handle = WinDivertInterop.InvalidHandle;
        }
    }
}
