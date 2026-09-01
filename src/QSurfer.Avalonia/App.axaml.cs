using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.Markup.Xaml;
using QSurfer.Core.Services;
using QSurfer.Avalonia.Services;

namespace QSurfer.Avalonia;

public sealed partial class App : Application
{
    private readonly WindowsHotkeyService _hotkey = new();
    private TrayIcon? _trayIcon;
    private MainWindow? _mainWindow;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
        TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;
        Dispatcher.UIThread.UnhandledException += Dispatcher_UnhandledException;

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _mainWindow = new MainWindow();
            desktop.MainWindow = _mainWindow;
            ClearExpiredFolderRestoreOverride();
            desktop.Exit += (_, _) => DisposeWindowsServices();

            Program.SingleInstance.ActivationRequested += () => Dispatcher.UIThread.Post(RestoreMainWindow);
            _hotkey.Pressed += () => Dispatcher.UIThread.Post(ToggleMainWindow);
            CreateTrayIcon();

            var registration = ConfigureGlobalHotkey(_mainWindow.Config.Behavior.GlobalHotkey);
            if (!registration.Registered)
            {
                _mainWindow.SetStatus(registration.Error);
            }

            var executable = Environment.ProcessPath ?? "unknown";
            AppLogger.Session($"session started executable=\"{executable}\" pid={Environment.ProcessId} avalonia=True");
        }

        base.OnFrameworkInitializationCompleted();
    }

    public HotkeyRegistrationResult ConfigureGlobalHotkey(string shortcut)
    {
        var registration = _hotkey.Configure(shortcut);
        if (registration.Registered && _mainWindow != null)
        {
            _mainWindow.Config.Behavior.GlobalHotkey = registration.NormalizedShortcut;
        }
        return registration;
    }

    private void CreateTrayIcon()
    {
        var showItem = new NativeMenuItem("Show QSurfer");
        showItem.Click += (_, _) => RestoreMainWindow();
        var exitItem = new NativeMenuItem("Exit");
        exitItem.Click += (_, _) => _mainWindow?.ExitApplication();
        _trayIcon = new TrayIcon
        {
            Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://QSurfer/Assets/app.ico"))),
            ToolTipText = "QSurfer",
            IsVisible = true,
            Menu = new NativeMenu
            {
                Items = { showItem, new NativeMenuItemSeparator(), exitItem },
            },
        };
        _trayIcon.Clicked += (_, _) => RestoreMainWindow();
    }

    private void ToggleMainWindow()
    {
        if (_mainWindow == null)
        {
            return;
        }
        if (_mainWindow.IsVisible && _mainWindow.WindowState != WindowState.Minimized)
        {
            _mainWindow.HideToTray();
            return;
        }
        RestoreMainWindow();
    }

    private void RestoreMainWindow() => _mainWindow?.RestoreFromTray();

    private void ClearExpiredFolderRestoreOverride()
    {
        if (_mainWindow?.Config.Behavior.OneTimeFolderRestore != true)
        {
            return;
        }

        _mainWindow.Config.Behavior.OneTimeFolderRestore = false;
        ConfigStore.Save(_mainWindow.Config);
        AppLogger.Info("timeline", "cleared unused one-time folder restore override from a prior session");
    }

    private void DisposeWindowsServices()
    {
        AppLogger.Session($"session ended pid={Environment.ProcessId}");
        AppDomain.CurrentDomain.UnhandledException -= CurrentDomain_UnhandledException;
        TaskScheduler.UnobservedTaskException -= TaskScheduler_UnobservedTaskException;
        Dispatcher.UIThread.UnhandledException -= Dispatcher_UnhandledException;
        _trayIcon?.Dispose();
        _trayIcon = null;
        _hotkey.Dispose();
    }

    private void Dispatcher_UnhandledException(object? sender, DispatcherUnhandledExceptionEventArgs e)
    {
        AppLogger.Error("app", e.Exception, "unhandled UI exception");
        _mainWindow?.SetStatus("An unexpected error was handled. Details were written to the QSurfer log.");
        e.Handled = true;
    }

    private static void CurrentDomain_UnhandledException(object? sender, UnhandledExceptionEventArgs e)
    {
        var exception = e.ExceptionObject as Exception ?? new InvalidOperationException(e.ExceptionObject?.ToString() ?? "Unknown fatal error.");
        AppLogger.Error("app", exception, "unhandled process exception");
    }

    private static void TaskScheduler_UnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        AppLogger.Error("app", e.Exception, "unobserved background task exception");
        e.SetObserved();
    }
}
