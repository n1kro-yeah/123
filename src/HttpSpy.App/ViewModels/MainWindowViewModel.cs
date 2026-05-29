using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HttpSpy.App.Services;
using HttpSpy.Core;
using HttpSpy.Core.Export;
using HttpSpy.Core.Models;
using HttpSpy.Core.Proxy;
using HttpSpy.Core.Rules;

namespace HttpSpy.App.ViewModels;

/// <summary>
/// The application's root view model. Owns the capture engine, the session
/// collection (with filtering), the inspector, dashboard, submitter, rules and
/// breakpoint queue, and exposes every toolbar/menu command.
/// </summary>
public sealed partial class MainWindowViewModel : ViewModelBase
{
    private readonly ProxyEngine _engine;
    private readonly Dictionary<Guid, SessionViewModel> _index = new();
    private readonly List<SessionViewModel> _all = new();
    private readonly DispatcherTimer _statsTimer;

    public IDialogService? Dialogs { get; set; }

    public MainWindowViewModel() : this(new ProxyEngine(new ProxyOptions())) { }

    public MainWindowViewModel(ProxyEngine engine)
    {
        _engine = engine;
        _engine.SessionStarted += OnSessionStarted;
        _engine.SessionCompleted += OnSessionCompleted;
        _engine.SessionUpdated += OnSessionUpdated;
        _engine.TransactionPaused += OnTransactionPaused;
        _engine.Log += OnLog;

        ListenPort = _engine.Options.ListenPort;
        DecryptHttps = _engine.Options.DecryptHttps;
        SetSystemProxy = _engine.Options.SetSystemProxy;

        Submitter.RequestSent += OnSubmitterRequest;

        _statsTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(750) };
        _statsTimer.Tick += (_, _) => UpdateStats();
        _statsTimer.Start();

