using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using HttpSpy.Core.Models;

namespace HttpSpy.App.ViewModels;

/// <summary>A single labelled bar in a dashboard chart.</summary>
public sealed class ChartItem
{
    public ChartItem(string label, long value, double fraction, IBrush brush)
    {
        Label = label;
        Value = value;
        // A zero-count bucket must render as nothing; the old formula's 20px floor
        // drew a stub bar next to a "0", which read as a non-zero value.
        BarWidth = value <= 0 ? 0 : 6 + fraction * 260;
        Brush = brush;
    }

    public string Label { get; }
    public long Value { get; }
    public double BarWidth { get; }
    public IBrush Brush { get; }
}

/// <summary>Aggregated analytics over the captured sessions (charts + totals).</summary>
public sealed class DashboardViewModel : ViewModelBase
{
    private static readonly IBrush[] Palette =
    {
        new SolidColorBrush(Color.FromRgb(0x29,0x80,0xB9)),
        new SolidColorBrush(Color.FromRgb(0x27,0xAE,0x60)),
        new SolidColorBrush(Color.FromRgb(0xE6,0x7E,0x22)),
        new SolidColorBrush(Color.FromRgb(0x8E,0x44,0xAD)),
        new SolidColorBrush(Color.FromRgb(0xC0,0x39,0x2B)),
        new SolidColorBrush(Color.FromRgb(0x16,0xA0,0x85)),
        new SolidColorBrush(Color.FromRgb(0xF3,0x9C,0x12)),
    };

    private int _total;
    private string _totalBytes = "0";
    private string _avgDuration = "0 ms";
    private int _errors;
    private int _httpsCount;

    public int Total { get => _total; private set => SetProperty(ref _total, value); }
    public string TotalBytes { get => _totalBytes; private set => SetProperty(ref _totalBytes, value); }
    public string AvgDuration { get => _avgDuration; private set => SetProperty(ref _avgDuration, value); }
    public int Errors { get => _errors; private set => SetProperty(ref _errors, value); }
    public int HttpsCount { get => _httpsCount; private set => SetProperty(ref _httpsCount, value); }

    public ObservableCollection<ChartItem> StatusDistribution { get; } = new();
    public ObservableCollection<ChartItem> TopHosts { get; } = new();
    public ObservableCollection<ChartItem> ContentTypes { get; } = new();
    public ObservableCollection<ChartItem> Methods { get; } = new();
    public ObservableCollection<ChartItem> TopProcesses { get; } = new();

    /// <summary>Requests per time bucket — the "Time Chart" in HTTP Debugger.</summary>
    public ObservableCollection<ChartItem> TimeChart { get; } = new();

    public void Recompute(IReadOnlyCollection<HttpSession> sessions)
    {
        Total = sessions.Count;
        long bytes = sessions.Sum(s => s.ResponseBodySize + s.RequestBodySize);
        TotalBytes = Converters.ByteSizeConverter.Format(bytes);
        var completed = sessions.Where(s => s.DurationMs > 0).ToList();
        AvgDuration = completed.Count > 0 ? $"{completed.Average(s => s.DurationMs):F0} ms" : "0 ms";
        Errors = sessions.Count(s => s.IsError);
        HttpsCount = sessions.Count(s => s.IsTls);

        BuildStatus(sessions);
        BuildBars(TopHosts, sessions.Where(s => !string.IsNullOrEmpty(s.Host))
            .GroupBy(s => s.Host).Select(g => (g.Key, (long)g.Count()))
            .OrderByDescending(t => t.Item2).Take(8));
        BuildBars(ContentTypes, sessions.Where(s => !string.IsNullOrEmpty(s.ResponseContentTypeShort))
            .GroupBy(s => s.ResponseContentTypeShort).Select(g => (g.Key, (long)g.Count()))
            .OrderByDescending(t => t.Item2).Take(8));
        BuildBars(Methods, sessions.GroupBy(s => s.Method)
            .Select(g => (g.Key, (long)g.Count())).OrderByDescending(t => t.Item2));
        BuildBars(TopProcesses, sessions.Where(s => !string.IsNullOrEmpty(s.ProcessName))
            .GroupBy(s => s.ProcessName).Select(g => (g.Key, (long)g.Count()))
            .OrderByDescending(t => t.Item2).Take(8));
        BuildTimeChart(sessions);
    }

    /// <summary>Buckets requests into ~12 equal time slices between first and last capture.</summary>
    private void BuildTimeChart(IReadOnlyCollection<HttpSession> sessions)
    {
        TimeChart.Clear();
        if (sessions.Count == 0) return;
        var times = sessions.Select(s => s.StartTime).OrderBy(t => t).ToList();
        var first = times[0];
        var last = times[^1];
        double span = (last - first).TotalSeconds;
        if (span <= 0) { TimeChart.Add(new ChartItem("now", sessions.Count, 1.0, Palette[0])); return; }

        const int buckets = 12;
        double slice = span / buckets;
        var counts = new long[buckets];
        foreach (var t in times)
        {
            int idx = (int)((t - first).TotalSeconds / slice);
            if (idx >= buckets) idx = buckets - 1;
            if (idx < 0) idx = 0;
            counts[idx]++;
        }
        long max = System.Math.Max(1, counts.Max());
        for (int i = 0; i < buckets; i++)
        {
            string label = $"+{i * slice:F0}s";
            TimeChart.Add(new ChartItem(label, counts[i], (double)counts[i] / max, Palette[2]));
        }
    }

    private void BuildStatus(IReadOnlyCollection<HttpSession> sessions)
    {
        StatusDistribution.Clear();
        var buckets = new (string Label, int Lo, int Hi, IBrush Brush)[]
        {
            ("2xx Success", 200, 299, new SolidColorBrush(Color.FromRgb(0x27,0xAE,0x60))),
            ("3xx Redirect", 300, 399, new SolidColorBrush(Color.FromRgb(0x8E,0x44,0xAD))),
            ("4xx Client", 400, 499, new SolidColorBrush(Color.FromRgb(0xE6,0x7E,0x22))),
            ("5xx Server", 500, 599, new SolidColorBrush(Color.FromRgb(0xC0,0x39,0x2B))),
        };
        long max = 1;
        var counts = buckets.Select(b => (long)sessions.Count(s => s.StatusCode >= b.Lo && s.StatusCode <= b.Hi)).ToArray();
        if (counts.Length > 0) max = System.Math.Max(1, counts.Max());
        for (int i = 0; i < buckets.Length; i++)
            StatusDistribution.Add(new ChartItem(buckets[i].Label, counts[i], (double)counts[i] / max, buckets[i].Brush));
    }

    private void BuildBars(ObservableCollection<ChartItem> target, IEnumerable<(string Label, long Count)> data)
    {
        target.Clear();
        var list = data.ToList();
        long max = list.Count > 0 ? System.Math.Max(1, list.Max(d => d.Count)) : 1;
        int i = 0;
        foreach (var (label, count) in list)
            target.Add(new ChartItem(label, count, (double)count / max, Palette[i++ % Palette.Length]));
    }
}
