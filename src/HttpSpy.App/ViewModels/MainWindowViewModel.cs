using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Styling;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HttpSpy.App.Services;
using HttpSpy.Core;
using HttpSpy.Core.Analysis;
using HttpSpy.Core.Export;
using HttpSpy.Core.Models;
using HttpSpy.Core.Proxy;
using HttpSpy.Core.Rules;

namespace HttpSpy.App.ViewModels;

/// <summary>
/// The application's root view model. Owns the capture engine, the session
/// collection (with sorting + filtering), the inspector, dashboard, submitter,
/// rules and breakpoint queue, and exposes every toolbar/menu command.
/// </summary>
public sealed partial class MainWindowViewModel : ViewModelBase
{
    private readonly ProxyEngine _engine;
    private readonly Dictionary<Guid, SessionViewModel> _index = new();
    private readonly HttpSpySettings _settings;
    private readonly DispatcherTimer _statsTimer;
    private readonly DispatcherTimer _refreshTimer;
    private bool _refreshPending;

    public IDialogService? Dialogs { get; set; }

    /// <summary>Raised when a fresh session is appended (used by the view for auto-scroll).</summary>
    public event Action<SessionViewModel>? SessionAppended;

    /// <summary>Raised when the user requests a side-by-side comparison of two sessions.</summary>
    public event Action<HttpSession, HttpSession>? CompareRequested;

    /// <summary>Raised when the user opens the Options dialog.</summary>
    public event Action? OptionsRequested;

    /// <summary>Raised when the user opens the Converter utility.</summary>
    public event Action? ConverterRequested;

    public MainWindowViewModel() : this(new ProxyEngine(new ProxyOptions())) { }

    public MainWindowViewModel(ProxyEngine engine)
    {
        _engine = engine;
        _engine.SessionStarted += OnSessionStarted;
        _engine.SessionCompleted += OnSessionCompleted;
        _engine.SessionUpdated += OnSessionUpdated;
        _engine.TransactionPaused += OnTransactionPaused;
        _engine.Log += OnLog;

        // ---- Restore persisted settings -------------------------------------
        _settings = SettingsStore.LoadSettings();
        _settings.ApplyTo(_engine.Options);

        ListenPort = _engine.Options.ListenPort;
        DecryptHttps = _engine.Options.DecryptHttps;
        SetSystemProxy = _engine.Options.SetSystemProxy;
        EnableHttp2 = _engine.Options.EnableHttp2;
        TransparentCapture = _engine.Options.TransparentCapture;
        ThrottleEnabled = _engine.Options.ThrottleEnabled;
        ThrottleKbps = _engine.Options.ThrottleKbps;
        ExtraLatencyMs = _engine.Options.ExtraLatencyMs;
        UpstreamProxy = string.IsNullOrEmpty(_engine.Options.UpstreamProxyHost)
            ? "" : $"{_engine.Options.UpstreamProxyHost}:{_engine.Options.UpstreamProxyPort}";
        PassthroughHosts = string.Join(Environment.NewLine, _engine.Options.TlsPassthroughHosts);
        AutoScroll = _settings.AutoScroll;
        IsDarkTheme = !string.Equals(_settings.Theme, "Light", StringComparison.OrdinalIgnoreCase);
        ApplyTheme();

        SessionsView = new DataGridCollectionView(AllSessions) { Filter = o => PassesFilter((SessionViewModel)o) };
        AllSessions.CollectionChanged += (_, _) => HasSessions = AllSessions.Count > 0;

        Submitter.RequestSent += OnSubmitterRequest;

        Analysis.SessionSource = () => AllSessions.Select(v => v.Model).ToList();
        Analysis.NavigateToSessionRequested += OnNavigateToSession;
        Analysis.ExportRequested += ExportAnalysisReportAsync;

        Structure.SessionSource = () => AllSessions.Select(v => v.Model).ToList();
        Structure.NavigateToSessionRequested += OnNavigateToSessionModel;

        _statsTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(750) };
        _statsTimer.Tick += (_, _) => UpdateStats();
        _statsTimer.Start();

        // Re-filtering the grid is O(n); doing it per completed transaction made
        // the UI unusable under load. Coalesce into one refresh per interval.
        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _refreshTimer.Tick += (_, _) => FlushPendingRefresh();
        _refreshTimer.Start();

        LoadRules();
        foreach (var f in _settings.Filters) Filters.Add(f.Clone());
        foreach (var f in _settings.CaptureFilters) CaptureFilters.Add(f.Clone());

