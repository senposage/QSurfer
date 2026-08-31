using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace QSurfer.Avalonia.Controls;

public sealed class HighlightTextBlock : TextBlock
{
    public static readonly StyledProperty<string?> SourceTextProperty =
        AvaloniaProperty.Register<HighlightTextBlock, string?>(nameof(SourceText));

    public static readonly StyledProperty<string?> HighlightQueryProperty =
        AvaloniaProperty.Register<HighlightTextBlock, string?>(nameof(HighlightQuery));

    public static readonly StyledProperty<bool> HighlightEnabledProperty =
        AvaloniaProperty.Register<HighlightTextBlock, bool>(nameof(HighlightEnabled), true);

    public static readonly StyledProperty<IBrush?> HighlightBrushProperty =
        AvaloniaProperty.Register<HighlightTextBlock, IBrush?>(nameof(HighlightBrush));

    static HighlightTextBlock()
    {
        SourceTextProperty.Changed.AddClassHandler<HighlightTextBlock>((control, _) => control.RefreshInlines());
        HighlightQueryProperty.Changed.AddClassHandler<HighlightTextBlock>((control, _) => control.RefreshInlines());
        HighlightEnabledProperty.Changed.AddClassHandler<HighlightTextBlock>((control, _) => control.RefreshInlines());
        HighlightBrushProperty.Changed.AddClassHandler<HighlightTextBlock>((control, _) => control.RefreshInlines());
    }

    public string? SourceText
    {
        get => GetValue(SourceTextProperty);
        set => SetValue(SourceTextProperty, value);
    }

    public string? HighlightQuery
    {
        get => GetValue(HighlightQueryProperty);
        set => SetValue(HighlightQueryProperty, value);
    }

    public bool HighlightEnabled
    {
        get => GetValue(HighlightEnabledProperty);
        set => SetValue(HighlightEnabledProperty, value);
    }

    public IBrush? HighlightBrush
    {
        get => GetValue(HighlightBrushProperty);
        set => SetValue(HighlightBrushProperty, value);
    }

    private void RefreshInlines()
    {
        var text = SourceText ?? "";
        var query = HighlightQuery?.Trim() ?? "";
        var inlines = Inlines;
        if (inlines == null)
        {
            return;
        }
        inlines.Clear();

        if (!HighlightEnabled || string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(query))
        {
            inlines.Add(new Run(text));
            return;
        }

        var start = 0;
        while (start < text.Length)
        {
            var match = text.IndexOf(query, start, StringComparison.OrdinalIgnoreCase);
            if (match < 0)
            {
                inlines.Add(new Run(text[start..]));
                break;
            }

            if (match > start)
            {
                inlines.Add(new Run(text[start..match]));
            }

            inlines.Add(new Run(text.Substring(match, query.Length)) { Background = HighlightBrush });
            start = match + query.Length;
        }
    }
}
