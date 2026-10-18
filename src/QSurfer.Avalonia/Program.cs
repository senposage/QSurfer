using Avalonia;
using System.Runtime.InteropServices;
using System.Text.Json;
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
            RunElevatedLocalIndexerInstall(
                args.Any(argument => string.Equals(argument, "--local-indexer-sqlite", StringComparison.OrdinalIgnoreCase)),
                GetOptionValue(args, "--local-indexer-progress"));
            return;
        }

        if (LocalIndexerManager.IsSupported && args.Any(argument => string.Equals(argument, "--stop-local-indexer-service", StringComparison.OrdinalIgnoreCase)))
        {
            RunElevatedLocalIndexerStop();
            return;
        }

        if (LocalIndexerManager.IsSupported && args.Any(argument => string.Equals(argument, "--start-local-indexer-service", StringComparison.OrdinalIgnoreCase)))
        {
            RunElevatedLocalIndexerStart(GetOptionValue(args, "--local-indexer-progress"));
            return;
        }

        if (LocalIndexerManager.IsSupported && args.Any(argument => string.Equals(argument, "--uninstall-local-indexer-service", StringComparison.OrdinalIgnoreCase)))
        {
            RunElevatedLocalIndexerUninstall();
            return;
        }

        if (LocalIndexerManager.IsSupported && args.Any(argument => string.Equals(argument, "--upgrade-local-indexer-service", StringComparison.OrdinalIgnoreCase)))
        {
            RunElevatedLocalIndexerUpgrade(
                args.Any(argument => string.Equals(argument, "--local-indexer-sqlite", StringComparison.OrdinalIgnoreCase)),
                GetOptionValue(args, "--local-indexer-progress"));
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

    private static void RunElevatedLocalIndexerInstall(bool useSqlite, string? progressPath)
    {
        try
        {
            using var manager = new LocalIndexerManager(stateDirectory: OperatingSystem.IsWindows() ? LocalIndexerManager.MachineStateRoot : null, usePostgres: !useSqlite, progressPath: progressPath);
            if (OperatingSystem.IsLinux())
            {
                manager.PrepareLinuxServiceAsync().GetAwaiter().GetResult();
            }
            else
            {
                manager.EnableAsync(forceWindowsService: true).GetAwaiter().GetResult();
            }
            ShowInstallerResult("QSurfer local indexing service is installed and running. Complete first-run setup in QIndexer administration if prompted.", 0x40);
            Environment.ExitCode = 0;
        }
        catch (Exception exception)
        {
            WriteLocalIndexerFailureProgress(progressPath, exception);
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

    private static void RunElevatedLocalIndexerStart(string? progressPath)
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
            WriteLocalIndexerFailureProgress(progressPath, exception);
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

    private static void RunElevatedLocalIndexerUpgrade(bool useSqlite, string? progressPath)
    {
        try
        {
            using var manager = new LocalIndexerManager(stateDirectory: OperatingSystem.IsWindows() ? LocalIndexerManager.MachineStateRoot : null, usePostgres: !useSqlite, progressPath: progressPath);
            var result = manager.UpgradeAsync().GetAwaiter().GetResult();
            ShowInstallerResult($"QSurfer local QIndexer updated to {result.Version} and is healthy.", 0x40);
            Environment.ExitCode = 0;
        }
        catch (Exception exception)
        {
            WriteLocalIndexerFailureProgress(progressPath, exception);
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

    private static string? GetOptionValue(string[] args, string option)
    {
        for (var index = 0; index < args.Length - 1; index++)
        {
            if (string.Equals(args[index], option, StringComparison.OrdinalIgnoreCase))
            {
                return args[index + 1];
            }
        }
        return null;
    }

    private static void WriteLocalIndexerFailureProgress(string? progressPath, Exception exception)
    {
        if (string.IsNullOrWhiteSpace(progressPath)) return;
        try
        {
            var message = exception.GetBaseException().Message.Replace('\r', ' ').Replace('\n', ' ').Trim();
            if (message.Length > 360) message = message[..360] + "...";
            var payload = JsonSerializer.Serialize(new
            {
                schema_version = 1,
                status = "failed",
                stage = "qsurfer_helper",
                percent = 100,
                message = string.IsNullOrWhiteSpace(message) ? "The privileged QIndexer helper did not finish." : message,
                updated_at = DateTimeOffset.UtcNow,
            });
            var temporaryPath = progressPath + ".tmp";
            File.WriteAllText(temporaryPath, payload);
            File.Move(temporaryPath, progressPath, overwrite: true);
        }
        catch
        {
            // The caller still receives the helper exit code when a progress file cannot be written.
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UsePlatformDetect()
        .WithInterFont()
        .LogToTrace();
}