        var hidden = _settings.HiddenColumns;
        ShowProtoColumn = !hidden.Contains("Proto");
        ShowMethodColumn = !hidden.Contains("Method");
        ShowTypeColumn = !hidden.Contains("Type");
        ShowSizeColumn = !hidden.Contains("Size");
        ShowTimeColumn = !hidden.Contains("Time");
        ShowProcessColumn = !hidden.Contains("Process");
        // These three are off by default: useful when you need them, noise otherwise.
        ShowSpeedColumn = _settings.ShownColumns.Contains("Speed");
        ShowServerColumn = _settings.ShownColumns.Contains("Server");
        ShowConnectionColumn = _settings.ShownColumns.Contains("Conn");
        _columnsLoaded = true;
    }

    // ---- Collections ---------------------------------------------------------
    public ObservableCollection<SessionViewModel> AllSessions { get; } = new();
    public DataGridCollectionView SessionsView { get; }
    public ObservableCollection<string> LogLines { get; } = new();
    public ObservableCollection<RuleViewModel> Rules { get; } = new();
    public ObservableCollection<BreakpointViewModel> Breakpoints { get; } = new();

    /// <summary>Persisted display filters edited from the Filters dialog.</summary>
    public ObservableCollection<DisplayFilter> Filters { get; } = new();

    /// <summary>Raised when the user opens the Filters dialog.</summary>
    public event Action? FiltersRequested;

    /// <summary>Raised when the user opens the capture-filter editor.</summary>
    public event Action? CaptureFiltersRequested;

    /// <summary>Raised when the user opens the regular-expression tester.</summary>
    public event Action? RegexTesterRequested;

    /// <summary>Capture-level rules that drop traffic before it is recorded.</summary>
    public ObservableCollection<CaptureFilter> CaptureFilters { get; } = new();

    public string[] FilterFields { get; } =
        { "URL", "Host", "Method", "Status", "ContentType", "Process", "AnyHeader", "Body" };

    public InspectorViewModel Inspector { get; } = new();
    public DashboardViewModel Dashboard { get; } = new();
    public SubmitterViewModel Submitter { get; } = new();
    public AnalysisViewModel Analysis { get; } = new();
    public StructureViewModel Structure { get; } = new();

    // ---- Capture state -------------------------------------------------------
    [ObservableProperty] private bool _isCapturing;
    [ObservableProperty] private int _listenPort;
    [ObservableProperty] private bool _decryptHttps;
    [ObservableProperty] private bool _setSystemProxy;

    /// <summary>
    /// Transient feedback for the last user action ("Copied 4 lines", "Saved…").
    /// Kept separate from <see cref="StatsText"/>: both used to share one property,
    /// so the 750 ms stats tick wiped every message before it could be read.
    /// </summary>
    [ObservableProperty] private string _statusText = "Ready";

    /// <summary>Live capture counters, refreshed on a timer.</summary>
    [ObservableProperty] private string _statsText = "■ Stopped";

    [ObservableProperty] private string _certStatus = "";
    [ObservableProperty] private bool _isCertTrusted;
    [ObservableProperty] private bool _hasSessions;
    [ObservableProperty] private SessionViewModel? _selectedSession;
    [ObservableProperty] private BreakpointViewModel? _currentBreakpoint;

    // ---- Options / network simulation ---------------------------------------
    [ObservableProperty] private bool _throttleEnabled;
    [ObservableProperty] private int _throttleKbps;
    [ObservableProperty] private int _extraLatencyMs;
    [ObservableProperty] private string _upstreamProxy = "";
    [ObservableProperty] private string _passthroughHosts = "";
    [ObservableProperty] private bool _autoScroll = true;
    [ObservableProperty] private bool _enableHttp2 = true;
    [ObservableProperty] private bool _transparentCapture;
    [ObservableProperty] private bool _isDarkTheme = true;
    [ObservableProperty] private SessionViewModel? _compareBaseline;

    // ---- Column visibility (View ▸ Columns) ---------------------------------
    [ObservableProperty] private bool _showProtoColumn = true;
    [ObservableProperty] private bool _showMethodColumn = true;
    [ObservableProperty] private bool _showTypeColumn = true;
    [ObservableProperty] private bool _showSizeColumn = true;
    [ObservableProperty] private bool _showTimeColumn = true;
    [ObservableProperty] private bool _showProcessColumn = true;
    [ObservableProperty] private bool _showSpeedColumn;
    [ObservableProperty] private bool _showServerColumn;
    [ObservableProperty] private bool _showConnectionColumn;

    /// <summary>Raised when a grid column is shown/hidden so the view can apply it.</summary>
    public event Action? ColumnVisibilityChanged;

    partial void OnShowProtoColumnChanged(bool value) => OnColumnToggled();
    partial void OnShowMethodColumnChanged(bool value) => OnColumnToggled();
    partial void OnShowTypeColumnChanged(bool value) => OnColumnToggled();
    partial void OnShowSizeColumnChanged(bool value) => OnColumnToggled();
    partial void OnShowTimeColumnChanged(bool value) => OnColumnToggled();
    partial void OnShowProcessColumnChanged(bool value) => OnColumnToggled();
    partial void OnShowSpeedColumnChanged(bool value) => OnColumnToggled();
    partial void OnShowServerColumnChanged(bool value) => OnColumnToggled();
    partial void OnShowConnectionColumnChanged(bool value) => OnColumnToggled();

    private bool _columnsLoaded;
    private void OnColumnToggled()
    {
        ColumnVisibilityChanged?.Invoke();
        if (_columnsLoaded) PersistSettings();
    }

    /// <summary>Maps each toggleable column to its current visibility flag.</summary>
    public IReadOnlyDictionary<string, bool> ColumnVisibility => new Dictionary<string, bool>
    {
        ["Proto"] = ShowProtoColumn,
        ["Method"] = ShowMethodColumn,
        ["Type"] = ShowTypeColumn,
        ["Size"] = ShowSizeColumn,
        ["Time"] = ShowTimeColumn,
        ["Process"] = ShowProcessColumn,
        ["Speed"] = ShowSpeedColumn,
        ["Server"] = ShowServerColumn,
        ["Conn"] = ShowConnectionColumn,
    };

    // ---- Filtering -----------------------------------------------------------
    [ObservableProperty] private string _filterText = "";
    [ObservableProperty] private bool _errorsOnly;
    [ObservableProperty] private bool _searchBodies;
    [ObservableProperty] private string _methodFilter = "All";

    public string[] MethodFilters { get; } = { "All", "GET", "POST", "PUT", "PATCH", "DELETE", "HEAD", "OPTIONS" };

    // ---- Quick filters (HTTP Debugger's toolbar dropdowns) -------------------
    /// <summary>
    /// Content-type buckets, grouped the way a developer thinks about a capture
    /// rather than by exact MIME string: data payloads, page assets, streaming.
    /// </summary>
    public string[] TypeFilters { get; } =
    {
        "All types",
        "— Data —", "JSON", "XML", "Form data",
        "— Page —", "HTML", "Script", "Style", "Image", "Font",
        "— Other —", "Text", "Binary",
        "— Streaming —", "WebSocket", "SSE", "gRPC",
    };

    [ObservableProperty] private string _typeFilter = "All types";

    /// <summary>Hosts present in the capture, newest first, for the Host dropdown.</summary>
    public ObservableCollection<string> HostFilters { get; } = new() { AllHosts };

    /// <summary>Originating processes present in the capture.</summary>
    public ObservableCollection<string> ProcessFilters { get; } = new() { AllProcesses };

    private const string AllHosts = "All hosts";
    private const string AllProcesses = "All processes";

    [ObservableProperty] private string _hostFilter = AllHosts;
    [ObservableProperty] private string _processFilter = AllProcesses;

    partial void OnTypeFilterChanged(string value)
    {
        // The separator entries are labels, not selections.
        if (value.StartsWith('—')) { TypeFilter = "All types"; return; }
        RequestRefresh();
    }

    partial void OnHostFilterChanged(string value) => RequestRefresh();
    partial void OnProcessFilterChanged(string value) => RequestRefresh();

    /// <summary>Keeps the Host/Process dropdowns in step with what has been captured.</summary>
    private void TrackQuickFilterValues(SessionViewModel vm)
    {
        var host = vm.Model.Host;
        if (!string.IsNullOrEmpty(host) && !HostFilters.Contains(host))
        {
            // Insert alphabetically after the "All" entry so the list stays scannable.
            int at = 1;
            while (at < HostFilters.Count &&
                   string.Compare(HostFilters[at], host, StringComparison.OrdinalIgnoreCase) < 0) at++;
            HostFilters.Insert(at, host);
        }

        var process = vm.Model.ProcessName;
        if (!string.IsNullOrEmpty(process) && !ProcessFilters.Contains(process))
            ProcessFilters.Add(process);
    }

    /// <summary>True when the session's response falls into the selected type bucket.</summary>
    private bool MatchesTypeFilter(SessionViewModel vm)
    {
        if (TypeFilter is "All types" || TypeFilter.StartsWith('—')) return true;

        var m = vm.Model;
        var media = m.ResponseContentTypeShort;

        return TypeFilter switch
        {
            "JSON" => media.Contains("json", StringComparison.OrdinalIgnoreCase),
            "XML" => media.Contains("xml", StringComparison.OrdinalIgnoreCase),
            "Form data" => (m.RequestHeaders["Content-Type"] ?? "")
                .Contains("form", StringComparison.OrdinalIgnoreCase),
            "HTML" => media.Contains("html", StringComparison.OrdinalIgnoreCase),
            "Script" => media.Contains("javascript", StringComparison.OrdinalIgnoreCase) ||
                        media.Contains("ecmascript", StringComparison.OrdinalIgnoreCase),
            "Style" => media.Contains("css", StringComparison.OrdinalIgnoreCase),
            "Image" => media.StartsWith("image/", StringComparison.OrdinalIgnoreCase),
            "Font" => media.StartsWith("font/", StringComparison.OrdinalIgnoreCase) ||
                      media.Contains("woff", StringComparison.OrdinalIgnoreCase),
            "Text" => media.StartsWith("text/", StringComparison.OrdinalIgnoreCase),
            "Binary" => m.ResponseBodyKind == BodyContentType.Binary,
            "WebSocket" => m.Kind == SessionKind.WebSocket,
            "SSE" => m.Kind == SessionKind.ServerSentEvents,
            "gRPC" => media.Contains("grpc", StringComparison.OrdinalIgnoreCase),
            _ => true,
        };
    }

    // ---- Grouping (tree list mode) ------------------------------------------
    [ObservableProperty] private string _groupBy = "None";
    public string[] GroupByOptions { get; } = { "None", "Host", "Process", "Method", "Status", "ContentType" };

    /// <summary>Sets the grouping from the menu (which cannot bind a ComboBox).</summary>
    [RelayCommand]
    private void SetGroupBy(string value) => GroupBy = value;

    partial void OnGroupByChanged(string value)
    {
        SessionsView.GroupDescriptions.Clear();
        string? path = value switch
        {
            "Host" => nameof(SessionViewModel.Host),
            "Process" => nameof(SessionViewModel.ProcessName),
            "Method" => nameof(SessionViewModel.Method),
            "Status" => nameof(SessionViewModel.StatusDisplay),
            "ContentType" => nameof(SessionViewModel.ContentType),
            _ => null,
        };
        if (path is not null)
            SessionsView.GroupDescriptions.Add(new DataGridPathGroupDescription(path));
        SessionsView.Refresh();
    }

    /// <summary>Bandwidth presets surfaced in the Options dialog.</summary>
    public string[] ThrottlePresets { get; } =
        { "Custom", "GPRS (50 kbps)", "2G (250 kbps)", "3G (750 kbps)", "DSL (2 Mbps)", "Wi-Fi (30 Mbps)" };

    partial void OnFilterTextChanged(string value) => RequestRefresh();
    partial void OnErrorsOnlyChanged(bool value) => RequestRefresh();
    partial void OnSearchBodiesChanged(bool value) => RequestRefresh();
    partial void OnMethodFilterChanged(string value) => RequestRefresh();

    partial void OnSelectedSessionChanged(SessionViewModel? value) => Inspector.Session = value?.Model;

    partial void OnListenPortChanged(int value)
    {
        _engine.Options.ListenPort = value;
        if (IsCapturing)
            StatusText = $"Port changed to {value} — restart the capture (F5 twice) for it to take effect.";
    }

    partial void OnDecryptHttpsChanged(bool value) => _engine.Options.DecryptHttps = value;
    partial void OnSetSystemProxyChanged(bool value) => _engine.Options.SetSystemProxy = value;
    partial void OnIsDarkThemeChanged(bool value) => ApplyTheme();
    partial void OnAutoScrollChanged(bool value) => PersistSettings();

    /// <summary>Upper bound on retained sessions; 0 disables trimming.</summary>
    [ObservableProperty] private int _maxSessions = 20000;

    // ---- Engine event handlers (marshaled to UI thread) ----------------------
    private void OnSessionStarted(HttpSession s) => Dispatcher.UIThread.Post(() =>
    {
        var vm = new SessionViewModel(s);
        _index[s.Id] = vm;
        AllSessions.Add(vm);
        TrackQuickFilterValues(vm);
        TrimToSessionLimit();
        if (AutoScroll) SessionAppended?.Invoke(vm);
    });

    private void OnSessionCompleted(HttpSession s) => Dispatcher.UIThread.Post(() =>
    {
        if (_index.TryGetValue(s.Id, out var vm))
        {
            vm.Refresh();
            if (ReferenceEquals(vm, SelectedSession)) Inspector.Session = s;
            RequestRefresh();
        }
    });

    /// <summary>
    /// Caps how many transactions are retained in the grid. An unattended capture
    /// otherwise grows until the process runs out of memory — bodies included.
    /// The oldest sessions are dropped first, but never the selected one, the
    /// compare baseline, or anything the user bookmarked.
    /// </summary>
    private void TrimToSessionLimit()
    {
        if (MaxSessions <= 0 || AllSessions.Count <= MaxSessions) return;

        int excess = AllSessions.Count - MaxSessions;
        int removed = 0;
        for (int i = 0; i < AllSessions.Count && removed < excess; )
        {
            var candidate = AllSessions[i];
            if (candidate.Model.Bookmarked ||
                ReferenceEquals(candidate, SelectedSession) ||
                ReferenceEquals(candidate, CompareBaseline))
            {
                i++;
                continue;
            }
            AllSessions.RemoveAt(i);
            _index.Remove(candidate.Model.Id);
            removed++;
        }

        if (removed > 0) _droppedSessions += removed;
    }

    private long _droppedSessions;

    /// <summary>How many old sessions have been discarded to honour <see cref="MaxSessions"/>.</summary>
    public long DroppedSessions => _droppedSessions;

    private void OnSessionUpdated(HttpSession s) => Dispatcher.UIThread.Post(() =>
    {
        if (_index.TryGetValue(s.Id, out var vm))
        {
            vm.Refresh();
            if (ReferenceEquals(vm, SelectedSession)) Inspector.RefreshStreaming();
        }
    });

    private void OnTransactionPaused(PausedTransaction p) => Dispatcher.UIThread.Post(() =>
    {
        var bp = new BreakpointViewModel(p);
        Breakpoints.Add(bp);
        CurrentBreakpoint ??= bp;
    });

    private void OnLog(string message) => Dispatcher.UIThread.Post(() =>
    {
        LogLines.Add($"{DateTime.Now:HH:mm:ss}  {message}");
        if (LogLines.Count > 1000) LogLines.RemoveAt(0);
    });

    [RelayCommand]
    private void ClearLog() => LogLines.Clear();

    [RelayCommand]
    private async Task CopyLog()
    {
        if (Dialogs is null || LogLines.Count == 0) return;
        await Dialogs.SetClipboardAsync(string.Join(Environment.NewLine, LogLines));
        StatusText = $"Copied {LogLines.Count} log line(s) to clipboard";
    }

    private void OnSubmitterRequest(HttpSession s) => Dispatcher.UIThread.Post(() =>
    {
        var vm = new SessionViewModel(s);
        _index[s.Id] = vm;
        AllSessions.Add(vm);
        if (AutoScroll) SessionAppended?.Invoke(vm);
    });

    // ---- Commands ------------------------------------------------------------
    [RelayCommand]
    private void ToggleCapture()
    {
        if (IsCapturing) StopCapture(); else StartCapture();
    }

    [RelayCommand]
    private void StartCapture()
    {
        if (IsCapturing) return;
        try
        {
            _engine.Start();
            IsCapturing = true;
            StatusText = $"Capturing on {_engine.Options.ListenAddress}:{_engine.Options.ListenPort}";
        }
        catch (Exception ex)
        {
            IsCapturing = false;
            StatusText = $"Failed to start: {ex.Message}";

            // The overwhelmingly common cause is the port already being taken;
            // say so instead of surfacing a bare socket error code.
            string hint = ex is System.Net.Sockets.SocketException
                ? $"\n\nPort {_engine.Options.ListenPort} is most likely already in use. " +
                  "Pick a different port in the toolbar and try again."
                : string.Empty;
            _ = Dialogs?.ShowMessageAsync("Cannot start capture", ex.Message + hint);
        }
    }

    [RelayCommand]
    private void StopCapture()
    {
        if (!IsCapturing) return;
        _engine.Stop();
        IsCapturing = false;

        // Stopping releases paused transactions engine-side; clear the UI queue too.
        Breakpoints.Clear();
        CurrentBreakpoint = null;
        StatusText = "Capture stopped";
    }

    [RelayCommand]
    private void ClearSessions()
    {
        AllSessions.Clear();
        _index.Clear();
        SelectedSession = null;
        // These held references into the cleared list; leaving them dangling made
        // "Compare with baseline" act on a session no longer in the grid.
        CompareBaseline = null;
        Inspector.Session = null;
        _droppedSessions = 0;

        HostFilters.Clear(); HostFilters.Add(AllHosts); HostFilter = AllHosts;
        ProcessFilters.Clear(); ProcessFilters.Add(AllProcesses); ProcessFilter = AllProcesses;

        StatusText = "Capture cleared";
    }

    [RelayCommand]
    private void DeleteSelected()
    {
        if (SelectedSession is null) return;
        var vm = SelectedSession;

        // Keep the selection somewhere sensible instead of dropping it entirely.
        int position = AllSessions.IndexOf(vm);
        AllSessions.Remove(vm);
        _index.Remove(vm.Model.Id);
        if (ReferenceEquals(CompareBaseline, vm)) CompareBaseline = null;

        SelectedSession = AllSessions.Count == 0
            ? null
            : AllSessions[Math.Min(position, AllSessions.Count - 1)];
    }

    /// <summary>Removes every session currently hidden by the active filters.</summary>
    [RelayCommand]
    private void DeleteFilteredOut()
    {
        var doomed = AllSessions.Where(v => !PassesFilter(v) && !v.Model.Bookmarked).ToList();
        if (doomed.Count == 0) { StatusText = "Nothing to remove"; return; }

        foreach (var vm in doomed)
        {
            AllSessions.Remove(vm);
            _index.Remove(vm.Model.Id);
            if (ReferenceEquals(CompareBaseline, vm)) CompareBaseline = null;
            if (ReferenceEquals(SelectedSession, vm)) SelectedSession = null;
        }
        RequestRefresh();
        StatusText = $"Removed {doomed.Count} filtered-out session(s)";
    }

    /// <summary>Removes everything except bookmarked sessions.</summary>
    [RelayCommand]
    private void KeepOnlyBookmarked()
    {
        var doomed = AllSessions.Where(v => !v.Model.Bookmarked).ToList();
        if (doomed.Count == 0) { StatusText = "Nothing to remove"; return; }

        foreach (var vm in doomed)
        {
            AllSessions.Remove(vm);
            _index.Remove(vm.Model.Id);
            if (ReferenceEquals(CompareBaseline, vm)) CompareBaseline = null;
            if (ReferenceEquals(SelectedSession, vm)) SelectedSession = null;
        }
        RequestRefresh();
        StatusText = $"Kept {AllSessions.Count} bookmarked session(s)";
    }

    [RelayCommand]
    private void ToggleBookmark()
    {
        if (SelectedSession is null) return;
        SelectedSession.Model.Bookmarked = !SelectedSession.Model.Bookmarked;
        SelectedSession.Refresh();
    }

    [RelayCommand]
    private async Task CopyUrl()
    {
        if (SelectedSession is null || Dialogs is null) return;
        await Dialogs.SetClipboardAsync(SelectedSession.Url);
    }

    [RelayCommand]
    private async Task CopyAsCurl()
    {
        if (SelectedSession is null || Dialogs is null) return;
        await Dialogs.SetClipboardAsync(CodeGenerator.Generate(SelectedSession.Model, CodeGenerator.Language.Curl));
    }

    [RelayCommand]
    private async Task CopyResponseBody()
    {
        if (SelectedSession is null || Dialogs is null) return;
        await Dialogs.SetClipboardAsync(SelectedSession.Model.ResponseBodyText);
        StatusText = $"Copied {SelectedSession.Model.ResponseBodySize:N0} byte response body";
    }

    [RelayCommand]
    private async Task CopyRequestBody()
    {
        if (SelectedSession is null || Dialogs is null) return;
        await Dialogs.SetClipboardAsync(SelectedSession.Model.RequestBodyText);
        StatusText = $"Copied {SelectedSession.Model.RequestBodySize:N0} byte request body";
    }

    /// <summary>Shows the full keyboard-shortcut reference.</summary>
    [RelayCommand]
    private async Task ShowShortcuts()
    {
        if (Dialogs is null) return;
        await Dialogs.ShowMessageAsync("Keyboard shortcuts",
            """
            Capture
              F5                Start / stop capturing
              Ctrl+L            Clear all sessions
              Delete            Delete the selected session

            Navigation
              Ctrl+1 … Ctrl+6   Capture, Dashboard, Analysis, Submitter, Rules, Log
              Ctrl+F            Focus the quick filter
              Ctrl+Shift+F      Focus the header/body search
              F3                Find next match
              Ctrl+Shift+L      Clear every filter

            Session
              Ctrl+R            Resend in the Submitter
              Ctrl+U            Copy the URL
              Ctrl+Shift+C      Copy as cURL
              Ctrl+B            Toggle bookmark

            Files & analysis
              Ctrl+O            Open a saved .hspy capture
              Ctrl+S            Save the capture
              Ctrl+Shift+A      Analyse the capture
            """);
    }

    /// <summary>Explains the first-run setup, which is easy to get wrong.</summary>
    [RelayCommand]
    private async Task ShowGettingStarted()
    {
        if (Dialogs is null) return;
        await Dialogs.ShowMessageAsync("Getting started",
            $"""
            1. Trust the root certificate
               HTTPS ▸ Trust root certificate. HttpSpy decrypts TLS by presenting its own
               certificate, so the client has to trust the HttpSpy root CA first.
               The public certificate is also exported to:
               {RootCertificatePath}

            2. Start capturing (F5)
               On Windows the system proxy is pointed at HttpSpy automatically.
               Elsewhere, configure your client to use the proxy at
               {_engine.Options.ListenAddress}:{_engine.Options.ListenPort}.

            3. Inspect
               Select any row to see headers, cookies, the decoded body, a JSON tree,
               the timing waterfall and ready-to-run client code.

            4. Intervene
               The Rules tab can block, redirect, mock, delay, rewrite and break on
               matching traffic. Press Apply to arm the rule set.

            5. Analyse (Ctrl+Shift+A)
               The Analysis tab scans the whole capture for security, privacy,
               performance, caching and correctness problems, and scores the result.
            """);
    }

    [RelayCommand]
    private async Task SaveResponseBody()
    {
        if (SelectedSession is null || Dialogs is null) return;
        var s = SelectedSession.Model;
        var suggested = Path.GetFileName(s.Path);
        if (string.IsNullOrWhiteSpace(suggested) || !suggested.Contains('.')) suggested = "response.bin";
        var path = await Dialogs.SaveFileAsync("Save response body", suggested,
            new[] { ("All files", "*"), ("Text", "txt"), ("JSON", "json"), ("Binary", "bin") });
        if (path is null) return;
        try
        {
            await File.WriteAllBytesAsync(path, s.ResponseBody);
            StatusText = $"Saved {s.ResponseBody.Length} bytes to {path}";
        }
        catch (Exception ex) { await Dialogs.ShowMessageAsync("Save failed", ex.Message); }
    }

    [RelayCommand]
    private void ResendToSubmitter()
    {
        if (SelectedSession is null) return;
        Submitter.LoadFrom(SelectedSession.Model);
        ActiveTabIndex = TabSubmitter;
        StatusText = $"Loaded #{SelectedSession.Index} into the Submitter";
    }

    [RelayCommand]
    private void MarkForCompare()
    {
        CompareBaseline = SelectedSession;
        StatusText = CompareBaseline is null ? "Nothing selected"
            : $"Baseline set: #{CompareBaseline.Index} {CompareBaseline.Method} {CompareBaseline.Host}";
    }

    [RelayCommand]
    private void CompareWithBaseline()
    {
        if (CompareBaseline is null || SelectedSession is null) return;
        CompareRequested?.Invoke(CompareBaseline.Model, SelectedSession.Model);
    }

    // ---- "Apply as filter" (Wireshark-style quick filtering) ----------------
    /// <summary>Adds a show-only display filter for the selected session's host.</summary>
    [RelayCommand]
    private void FocusHost() => QuickFilter("Host", SelectedSession?.Host, hide: false);

    [RelayCommand]
    private void HideHost() => QuickFilter("Host", SelectedSession?.Host, hide: true);

    [RelayCommand]
    private void FocusProcess() => QuickFilter("Process", SelectedSession?.ProcessName, hide: false);

    [RelayCommand]
    private void HideProcess() => QuickFilter("Process", SelectedSession?.ProcessName, hide: true);

    [RelayCommand]
    private void FocusStatus() =>
        QuickFilter("Status", SelectedSession?.StatusCode is { } sc and > 0 ? sc.ToString() : null, hide: false);

    [RelayCommand]
    private void FocusContentType() =>
        QuickFilter("ContentType", SelectedSession?.Model.ResponseContentTypeShort, hide: false);

    /// <summary>Clears the quick text filter and every persisted display filter.</summary>
    [RelayCommand]
    private void ClearAllFilters()
    {
        FilterText = "";
        ErrorsOnly = false;
        MethodFilter = "All";
        TypeFilter = "All types";
        HostFilter = AllHosts;
        ProcessFilter = AllProcesses;
        Filters.Clear();
        ApplyFilters();
        StatusText = "Filters cleared";
    }

    private void QuickFilter(string field, string? value, bool hide)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        // Avoid stacking duplicate filters for the same field/value/direction.
        bool exists = Filters.Any(f =>
            f.Field == field && f.Hide == hide &&
            string.Equals(f.Pattern, value, StringComparison.OrdinalIgnoreCase));
        if (!exists)
            Filters.Add(new DisplayFilter { Field = field, Pattern = value, Hide = hide, Enabled = true });
        ApplyFilters();
        StatusText = (hide ? "Hiding " : "Focusing ") + $"{field} = {value}";
    }

    // ---- Advanced search (headers + content), Find / Find Next --------------
    [ObservableProperty] private string _advancedSearchText = "";
    [ObservableProperty] private string _advancedSearchStatus = "";

    [RelayCommand]
    private void FindNext()
    {
        var needle = (AdvancedSearchText ?? string.Empty).Trim();
        if (needle.Length == 0) { AdvancedSearchStatus = ""; return; }

        var list = AllSessions.ToList();
        if (list.Count == 0) { AdvancedSearchStatus = "no sessions"; return; }

        int start = SelectedSession is null ? -1 : list.IndexOf(SelectedSession);
        int total = list.Count(v => SessionMatchesText(v, needle));
        if (total == 0) { AdvancedSearchStatus = "0 matches"; return; }

        // Walk forward from the item after the current selection, wrapping around.
        for (int step = 1; step <= list.Count; step++)
        {
            int i = ((start + step) % list.Count + list.Count) % list.Count;
            if (SessionMatchesText(list[i], needle))
            {
                SelectedSession = list[i];
                SessionAppended?.Invoke(list[i]); // reuse scroll-into-view hook
                AdvancedSearchStatus = $"match {RankOf(list, i, needle)} / {total}";
                return;
            }
        }
    }

    private static int RankOf(List<SessionViewModel> list, int index, string needle)
    {
        int rank = 0;
        for (int i = 0; i <= index && i < list.Count; i++)
            if (SessionMatchesText(list[i], needle)) rank++;
        return rank;
    }

    /// <summary>True when the needle appears in the URL, any header, or either body.</summary>
    private static bool SessionMatchesText(SessionViewModel vm, string needle)
    {
        var m = vm.Model;
        if (m.FullUrl.Contains(needle, StringComparison.OrdinalIgnoreCase)) return true;
        if (HttpSpy.Core.Rules.HttpModifier.RawHeaderBlock(m.RequestHeaders)
                .Contains(needle, StringComparison.OrdinalIgnoreCase)) return true;
        if (HttpSpy.Core.Rules.HttpModifier.RawHeaderBlock(m.ResponseHeaders)
                .Contains(needle, StringComparison.OrdinalIgnoreCase)) return true;
        if (m.RequestBodyText.Contains(needle, StringComparison.OrdinalIgnoreCase)) return true;
        if (m.ResponseBodyText.Contains(needle, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    [RelayCommand]
    private void NextBookmark()
    {
        var list = AllSessions.ToList();
        if (list.Count == 0) return;
        int start = SelectedSession is null ? -1 : list.IndexOf(SelectedSession);
        for (int step = 1; step <= list.Count; step++)
        {
            int i = ((start + step) % list.Count + list.Count) % list.Count;
            if (list[i].Model.Bookmarked)
            {
                SelectedSession = list[i];
                SessionAppended?.Invoke(list[i]);
                return;
            }
        }
        StatusText = "No bookmarks";
    }

    [RelayCommand]
    private void OpenOptions() => OptionsRequested?.Invoke();

    // ---- Display filters (multi-rule, show/hide) ----------------------------
    [RelayCommand]
    private void OpenFilters() => FiltersRequested?.Invoke();

    [RelayCommand]
    private void OpenConverter() => ConverterRequested?.Invoke();

    [RelayCommand]
    private void OpenCaptureFilters() => CaptureFiltersRequested?.Invoke();

    [RelayCommand]
    private void OpenRegexTester() => RegexTesterRequested?.Invoke();

    [RelayCommand]
    private void AddCaptureFilter() =>
        CaptureFilters.Add(new CaptureFilter { Field = CaptureFilterField.Host, Exclude = true });

    [RelayCommand]
    private void RemoveCaptureFilter(CaptureFilter? filter)
    {
        if (filter is not null) CaptureFilters.Remove(filter);
    }

    /// <summary>Pushes capture filters to the live engine and persists them.</summary>
    public void ApplyCaptureFilters()
    {
        _engine.Options.CaptureFilters = CaptureFilters.Select(f => f.Clone()).ToList();
        PersistSettings();
        int active = CaptureFilters.Count(f => f.Enabled && !string.IsNullOrWhiteSpace(f.Pattern));
        StatusText = active == 0
            ? "Capture filters cleared — everything will be recorded"
            : $"{active} capture filter(s) active";
    }

    /// <summary>Writes the whole configuration to a file so it can be shared or restored.</summary>
    [RelayCommand]
    private async Task ExportSettings()
    {
        if (Dialogs is null) return;
        var path = await Dialogs.SaveFileAsync("Export settings", "httpspy-settings.json",
            new[] { ("HttpSpy settings", "json") });
        if (path is null) return;

        try
        {
            PersistSettings();
            var bundle = SettingsStore.ExportBundle(
                HttpSpySettings.FromOptions(_engine.Options), Rules.Select(r => r.Rule));
            await File.WriteAllTextAsync(path, bundle);
            StatusText = $"Settings and rules exported to {path}";
        }
        catch (Exception ex) { await Dialogs.ShowMessageAsync("Export failed", ex.Message); }
    }

    /// <summary>Restores a previously exported configuration bundle.</summary>
    [RelayCommand]
    private async Task ImportSettings()
    {
        if (Dialogs is null) return;
        var path = await Dialogs.OpenFileAsync("Import settings", new[] { ("HttpSpy settings", "json") });
        if (path is null) return;

        if (!await Dialogs.ConfirmAsync("Import settings",
                "This replaces your current options, rules and filters. Continue?"))
            return;

        try
        {
            var (settings, rules) = SettingsStore.ImportBundle(await File.ReadAllTextAsync(path));

            settings.ApplyTo(_engine.Options);
            ListenPort = settings.ListenPort;
            DecryptHttps = settings.DecryptHttps;
            SetSystemProxy = settings.SetSystemProxy;
            EnableHttp2 = settings.EnableHttp2;
            TransparentCapture = settings.TransparentCapture;
            ThrottleEnabled = settings.ThrottleEnabled;
            ThrottleKbps = settings.ThrottleKbps;
            ExtraLatencyMs = settings.ExtraLatencyMs;
            AutoScroll = settings.AutoScroll;
            IsDarkTheme = !string.Equals(settings.Theme, "Light", StringComparison.OrdinalIgnoreCase);
            UpstreamProxy = string.IsNullOrEmpty(settings.UpstreamProxyHost)
                ? "" : $"{settings.UpstreamProxyHost}:{settings.UpstreamProxyPort}";
            PassthroughHosts = string.Join(Environment.NewLine, settings.TlsPassthroughHosts);

            Filters.Clear();
            foreach (var f in settings.Filters) Filters.Add(f.Clone());

            CaptureFilters.Clear();
            foreach (var f in settings.CaptureFilters) CaptureFilters.Add(f.Clone());

            Rules.Clear();
            foreach (var r in rules) Rules.Add(new RuleViewModel(r));

            ApplyRules();
            ApplyCaptureFilters();
            ApplyFilters();
            StatusText = $"Imported {rules.Count} rule(s) and {settings.Filters.Count} filter(s) from {path}";
        }
        catch (Exception ex) { await Dialogs.ShowMessageAsync("Import failed", ex.Message); }
    }

    [RelayCommand]
    private void AddFilter() => Filters.Add(new DisplayFilter { Field = "URL", Pattern = "" });

    [RelayCommand]
    private void RemoveFilter(DisplayFilter? filter)
    {
        if (filter is not null) Filters.Remove(filter);
    }

    /// <summary>Applies the current filter set to the grid and persists it.</summary>
    public void ApplyFilters()
    {
        SessionsView.Refresh();
        PersistSettings();
        int hidden = AllSessions.Count(v => !PassesFilter(v));
        StatusText = hidden > 0 ? $"Filters applied — {hidden} hidden" : "Filters applied";
    }

    [RelayCommand]
    private void ToggleTheme()
    {
        IsDarkTheme = !IsDarkTheme;
        PersistSettings();
    }

    [ObservableProperty] private int _activeTabIndex;

    partial void OnActiveTabIndexChanged(int value)
    {
        // These aggregate the whole capture, so recompute lazily when the tab is
        // actually shown rather than on every captured transaction.
        if (value == TabDashboard) Dashboard.Recompute(AllSessions.Select(v => v.Model).ToList());
        else if (value == TabStructure) Structure.Rebuild();
    }

    [RelayCommand]
    private void RefreshDashboard()
    {
        Dashboard.Recompute(AllSessions.Select(v => v.Model).ToList());
        StatusText = $"Dashboard refreshed over {AllSessions.Count} session(s)";
    }

    /// <summary>Tab indices, kept in one place so shortcuts and code agree.</summary>
    public const int TabCapture = 0;
    public const int TabStructure = 1;
    public const int TabDashboard = 2;
    public const int TabAnalysis = 3;
    public const int TabSubmitter = 4;
    public const int TabRules = 5;
    public const int TabLog = 6;
    private const int TabCount = 7;

    /// <summary>Switches the main tab (used by the Ctrl+1..6 keyboard shortcuts).</summary>
    [RelayCommand]
    private void SelectTab(string index)
    {
        if (int.TryParse(index, out var i) && i >= 0 && i < TabCount) ActiveTabIndex = i;
    }

    /// <summary>Selects a session by identity and reveals it in the grid.</summary>
    private void OnNavigateToSessionModel(HttpSession session)
    {
        var match = AllSessions.FirstOrDefault(v => ReferenceEquals(v.Model, session));
        if (match is null) { StatusText = "That session is no longer in the capture"; return; }
        ActiveTabIndex = TabCapture;
        SelectedSession = match;
        SessionAppended?.Invoke(match);
        StatusText = $"Jumped to session #{match.Index}";
    }

    /// <summary>Selects the session with the given grid index and reveals it.</summary>
    private void OnNavigateToSession(int sessionIndex)
    {
        var match = AllSessions.FirstOrDefault(v => v.Index == sessionIndex);
        if (match is null)
        {
            StatusText = $"Session #{sessionIndex} is no longer in the capture";
            return;
        }
        ActiveTabIndex = TabCapture;
        SelectedSession = match;
        SessionAppended?.Invoke(match); // reuse the scroll-into-view hook
        StatusText = $"Jumped to session #{sessionIndex}";
    }

    /// <summary>Writes an analysis report to disk in the requested format.</summary>
    private async Task ExportAnalysisReportAsync(AnalysisReport report, string format)
    {
        if (Dialogs is null) return;

        var (title, suggested, filter) = format switch
        {
            "html" => ("Export analysis report", "httpspy-analysis.html", ("HTML report", "html")),
            "json" => ("Export analysis report", "httpspy-analysis.json", ("JSON", "json")),
            _ => ("Export analysis report", "httpspy-analysis.txt", ("Text", "txt")),
        };

        var path = await Dialogs.SaveFileAsync(title, suggested, new[] { filter });
        if (path is null) return;

        try
        {
            string content = format switch
            {
                "html" => AnalysisReportWriter.ToHtml(report),
                "json" => AnalysisReportWriter.ToJson(report),
                _ => AnalysisReportWriter.ToText(report),
            };
            await File.WriteAllTextAsync(path, content);
            StatusText = $"Analysis report exported to {path}";
        }
        catch (Exception ex)
        {
            await Dialogs.ShowMessageAsync("Export failed", ex.Message);
        }
    }

    [RelayCommand]
    private async Task SaveSession()
    {
        if (Dialogs is null) return;
        var path = await Dialogs.SaveFileAsync("Save session", "capture.hspy",
            new[] { ("HttpSpy session", "hspy") });
        if (path is null) return;
        try
        {
            await SessionStore.SaveAsync(path, AllSessions.Select(v => v.Model));
            StatusText = $"Saved {AllSessions.Count} sessions to {path}";
        }
        catch (Exception ex) { await Dialogs.ShowMessageAsync("Save failed", ex.Message); }
    }

    [RelayCommand]
    private async Task OpenSession()
    {
        if (Dialogs is null) return;
        var path = await Dialogs.OpenFileAsync("Open session", new[] { ("HttpSpy session", "hspy") });
        if (path is null) return;
        try
        {
            var loaded = await SessionStore.LoadAsync(path);
            ClearSessions();
            foreach (var s in loaded)
            {
                var vm = new SessionViewModel(s);
                _index[s.Id] = vm;
                AllSessions.Add(vm);
            }
            StatusText = $"Loaded {loaded.Count} sessions from {path}";
        }
        catch (Exception ex) { await Dialogs.ShowMessageAsync("Open failed", ex.Message); }
    }

    [RelayCommand]
    private async Task ExportHar()
    {
        if (Dialogs is null) return;
        var path = await Dialogs.SaveFileAsync("Export HAR", "capture.har", new[] { ("HTTP Archive", "har") });
        if (path is null) return;
        try
        {
            HarExporter.ExportToFile(AllSessions.Select(v => v.Model), path);
            StatusText = $"Exported HAR to {path}";
        }
        catch (Exception ex) { await Dialogs.ShowMessageAsync("Export failed", ex.Message); }
    }

    [RelayCommand]
    private async Task ExportCsv()
    {
        if (Dialogs is null) return;
        var path = await Dialogs.SaveFileAsync("Export CSV", "capture.csv", new[] { ("CSV", "csv") });
        if (path is null) return;
        try
        {
            CsvExporter.Export(AllSessions.Select(v => v.Model), path);
            StatusText = $"Exported CSV to {path}";
        }
        catch (Exception ex) { await Dialogs.ShowMessageAsync("Export failed", ex.Message); }
    }

    [RelayCommand]
    private async Task ExportJson()
    {
        if (Dialogs is null) return;
        var path = await Dialogs.SaveFileAsync("Export JSON", "capture.json", new[] { ("JSON", "json") });
        if (path is null) return;
        try
        {
            JsonExporter.Export(AllSessions.Select(v => v.Model), path);
            StatusText = $"Exported JSON to {path}";
        }
        catch (Exception ex) { await Dialogs.ShowMessageAsync("Export failed", ex.Message); }
    }

    [RelayCommand]
    private async Task ExportXml()
    {
        if (Dialogs is null) return;
        var path = await Dialogs.SaveFileAsync("Export XML", "capture.xml", new[] { ("XML", "xml") });
        if (path is null) return;
        try
        {
            XmlExporter.Export(AllSessions.Select(v => v.Model), path);
            StatusText = $"Exported XML to {path}";
        }
        catch (Exception ex) { await Dialogs.ShowMessageAsync("Export failed", ex.Message); }
    }

    [RelayCommand]
    private async Task ExportTxt()
    {
        if (Dialogs is null) return;
        var path = await Dialogs.SaveFileAsync("Export TXT", "capture.txt", new[] { ("Text", "txt") });
        if (path is null) return;
        try
        {
            TxtExporter.Export(AllSessions.Select(v => v.Model), path);
            StatusText = $"Exported TXT to {path}";
        }
        catch (Exception ex) { await Dialogs.ShowMessageAsync("Export failed", ex.Message); }
    }

    [RelayCommand]
    private async Task ExportSeparateFiles()
    {
        if (Dialogs is null) return;
        var dir = await Dialogs.SelectFolderAsync("Select a folder to save each session as a file");
        if (dir is null) return;
        try
        {
            int n = SeparateFilesExporter.Export(AllSessions.Select(v => v.Model), dir);
            StatusText = $"Saved {n} sessions to {dir}";
        }
        catch (Exception ex) { await Dialogs.ShowMessageAsync("Export failed", ex.Message); }
    }

    [RelayCommand]
    private async Task InstallCertificate()
    {
        if (Dialogs is null) return;
        if (OperatingSystem.IsWindows())
        {
            try
            {
                CertTrust.Install(_engine.CertificateAuthority.RootCertificate);
                RefreshCertStatus();
                await Dialogs.ShowMessageAsync("Certificate installed",
                    "The HttpSpy root certificate was added to your Trusted Root store, so HTTPS decryption is ready.\n\n" +
                    "Note: Firefox keeps its own certificate store — to decrypt Firefox traffic, import the " +
                    "exported .cer via Settings ▸ Privacy & Security ▸ View Certificates ▸ Authorities ▸ Import.");
            }
            catch (Exception ex)
            {
                await Dialogs.ShowMessageAsync("Install failed",
                    $"Could not install the certificate automatically: {ex.Message}\n\n" +
                    $"You can import it manually from:\n{_engine.CertificateAuthority.RootCertificatePath}");
            }
        }
        else
        {
            // No user-writable trust store outside Windows, so hand over the exact
            // commands for this platform instead of a shrug. The PEM is written
            // next to the .cer because nothing outside Windows wants DER.
            string pem = WriteRootPem();
            await Dialogs.ShowMessageAsync($"Trust the root certificate on {PlatformIntegration.PlatformName}",
                $"The root certificate is exported at:\n{pem}\n\n" +
                PlatformIntegration.Render(PlatformIntegration.TrustSteps(pem)) +
                "\n\n(Commands copied to the clipboard.)");
            await Dialogs.SetClipboardAsync(
                PlatformIntegration.Render(PlatformIntegration.TrustSteps(pem)));
            RefreshCertStatus();
        }
    }

    /// <summary>
    /// Writes the root CA in PEM form beside the DER copy and returns its path.
    /// Every non-Windows trust mechanism — OpenSSL, curl, Node, the Linux CA
    /// bundle, the macOS keychain importer — expects PEM.
    /// </summary>
    private string WriteRootPem()
    {
        var der = _engine.CertificateAuthority.RootCertificatePath;
        var pem = Path.Combine(Path.GetDirectoryName(der) ?? ".", PlatformIntegration.SuggestedPemName);
        PlatformIntegration.ExportPem(_engine.CertificateAuthority.RootCertificate, pem);
        return pem;
    }

    /// <summary>
    /// Shows everything needed to get traffic flowing through HttpSpy on this
    /// machine: how to trust the CA and how to point clients at the proxy, with
    /// the commands filled in for the current platform, port and paths.
    /// </summary>
    [RelayCommand]
    private async Task ShowSetupGuide()
    {
        if (Dialogs is null) return;
        string pem = WriteRootPem();
        var text =
            $"Platform: {PlatformIntegration.PlatformName}\n" +
            $"Proxy:    {_engine.Options.ListenAddress}:{_engine.Options.ListenPort}\n" +
            $"Root CA:  {pem}\n\n" +
            "── Trust the HttpSpy root certificate ──\n" +
            PlatformIntegration.Render(PlatformIntegration.TrustSteps(pem)) + "\n\n" +
            "── Send traffic through HttpSpy ──\n" +
            PlatformIntegration.Render(PlatformIntegration.ProxySteps(
                _engine.Options.ListenAddress, _engine.Options.ListenPort));

        await Dialogs.SetClipboardAsync(text);
        await Dialogs.ShowMessageAsync("Setup guide", text + "\n\n(Copied to the clipboard.)");
    }

    /// <summary>Removes the HttpSpy root CA from the Windows trust store (after confirmation).</summary>
    [RelayCommand]
    private async Task UninstallCertificate()
    {
        if (Dialogs is null) return;
        if (!OperatingSystem.IsWindows())
        {
            string pem = WriteRootPem();
            await Dialogs.ShowMessageAsync($"Removing trust on {PlatformIntegration.PlatformName}",
                PlatformIntegration.Current switch
                {
                    HostPlatform.Linux =>
                        "sudo rm -f /usr/local/share/ca-certificates/httpspy.crt && sudo update-ca-certificates --fresh\n\n" +
                        "Also unset SSL_CERT_FILE / NODE_EXTRA_CA_CERTS in any shell where you set them.",
                    HostPlatform.MacOS =>
                        $"sudo security delete-certificate -c HttpSpy /Library/Keychains/System.keychain\n\n" +
                        $"(or drop the entry for {pem} from Keychain Access)",
                    _ => "Remove the HttpSpy root certificate from your platform's trust store.",
                });
            return;
        }
        if (!await Dialogs.ConfirmAsync("Remove trusted certificate",
                "Remove the HttpSpy root CA from your Trusted Root store? HTTPS decryption will stop working until you trust it again."))
            return;
        try
        {
            CertTrust.Uninstall(_engine.CertificateAuthority.RootCertificate);
            RefreshCertStatus();
            await Dialogs.ShowMessageAsync("Certificate removed",
                "The HttpSpy root certificate was removed from your Trusted Root store.");
        }
        catch (Exception ex)
        {
            await Dialogs.ShowMessageAsync("Remove failed", ex.Message);
        }
    }

    /// <summary>Exports the public root certificate (.cer) to a user-chosen location.</summary>
    [RelayCommand]
    private async Task ExportCertificate()
    {
        if (Dialogs is null) return;
        // Default to PEM on platforms whose tooling cannot read DER.
        bool preferPem = PlatformIntegration.Current != HostPlatform.Windows;
        var path = await Dialogs.SaveFileAsync("Export root certificate",
            preferPem ? PlatformIntegration.SuggestedPemName : "HttpSpyRootCA.cer",
            new[] { ("PEM certificate", "pem"), ("DER certificate", "cer"), ("Certificate", "crt") });
        if (path is null) return;
        try
        {
            bool pem = path.EndsWith(".pem", StringComparison.OrdinalIgnoreCase) ||
                       path.EndsWith(".crt", StringComparison.OrdinalIgnoreCase);
            if (pem) PlatformIntegration.ExportPem(_engine.CertificateAuthority.RootCertificate, path);
            else _engine.CertificateAuthority.ExportRootCertificate(path);

            StatusText = $"Root certificate exported to {path}";
            await Dialogs.ShowMessageAsync("Certificate exported",
                $"The HttpSpy root certificate (public key only) was saved to:\n{path}\n\n" +
                $"Format: {(pem ? "PEM — what OpenSSL, curl, Node and Linux trust stores expect" : "DER")}\n\n" +
                "Import it into any device or browser that should trust HttpSpy.");
        }
        catch (Exception ex) { await Dialogs.ShowMessageAsync("Export failed", ex.Message); }
    }

    /// <summary>Shows the certificate details (subject, validity, thumbprint) and copies the thumbprint.</summary>
    [RelayCommand]
    private async Task ShowCertificateInfo()
    {
        if (Dialogs is null) return;
        var ca = _engine.CertificateAuthority.RootCertificate;
        var info =
            $"Subject:    {ca.Subject}\n" +
            $"Issued:     {ca.NotBefore:yyyy-MM-dd}\n" +
            $"Expires:    {ca.NotAfter:yyyy-MM-dd}\n" +
            $"Thumbprint: {ca.Thumbprint}\n\n" +
            $"Public .cer: {_engine.CertificateAuthority.RootCertificatePath}\n" +
            PlatformIntegration.QueryTrust(_engine.CertificateAuthority.RootCertificate) switch
            {
                TrustState.Trusted => $"Status: trusted on {PlatformIntegration.PlatformName}.",
                TrustState.NotTrusted => "Status: NOT trusted yet — click \"Trust cert\".",
                _ => $"Status: no readable trust store on {PlatformIntegration.PlatformName} — " +
                     "use \"Setup guide\" for the commands.",
            };
        await Dialogs.SetClipboardAsync(ca.Thumbprint ?? "");
        await Dialogs.ShowMessageAsync("HttpSpy root certificate", info + "\n\n(Thumbprint copied to clipboard.)");
    }

    /// <summary>
    /// Re-reads whatever trust store this platform exposes and updates the status
    /// indicator. An unreadable store reports "unknown" rather than "not trusted",
    /// because a false alarm sends people chasing a problem they do not have.
    /// </summary>
    public void RefreshCertStatus()
    {
        switch (PlatformIntegration.QueryTrust(_engine.CertificateAuthority.RootCertificate))
        {
            case TrustState.Trusted:
                IsCertTrusted = true;
                CertStatus = "🔒 Root CA trusted";
                break;
            case TrustState.NotTrusted:
                IsCertTrusted = false;
                CertStatus = "⚠ Root CA not trusted";
                break;
            default:
                IsCertTrusted = false;
                CertStatus = "🔒 Root CA — trust unverified";
                break;
        }
    }

    // ---- Breakpoints ---------------------------------------------------------
    [RelayCommand]
    private void ContinueBreakpoint()
    {
        if (CurrentBreakpoint is null) return;
        CurrentBreakpoint.ContinueWithEdits();
        DequeueBreakpoint();
    }

    [RelayCommand]
    private void AbortBreakpoint()
    {
        if (CurrentBreakpoint is null) return;
        CurrentBreakpoint.Abort();
        DequeueBreakpoint();
    }

    private void DequeueBreakpoint()
    {
        if (CurrentBreakpoint is not null) Breakpoints.Remove(CurrentBreakpoint);
        CurrentBreakpoint = Breakpoints.FirstOrDefault();
    }

    // ---- Rules ---------------------------------------------------------------
    [RelayCommand]
    private void AddRule()
    {
        var rule = new Rule { Name = "New rule", Action = RuleAction.Highlight };
        var vm = new RuleViewModel(rule);
        Rules.Add(vm);
        SelectedRule = vm;
        ApplyRules();
    }

    [RelayCommand]
    private void RemoveRule()
    {
        if (SelectedRule is null) return;
        Rules.Remove(SelectedRule);
        SelectedRule = null;
        ApplyRules();
    }

    [RelayCommand]
    private void ApplyRules()
    {
        _engine.Rules.SetRules(Rules.Select(r => r.Rule));
        foreach (var r in Rules) r.RefreshSummary();
        StatusText = $"{Rules.Count(r => r.Enabled)} active rule(s)";
        try { SettingsStore.SaveRules(Rules.Select(r => r.Rule)); } catch { /* ignore */ }
    }

    [ObservableProperty] private RuleViewModel? _selectedRule;

    private void LoadRules()
    {
        var persisted = SettingsStore.LoadRules();
        if (persisted.Count > 0)
            foreach (var r in persisted) Rules.Add(new RuleViewModel(r));
        else
            foreach (var r in DefaultRules()) Rules.Add(new RuleViewModel(r));

        _engine.Rules.SetRules(Rules.Select(r => r.Rule));
        foreach (var r in Rules) r.RefreshSummary();
    }

    /// <summary>
    /// The rule set a fresh install starts with. Errors, slow responses and
    /// oversized payloads are highlighted out of the box: previously the only
    /// seeded rule was disabled, so the highlighting engine looked broken until
    /// the user happened to author a rule themselves.
    /// </summary>
    public static IEnumerable<Rule> DefaultRules() => new[]
    {
        new Rule
        {
            Name = "Server errors (5xx)",
            Action = RuleAction.Highlight,
            UrlMatchMode = MatchMode.Wildcard, UrlPattern = "*",
            HighlightColumn = HighlightColumn.Status,
            HighlightOperator = HighlightOperator.IsBigger,
            HighlightValue = "499",
            HighlightColor = 0xFFE5484A,
        },
        new Rule
        {
            Name = "Client errors (4xx)",
            Action = RuleAction.Highlight,
            UrlMatchMode = MatchMode.Wildcard, UrlPattern = "*",
            HighlightColumn = HighlightColumn.Status,
            HighlightOperator = HighlightOperator.IsBetween,
            HighlightValue = "400", HighlightValue2 = "499",
            HighlightColor = 0xFFE16F24,
        },
        new Rule
        {
            Name = "Slow responses (over 2 s)",
            Action = RuleAction.Highlight,
            UrlMatchMode = MatchMode.Wildcard, UrlPattern = "*",
            HighlightColumn = HighlightColumn.Duration,
            HighlightOperator = HighlightOperator.IsBigger,
            HighlightValue = "2000",
            HighlightColor = 0xFFD4A72C,
        },
        new Rule
        {
            Name = "Large responses (over 2 MB)",
            Action = RuleAction.Highlight,
            UrlMatchMode = MatchMode.Wildcard, UrlPattern = "*",
            HighlightColumn = HighlightColumn.ResponseSize,
            HighlightOperator = HighlightOperator.IsBigger,
            HighlightValue = "2097152",
            HighlightColor = 0xFF8B5CF6,
        },
    };

    /// <summary>Restores the shipped rule set, replacing whatever is configured.</summary>
    [RelayCommand]
    private async Task RestoreDefaultRules()
    {
        if (Dialogs is not null &&
            !await Dialogs.ConfirmAsync("Restore default rules",
                "Replace the current rule set with the shipped defaults? Your rules will be lost."))
            return;

        Rules.Clear();
        foreach (var r in DefaultRules()) Rules.Add(new RuleViewModel(r));
        ApplyRules();
        StatusText = "Default rules restored";
    }

    // ---- Options -------------------------------------------------------------
    /// <summary>Pushes the Options-dialog fields into the live engine and persists them.</summary>
    public void ApplyOptions()
    {
        _engine.Options.EnableHttp2 = EnableHttp2;
        _engine.Options.TransparentCapture = TransparentCapture;
        _engine.Options.ThrottleEnabled = ThrottleEnabled;
        _engine.Options.ThrottleKbps = ThrottleKbps;
        _engine.Options.ExtraLatencyMs = ExtraLatencyMs;

        if (!string.IsNullOrWhiteSpace(UpstreamProxy))
        {
            var hp = UpstreamProxy.Split(':', 2);
            _engine.Options.UpstreamProxyHost = hp[0].Trim();
            _engine.Options.UpstreamProxyPort = hp.Length > 1 && int.TryParse(hp[1], out var p) ? p : 8080;
        }
        else
        {
            _engine.Options.UpstreamProxyHost = null;
        }

        _engine.Options.TlsPassthroughHosts = PassthroughHosts
            .Split(new[] { '\r', '\n', ',' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

        PersistSettings();
        StatusText = ThrottleEnabled
            ? $"Network simulation: {ThrottleKbps} kbps, +{ExtraLatencyMs} ms latency"
            : "Options applied";
    }

    private void ApplyTheme()
    {
        if (Application.Current is { } app)
            app.RequestedThemeVariant = IsDarkTheme ? ThemeVariant.Dark : ThemeVariant.Light;
    }

    private void PersistSettings()
    {
        try
        {
            var s = HttpSpySettings.FromOptions(_engine.Options);
            s.ListenPort = ListenPort;
            s.DecryptHttps = DecryptHttps;
            s.SetSystemProxy = SetSystemProxy;
            s.Theme = IsDarkTheme ? "Dark" : "Light";
            s.AutoScroll = AutoScroll;
            s.Filters = Filters.Select(f => f.Clone()).ToList();
            s.HiddenColumns = ColumnVisibility.Where(kv => !kv.Value).Select(kv => kv.Key).ToList();
            s.ShownColumns = ColumnVisibility.Where(kv => kv.Value).Select(kv => kv.Key).ToList();
            s.CaptureFilters = _engine.Options.CaptureFilters.Select(f => f.Clone()).ToList();
            SettingsStore.SaveSettings(s);
        }
        catch { /* ignore persistence errors */ }
    }

    // ---- Filtering helpers ---------------------------------------------------
    private bool PassesFilter(SessionViewModel vm)
    {
        if (ErrorsOnly && !vm.Model.IsError) return false;
        if (MethodFilter != "All" && !string.Equals(vm.Method, MethodFilter, StringComparison.OrdinalIgnoreCase))
            return false;
        if (HostFilter != AllHosts && !string.Equals(vm.Host, HostFilter, StringComparison.OrdinalIgnoreCase))
            return false;
        if (ProcessFilter != AllProcesses &&
            !string.Equals(vm.ProcessName, ProcessFilter, StringComparison.OrdinalIgnoreCase))
            return false;
        if (!MatchesTypeFilter(vm)) return false;
        if (!string.IsNullOrWhiteSpace(FilterText))
        {
            var f = FilterText.Trim();
            bool match = vm.Url.Contains(f, StringComparison.OrdinalIgnoreCase)
                         || vm.Host.Contains(f, StringComparison.OrdinalIgnoreCase)
                         || vm.Method.Contains(f, StringComparison.OrdinalIgnoreCase)
                         || vm.StatusCode.ToString().Contains(f)
                         || vm.ProcessName.Contains(f, StringComparison.OrdinalIgnoreCase)
                         || vm.ContentType.Contains(f, StringComparison.OrdinalIgnoreCase);
            if (!match && SearchBodies)
                match = vm.Model.ResponseBodyText.Contains(f, StringComparison.OrdinalIgnoreCase)
                        || vm.Model.RequestBodyText.Contains(f, StringComparison.OrdinalIgnoreCase);
            if (!match) return false;
        }
        return PassesDisplayFilters(vm);
    }

    /// <summary>
    /// Applies the multi-rule display filters: a session is hidden if it matches
    /// any active "hide" filter, and — when any "show-only" filters exist — must
    /// match at least one of them.
    /// </summary>
    private bool PassesDisplayFilters(SessionViewModel vm)
    {
        bool anyShow = false, matchedShow = false;
        foreach (var f in Filters)
        {
            if (!f.Enabled || string.IsNullOrWhiteSpace(f.Pattern)) continue;
            bool isMatch = FilterFieldMatches(vm, f);
            if (f.Hide)
            {
                if (isMatch) return false;
            }
            else
            {
                anyShow = true;
                matchedShow |= isMatch;
            }
        }
        return !anyShow || matchedShow;
    }

    private static bool FilterFieldMatches(SessionViewModel vm, DisplayFilter f)
    {
        var m = vm.Model;
        string value = f.Field switch
        {
            "Host" => m.Host,
            "Method" => m.Method,
            "Status" => m.StatusCode.ToString(),
            "ContentType" => m.ResponseContentTypeShort,
            "Process" => m.ProcessName,
            "AnyHeader" => HttpSpy.Core.Rules.HttpModifier.RawHeaderBlock(m.RequestHeaders) +
                           HttpSpy.Core.Rules.HttpModifier.RawHeaderBlock(m.ResponseHeaders),
            "Body" => m.RequestBodyText + "\n" + m.ResponseBodyText,
            _ => m.FullUrl,
        };
        if (f.UseRegex)
        {
            try { return System.Text.RegularExpressions.Regex.IsMatch(value, f.Pattern,
                System.Text.RegularExpressions.RegexOptions.IgnoreCase); }
            catch { return false; }
        }
        return value.Contains(f.Pattern, StringComparison.OrdinalIgnoreCase);
    }

    private void UpdateStats()
    {
        var st = _engine.Statistics;
        string capturing = IsCapturing ? "● REC" : "■ Stopped";
        string sim = _engine.Options.ThrottleEnabled ? $"   ⚡ {_engine.Options.ThrottleKbps} kbps" : "";
        int shown = SessionsView.Count;
        string filtered = shown != AllSessions.Count ? $" ({shown} shown)" : "";

        StatsText = $"{capturing}   Sessions: {AllSessions.Count}{filtered}   " +
                    $"Active conns: {st.ActiveConnections}   " +
                    $"In: {Converters.ByteSizeConverter.Format(st.BytesReceived)}   " +
                    $"Out: {Converters.ByteSizeConverter.Format(st.BytesSent)}   Errors: {st.Errors}{sim}";

        if (string.IsNullOrEmpty(CertStatus))
            RefreshCertStatus();
    }

    /// <summary>
    /// Requests a grid re-filter. Many of these collapse into a single refresh on
    /// the next timer tick, which is what keeps the UI responsive when hundreds of
    /// transactions complete per second.
    /// </summary>
    private void RequestRefresh() => _refreshPending = true;

    private void FlushPendingRefresh()
    {
        if (!_refreshPending) return;
        _refreshPending = false;
        SessionsView.Refresh();
    }

    public string RootCertificatePath => _engine.CertificateAuthority.RootCertificatePath;

    public void Shutdown()
    {
        PersistSettings();
        try { SettingsStore.SaveRules(Rules.Select(r => r.Rule)); } catch { /* ignore */ }

        _statsTimer.Stop();
        _refreshTimer.Stop();

        // Release anything the user left paused, or the engine's worker tasks
        // would block on a breakpoint that no window is left to resolve.
        foreach (var bp in Breakpoints.ToList()) bp.Paused.Resume();
        Breakpoints.Clear();
        CurrentBreakpoint = null;

        _engine.Dispose();
    }
}
