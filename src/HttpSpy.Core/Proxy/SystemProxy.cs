using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace HttpSpy.Core.Proxy;

/// <summary>
/// Configures the Windows (WinINET) system proxy so traffic from browsers and
/// most desktop apps flows through HttpSpy. Writing the registry alone is not
/// enough — WinINET must be told to refresh via InternetSetOption.
/// </summary>
[SupportedOSPlatform("windows")]
public static class SystemProxy
{
    private const string RegPath = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";

    public static void Enable(string host, int port)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RegPath, writable: true)
                        ?? Registry.CurrentUser.CreateSubKey(RegPath);
        key.SetValue("ProxyEnable", 1, RegistryValueKind.DWord);
        key.SetValue("ProxyServer", $"{host}:{port}", RegistryValueKind.String);
        // Bypass local addresses so the loopback listener itself is reachable.
        key.SetValue("ProxyOverride", "<local>", RegistryValueKind.String);
        Refresh();
    }

    public static void Disable()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RegPath, writable: true);
        if (key is null) return;
        key.SetValue("ProxyEnable", 0, RegistryValueKind.DWord);
        Refresh();
    }

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RegPath);
        return key?.GetValue("ProxyEnable") is int v && v != 0;
    }

    private static void Refresh()
    {
        InternetSetOption(IntPtr.Zero, INTERNET_OPTION_SETTINGS_CHANGED, IntPtr.Zero, 0);
        InternetSetOption(IntPtr.Zero, INTERNET_OPTION_REFRESH, IntPtr.Zero, 0);
    }

    private const int INTERNET_OPTION_SETTINGS_CHANGED = 39;
    private const int INTERNET_OPTION_REFRESH = 37;

    [DllImport("wininet.dll", SetLastError = true)]
    private static extern bool InternetSetOption(IntPtr hInternet, int dwOption, IntPtr lpBuffer, int dwBufferLength);
}
