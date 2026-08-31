using System.Runtime.InteropServices;

namespace QSurfer.Avalonia.Services;

internal sealed class WindowsHotkeyService : IDisposable
{
    private const uint WmHotkey = 0x0312;
    private const uint WmConfigure = 0x8001;
    private const int HotkeyId = 1;
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint ModWin = 0x0008;
    private const uint ModNoRepeat = 0x4000;
    private const uint WmClose = 0x0010;
    private readonly object _gate = new();
    private readonly AutoResetEvent _ready = new(false);
    private readonly AutoResetEvent _configured = new(false);
    private Thread? _thread;
    private IntPtr _window;
    private Shortcut? _activeShortcut;
    private Shortcut? _pendingShortcut;
    private bool _pendingResult;
    private bool _disposed;

    public event Action? Pressed;

    public bool Start(string shortcut)
    {
        if (string.IsNullOrWhiteSpace(shortcut))
        {
            return true;
        }
        if (!TryParse(shortcut, out var parsed, out _))
        {
            return false;
        }

        lock (_gate)
        {
            if (_thread != null)
            {
                return Configure(parsed);
            }

            _pendingShortcut = parsed;
            _thread = new Thread(MessageLoop)
            {
                IsBackground = true,
                Name = "QSurfer Global Hotkey",
            };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
        }

        return _ready.WaitOne(TimeSpan.FromSeconds(2)) && _configured.WaitOne(TimeSpan.FromSeconds(2)) && _pendingResult;
    }

    public HotkeyRegistrationResult Configure(string shortcut)
    {
        if (string.IsNullOrWhiteSpace(shortcut))
        {
            lock (_gate)
            {
                if (_thread == null)
                {
                    return HotkeyRegistrationResult.Success("");
                }

                _pendingShortcut = null;
                _configured.Reset();
                PostMessage(_window, WmConfigure, IntPtr.Zero, IntPtr.Zero);
            }

            return _configured.WaitOne(TimeSpan.FromSeconds(2)) && _pendingResult
                ? HotkeyRegistrationResult.Success("")
                : HotkeyRegistrationResult.Unavailable("Windows could not clear the global shortcut.");
        }
        if (!TryParse(shortcut, out var parsed, out var normalized))
        {
            return HotkeyRegistrationResult.Invalid("Use a shortcut such as Ctrl+S, Alt+Q, or Ctrl+Shift+F.");
        }

        lock (_gate)
        {
            if (_thread == null)
            {
                return Start(normalized)
                    ? HotkeyRegistrationResult.Success(normalized)
                    : HotkeyRegistrationResult.Unavailable("Windows could not register that shortcut. It may already belong to another app.");
            }

            _pendingShortcut = parsed;
            _configured.Reset();
            PostMessage(_window, WmConfigure, IntPtr.Zero, IntPtr.Zero);
        }

        return _configured.WaitOne(TimeSpan.FromSeconds(2)) && _pendingResult
            ? HotkeyRegistrationResult.Success(normalized)
            : HotkeyRegistrationResult.Unavailable("Windows could not register that shortcut. It may already belong to another app.");
    }

    public static bool TryNormalize(string? shortcut, out string normalized, out string error)
    {
        if (string.IsNullOrWhiteSpace(shortcut))
        {
            normalized = "";
            error = "";
            return true;
        }
        if (TryParse(shortcut, out _, out normalized))
        {
            error = "";
            return true;
        }

        error = "Use a shortcut such as Ctrl+S, Alt+Q, or Ctrl+Shift+F.";
        return false;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            if (_window != IntPtr.Zero)
            {
                PostMessage(_window, WmClose, IntPtr.Zero, IntPtr.Zero);
            }
        }

