using System;
using System.IO;
using System.Linq;
using HttpSpy.Core.Analysis;
using Xunit;

namespace HttpSpy.Tests;

/// <summary>
/// A single analysis report says what is wrong now; it cannot say what changed.
/// These cover the baseline that turns the analyzer into a regression check.
/// </summary>
public class AnalysisBaselineTests
{
    private static Finding Make(string ruleId, string subject, FindingSeverity severity, int occurrences = 1) =>
        new()
        {
            RuleId = ruleId,
            Title = $"Title for {ruleId}",
            Category = FindingCategory.Security,
            Severity = severity,
            Subject = subject,
            Detail = "detail",
            Occurrences = occurrences,
        };

    private static AnalysisReport Report(int score, params Finding[] findings)
    {
        var report = new AnalysisReport { SessionsAnalyzed = 10 };
        report.Findings.AddRange(findings);
        report.OverallScore = score;
        return report;
    }

    [Fact]
    public void An_identical_run_reports_no_change()
    {
        var report = Report(90, Make("SEC001", "example.com", FindingSeverity.High));
        var comparison = AnalysisBaseline.From(report).Compare(report);

        Assert.True(comparison.IsClean);
        Assert.Empty(comparison.New);
        Assert.Empty(comparison.Fixed);
        Assert.All(comparison.Deltas, d => Assert.Equal(BaselineStatus.Unchanged, d.Status));
    }

    [Fact]
    public void A_finding_that_appeared_is_new_and_counts_as_a_regression()
    {
        var baseline = AnalysisBaseline.From(Report(95, Make("SEC001", "a.test", FindingSeverity.Low)));
        var current = Report(70,
            Make("SEC001", "a.test", FindingSeverity.Low),
            Make("SEC009", "b.test", FindingSeverity.Critical));

        var comparison = baseline.Compare(current);

        var added = Assert.Single(comparison.New);
        Assert.Equal("b.test", added.Subject);
        Assert.Equal(FindingSeverity.Critical, added.Severity);
        Assert.Null(added.PreviousSeverity);
        Assert.False(comparison.IsClean);
    }

    [Fact]
    public void A_finding_that_went_away_is_reported_as_fixed()
    {
        var baseline = AnalysisBaseline.From(Report(60,
            Make("SEC001", "a.test", FindingSeverity.High),
            Make("PRF003", "b.test", FindingSeverity.Medium)));

        var comparison = baseline.Compare(Report(85, Make("SEC001", "a.test", FindingSeverity.High)));

        var gone = Assert.Single(comparison.Fixed);
        Assert.Equal("b.test", gone.Subject);
        Assert.Equal(0, gone.Occurrences);
        Assert.Equal(1, gone.PreviousOccurrences);
        Assert.True(comparison.IsClean, "a fix is not a regression");
    }

    [Fact]
    public void Rising_severity_is_a_regression_falling_severity_is_not()
    {
        var baseline = AnalysisBaseline.From(Report(80, Make("SEC001", "a.test", FindingSeverity.Low)));

        var worse = baseline.Compare(Report(50, Make("SEC001", "a.test", FindingSeverity.Critical)));
        var worseDelta = Assert.Single(worse.Changed);
        Assert.Equal(FindingSeverity.Low, worseDelta.PreviousSeverity);
        Assert.True(worseDelta.IsRegression);
        Assert.False(worse.IsClean);

        var better = AnalysisBaseline.From(Report(50, Make("SEC001", "a.test", FindingSeverity.Critical)))
            .Compare(Report(80, Make("SEC001", "a.test", FindingSeverity.Low)));
        Assert.True(better.IsClean);
    }

    [Fact]
    public void A_changed_occurrence_count_is_reported_without_being_a_regression()
    {
        var baseline = AnalysisBaseline.From(Report(80, Make("SEC001", "a.test", FindingSeverity.Medium, 3)));
        var comparison = baseline.Compare(Report(80, Make("SEC001", "a.test", FindingSeverity.Medium, 9)));

        var delta = Assert.Single(comparison.Changed);
        Assert.Equal(3, delta.PreviousOccurrences);
        Assert.Equal(9, delta.Occurrences);
        Assert.False(delta.IsRegression);
    }

    [Fact]
    public void The_same_rule_on_a_different_subject_is_a_different_finding()
    {
        var baseline = AnalysisBaseline.From(Report(80, Make("SEC001", "a.test", FindingSeverity.High)));
        var comparison = baseline.Compare(Report(80, Make("SEC001", "b.test", FindingSeverity.High)));

        Assert.Single(comparison.New);
        Assert.Single(comparison.Fixed);
    }

    [Fact]
    public void Session_indices_never_affect_identity()
    {
        // The same misconfigured header is the same finding whether it turned up
        // on request 12 or request 4 000.
        var first = Make("SEC001", "a.test", FindingSeverity.High);
        first.SessionIndices.AddRange(new[] { 1, 2, 3 });
        var second = Make("SEC001", "a.test", FindingSeverity.High);
        second.SessionIndices.AddRange(new[] { 900, 901 });

        var comparison = AnalysisBaseline.From(Report(80, first)).Compare(Report(80, second));
        Assert.True(comparison.IsClean);
        Assert.Empty(comparison.New);
    }

