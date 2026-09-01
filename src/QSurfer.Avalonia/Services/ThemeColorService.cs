using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using QSurfer.Core.Models;

namespace QSurfer.Avalonia.Services;

internal static class ThemeColorService
{
    public static void Apply(ThemeColorConfig? colors, bool useWindowsAccentColor)
    {
        colors ??= new ThemeColorConfig();
        var windowsAccent = useWindowsAccentColor && WindowsAccentColorService.TryGet(out var color) ? color : (Color?)null;
        ApplyVariant(ThemeVariant.Light, colors.LightSurface, colors.LightAccent, colors.LightSelection, colors.LightHover, colors.LightMatch, windowsAccent);
        ApplyVariant(ThemeVariant.Dark, colors.DarkSurface, colors.DarkAccent, colors.DarkSelection, colors.DarkHover, colors.DarkMatch, windowsAccent);
    }

    public static bool TryNormalize(string? input, out string normalized)
    {
        var candidate = (input ?? "").Trim();
        if (!candidate.StartsWith('#'))
        {
            candidate = "#" + candidate;
        }

        if (Color.TryParse(candidate, out var color))
        {
            normalized = ToHex(color);
            return true;
        }

        normalized = "";
        return false;
    }

    public static string ToHex(Color color) => $"#{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}";

    private static void ApplyVariant(ThemeVariant variant, string surface, string accent, string selection, string hover, string match, Color? windowsAccent)
    {
        var app = Application.Current;
        if (app?.Resources.ThemeDictionaries.TryGetValue(variant, out var provider) != true ||
            provider is not IResourceDictionary dictionary)
        {
            return;
        }

        var surfaceColor = Parse(surface, variant == ThemeVariant.Light ? "#FFFFFFFF" : "#FF242830");
        var accentColor = windowsAccent ?? Parse(accent, variant == ThemeVariant.Light ? "#FF0067B8" : "#FF36B9AD");
        var selectionColor = Parse(selection, variant == ThemeVariant.Light ? "#FFD7EBFF" : "#FF27495F");
        var hoverColor = Parse(hover, variant == ThemeVariant.Light ? "#FFE8F3FD" : "#FF2C3847");
        var matchColor = Parse(match, variant == ThemeVariant.Light ? "#FFFFE29A" : "#FF725826");

        Set(dictionary, "QSurfer.PanelBackground", surfaceColor);
        Set(dictionary, "QSurfer.Accent", accentColor);
        Set(dictionary, "QSurfer.OnAccent", ContrastColor(accentColor));
        Set(dictionary, "QSurfer.SelectionBackground", selectionColor);
        Set(dictionary, "QSurfer.SelectionForeground", ContrastColor(selectionColor));
        Set(dictionary, "QSurfer.HoverBackground", hoverColor);
        Set(dictionary, "QSurfer.MatchHighlight", matchColor);
        Set(dictionary, "QSurfer.IconOpen", accentColor);
        Set(dictionary, "QSurfer.IconBrowse", accentColor);
        Set(dictionary, "QSurfer.IconShow", accentColor);
        Set(dictionary, "QSurfer.IconPreview", accentColor);
        Set(dictionary, "QSurfer.IconFavorite", accentColor);
        Set(dictionary, "QSurfer.IconFavoritesPane", accentColor);
        Set(dictionary, "QSurfer.IconHelp", accentColor);
    }

    private static void Set(IResourceDictionary dictionary, string key, Color color) =>
        dictionary[key] = new SolidColorBrush(color);

    private static Color Parse(string value, string fallback) =>
        Color.TryParse(value, out var color) ? color : Color.Parse(fallback);

    public static Color ContrastColor(Color color)
    {
        var luminance = (0.2126 * color.R + 0.7152 * color.G + 0.0722 * color.B) / 255d;
        return luminance > 0.55 ? Color.Parse("#FF101820") : Color.Parse("#FFF7FAFF");
    }
}
