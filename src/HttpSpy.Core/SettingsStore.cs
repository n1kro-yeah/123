using System.Text.Json;
using System.Text.Json.Serialization;
using HttpSpy.Core.Models;
using HttpSpy.Core.Proxy;
using HttpSpy.Core.Rules;

namespace HttpSpy.Core;

/// <summary>
/// User-level application settings that survive between runs (capture options,
/// network simulation, theme, last layout). Persisted as JSON under
/// <c>%APPDATA%\HttpSpy\settings.json</c> alongside the rule set.
/// </summary>
public sealed class HttpSpySettings
{
    public int ListenPort { get; set; } = 8888;
    public bool DecryptHttps { get; set; } = true;
    public bool SetSystemProxy { get; set; } = true;
    public bool CaptureWebSockets { get; set; } = true;
    public bool ResolveProcess { get; set; } = true;
    public bool EnableHttp2 { get; set; } = true;
    public bool TransparentCapture { get; set; }
    public int TransparentListenPort { get; set; } = 8889;

    public bool ThrottleEnabled { get; set; }
    public int ThrottleKbps { get; set; }
    public int ExtraLatencyMs { get; set; }

    public string? UpstreamProxyHost { get; set; }
    public int UpstreamProxyPort { get; set; }

    public List<string> TlsPassthroughHosts { get; set; } = new();

    /// <summary>"Dark" or "Light".</summary>
    public string Theme { get; set; } = "Dark";

    public bool AutoScroll { get; set; } = true;

    /// <summary>Persisted visibility of the optional session-grid columns.</summary>
    public List<string> HiddenColumns { get; set; } = new();

    /// <summary>Persisted display filters (HTTP Debugger "Set Filters" analog).</summary>
    public List<DisplayFilter> Filters { get; set; } = new();

    public void ApplyTo(ProxyOptions options)
    {
        options.ListenPort = ListenPort;
        options.DecryptHttps = DecryptHttps;
        options.SetSystemProxy = SetSystemProxy;
        options.CaptureWebSockets = CaptureWebSockets;
        options.ResolveProcess = ResolveProcess;
        options.EnableHttp2 = EnableHttp2;
        options.TransparentCapture = TransparentCapture;
        options.TransparentListenPort = TransparentListenPort;
        options.ThrottleEnabled = ThrottleEnabled;
        options.ThrottleKbps = ThrottleKbps;
        options.ExtraLatencyMs = ExtraLatencyMs;
        options.UpstreamProxyHost = string.IsNullOrWhiteSpace(UpstreamProxyHost) ? null : UpstreamProxyHost;
        options.UpstreamProxyPort = UpstreamProxyPort;
        options.TlsPassthroughHosts = new List<string>(TlsPassthroughHosts);
    }

    public static HttpSpySettings FromOptions(ProxyOptions o) => new()
    {
        ListenPort = o.ListenPort,
        DecryptHttps = o.DecryptHttps,
        SetSystemProxy = o.SetSystemProxy,
        CaptureWebSockets = o.CaptureWebSockets,
        ResolveProcess = o.ResolveProcess,
        EnableHttp2 = o.EnableHttp2,
        TransparentCapture = o.TransparentCapture,
        TransparentListenPort = o.TransparentListenPort,
        ThrottleEnabled = o.ThrottleEnabled,
        ThrottleKbps = o.ThrottleKbps,
        ExtraLatencyMs = o.ExtraLatencyMs,
        UpstreamProxyHost = o.UpstreamProxyHost,
        UpstreamProxyPort = o.UpstreamProxyPort,
        TlsPassthroughHosts = new List<string>(o.TlsPassthroughHosts),
    };
}

/// <summary>
/// A single display filter row (HTTP Debugger "Set Filters"). When at least one
/// "show-only" filter is active, a session must match one of them; any matched
/// "hide" filter removes the session from the grid.
/// </summary>
public sealed class DisplayFilter
{
    public bool Enabled { get; set; } = true;
    /// <summary>URL, Host, Method, Status, ContentType, Process, AnyHeader or Body.</summary>
    public string Field { get; set; } = "URL";
    /// <summary>true = hide matching rows; false = show only matching rows.</summary>
    public bool Hide { get; set; }
    public string Pattern { get; set; } = string.Empty;
    public bool UseRegex { get; set; }

    public DisplayFilter Clone() => new()
        { Enabled = Enabled, Field = Field, Hide = Hide, Pattern = Pattern, UseRegex = UseRegex };
}

/// <summary>Loads and saves <see cref="HttpSpySettings"/> and the persisted rule set.</summary>
public static class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string Directory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "HttpSpy");

    public static string SettingsPath => Path.Combine(Directory, "settings.json");
    public static string RulesPath => Path.Combine(Directory, "rules.json");

    public static HttpSpySettings LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsPath))
                return JsonSerializer.Deserialize<HttpSpySettings>(File.ReadAllText(SettingsPath), JsonOptions)
                       ?? new HttpSpySettings();
        }
        catch { /* fall back to defaults */ }
        return new HttpSpySettings();
    }

    public static void SaveSettings(HttpSpySettings settings) =>
        WriteAtomic(SettingsPath, JsonSerializer.Serialize(settings, JsonOptions));

    public static List<Rule> LoadRules()
    {
        try
        {
            if (File.Exists(RulesPath))
                return JsonSerializer.Deserialize<List<Rule>>(File.ReadAllText(RulesPath), JsonOptions)
                       ?? new List<Rule>();
        }
        catch { /* ignore corrupt rule file */ }
        return new List<Rule>();
    }

    public static void SaveRules(IEnumerable<Rule> rules) =>
        WriteAtomic(RulesPath, JsonSerializer.Serialize(rules.ToList(), JsonOptions));

    /// <summary>
    /// Writes via a temporary file and a rename. Settings are saved on every
    /// option toggle and on shutdown; a crash part-way through a direct write
    /// would leave truncated JSON that the next launch silently discards.
    /// </summary>
    private static void WriteAtomic(string path, string contents)
    {
        System.IO.Directory.CreateDirectory(Directory);
        string temp = path + ".tmp";
        File.WriteAllText(temp, contents);
        if (File.Exists(path)) File.Replace(temp, path, destinationBackupFileName: null);
        else File.Move(temp, path);
    }
}
