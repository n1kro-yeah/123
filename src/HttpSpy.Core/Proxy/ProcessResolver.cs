using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace HttpSpy.Core.Proxy;

/// <summary>
/// Resolves the Windows process that owns a given local TCP port, so each
/// captured session can be attributed to the application that made it. Uses the
/// IP Helper API (GetExtendedTcpTable). On non-Windows platforms this is a
/// best-effort no-op.
/// </summary>
public sealed class ProcessResolver
{
    private readonly Dictionary<int, string> _nameCache = new();
    private readonly object _gate = new();

    public (int pid, string name) Resolve(int localPort)
    {
        if (!OperatingSystem.IsWindows()) return (0, string.Empty);
        try
        {
            int pid = LookupPidWindows(localPort);
            if (pid <= 0) return (0, string.Empty);
            return (pid, ResolveName(pid));
        }
        catch
        {
            return (0, string.Empty);
        }
    }

    private string ResolveName(int pid)
    {
        lock (_gate)
        {
            if (_nameCache.TryGetValue(pid, out var cached)) return cached;
        }

        string name;
        try
        {
            using var proc = Process.GetProcessById(pid);
            name = proc.ProcessName;
        }
        catch
        {
            name = $"pid {pid}";
        }

        lock (_gate) _nameCache[pid] = name;
        return name;
    }

    // ---- Windows IP Helper interop ------------------------------------------

    [SupportedOSPlatform("windows")]
    private static int LookupPidWindows(int localPort)
    {
        const int AF_INET = 2;
        int bufferSize = 0;
        uint result = GetExtendedTcpTable(IntPtr.Zero, ref bufferSize, false, AF_INET,
            TCP_TABLE_CLASS.TCP_TABLE_OWNER_PID_ALL, 0);

        IntPtr buffer = Marshal.AllocHGlobal(bufferSize);
        try
        {
            result = GetExtendedTcpTable(buffer, ref bufferSize, false, AF_INET,
                TCP_TABLE_CLASS.TCP_TABLE_OWNER_PID_ALL, 0);
            if (result != 0) return 0;

            int rowCount = Marshal.ReadInt32(buffer);
            IntPtr rowPtr = buffer + 4;
            int rowSize = Marshal.SizeOf<MIB_TCPROW_OWNER_PID>();

            for (int i = 0; i < rowCount; i++)
            {
                var row = Marshal.PtrToStructure<MIB_TCPROW_OWNER_PID>(rowPtr);
                int port = (int)(((row.localPort & 0xFF) << 8) | ((row.localPort >> 8) & 0xFF));
                if (port == localPort) return (int)row.owningPid;
                rowPtr += rowSize;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
        return 0;
    }

    private enum TCP_TABLE_CLASS
    {
        TCP_TABLE_OWNER_PID_ALL = 5
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MIB_TCPROW_OWNER_PID
    {
        public uint state;
        public uint localAddr;
        public uint localPort;
        public uint remoteAddr;
        public uint remotePort;
        public uint owningPid;
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(IntPtr pTcpTable, ref int dwOutBufLen,
        bool sort, int ipVersion, TCP_TABLE_CLASS tblClass, uint reserved);
}