        _thread?.Join(TimeSpan.FromSeconds(1));
        _ready.Dispose();
        _configured.Dispose();
    }

    private bool Configure(Shortcut shortcut)
    {
        _pendingShortcut = shortcut;
        _configured.Reset();
        PostMessage(_window, WmConfigure, IntPtr.Zero, IntPtr.Zero);
        return _configured.WaitOne(TimeSpan.FromSeconds(2)) && _pendingResult;
    }

    private void MessageLoop()
    {
        var className = $"QSurferHotkeyWindow.{Environment.ProcessId}";
        var windowClass = new WndClass
        {
            LpfnWndProc = WindowProc,
            LpszClassName = className,
        };
        RegisterClass(ref windowClass);
        _window = CreateWindowEx(0, className, "QSurfer Hotkey", 0, 0, 0, 0, 0, new IntPtr(-3), IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        _ready.Set();
        ApplyPendingShortcut();

        while (GetMessage(out var message, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref message);
            DispatchMessage(ref message);
        }

        if (_window != IntPtr.Zero)
        {
            UnregisterHotKey(_window, HotkeyId);
            DestroyWindow(_window);
            _window = IntPtr.Zero;
        }
        UnregisterClass(className, IntPtr.Zero);
    }

    private IntPtr WindowProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        switch (message)
        {
            case WmHotkey:
                Pressed?.Invoke();
                return IntPtr.Zero;
            case WmConfigure:
                ApplyPendingShortcut();
                return IntPtr.Zero;
            case WmClose:
                PostQuitMessage(0);
                return IntPtr.Zero;
            default:
                return DefWindowProc(hwnd, message, wParam, lParam);
        }
    }

    private void ApplyPendingShortcut()
    {
        var pending = _pendingShortcut;
        var previous = _activeShortcut;
        if (_window == IntPtr.Zero)
        {
            _pendingResult = false;
            _configured.Set();
            return;
        }

        if (pending == null)
        {
            if (previous != null)
            {
                UnregisterHotKey(_window, HotkeyId);
            }
            _activeShortcut = null;
            _pendingResult = true;
            _configured.Set();
            return;
        }

        if (previous != null)
        {
            UnregisterHotKey(_window, HotkeyId);
        }

        _pendingResult = RegisterHotKey(_window, HotkeyId, pending.Modifiers | ModNoRepeat, pending.VirtualKey);
        if (_pendingResult)
        {
            _activeShortcut = pending;
        }
        else if (previous != null)
        {
            RegisterHotKey(_window, HotkeyId, previous.Modifiers | ModNoRepeat, previous.VirtualKey);
        }
        _configured.Set();
    }

    private static bool TryParse(string? value, out Shortcut shortcut, out string normalized)
    {
        shortcut = default!;
        normalized = "";
        var parts = (value ?? "").Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 2)
        {
            return false;
        }

        uint modifiers = 0;
        var modifierNames = new List<string>();
        foreach (var part in parts[..^1])
        {
            switch (part.Trim().ToUpperInvariant())
            {
                case "CTRL":
                case "CONTROL":
                    modifiers |= ModControl;
                    if (!modifierNames.Contains("Ctrl")) modifierNames.Add("Ctrl");
                    break;
                case "ALT":
                    modifiers |= ModAlt;
                    if (!modifierNames.Contains("Alt")) modifierNames.Add("Alt");
                    break;
                case "SHIFT":
                    modifiers |= ModShift;
                    if (!modifierNames.Contains("Shift")) modifierNames.Add("Shift");
                    break;
                case "WIN":
                case "WINDOWS":
                    modifiers |= ModWin;
                    if (!modifierNames.Contains("Win")) modifierNames.Add("Win");
                    break;
                default:
                    return false;
            }
        }
        if (modifiers == 0 || !TryGetVirtualKey(parts[^1], out var virtualKey, out var keyName))
        {
            return false;
        }

        shortcut = new Shortcut(modifiers, virtualKey);
        normalized = string.Join('+', modifierNames.Append(keyName));
        return true;
    }

    private static bool TryGetVirtualKey(string keyText, out uint virtualKey, out string keyName)
    {
        var key = keyText.Trim().ToUpperInvariant();
        if (key == "SPACE")
        {
            virtualKey = 0x20;
            keyName = "Space";
            return true;
        }
        if (key.Length == 1 && char.IsLetterOrDigit(key[0]))
        {
            virtualKey = key[0];
            keyName = key;
            return true;
        }
        if (key.StartsWith('F') && int.TryParse(key[1..], out var functionKey) && functionKey is >= 1 and <= 24)
        {
            virtualKey = (uint)(0x70 + functionKey - 1);
            keyName = $"F{functionKey}";
            return true;
        }
        virtualKey = 0;
        keyName = "";
        return false;
    }

    private sealed record Shortcut(uint Modifiers, uint VirtualKey);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WndClass
    {
        public uint Style;
        public WndProcDelegate LpfnWndProc;
        public int CbClsExtra;
        public int CbWndExtra;
        public IntPtr HInstance;
        public IntPtr HIcon;
        public IntPtr HCursor;
        public IntPtr HbrBackground;
        public string? LpszMenuName;
        public string LpszClassName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg
    {
        public IntPtr Hwnd;
        public uint Message;
        public IntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public Point Pt;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    private delegate IntPtr WndProcDelegate(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClass(ref WndClass windowClass);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool UnregisterClass(string className, IntPtr instance);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(uint exStyle, string className, string windowName, uint style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);
    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr window);
    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint virtualKey);
    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr window, int id);
    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")]
    private static extern int GetMessage(out Msg message, IntPtr window, uint minimumFilter, uint maximumFilter);
    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref Msg message);
    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref Msg message);
    [DllImport("user32.dll")]
    private static extern void PostQuitMessage(int exitCode);
}

public readonly record struct HotkeyRegistrationResult(bool Registered, string NormalizedShortcut, string Error)
{
    public static HotkeyRegistrationResult Success(string shortcut) => new(true, shortcut, "");
    public static HotkeyRegistrationResult Invalid(string error) => new(false, "", error);
    public static HotkeyRegistrationResult Unavailable(string error) => new(false, "", error);
}
