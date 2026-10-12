using Avalonia;
using System.Runtime.InteropServices;
using QSurfer.Avalonia.Services;
using QSurfer.Core.Services;

namespace QSurfer.Avalonia;

internal static class Program
{
    internal static SingleInstanceService SingleInstance { get; } = new();

    [STAThread]
    public static void Main(string[] args)
    {
        if (LocalIndexerManager.IsSupported && args.Any(argument => string.Equals(argument, "--install-local-indexer-service", StringComparison.OrdinalIgnoreCase)))
        {
            RunElevatedLocalIndexerInstall();
            return;
        }

        if (LocalIndexerManager.IsSupported && args.Any(argument => string.Equals(argument, "--stop-local-indexer-service", StringComparison.OrdinalIgnoreCase)))
        {
            RunElevatedLocalIndexerStop();
            return;
        }

        if (LocalIndexerManager.IsSupported && args.Any(argument => string.Equals(argument, "--start-local-indexer-service", StringComparison.OrdinalIgnoreCase)))
        {
            RunElevatedLocalIndexerStart();
            return;
        }

        if (LocalIndexerManager.IsSupported && args.Any(argument => string.Equals(argument, "--uninstall-local-indexer-service", StringComparison.OrdinalIgnoreCase)))
        {
            RunElevatedLocalIndexerUninstall();
            return;
        }

        if (LocalIndexerManager.IsSupported && args.Any(argument => string.Equals(argument, "--upgrade-local-indexer-service", StringComparison.OrdinalIgnoreCase)))
        {
            RunElevatedLocalIndexerUpgrade();
            return;
        }

        RuntimeMode.Configure(args);
        RuntimeMode.ResetDemoWorkspace();
        if (!SingleInstance.TryAcquire())
        {
            return;
        }

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            SingleInstance.Dispose();
        }
    }

    private static void RunElevatedLocalIndexerInstall()
    {
        try
        {
            using var manager = new LocalIndexerManager(stateDirectory: OperatingSystem.IsWindows() ? LocalIndexerManager.MachineStateRoot : null);
            if (OperatingSystem.IsLinux())
            {
                manager.PrepareLinuxServiceAsync().GetAwaiter().GetResult();
            }
            else
            {
                manager.EnableAsync(forceWindowsService: true).GetAwaiter().GetResult();
            }
            ShowInstallerResult("QSurfer local indexing service is installed, running, and healthy.", 0x40);
            Environment.ExitCode = 0;
        }
        catch (Exception exception)
        {
            // Never include configuration paths or secrets in this one-shot helper.
            ShowInstallerResult($"QSurfer could not install the local indexing service. {exception.Message}", 0x10);
            Environment.ExitCode = 1;
        }
    }

    private static void RunElevatedLocalIndexerStop()
    {
        try
        {
            using var manager = new LocalIndexerManager(stateDirectory: OperatingSystem.IsWindows() ? LocalIndexerManager.MachineStateRoot : null);
            manager.DisableAsync().GetAwaiter().GetResult();
            ShowInstallerResult("QSurfer local indexing service has stopped. Its index and configuration were kept.", 0x40);
            Environment.ExitCode = 0;
        }
        catch (Exception exception)
        {
            ShowInstallerResult($"QSurfer could not stop the local indexing service. {exception.Message}", 0x10);
            Environment.ExitCode = 1;
        }
    }

    private static void RunElevatedLocalIndexerStart()
    {
        try
        {
            using var manager = new LocalIndexerManager(stateDirectory: OperatingSystem.IsWindows() ? LocalIndexerManager.MachineStateRoot : null);
            manager.StartAsync(forceWindowsService: OperatingSystem.IsWindows()).GetAwaiter().GetResult();
            ShowInstallerResult("QSurfer local indexing service has started.", 0x40);
            Environment.ExitCode = 0;
        }
        catch (Exception exception)
        {
            ShowInstallerResult($"QSurfer could not start the local indexing service. {exception.Message}", 0x10);
            Environment.ExitCode = 1;
        }
    }

    private static void RunElevatedLocalIndexerUninstall()
    {
        try
        {
            using var manager = new LocalIndexerManager(stateDirectory: OperatingSystem.IsWindows() ? LocalIndexerManager.MachineStateRoot : null);
            manager.UninstallAsync(removeIndexData: false).GetAwaiter().GetResult();
            ShowInstallerResult("QSurfer local indexing service was uninstalled. Indexed data was kept.", 0x40);
            Environment.ExitCode = 0;
        }
        catch (Exception exception)
        {
            ShowInstallerResult($"QSurfer could not uninstall the local indexing service. {exception.Message}", 0x10);
            Environment.ExitCode = 1;
        }
    }

    private static void RunElevatedLocalIndexerUpgrade()
    {
        try
        {
            using var manager = new LocalIndexerManager(stateDirectory: OperatingSystem.IsWindows() ? LocalIndexerManager.MachineStateRoot : null);
            var result = manager.UpgradeAsync().GetAwaiter().GetResult();
            ShowInstallerResult($"QSurfer local QIndexer updated to {result.Version} and is healthy.", 0x40);
            Environment.ExitCode = 0;
        }
        catch (Exception exception)
        {
            ShowInstallerResult($"QSurfer could not update the local QIndexer. {exception.Message}", 0x10);
            Environment.ExitCode = 1;
        }
    }

    private static void ShowInstallerResult(string message, uint style)
    {
        if (OperatingSystem.IsWindows())
        {
            _ = MessageBoxW(IntPtr.Zero, message, "QSurfer local indexing", style);
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UsePlatformDetect()
        .WithInterFont()
        .LogToTrace();
}
