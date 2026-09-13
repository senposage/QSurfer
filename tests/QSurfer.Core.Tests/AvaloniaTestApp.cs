using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using QSurfer.Avalonia;

[assembly: AvaloniaTestApplication(typeof(QSurfer.Core.Tests.AvaloniaTestApp))]

namespace QSurfer.Core.Tests;

public static class AvaloniaTestApp
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder
        .Configure<App>()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions());
}
