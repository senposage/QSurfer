using Avalonia;
using QSurfer.Avalonia.Services;

namespace QSurfer.Avalonia;

internal static class Program
{
    internal static SingleInstanceService SingleInstance { get; } = new();

    [STAThread]
    public static void Main(string[] args)
    {
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

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UsePlatformDetect()
        .WithInterFont()
        .LogToTrace();
}
