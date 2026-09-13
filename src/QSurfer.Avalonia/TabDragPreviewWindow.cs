using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Layout = global::Avalonia.Layout;

namespace QSurfer.Avalonia;

/// <summary>Small non-activating visual used while a tab is being torn off.</summary>
internal sealed class TabDragPreviewWindow : Window
{
    private readonly TextBlock _title;
    private readonly TextBlock _hint;

    public TabDragPreviewWindow(string title)
    {
        Width = 330;
        Height = 42;
        MinWidth = 330;
        MinHeight = 42;
        MaxWidth = 330;
        MaxHeight = 42;
        CanResize = false;
        ShowInTaskbar = false;
        ShowActivated = false;
        SystemDecorations = SystemDecorations.None;
        Topmost = true;
        Background = new SolidColorBrush(Color.Parse("#FF242A33"));

        _title = new TextBlock
        {
            Text = title,
            Width = 120,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = Layout.VerticalAlignment.Center,
        };
        _hint = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.Parse("#FF9AA8BA")),
            FontSize = 12,
            VerticalAlignment = Layout.VerticalAlignment.Center,
        };

        var contents = new StackPanel
        {
            Orientation = Layout.Orientation.Horizontal,
            Spacing = 7,
        };
        contents.Children.Add(new TextBlock
        {
            Text = "\U0001F50D",
            VerticalAlignment = Layout.VerticalAlignment.Center,
        });
        contents.Children.Add(_title);
        contents.Children.Add(_hint);

        Content = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#FF242A33")),
            BorderBrush = new SolidColorBrush(Color.Parse("#FF5AA8FF")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 6),
            Child = contents,
        };
    }

    public void MoveTo(PixelPoint position) => Position = position;

    public void Update(string title, bool isOutsideSource)
    {
        _title.Text = title;
        _hint.Text = isOutsideSource ? "Release to open window" : "Drag outside to open";
    }
}
