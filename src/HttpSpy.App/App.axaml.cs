using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data.Core.Plugins;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using HttpSpy.App.ViewModels;
using HttpSpy.App.Views;
using HttpSpy.Core.Models;

namespace HttpSpy.App;

public partial class App : Application
{
    private MainWindowViewModel? _viewModel;
    private bool _shutdownDone;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            DisableAvaloniaDataAnnotationValidation();
            InstallCrashHandlers();

            // A crashed run leaves its spilled bodies behind; clear out the ones
            // whose owning process is gone before adding more.
            try { BodyStore.CleanOrphans(); } catch { /* best effort */ }

            _viewModel = new MainWindowViewModel();
            var window = new MainWindow { DataContext = _viewModel };
            _viewModel.Dialogs = window;
            window.Closed += (_, _) => ShutdownOnce();
            desktop.MainWindow = window;

            // The engine owns a listening socket, live connections and (on Windows)
            // the system-proxy registry entries. Release them on every exit path,
            // not just when the main window happens to raise Closed.
            desktop.ShutdownRequested += (_, _) => ShutdownOnce();
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void ShutdownOnce()
    {
        if (_shutdownDone) return;
        _shutdownDone = true;
        try { _viewModel?.Shutdown(); }
        catch (Exception ex) { LogFatal("Shutdown", ex); }

        // Drop the temporary files holding spilled bodies.
        try { BodyStore.Shared.Dispose(); }
        catch (Exception ex) { LogFatal("BodyStore", ex); }
    }

    /// <summary>
    /// Routes otherwise-fatal exceptions to a log file and, where possible, keeps
    /// the app alive. A debugging proxy runs a lot of work on background tasks
    /// over hostile input; one faulted task should not silently kill a capture
    /// the user has been collecting for an hour.
    /// </summary>
    private void InstallCrashHandlers()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            LogFatal("AppDomain", e.ExceptionObject as Exception);

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            LogFatal("Task", e.Exception);
            e.SetObserved(); // a dropped connection must not tear down the process
        };

        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            LogFatal("UI", e.Exception);
            // Swallowing a layout/binding failure keeps the window usable; the
            // detail is preserved in the crash log for diagnosis.
            e.Handled = true;
        };
    }

    /// <summary>Where crash details are written, alongside settings and rules.</summary>
    public static string CrashLogPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "HttpSpy", "crash.log");

    private static void LogFatal(string source, Exception? ex)
    {
        if (ex is null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CrashLogPath)!);
            File.AppendAllText(CrashLogPath,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {source}: {ex}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
            // Logging must never itself throw while handling a crash.
        }
    }

    private static void DisableAvaloniaDataAnnotationValidation()
    {
        var pluginsToRemove = BindingPlugins.DataValidators
            .OfType<DataAnnotationsValidationPlugin>().ToArray();
        foreach (var plugin in pluginsToRemove)
            BindingPlugins.DataValidators.Remove(plugin);
    }
}