        SeedDefaultRules();
    }

    // ---- Collections ---------------------------------------------------------
    public ObservableCollection<SessionViewModel> Sessions { get; } = new();
    public ObservableCollection<string> LogLines { get; } = new();
    public ObservableCollection<RuleViewModel> Rules { get; } = new();
    public ObservableCollection<BreakpointViewModel> Breakpoints { get; } = new();

    public InspectorViewModel Inspector { get; } = new();
    public DashboardViewModel Dashboard { get; } = new();
    public SubmitterViewModel Submitter { get; } = new();

    // ---- Capture state -------------------------------------------------------
    [ObservableProperty] private bool _isCapturing;
    [ObservableProperty] private int _listenPort;
    [ObservableProperty] private bool _decryptHttps;
    [ObservableProperty] private bool _setSystemProxy;
    [ObservableProperty] private string _statusText = "Idle";
    [ObservableProperty] private string _certStatus = "";
    [ObservableProperty] private SessionViewModel? _selectedSession;
    [ObservableProperty] private BreakpointViewModel? _currentBreakpoint;

    // ---- Filtering -----------------------------------------------------------
    [ObservableProperty] private string _filterText = "";
    [ObservableProperty] private bool _errorsOnly;
    [ObservableProperty] private string _methodFilter = "All";

    public string[] MethodFilters { get; } = { "All", "GET", "POST", "PUT", "PATCH", "DELETE", "HEAD", "OPTIONS" };

    partial void OnFilterTextChanged(string value) => RebuildFilter();
    partial void OnErrorsOnlyChanged(bool value) => RebuildFilter();
    partial void OnMethodFilterChanged(string value) => RebuildFilter();

    partial void OnSelectedSessionChanged(SessionViewModel? value)
    {
        Inspector.Session = value?.Model;
    }

    partial void OnListenPortChanged(int value) => _engine.Options.ListenPort = value;
    partial void OnDecryptHttpsChanged(bool value) => _engine.Options.DecryptHttps = value;
    partial void OnSetSystemProxyChanged(bool value) => _engine.Options.SetSystemProxy = value;

    // ---- Engine event handlers (marshaled to UI thread) ----------------------
    private void OnSessionStarted(HttpSession s) => Dispatcher.UIThread.Post(() =>
    {
        var vm = new SessionViewModel(s);
        _index[s.Id] = vm;
        _all.Add(vm);
        if (PassesFilter(vm)) Sessions.Add(vm);
    });

    private void OnSessionCompleted(HttpSession s) => Dispatcher.UIThread.Post(() =>
    {
        if (_index.TryGetValue(s.Id, out var vm))
        {
            vm.Refresh();
            if (ReferenceEquals(vm, SelectedSession)) Inspector.Session = s;
            // a session may only pass the filter once completed (e.g. errors-only)
            if (PassesFilter(vm) && !Sessions.Contains(vm)) Sessions.Add(vm);
            else if (!PassesFilter(vm)) Sessions.Remove(vm);
        }
    });

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

    private void OnSubmitterRequest(HttpSession s) => Dispatcher.UIThread.Post(() =>
    {
        var vm = new SessionViewModel(s);
        _index[s.Id] = vm;
        _all.Add(vm);
        if (PassesFilter(vm)) Sessions.Add(vm);
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
            StatusText = $"Capturing on 127.0.0.1:{_engine.Options.ListenPort}";
        }
        catch (Exception ex)
        {
            StatusText = $"Failed to start: {ex.Message}";
            Dialogs?.ShowMessageAsync("Cannot start capture", ex.Message);
        }
    }

    [RelayCommand]
    private void StopCapture()
    {
        if (!IsCapturing) return;
        _engine.Stop();
        IsCapturing = false;
        StatusText = "Stopped";
    }

    [RelayCommand]
    private void ClearSessions()
    {
        Sessions.Clear();
        _all.Clear();
        _index.Clear();
        SelectedSession = null;
        Inspector.Session = null;
    }

    [RelayCommand]
    private void DeleteSelected()
    {
        if (SelectedSession is null) return;
        var vm = SelectedSession;
        Sessions.Remove(vm);
        _all.Remove(vm);
        _index.Remove(vm.Model.Id);
        SelectedSession = null;
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
    private void ResendToSubmitter()
    {
        if (SelectedSession is null) return;
        Submitter.LoadFrom(SelectedSession.Model);
        ActiveTabIndex = 2; // Submitter tab
    }

    [ObservableProperty] private int _activeTabIndex;

    partial void OnActiveTabIndexChanged(int value)
    {
        if (value == 1) RefreshDashboard();
    }

    [RelayCommand]
    private void RefreshDashboard() => Dashboard.Recompute(_all.Select(v => v.Model).ToList());

    [RelayCommand]
    private async Task SaveSession()
    {
        if (Dialogs is null) return;
        var path = await Dialogs.SaveFileAsync("Save session", "capture.hspy",
            new[] { ("HttpSpy session", "hspy") });
        if (path is null) return;
        try
        {
            await SessionStore.SaveAsync(path, _all.Select(v => v.Model));
            StatusText = $"Saved {_all.Count} sessions to {path}";
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
                _all.Add(vm);
                if (PassesFilter(vm)) Sessions.Add(vm);
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
            HarExporter.ExportToFile(_all.Select(v => v.Model), path);
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
            CsvExporter.Export(_all.Select(v => v.Model), path);
            StatusText = $"Exported CSV to {path}";
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
                CertStatus = "Root CA trusted";
                await Dialogs.ShowMessageAsync("Certificate installed",
                    "The HttpSpy root certificate was added to your Trusted Root store. HTTPS decryption is ready.");
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
            await Dialogs.ShowMessageAsync("Trust the root certificate",
                "Automatic trust-store installation is only implemented on Windows.\n\n" +
                $"The root certificate is exported at:\n{_engine.CertificateAuthority.RootCertificatePath}\n\n" +
                "Import it into your OS / browser trust store to decrypt HTTPS.");
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
    }

    [ObservableProperty] private RuleViewModel? _selectedRule;

    private void SeedDefaultRules()
    {
        var highlightErrors = new Rule
        {
            Name = "Highlight server errors", Action = RuleAction.Highlight,
            UrlMatchMode = MatchMode.Wildcard, UrlPattern = "*", StatusFilter = null,
            HighlightColor = 0xFFFFE0E0, Enabled = false
        };
        Rules.Add(new RuleViewModel(highlightErrors));
        ApplyRules();
    }

    // ---- Filtering helpers ---------------------------------------------------
    private bool PassesFilter(SessionViewModel vm)
    {
        if (ErrorsOnly && !vm.Model.IsError) return false;
        if (MethodFilter != "All" && !string.Equals(vm.Method, MethodFilter, StringComparison.OrdinalIgnoreCase))
            return false;
        if (!string.IsNullOrWhiteSpace(FilterText))
        {
            var f = FilterText.Trim();
            bool match = vm.Url.Contains(f, StringComparison.OrdinalIgnoreCase)
                         || vm.Host.Contains(f, StringComparison.OrdinalIgnoreCase)
                         || vm.Method.Contains(f, StringComparison.OrdinalIgnoreCase)
                         || vm.StatusCode.ToString().Contains(f)
                         || vm.ProcessName.Contains(f, StringComparison.OrdinalIgnoreCase)
                         || vm.ContentType.Contains(f, StringComparison.OrdinalIgnoreCase);
            if (!match) return false;
        }
        return true;
    }

    private void RebuildFilter()
    {
        Sessions.Clear();
        foreach (var vm in _all)
            if (PassesFilter(vm)) Sessions.Add(vm);
    }

    private void UpdateStats()
    {
        var st = _engine.Statistics;
        string capturing = IsCapturing ? "● REC" : "■ Stopped";
        StatusText = $"{capturing}   Sessions: {_all.Count}   Active conns: {st.ActiveConnections}   " +
                     $"In: {Converters.ByteSizeConverter.Format(st.BytesReceived)}   " +
                     $"Out: {Converters.ByteSizeConverter.Format(st.BytesSent)}   Errors: {st.Errors}";
        if (OperatingSystem.IsWindows() && string.IsNullOrEmpty(CertStatus))
        {
            try
            {
                CertStatus = CertTrust.IsInstalled(_engine.CertificateAuthority.RootCertificate)
                    ? "Root CA trusted" : "Root CA not trusted";
            }
            catch { /* ignore */ }
        }
    }

    public string RootCertificatePath => _engine.CertificateAuthority.RootCertificatePath;

    public void Shutdown()
    {
        _statsTimer.Stop();
        _engine.Dispose();
    }
}
