using System.IO;
using System.Runtime.InteropServices;
using AvaloniaBitmap = Avalonia.Media.Imaging.Bitmap;
using DrawingIcon = System.Drawing.Icon;

namespace QSurfer.Avalonia.Services;

internal static class WindowsShellIconService
{
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
            using var icon = (DrawingIcon)DrawingIcon.FromHandle(info.IconHandle).Clone();
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
        finally
        {
            DestroyIcon(info.IconHandle);
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(
        string path,
        FileAttributes fileAttributes,
        ref ShellFileInfo shellFileInfo,
        uint fileInfoSize,
        ShellFileInfoFlags flags);

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

    [Flags]
    private enum ShellFileInfoFlags : uint
    {
        Icon = 0x000000100,
        LargeIcon = 0x000000000,
        UseFileAttributes = 0x000000010,
    }
}
