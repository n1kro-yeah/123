using System.Collections.Generic;
using System.Threading.Tasks;

namespace HttpSpy.App.Services;

/// <summary>Abstracts platform dialogs / clipboard so view models stay testable.</summary>
public interface IDialogService
{
    Task<string?> SaveFileAsync(string title, string suggestedName, IReadOnlyList<(string Name, string Ext)> filters);
    Task<string?> OpenFileAsync(string title, IReadOnlyList<(string Name, string Ext)> filters);
    Task<string?> SelectFolderAsync(string title);
    Task ShowMessageAsync(string title, string message);
    Task<bool> ConfirmAsync(string title, string message);
    Task SetClipboardAsync(string text);
}
