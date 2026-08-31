using Avalonia.Controls;
using Avalonia.Interactivity;

namespace QSurfer.Avalonia;

public sealed partial class TextEntryWindow : Window
{
    public TextEntryWindow(string title, string prompt, string action, string value = "")
    {
        InitializeComponent();
        Title = title;
        PromptText.Text = prompt;
        AcceptButton.Content = action;
        ValueBox.Text = value;
        Opened += (_, _) =>
        {
            ValueBox.Focus();
            ValueBox.SelectAll();
        };
    }

    public string Value => ValueBox.Text?.Trim() ?? "";

    private void Accept_Click(object? sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(Value))
        {
            ErrorText.Text = "Enter a name.";
            return;
        }
        Close(true);
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(false);
}
