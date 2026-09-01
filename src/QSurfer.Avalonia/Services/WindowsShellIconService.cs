using System.IO;
using System.Runtime.InteropServices;
using AvaloniaBitmap = Avalonia.Media.Imaging.Bitmap;
using DrawingIcon = System.Drawing.Icon;

namespace QSurfer.Avalonia.Services;

internal static class WindowsShellIconService
{
    public static AvaloniaBitmap? RecycleBinIcon()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        var info = new StockIconInfo { Size = (uint)Marshal.SizeOf<StockIconInfo>() };
        var result = SHGetStockIconInfo(StockIconRecyclerFull, ShellStockIconFlags.Icon | ShellStockIconFlags.LargeIcon, ref info);
        if (result < 0 || info.IconHandle == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            return ToBitmap(info.IconHandle);
        }
        finally
        {
            DestroyIcon(info.IconHandle);
        }
    }

    public static AvaloniaBitmap? FileTypeIcon(string extension, bool isFolder)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        var flags = ShellFileInfoFlags.Icon | ShellFileInfoFlags.UseFileAttributes | ShellFileInfoFlags.LargeIcon;
        var attributes = isFolder ? FileAttributes.Directory : FileAttributes.Normal;
        var name = isFolder ? "folder" : string.IsNullOrWhiteSpace(extension) ? "file" : "." + extension.TrimStart('.');
        var info = new ShellFileInfo();
        var result = SHGetFileInfo(name, attributes, ref info, (uint)Marshal.SizeOf<ShellFileInfo>(), flags);
        if (result == IntPtr.Zero || info.IconHandle == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            return ToBitmap(info.IconHandle);
        }
        catch
        {
            return null;
        }
        finally
        {
            DestroyIcon(info.IconHandle);
        }
    }

    private static AvaloniaBitmap? ToBitmap(IntPtr iconHandle)
    {
        try
        {
            using var icon = (DrawingIcon)DrawingIcon.FromHandle(iconHandle).Clone();
            using var image = icon.ToBitmap();
            using var stream = new MemoryStream();
            image.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
            stream.Position = 0;
            return new AvaloniaBitmap(stream);
        }
        catch
        {
            return null;
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(
        string path,
        FileAttributes fileAttributes,
        ref ShellFileInfo shellFileInfo,
        uint fileInfoSize,
        ShellFileInfoFlags flags);

    [DllImport("shell32.dll")]
    private static extern int SHGetStockIconInfo(uint stockIconId, ShellStockIconFlags flags, ref StockIconInfo stockIconInfo);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShellFileInfo
    {
        public IntPtr IconHandle;
        public int IconIndex;
        public uint Attributes;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string DisplayName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string TypeName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StockIconInfo
    {
        public uint Size;
        public IntPtr IconHandle;
        public int SystemImageIndex;
        public int IconIndex;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string Path;
    }

    [Flags]
    private enum ShellFileInfoFlags : uint
    {
        Icon = 0x000000100,
        LargeIcon = 0x000000000,
        UseFileAttributes = 0x000000010,
    }

    [Flags]
    private enum ShellStockIconFlags : uint
    {
        Icon = 0x00000100,
        LargeIcon = 0x00000000,
    }

    private const uint StockIconRecyclerFull = 32;
}
