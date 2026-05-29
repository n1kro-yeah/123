using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using HttpSpy.App.Services;
using HttpSpy.App.ViewModels;

namespace HttpSpy.App.Views;

public partial class MainWindow : Window, IDialogService
{
    public MainWindow()
    {
        InitializeComponent();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

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

    public async Task ShowMessageAsync(string title, string message) =>
        await MessageWindow.ShowAsync(this, title, message, confirm: false);

    public async Task<bool> ConfirmAsync(string title, string message) =>
        await MessageWindow.ShowAsync(this, title, message, confirm: true);

    public async Task SetClipboardAsync(string text)
    {
        if (Clipboard is not null) await Clipboard.SetTextAsync(text);
    }

    private static List<FilePickerFileType> ToFileTypes(IReadOnlyList<(string Name, string Ext)> filters) =>
        filters.Select(f => new FilePickerFileType(f.Name) { Patterns = new[] { $"*.{f.Ext}" } }).ToList();
}
