using System.IO;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Threading;
using Microsoft.Win32;
using QSurfer.Core.Services;

namespace QSurfer.Avalonia.Services;

// Hosts Windows' registered preview handler in an Avalonia native child window.
public sealed class ShellPreviewHost : NativeControlHost, IDisposable
{
    private const string PreviewHandlerCategory = "{8895b1c6-b41f-4c1c-a562-0d564250836f}";
    private const uint StorageRead = 0;
    private const uint StorageShareDenyNone = 0x40;
    private const uint ClsctxInprocServer = 0x1;
    private const uint ClsctxLocalServer = 0x4;
    private const int WsChild = 0x40000000;
    private const int WsVisible = 0x10000000;
    private const int WsClipChildren = 0x02000000;
    private const int WsClipSiblings = 0x04000000;
    private static readonly ConcurrentDictionary<string, PreviewHandlerRegistration> PreviewHandlerCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _path;
    private readonly Guid _handlerClassId;
    private IPreviewHandler? _handler;
    private IStream? _stream;
    private IShellItem? _shellItem;
    private PreviewHandlerFrame? _site;
    private IntPtr _childHandle;
    private NativeRect _lastRect;
    private bool _previewStarted;
    private bool _disposed;

    private ShellPreviewHost(string path, Guid handlerClassId)
    {
        _path = path;
        _handlerClassId = handlerClassId;
    }

    public event EventHandler<PreviewFailureEventArgs>? PreviewFailed;

    public static bool IsVideoFile(string extension) => VideoExtensions.Contains(extension.Trim().TrimStart('.'));

