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
            var name = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? "SearchBox" : "FilterBox";
            var box = this.FindControl<TextBox>(name);
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
            _vm.ColumnVisibilityChanged -= ApplyColumnVisibility;
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
            _vm.ColumnVisibilityChanged += ApplyColumnVisibility;
            ApplyColumnVisibility();
        }
    }

    /// <summary>Shows/hides the optional grid columns per the view model flags.</summary>
    private void ApplyColumnVisibility()
    {
        if (_vm is null) return;
        var grid = this.FindControl<DataGrid>("SessionGrid");
        if (grid is null) return;
        var vis = _vm.ColumnVisibility;
        foreach (var column in grid.Columns)
            if (column.Header is string header && vis.TryGetValue(header, out var v))
                column.IsVisible = v;
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
