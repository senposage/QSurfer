using Avalonia.Controls;
using Avalonia.Interactivity;

namespace QSurfer.Avalonia;

public sealed partial class HelpWindow : Window
{
    public HelpWindow()
    {
        InitializeComponent();
    }

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();

    private async void GitHub_Click(object? sender, RoutedEventArgs e)
    {
        var launcher = GetTopLevel(this)?.Launcher;
        if (launcher != null)
        {
            await launcher.LaunchUriAsync(new Uri("https://github.com/senposage/QSurfer"));
        }
    }

    private async void Donate_Click(object? sender, RoutedEventArgs e)
    {
        var launcher = GetTopLevel(this)?.Launcher;
        if (launcher != null)
        {
            await launcher.LaunchUriAsync(new Uri("https://www.paypal.com/paypalme/rjc862003"));
        }
    }
}
