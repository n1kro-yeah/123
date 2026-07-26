using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HttpSpy.Core.Analysis;
using HttpSpy.Core.Models;

namespace HttpSpy.App.ViewModels;

/// <summary>One row in the findings list.</summary>
public sealed class FindingViewModel
{
    public FindingViewModel(Finding finding)
    {
        Finding = finding;
    }

    public Finding Finding { get; }

    public string RuleId => Finding.RuleId;
    public string Title => Finding.Title;
    public string Subject => Finding.Subject;
    public string Detail => Finding.Detail;
    public string Remediation => Finding.Remediation;
    public string? Evidence => Finding.Evidence;
    public FindingSeverity Severity => Finding.Severity;
    public FindingCategory Category => Finding.Category;
    public string SeverityText => Finding.Severity.ToString().ToUpperInvariant();
    public string CategoryText => Finding.Category.ToString();

    public bool HasEvidence => !string.IsNullOrEmpty(Finding.Evidence);
    public bool HasRemediation => !string.IsNullOrEmpty(Finding.Remediation);
    public bool IsRepeated => Finding.Occurrences > 1;
    public string OccurrenceText => Finding.Occurrences > 1 ? $"{Finding.Occurrences:N0}×" : string.Empty;

    /// <summary>Session numbers the finding was seen on, for the "jump to" affordance.</summary>
    public string SessionsText => Finding.SessionIndices.Count == 0
        ? string.Empty
        : "#" + string.Join(", #", Finding.SessionIndices.Take(10)) +
          (Finding.SessionIndices.Count > 10 ? " …" : string.Empty);

    /// <summary>The first session index, used to select the offending row in the grid.</summary>
    public int? FirstSessionIndex =>
        Finding.SessionIndices.Count > 0 ? Finding.SessionIndices[0] : null;
}

/// <summary>A category row in the score panel.</summary>
public sealed class CategoryScoreViewModel
{
    public CategoryScoreViewModel(CategorySummary summary, int maxFindings)
    {
        Summary = summary;
        // Bar width is relative to the busiest category so the panel reads at a glance.
        BarWidth = maxFindings <= 0 ? 0 : 20 + (double)summary.Findings / maxFindings * 200;
    }

    public CategorySummary Summary { get; }
    public string Name => Summary.Category.ToString();
    public int Score => Summary.Score;
    public int Findings => Summary.Findings;
    public double BarWidth { get; }

    public string Breakdown =>
        string.Join("  ", new[]
        {
            Summary.Critical > 0 ? $"{Summary.Critical} critical" : null,
            Summary.High > 0 ? $"{Summary.High} high" : null,
            Summary.Medium > 0 ? $"{Summary.Medium} medium" : null,
            Summary.Low > 0 ? $"{Summary.Low} low" : null,
            Summary.Info > 0 ? $"{Summary.Info} info" : null,
        }.Where(s => s is not null));
}

/// <summary>
/// Drives the Analysis tab: runs <see cref="TrafficAnalyzer"/> over the current
/// capture on a background thread and presents the findings with filtering,
/// grouping and export.
/// </summary>
public sealed partial class AnalysisViewModel : ViewModelBase
{
    private readonly TrafficAnalyzer _analyzer = new();
    private CancellationTokenSource? _running;

    /// <summary>Supplies the sessions to analyse; set by the main view model.</summary>
    public Func<IReadOnlyList<HttpSession>>? SessionSource { get; set; }

    /// <summary>Raised when the user asks to jump to the session behind a finding.</summary>
    public event Action<int>? NavigateToSessionRequested;

    /// <summary>Raised when the user wants to export the report.</summary>
    public event Func<AnalysisReport, string, Task>? ExportRequested;

    public ObservableCollection<FindingViewModel> Findings { get; } = new();
    public ObservableCollection<CategoryScoreViewModel> Categories { get; } = new();

    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private bool _hasReport;
    [ObservableProperty] private int _overallScore = 100;
    [ObservableProperty] private string _verdict = "Not analysed yet";
    [ObservableProperty] private string _summaryLine = "";
    [ObservableProperty] private FindingViewModel? _selectedFinding;

    [ObservableProperty] private int _criticalCount;
    [ObservableProperty] private int _highCount;
    [ObservableProperty] private int _mediumCount;
    [ObservableProperty] private int _lowCount;
    [ObservableProperty] private int _infoCount;

    // ---- Filters -------------------------------------------------------------
    public string[] SeverityFilters { get; } = { "All", "Critical", "High", "Medium", "Low", "Info" };
    public string[] CategoryFilters { get; } =
        { "All", "Security", "Privacy", "Performance", "Caching", "Correctness", "Compatibility", "ApiDesign" };

    [ObservableProperty] private string _severityFilter = "All";
    [ObservableProperty] private string _categoryFilter = "All";
    [ObservableProperty] private string _searchText = "";

