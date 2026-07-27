using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using HttpSpy.App.Services;
using HttpSpy.App.ViewModels;
using HttpSpy.Core.Models;

namespace HttpSpy.App.Views;

public partial class MainWindow : Window, IDialogService
{
    private MainWindowViewModel? _vm;

    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        AddHandler(KeyDownEvent, OnWindowKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    /// <summary>Ctrl+F focuses the quick filter, Ctrl+Shift+F the content search.</summary>
    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.Key == Key.F)
        {
            // Ctrl+Shift+F is "find across everything" in every editor people
            // already use; the per-grid boxes stay one keystroke away.
            if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
            {
                OnSearchRequested();
                e.Handled = true;
                return;
            }

            var box = this.FindControl<TextBox>("FilterBox");
            if (box is not null)
            {
                box.Focus();
                box.SelectAll();
                e.Handled = true;
            }
        }
        else if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.Key == Key.E)
        {
            var box = this.FindControl<TextBox>("SearchBox");
            if (box is not null)
            {
                box.Focus();
                box.SelectAll();
                e.Handled = true;
            }
        }
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_vm is not null)
        {
            _vm.SessionAppended -= OnSessionAppended;
            _vm.CompareRequested -= OnCompareRequested;
            _vm.OptionsRequested -= OnOptionsRequested;
            _vm.FiltersRequested -= OnFiltersRequested;
            _vm.ConverterRequested -= OnConverterRequested;
            _vm.CaptureFiltersRequested -= OnCaptureFiltersRequested;
            _vm.RegexTesterRequested -= OnRegexTesterRequested;
            _vm.SearchRequested -= OnSearchRequested;
            _vm.ColumnVisibilityChanged -= ApplyColumnVisibility;
            _vm.ColumnLayoutResetRequested -= ResetColumnLayout;
            _vm.LogLines.CollectionChanged -= OnLogLinesChanged;
        }
        _vm = DataContext as MainWindowViewModel;
        if (_vm is not null)
        {
            _vm.LogLines.CollectionChanged += OnLogLinesChanged;
            _vm.SessionAppended += OnSessionAppended;
            _vm.CompareRequested += OnCompareRequested;
            _vm.OptionsRequested += OnOptionsRequested;
            _vm.FiltersRequested += OnFiltersRequested;
            _vm.ConverterRequested += OnConverterRequested;
            _vm.CaptureFiltersRequested += OnCaptureFiltersRequested;
            _vm.RegexTesterRequested += OnRegexTesterRequested;
            _vm.SearchRequested += OnSearchRequested;
            _vm.ColumnVisibilityChanged += ApplyColumnVisibility;
            _vm.ColumnLayoutResetRequested += ResetColumnLayout;
            ApplyColumnVisibility();
            ApplyColumnLayout();

            // Ask about a crashed session once the window can actually show a
            // dialog — the view model is constructed long before that.
            Avalonia.Threading.Dispatcher.UIThread.Post(
                () => _ = _vm.CheckForRecoveryAsync(),
                Avalonia.Threading.DispatcherPriority.Background);
        }
    }

    /// <summary>
    /// Records the shutdown as clean, so the next launch does not offer to
    /// recover a capture the user deliberately closed.
    /// </summary>
    protected override void OnClosed(EventArgs e)
    {
        SaveColumnLayout();
        _vm?.ShutdownCleanly();
        base.OnClosed(e);
    }

    // ---- Column layout -------------------------------------------------------
    //
    // Visibility was already persisted, but order and width were not: drag a
    // column somewhere useful, restart, and it was back where it started. That
    // is the kind of forgetfulness that makes an application feel unfinished.

    /// <summary>Default order and widths, captured before any saved layout is applied.</summary>
    private int[]? _defaultOrder;
    private DataGridLength[]? _defaultWidths;

    private void ApplyColumnLayout()
    {
        var grid = this.FindControl<DataGrid>("SessionGrid");
        if (grid is null || _vm is null) return;

        // Declared widths, not measured ones: layout has not run yet at this
        // point, so ActualWidth would record every default as zero.
        _defaultOrder ??= grid.Columns.Select(c => c.DisplayIndex).ToArray();
        _defaultWidths ??= grid.Columns.Select(c => c.Width).ToArray();

        var saved = new Dictionary<string, (int Index, double Width)>(StringComparer.Ordinal);
        foreach (var entry in _vm.SavedColumnLayout)
        {
            var parts = entry.Split('|');
            if (parts.Length != 3) continue;
            if (!int.TryParse(parts[1], out int index)) continue;
            if (!double.TryParse(parts[2], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double width)) continue;
            saved[parts[0]] = (index, width);
        }
        if (saved.Count == 0) return;

        foreach (var column in grid.Columns)
        {
            if (column.Tag is not string tag || !saved.TryGetValue(tag, out var layout)) continue;
            // A stale layout from an older build can carry an index past the end
            // of today's column set; clamping beats throwing during startup.
            column.DisplayIndex = Math.Clamp(layout.Index, 0, grid.Columns.Count - 1);
            if (layout.Width > 20) column.Width = new DataGridLength(layout.Width);
        }
    }

    private void SaveColumnLayout()
    {
        var grid = this.FindControl<DataGrid>("SessionGrid");
        if (grid is null || _vm is null) return;

        var entries = grid.Columns
            .Where(c => c.Tag is string)
            .Select(c => string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"{(string)c.Tag!}|{c.DisplayIndex}|{c.ActualWidth:0}"))
            .ToList();

        try { _vm.CaptureColumnLayout(entries); } catch { /* never block shutdown */ }
    }

    private void ResetColumnLayout()
    {
        var grid = this.FindControl<DataGrid>("SessionGrid");
        if (grid is null || _defaultOrder is null || _defaultWidths is null) return;

        for (int i = 0; i < grid.Columns.Count; i++)
        {
            grid.Columns[i].DisplayIndex = _defaultOrder[i];
            grid.Columns[i].Width = _defaultWidths[i];
        }
    }

    /// <summary>Shows/hides the optional grid columns per the view model flags.</summary>
    private void ApplyColumnVisibility()
    {
        if (_vm is null) return;
        var grid = this.FindControl<DataGrid>("SessionGrid");
        if (grid is null) return;
        // Match on Tag, not Header: headers are localized and would stop
        // matching the moment the interface language changes.
        var vis = _vm.ColumnVisibility;
        foreach (var column in grid.Columns)
        {
            var key = column.Tag as string ?? column.Header as string;
            if (key is not null && vis.TryGetValue(key, out var v)) column.IsVisible = v;
        }
    }

    private void OnSessionAppended(SessionViewModel vm)
    {
        var grid = this.FindControl<DataGrid>("SessionGrid");
        try { grid?.ScrollIntoView(vm, null); } catch { /* grid not ready */ }
    }

    private void OnCompareRequested(HttpSession left, HttpSession right)
    {
        new CompareWindow(left, right).Show(this);
    }

    private void OnOptionsRequested()
    {
        if (_vm is not null) new OptionsWindow(_vm).ShowDialog(this);
    }

    private void OnFiltersRequested()
    {
        if (_vm is not null) new FiltersWindow(_vm).ShowDialog(this);
    }

    private void OnConverterRequested() => new ConverterWindow().ShowDialog(this);

    private void OnCaptureFiltersRequested()
    {
        if (_vm is not null) new CaptureFiltersWindow(_vm).ShowDialog(this);
    }

    // Modeless: the point of the tester is to keep it open beside the rule editor
    // while iterating on a pattern.
    private void OnRegexTesterRequested() => new RegexTesterWindow().Show(this);

    private SearchWindow? _searchWindow;

    /// <summary>
    /// Opens the search-everything window, reusing the existing one so repeated
    /// Ctrl+Shift+F does not stack windows on top of each other.
    /// </summary>
    private void OnSearchRequested()
    {
        if (_vm is null) return;

        if (_searchWindow is not null)
        {
            _searchWindow.Activate();
            return;
        }

        _searchWindow = new SearchWindow(
            () => _vm.AllSessions.Select(v => v.Model).ToList(),
            SelectSession);
        _searchWindow.Closed += (_, _) => _searchWindow = null;
        _searchWindow.Show(this);
    }

    /// <summary>Brings a transaction into view in the grid and selects it.</summary>
    private void SelectSession(HttpSession session)
    {
        if (_vm is null) return;
        var vm = _vm.AllSessions.FirstOrDefault(v => v.Model.Id == session.Id);
        if (vm is null) return;

        _vm.ActiveTabIndex = MainWindowViewModel.TabCapture;
        _vm.SelectedSession = vm;
        var grid = this.FindControl<DataGrid>("SessionGrid");
        try { grid?.ScrollIntoView(vm, null); } catch { /* grid not ready */ }
        Activate();
    }

    /// <summary>Keeps the diagnostic log pinned to the latest entry as events arrive.</summary>
    private void OnLogLinesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Add) return;
        var scroll = this.FindControl<ScrollViewer>("LogScroll");
        if (scroll is not null)
            Avalonia.Threading.Dispatcher.UIThread.Post(scroll.ScrollToEnd,
                Avalonia.Threading.DispatcherPriority.Background);
    }

    public async Task<string?> SaveFileAsync(string title, string suggestedName,
        IReadOnlyList<(string Name, string Ext)> filters)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggestedName,
            FileTypeChoices = ToFileTypes(filters),
        });
        return file?.TryGetLocalPath();
    }

    public async Task<string?> OpenFileAsync(string title, IReadOnlyList<(string Name, string Ext)> filters)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = ToFileTypes(filters),
        });
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    public async Task<string?> SelectFolderAsync(string title)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
        });
        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    }

    public async Task ShowMessageAsync(string title, string message) =>
        await MessageWindow.ShowAsync(this, title, message, confirm: false);

    public async Task<bool> ConfirmAsync(string title, string message) =>
        await MessageWindow.ShowAsync(this, title, message, confirm: true);

    public async Task SetClipboardAsync(string text)
    {
        if (Clipboard is not null) await Clipboard.SetTextAsync(text);
    }

    public async Task<string?> GetClipboardAsync() =>
        Clipboard is null ? null : await Clipboard.GetTextAsync();

    private static List<FilePickerFileType> ToFileTypes(IReadOnlyList<(string Name, string Ext)> filters) =>
        filters.Select(f => new FilePickerFileType(f.Name) { Patterns = new[] { $"*.{f.Ext}" } }).ToList();
}
