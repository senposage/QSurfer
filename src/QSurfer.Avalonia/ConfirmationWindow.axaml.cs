using Avalonia.Controls;
using Avalonia.Interactivity;

namespace QSurfer.Avalonia;

public sealed partial class ConfirmationWindow : Window
{
    public event EventHandler? Confirmed;

    public ConfirmationWindow(string title, string message, string action, string cancelAction = "Cancel")
    {
        InitializeComponent();
        Title = title;
        MessageText.Text = message;
        ConfirmButton.Content = action;
        CancelButton.Content = cancelAction;
    }

    private void Confirm_Click(object? sender, RoutedEventArgs e)
    {
        Confirmed?.Invoke(this, EventArgs.Empty);
        Close(true);
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(false);
}
