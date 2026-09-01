using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using QSurfer.Core.Models;

namespace QSurfer.Avalonia.Services;

internal static class WindowChromeService
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaBorderColor = 34;
    private const int DwmwaCaptionColor = 35;
    private const int DwmwaTextColor = 36;

    public static void Apply(Window window, ThemeColorConfig? colors)
    {
        if (!OperatingSystem.IsWindows() || window.TryGetPlatformHandle()?.Handle is not { } handle)
        {
            return;
        }

        colors ??= new ThemeColorConfig();
        var isLight = Application.Current?.ActualThemeVariant == ThemeVariant.Light;
        var surface = Parse(isLight ? colors.LightSurface : colors.DarkSurface, isLight ? "#FFFFFFFF" : "#FF242830");
        var text = ThemeColorService.ContrastColor(surface);
        var useDarkCaption = isLight ? 0 : 1;

        SetAttribute(handle, DwmwaUseImmersiveDarkMode, (uint)useDarkCaption);
        SetAttribute(handle, DwmwaBorderColor, ToColorRef(surface));
        SetAttribute(handle, DwmwaCaptionColor, ToColorRef(surface));
        SetAttribute(handle, DwmwaTextColor, ToColorRef(text));
    }

    private static Color Parse(string value, string fallback) =>
        Color.TryParse(value, out var color) ? color : Color.Parse(fallback);

    private static uint ToColorRef(Color color) => (uint)(color.R | (color.G << 8) | (color.B << 16));

    private static void SetAttribute(IntPtr handle, int attribute, uint value) =>
        DwmSetWindowAttribute(handle, attribute, ref value, sizeof(uint));

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref uint value, int size);
}