    public static ShellPreviewHost? TryCreate(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        var extension = Path.GetExtension(path);
        if (string.IsNullOrWhiteSpace(extension))
        {
            return null;
        }

        var extensionKey = extension.StartsWith('.') ? extension : "." + extension;
        var registration = PreviewHandlerCache.GetOrAdd(extensionKey, static key => FindPreviewHandler(key));
        return registration.IsAvailable ? new ShellPreviewHost(path, registration.ClassId) : null;
    }

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        _childHandle = CreateWindowEx(0, "static", "", WsChild | WsVisible | WsClipChildren | WsClipSiblings,
            0, 0, 0, 0, parent.Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (_childHandle == IntPtr.Zero)
        {
            throw new InvalidOperationException("Windows could not create a preview host.");
        }

        try
        {
            InitializeHandler();
        }
        catch (Exception ex)
        {
            FailPreview(ex, "initialize");
        }
        return new PlatformHandle(_childHandle, "HWND");
    }

    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        ReleaseHandler();
        if (_childHandle != IntPtr.Zero)
        {
            DestroyWindow(_childHandle);
            _childHandle = IntPtr.Zero;
        }
        base.DestroyNativeControlCore(control);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        UpdatePreviewSize(finalSize);
        return base.ArrangeOverride(finalSize);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        ReleaseHandler();
    }

    private void InitializeHandler()
    {
        _handler = CreatePreviewHandler(_handlerClassId);
        if (_handler is IObjectWithSite objectWithSite)
        {
            _site = new PreviewHandlerFrame(_childHandle);
            objectWithSite.SetSite(_site);
        }

        var initializationMode = "";
        if (_handler is IInitializeWithStream initializeWithStream)
        {
            _stream = OpenReadStream(_path);
            initializeWithStream.Initialize(_stream, StorageRead);
            initializationMode = "stream";
        }
        else if (_handler is IInitializeWithFile initializeWithFile)
        {
            initializeWithFile.Initialize(_path, StorageRead);
            initializationMode = "file";
        }
        else if (_handler is IInitializeWithItem initializeWithItem)
        {
            _shellItem = CreateShellItem(_path);
            initializeWithItem.Initialize(_shellItem, StorageRead);
            initializationMode = "shell-item";
        }
        else
        {
            throw new InvalidOperationException("The registered preview handler does not support file initialization.");
        }
        AppLogger.Info("preview", $"native handler initialized mode={initializationMode} path=\"{_path}\" handler=\"{_handlerClassId}\"");
    }

    private void UpdatePreviewSize(Size size)
    {
        if (_handler == null || _childHandle == IntPtr.Zero)
        {
            return;
        }

        var rect = new NativeRect(0, 0, Math.Max(0, (int)size.Width), Math.Max(0, (int)size.Height));
        if (rect.Right == _lastRect.Right && rect.Bottom == _lastRect.Bottom)
        {
            return;
        }
        try
        {
            SetWindowPos(_childHandle, IntPtr.Zero, 0, 0, rect.Right, rect.Bottom, 0x0004);
            if (!_previewStarted)
            {
                _handler.SetWindow(_childHandle, ref rect);
                _handler.SetRect(ref rect);
                _handler.DoPreview();
                _previewStarted = true;
                AppLogger.Info("preview", $"native handler started path=\"{_path}\" size={rect.Right}x{rect.Bottom}");
            }
            else
            {
                _handler.SetRect(ref rect);
            }
            _lastRect = rect;
        }
        catch (Exception ex)
        {
            FailPreview(ex, "start or resize");
        }
    }

    private void FailPreview(Exception exception, string operation)
    {
        AppLogger.Error("preview", exception, $"native preview {operation} failed path=\"{_path}\" handler=\"{_handlerClassId}\"");
        ReleaseHandler();
        Dispatcher.UIThread.Post(() => PreviewFailed?.Invoke(this, new PreviewFailureEventArgs("Windows could not load the registered preview handler for this file.")));
    }

    private void ReleaseHandler()
    {
        var handler = _handler;
        _handler = null;
        try
        {
            if (handler is IObjectWithSite objectWithSite)
            {
                objectWithSite.SetSite(null);
            }
            handler?.Unload();
        }
        catch
        {
        }
        finally
        {
            if (handler != null)
            {
                Marshal.FinalReleaseComObject(handler);
            }
            if (_stream != null)
            {
                Marshal.FinalReleaseComObject(_stream);
                _stream = null;
            }
            if (_shellItem != null)
            {
                Marshal.FinalReleaseComObject(_shellItem);
                _shellItem = null;
            }
            _site = null;
        }
    }

    private static IStream OpenReadStream(string path)
    {
        SHCreateStreamOnFileEx(path, StorageRead | StorageShareDenyNone, 0, false, null, out var stream);
        return stream;
    }

    private static IPreviewHandler CreatePreviewHandler(Guid handlerClassId)
    {
        var interfaceId = typeof(IPreviewHandler).GUID;
        try
        {
            CoCreateInstance(ref handlerClassId, IntPtr.Zero, ClsctxLocalServer, ref interfaceId, out var handler);
            return (IPreviewHandler)handler;
        }
        catch (COMException)
        {
            CoCreateInstance(ref handlerClassId, IntPtr.Zero, ClsctxInprocServer, ref interfaceId, out var handler);
            return (IPreviewHandler)handler;
        }
    }

    private static IShellItem CreateShellItem(string path)
    {
        var interfaceId = typeof(IShellItem).GUID;
        SHCreateItemFromParsingName(path, IntPtr.Zero, ref interfaceId, out var item);
        return item;
    }

    private static PreviewHandlerRegistration FindPreviewHandler(string extensionKey)
    {
        var handler = ReadHandler(extensionKey);
        if (handler == null)
        {
            using var key = Registry.ClassesRoot.OpenSubKey(extensionKey);
            handler = key?.GetValue(null) is string programId ? ReadHandler(programId) : null;
        }
        return handler != null && Guid.TryParse(handler, out var handlerClassId)
            ? new PreviewHandlerRegistration(true, handlerClassId)
            : default;
    }

    private static string? ReadHandler(string className)
    {
        using var key = Registry.ClassesRoot.OpenSubKey($"{className}\\shellex\\{PreviewHandlerCategory}");
        return key?.GetValue(null) as string;
    }

    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "3gp", "3g2", "asf", "avi", "flv", "m2ts", "m4v", "mkv", "mov", "mp4", "mpeg", "mpg", "mts", "ts", "webm", "wmv",
    };

    private readonly record struct PreviewHandlerRegistration(bool IsAvailable, Guid ClassId);

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeRect(int left, int top, int right, int bottom)
    {
        public int Left = left;
        public int Top = top;
        public int Right = right;
        public int Bottom = bottom;
    }

    [ComImport, Guid("8895b1c6-b41f-4c1c-a562-0d564250836f"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPreviewHandler
    {
        void SetWindow(IntPtr hwnd, ref NativeRect rect);
        void SetRect(ref NativeRect rect);
        void DoPreview();
        void Unload();
        void SetFocus();
        void QueryFocus(out IntPtr hwnd);
        void TranslateAccelerator(IntPtr message);
    }

    [ComImport, Guid("b7d14566-0509-4cce-a71f-0a554233bd9b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IInitializeWithFile { void Initialize([MarshalAs(UnmanagedType.LPWStr)] string path, uint mode); }
    [ComImport, Guid("b824b49d-22ac-4161-ac8a-9916e8fa3f7f"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IInitializeWithStream { void Initialize(IStream stream, uint mode); }
    [ComImport, Guid("7f73be3f-fb79-493c-a6c7-7ee14e245841"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IInitializeWithItem { void Initialize(IShellItem item, uint mode); }
    [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem { }
    [ComImport, Guid("fc4801a3-2ba9-11cf-a229-00aa003d7352"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IObjectWithSite { void SetSite([MarshalAs(UnmanagedType.IUnknown)] object? site); void GetSite(ref Guid interfaceId, [MarshalAs(UnmanagedType.IUnknown)] out object site); }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateWindowEx(int exStyle, string className, string windowName, int style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool DestroyWindow(IntPtr window);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode, PreserveSig = false)] private static extern void SHCreateStreamOnFileEx(string fileName, uint mode, uint attributes, [MarshalAs(UnmanagedType.Bool)] bool create, IStream? templateStream, out IStream stream);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)] private static extern void SHCreateItemFromParsingName(string path, IntPtr bindingContext, ref Guid interfaceId, [MarshalAs(UnmanagedType.Interface)] out IShellItem item);
    [DllImport("ole32.dll", PreserveSig = false)] private static extern void CoCreateInstance(ref Guid classId, IntPtr outer, uint context, ref Guid interfaceId, [MarshalAs(UnmanagedType.Interface)] out object instance);
}

public sealed class PreviewFailureEventArgs(string message) : EventArgs
{
    public string Message { get; } = message;
}

[ComVisible(true), Guid("fec87aaf-35f9-447a-adb7-20234491401a"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IPreviewHandlerFrame
{
    int GetWindowContext(out PreviewHandlerFrameInfo info);
    int TranslateAccelerator(IntPtr message);
}

[ComVisible(true), ClassInterface(ClassInterfaceType.None)]
public sealed class PreviewHandlerFrame(IntPtr window) : IPreviewHandlerFrame
{
    public int GetWindowContext(out PreviewHandlerFrameInfo info)
    {
        GetClientRect(window, out var rect);
        info = new PreviewHandlerFrameInfo { Size = (uint)Marshal.SizeOf<PreviewHandlerFrameInfo>(), Window = window, FrameRect = rect };
        return 0;
    }

    public int TranslateAccelerator(IntPtr message) => 1;

    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetClientRect(IntPtr window, out ShellPreviewHost.NativeRect rect);
}

[StructLayout(LayoutKind.Sequential)]
public struct PreviewHandlerFrameInfo
{
    public uint Size;
    public IntPtr Window;
    internal ShellPreviewHost.NativeRect FrameRect;
    public IntPtr AcceleratorTable;
    public uint AcceleratorCount;
}