    partial void OnSeverityFilterChanged(string value) => ApplyFilters();
    partial void OnCategoryFilterChanged(string value) => ApplyFilters();
    partial void OnSearchTextChanged(string value) => ApplyFilters();

    private AnalysisReport? _report;

    /// <summary>The most recent report, or null when nothing has been analysed yet.</summary>
    public AnalysisReport? Report => _report;

    /// <summary>Runs the analyzer over the current capture.</summary>
    [RelayCommand]
    private async Task RunAnalysis()
    {
        var sessions = SessionSource?.Invoke();
        if (sessions is null || sessions.Count == 0)
        {
            SummaryLine = "Nothing to analyse — capture some traffic first.";
            HasReport = false;
            return;
        }

        // A second click while a run is in flight replaces it rather than queueing.
        _running?.Cancel();
        var cts = new CancellationTokenSource();
        _running = cts;

        IsRunning = true;
        SummaryLine = $"Analysing {sessions.Count:N0} session(s)…";

        try
        {
            // Analysis is CPU-bound over the whole capture; keep it off the UI thread.
            var report = await Task.Run(() => _analyzer.Analyze(sessions, BuildOptions(), cts.Token), cts.Token)
                .ConfigureAwait(true);

            if (cts.IsCancellationRequested) return;
            Present(report);
        }
        catch (OperationCanceledException)
        {
            // superseded by a newer run
        }
        catch (Exception ex)
        {
            SummaryLine = $"Analysis failed: {ex.Message}";
            HasReport = false;
        }
        finally
        {
            if (ReferenceEquals(_running, cts))
            {
                IsRunning = false;
                _running = null;
            }
            cts.Dispose();
        }
    }

    private static AnalysisOptions BuildOptions() => new();

    private void Present(AnalysisReport report)
    {
        _report = report;
        OverallScore = report.OverallScore;
        Verdict = report.Verdict;

        CriticalCount = report.CountOf(FindingSeverity.Critical);
        HighCount = report.CountOf(FindingSeverity.High);
        MediumCount = report.CountOf(FindingSeverity.Medium);
        LowCount = report.CountOf(FindingSeverity.Low);
        InfoCount = report.CountOf(FindingSeverity.Info);

        Categories.Clear();
        var active = report.Categories.Where(c => c.Findings > 0).ToList();
        int max = active.Count == 0 ? 0 : active.Max(c => c.Findings);
        foreach (var c in active.OrderBy(c => c.Score))
            Categories.Add(new CategoryScoreViewModel(c, max));

        SummaryLine = report.Findings.Count == 0
            ? $"No issues found across {report.SessionsAnalyzed:N0} session(s) in " +
              $"{report.Duration.TotalMilliseconds:F0} ms."
            : $"{report.Findings.Count:N0} distinct issue(s) across {report.SessionsAnalyzed:N0} session(s), " +
              $"{report.TotalOccurrences:N0} occurrence(s), analysed in {report.Duration.TotalMilliseconds:F0} ms.";

        HasReport = true;
        ApplyFilters();
    }

    private void ApplyFilters()
    {
        Findings.Clear();
        if (_report is null) return;

        IEnumerable<Finding> query = _report.Findings;

        if (SeverityFilter != "All" && Enum.TryParse<FindingSeverity>(SeverityFilter, out var severity))
            query = query.Where(f => f.Severity == severity);

        if (CategoryFilter != "All" && Enum.TryParse<FindingCategory>(CategoryFilter, out var category))
            query = query.Where(f => f.Category == category);

        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            var needle = SearchText.Trim();
            query = query.Where(f =>
                f.Title.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
                f.Subject.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
                f.Detail.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
                f.RuleId.Contains(needle, StringComparison.OrdinalIgnoreCase));
        }

        foreach (var f in query) Findings.Add(new FindingViewModel(f));

        if (_report.Findings.Count > 0 && Findings.Count == 0)
            SummaryLine = "No findings match the current filters.";
    }

    [RelayCommand]
    private void ClearFilters()
    {
        SeverityFilter = "All";
        CategoryFilter = "All";
        SearchText = "";
    }

    /// <summary>Selects the first session a finding was observed on, in the capture grid.</summary>
    [RelayCommand]
    private void GoToSession(FindingViewModel? finding)
    {
        var target = finding ?? SelectedFinding;
        if (target?.FirstSessionIndex is { } index) NavigateToSessionRequested?.Invoke(index);
    }

    [RelayCommand]
    private async Task ExportText()
    {
        if (_report is not null && ExportRequested is not null)
            await ExportRequested(_report, "txt");
    }

    [RelayCommand]
    private async Task ExportHtml()
    {
        if (_report is not null && ExportRequested is not null)
            await ExportRequested(_report, "html");
    }

    [RelayCommand]
    private async Task ExportJson()
    {
        if (_report is not null && ExportRequested is not null)
            await ExportRequested(_report, "json");
    }
}
