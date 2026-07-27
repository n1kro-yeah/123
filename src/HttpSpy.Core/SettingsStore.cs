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

    /// <summary>
    /// Credentials for a chained proxy. Stored in plain text, like every other
    /// setting — the file is restricted to the owner on Unix, but treat it as
    /// you would any other developer credential on disk.
    /// </summary>
    public string? UpstreamProxyUser { get; set; }
    public string? UpstreamProxyPassword { get; set; }

    /// <summary>Client certificates presented to origins that ask for one.</summary>
    public List<ClientCertificateBinding> ClientCertificates { get; set; } = new();

    public List<string> TlsPassthroughHosts { get; set; } = new();

    /// <summary>"Dark" or "Light".</summary>
    public string Theme { get; set; } = "Dark";

    /// <summary>Interface language: "system", "en" or "ru".</summary>
    public string Language { get; set; } = "system";

    public bool AutoScroll { get; set; } = true;

    /// <summary>
    /// Snapshot the live capture periodically so an unclean exit is recoverable.
    /// </summary>
    public bool AutosaveEnabled { get; set; } = true;

    /// <summary>Seconds between autosave snapshots. Clamped when read back.</summary>
    public int AutosaveIntervalSeconds { get; set; } = 30;

    /// <summary>Denser grid rows, for fitting more of the capture on screen.</summary>
    public bool CompactRows { get; set; }

    /// <summary>
    /// Persisted column order and width, one entry per column as
    /// <c>tag|displayIndex|width</c>. Kept separate from the visibility lists
    /// because a column can be hidden and still have a remembered position.
    /// </summary>
    public List<string> ColumnLayout { get; set; } = new();

    /// <summary>Persisted visibility of the optional session-grid columns.</summary>
    public List<string> HiddenColumns { get; set; } = new();

    /// <summary>
    /// Columns explicitly turned on. Needed alongside HiddenColumns because some
    /// columns default to off — "not hidden" and "shown" are not the same thing.
    /// </summary>
    public List<string> ShownColumns { get; set; } = new();

    /// <summary>Capture-level filters that drop traffic before it is recorded.</summary>
    public List<CaptureFilter> CaptureFilters { get; set; } = new();

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
        options.UpstreamProxyUser = string.IsNullOrWhiteSpace(UpstreamProxyUser) ? null : UpstreamProxyUser;
        options.UpstreamProxyPassword = UpstreamProxyPassword;
        options.ClientCertificates = ClientCertificates.Select(c => c.Clone()).ToList();
        options.TlsPassthroughHosts = new List<string>(TlsPassthroughHosts);
        options.CaptureFilters = CaptureFilters.Select(f => f.Clone()).ToList();
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
        UpstreamProxyUser = o.UpstreamProxyUser,
        UpstreamProxyPassword = o.UpstreamProxyPassword,
        ClientCertificates = o.ClientCertificates.Select(c => c.Clone()).ToList(),
        TlsPassthroughHosts = new List<string>(o.TlsPassthroughHosts),
        CaptureFilters = o.CaptureFilters.Select(f => f.Clone()).ToList(),
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
    /// Options, rules and filters bundled into one portable document, so a
    /// working configuration can be shared with a colleague or committed
    /// alongside a project rather than re-created by hand on each machine.
    /// </summary>
    private sealed class SettingsBundle
    {
        public int Version { get; set; } = 1;
        public string? ExportedAt { get; set; }
        public HttpSpySettings? Settings { get; set; }
        public List<Rule>? Rules { get; set; }
    }

    public static string ExportBundle(HttpSpySettings settings, IEnumerable<Rule> rules) =>
        JsonSerializer.Serialize(new SettingsBundle
        {
            ExportedAt = DateTimeOffset.Now.ToString("O"),
            Settings = settings,
            Rules = rules.ToList(),
        }, JsonOptions);

    /// <summary>
    /// Parses an exported bundle. Accepts a bare settings object too, so a
    /// hand-written or older settings.json can be imported directly.
    /// </summary>
    public static (HttpSpySettings Settings, List<Rule> Rules) ImportBundle(string json)
    {
        var bundle = JsonSerializer.Deserialize<SettingsBundle>(json, JsonOptions);
        if (bundle?.Settings is not null)
            return (bundle.Settings, bundle.Rules ?? new List<Rule>());

        var bare = JsonSerializer.Deserialize<HttpSpySettings>(json, JsonOptions)
                   ?? throw new InvalidDataException("The file does not contain HttpSpy settings.");
        return (bare, new List<Rule>());
    }

    /// <summary>
    /// Writes via a temporary file and a rename. Settings are saved on every
    /// option toggle and on shutdown; a crash part-way through a direct write
    /// would leave truncated JSON that the next launch silently discards.
    /// </summary>
    /// <summary>
    /// Keeps the settings file owner-readable on Unix. It can hold an upstream
    /// proxy password and paths to client certificates, so it does not belong in
    /// a world-readable home directory.
    /// </summary>
    private static void RestrictToOwner(string path)
    {
        if (OperatingSystem.IsWindows()) return; // inherits the user profile ACL
        try { File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
        catch (Exception) { /* a filesystem that cannot express this is not fatal */ }
    }

    private static void WriteAtomic(string path, string contents)
    {
        System.IO.Directory.CreateDirectory(Directory);
        string temp = path + ".tmp";
        File.WriteAllText(temp, contents);
        RestrictToOwner(temp);
        if (File.Exists(path)) File.Replace(temp, path, destinationBackupFileName: null);
        else File.Move(temp, path);
    }
}
