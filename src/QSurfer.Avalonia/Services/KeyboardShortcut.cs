using Avalonia.Input;

namespace QSurfer.Avalonia.Services;

public readonly record struct KeyboardShortcut(Key Key, KeyModifiers Modifiers, string DisplayText)
{
    public bool Matches(KeyEventArgs args) => args.Key == Key && args.KeyModifiers == Modifiers;

    public static bool TryCapture(KeyEventArgs args, out KeyboardShortcut shortcut)
    {
        shortcut = default;
        if (args.Key is Key.None or Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
        {
            return false;
        }

        var modifierNames = new List<string>();
        if (args.KeyModifiers.HasFlag(KeyModifiers.Control)) modifierNames.Add("Ctrl");
        if (args.KeyModifiers.HasFlag(KeyModifiers.Alt)) modifierNames.Add("Alt");
        if (args.KeyModifiers.HasFlag(KeyModifiers.Shift)) modifierNames.Add("Shift");
        if (args.KeyModifiers.HasFlag(KeyModifiers.Meta)) modifierNames.Add("Win");

        var keyName = args.Key switch
        {
            Key.Space => "Space",
            Key.Enter => "Enter",
            Key.Left => "Left",
            Key.Right => "Right",
            Key.Up => "Up",
            Key.Down => "Down",
            _ => args.Key.ToString(),
        };
        shortcut = new KeyboardShortcut(args.Key, args.KeyModifiers, string.Join('+', modifierNames.Append(keyName)));
        return true;
    }

    public static bool TryParse(string? value, out KeyboardShortcut shortcut, out string error)
    {
        shortcut = default;
        error = "";
        var parts = (value ?? "").Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            error = "Enter a key, such as Ctrl+F or F5.";
            return false;
        }

        var modifiers = KeyModifiers.None;
        var modifierNames = new List<string>();
        foreach (var part in parts[..^1])
        {
            switch (part.ToUpperInvariant())
            {
                case "CTRL":
                case "CONTROL":
                    modifiers |= KeyModifiers.Control;
                    if (!modifierNames.Contains("Ctrl")) modifierNames.Add("Ctrl");
                    break;
                case "ALT":
                    modifiers |= KeyModifiers.Alt;
                    if (!modifierNames.Contains("Alt")) modifierNames.Add("Alt");
                    break;
                case "SHIFT":
                    modifiers |= KeyModifiers.Shift;
                    if (!modifierNames.Contains("Shift")) modifierNames.Add("Shift");
                    break;
                case "WIN":
                case "WINDOWS":
                    error = "Windows-key shortcuts are reserved by Windows.";
                    return false;
                default:
                    error = $"{part} is not a supported modifier.";
                    return false;
            }
        }

        if (!TryParseKey(parts[^1], out var key, out var keyName))
        {
            error = "Use a letter, number, function key, Enter, Escape, or an arrow key.";
            return false;
        }

        shortcut = new KeyboardShortcut(key, modifiers, string.Join('+', modifierNames.Append(keyName)));
        return true;
    }

    private static bool TryParseKey(string text, out Key key, out string displayName)
    {
        var normalized = text.Trim().ToUpperInvariant() switch
        {
            "RETURN" => "ENTER",
            "ESC" => "ESCAPE",
            "LEFTARROW" => "LEFT",
            "RIGHTARROW" => "RIGHT",
            "UPARROW" => "UP",
            "DOWNARROW" => "DOWN",
            _ => text.Trim().ToUpperInvariant(),
        };

        if (!Enum.TryParse(normalized, true, out key) || key == Key.None)
        {
            displayName = "";
            return false;
        }

        displayName = key switch
        {
            Key.Enter => "Enter",
            Key.Escape => "Escape",
            Key.Left => "Left",
            Key.Right => "Right",
            Key.Up => "Up",
            Key.Down => "Down",
            _ => key.ToString(),
        };
        return true;
    }
}
