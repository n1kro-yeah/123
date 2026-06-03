using System.Runtime.InteropServices;

namespace HttpSpy.Core.Proxy.Transparent;

/// <summary>
/// P/Invoke bindings for WinDivert (https://reqrypt.org/windivert.html), the
/// signed user-mode packet-capture/injection driver. This lets HttpSpy redirect
/// outbound TCP to the local proxy with no system-proxy configuration ("driver"
/// capture, like HTTP Debugger). Windows-only: the DLL is never loaded on other
/// platforms because the redirector guards every call with
/// <see cref="OperatingSystem.IsWindows"/>.
/// </summary>
internal static class WinDivertInterop
{
    private const string Dll = "WinDivert.dll";

    public enum Layer { Network = 0, NetworkForward = 1, Flow = 2, Socket = 3, Reflect = 4 }

    [Flags]
    public enum Flag : ulong
    {
        None = 0,
        Sniff = 1,
        Drop = 2,
        RecvOnly = 4,
        SendOnly = 8,
        NoInstall = 16,
        Fragments = 32,
    }

    /// <summary>
    /// WINDIVERT_ADDRESS, laid out to match the native 64-byte structure.
    /// The native struct packs Layer/Event/flag bits into one 32-bit word; we
    /// expose the individual bytes and read the flag bits via helpers.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct Address
    {
        public long Timestamp;     // offset 0
        public byte Layer;         // offset 8  (Layer:8)
        public byte Event;         // offset 9  (Event:8)
        public byte Flags;         // offset 10 (Sniffed:1,Outbound:1,Loopback:1,Impostor:1,IPv6:1,...)
        public byte Reserved1;     // offset 11
        public uint Reserved2;     // offset 12
        // 40-byte union (WINDIVERT_DATA_*). For the network layer the first two
        // dwords are IfIdx/SubIfIdx; the rest is reserved padding.
        public uint IfIdx;
        public uint SubIfIdx;
        public ulong Pad0;
        public ulong Pad1;
        public ulong Pad2;
        public ulong Pad3;

        public readonly bool Outbound => (Flags & 0x02) != 0;
        public readonly bool Loopback => (Flags & 0x04) != 0;
        public readonly bool Impostor => (Flags & 0x08) != 0;
        public readonly bool IPv6 => (Flags & 0x10) != 0;
    }

    [DllImport(Dll, SetLastError = true, CharSet = CharSet.Ansi)]
    public static extern IntPtr WinDivertOpen(string filter, Layer layer, short priority, Flag flags);

    [DllImport(Dll, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool WinDivertRecv(IntPtr handle, byte[] packet, uint packetLen,
        out uint readLen, ref Address addr);

    [DllImport(Dll, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool WinDivertSend(IntPtr handle, byte[] packet, uint packetLen,
        out uint sendLen, ref Address addr);

    [DllImport(Dll, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool WinDivertClose(IntPtr handle);

    [DllImport(Dll, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool WinDivertHelperCalcChecksums(byte[] packet, uint packetLen,
        ref Address addr, ulong flags);

    public static readonly IntPtr InvalidHandle = new(-1);
}
