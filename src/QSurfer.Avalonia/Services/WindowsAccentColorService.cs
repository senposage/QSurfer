using System.Runtime.InteropServices;
using Avalonia.Media;

namespace QSurfer.Avalonia.Services;

internal static class WindowsAccentColorService
{
    public static bool TryGet(out Color color)
    {
        color = default;
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            if (DwmGetColorizationColor(out var argb, out _) < 0)
            {
                return false;
            }

            color = Color.FromArgb(255, (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);
            return true;
        }
        catch (DllNotFoundException)
        {
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetColorizationColor(out uint colorizationColor, [MarshalAs(UnmanagedType.Bool)] out bool opaqueBlend);
}
