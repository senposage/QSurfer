using Avalonia.Controls;
using Avalonia.Interactivity;

namespace QSurfer.Avalonia;

public sealed partial class ConfirmationWindow : Window
{
    public ConfirmationWindow(string title, string message, string action)
    {
        InitializeComponent();
        Title = title;
        MessageText.Text = message;
        ConfirmButton.Content = action;
    }

    private void Confirm_Click(object? sender, RoutedEventArgs e) => Close(true);

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(false);
}