    [Fact]
    public void Regressions_sort_above_everything_else()
    {
        var baseline = AnalysisBaseline.From(Report(90,
            Make("A", "same.test", FindingSeverity.Low),
            Make("B", "gone.test", FindingSeverity.High)));

        var comparison = baseline.Compare(Report(40,
            Make("A", "same.test", FindingSeverity.Low),
            Make("C", "new.test", FindingSeverity.Critical)));

        Assert.Equal(BaselineStatus.New, comparison.Deltas[0].Status);
        Assert.Equal(BaselineStatus.Unchanged, comparison.Deltas[^1].Status);
    }

    [Fact]
    public void Score_movement_is_reported()
    {
        var comparison = AnalysisBaseline.From(Report(95)).Compare(Report(72));
        Assert.Equal(95, comparison.BaselineScore);
        Assert.Equal(72, comparison.Score);
        Assert.Equal(-23, comparison.ScoreDelta);
        Assert.Contains("95 → 72", comparison.Summary());
    }

    [Fact]
    public void A_baseline_round_trips_through_a_file()
    {
        var path = Path.Combine(Path.GetTempPath(), $"httpspy-baseline-{Guid.NewGuid():N}.json");
        try
        {
            var report = Report(77,
                Make("SEC001", "a.test", FindingSeverity.High, 4),
                Make("PRF002", "b.test", FindingSeverity.Medium));

            AnalysisBaseline.From(report, "release-1.2").Save(path);
            var loaded = AnalysisBaseline.Load(path);

            Assert.Equal("release-1.2", loaded.Label);
            Assert.Equal(77, loaded.OverallScore);
            Assert.Equal(2, loaded.Findings.Count);
            Assert.True(loaded.Compare(report).IsClean);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void An_unreadable_baseline_is_rejected_clearly()
    {
        var path = Path.Combine(Path.GetTempPath(), $"httpspy-baseline-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, "{ not json");
            Assert.Throws<InvalidDataException>(() => AnalysisBaseline.Load(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void A_baseline_from_a_newer_format_is_refused_rather_than_misread()
    {
        var path = Path.Combine(Path.GetTempPath(), $"httpspy-baseline-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, "{ \"Version\": 99, \"Findings\": [] }");
            var ex = Assert.Throws<InvalidDataException>(() => AnalysisBaseline.Load(path));
            Assert.Contains("newer version", ex.Message);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void A_duplicated_key_in_a_baseline_keeps_the_worse_severity()
    {
        var baseline = new AnalysisBaseline
        {
            Findings =
            {
                new AnalysisBaseline.BaselineFinding
                    { RuleId = "SEC001", Subject = "a.test", Severity = FindingSeverity.Low },
                new AnalysisBaseline.BaselineFinding
                    { RuleId = "SEC001", Subject = "a.test", Severity = FindingSeverity.Critical },
            },
        };

        var delta = Assert.Single(baseline.Compare(Report(80, Make("SEC001", "a.test", FindingSeverity.Critical))).Deltas);
        Assert.Equal(BaselineStatus.Unchanged, delta.Status);
    }

    [Fact]
    public void Render_lists_the_regressions_first_and_states_the_verdict()
    {
        var baseline = AnalysisBaseline.From(Report(90, Make("A", "gone.test", FindingSeverity.Medium)), "v1");
        var text = AnalysisBaseline.Render(baseline.Compare(
            Report(40, Make("C", "new.test", FindingSeverity.Critical))));

        Assert.Contains("baseline \"v1\"", text);
        Assert.Contains("New (1)", text);
        Assert.Contains("Fixed (1)", text);
        Assert.True(text.IndexOf("New (1)", StringComparison.Ordinal) <
                    text.IndexOf("Fixed (1)", StringComparison.Ordinal));
    }

    [Fact]
    public void Render_says_so_plainly_when_nothing_regressed()
    {
        var report = Report(90, Make("A", "a.test", FindingSeverity.Low));
        var text = AnalysisBaseline.Render(AnalysisBaseline.From(report).Compare(report));
        Assert.Contains("No regressions", text);
    }

    [Fact]
    public void Every_finding_on_either_side_appears_exactly_once()
    {
        var baseline = AnalysisBaseline.From(Report(80,
            Make("A", "1", FindingSeverity.Low),
            Make("B", "2", FindingSeverity.Low)));

        var comparison = baseline.Compare(Report(80,
            Make("B", "2", FindingSeverity.Low),
            Make("C", "3", FindingSeverity.Low)));

        Assert.Equal(3, comparison.Deltas.Count);
        Assert.Equal(3, comparison.Deltas.Select(d => d.Key).Distinct().Count());
    }
}
