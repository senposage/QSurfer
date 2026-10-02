using Avalonia.Controls;
using Avalonia.Interactivity;

namespace QSurfer.Avalonia;

public sealed partial class WhatsNewWindow : Window
{
    private readonly string _version;
    private readonly Control[] _pages;
    private readonly bool _isUpdate;
    private int _pageIndex;

    public WhatsNewWindow(string version, bool isUpdate)
    {
        InitializeComponent();
        _version = version;
        _isUpdate = isUpdate;
        _pages = isUpdate
            ? [UpdatePage]
            : [WelcomePage, ConnectionPage, SearchPage, BrowsePage, RecoveryPage];
        ShowPage();
    }

    private async void Help_Click(object? sender, RoutedEventArgs e)
    {
        await new HelpWindow().ShowDialog(this);
    }

    private void Back_Click(object? sender, RoutedEventArgs e)
    {
        if (_pageIndex > 0)
        {
            _pageIndex--;
            ShowPage();
        }
    }

    private void Next_Click(object? sender, RoutedEventArgs e)
    {
        if (_pageIndex == _pages.Length - 1)
        {
            Close();
            return;
        }

        _pageIndex++;
        ShowPage();
    }

    private void ShowPage()
    {
        for (var index = 0; index < _pages.Length; index++)
        {
            _pages[index].IsVisible = index == _pageIndex;
        }

        HeadingText.Text = _isUpdate
            ? $"What's new in QSurfer {_version}"
            : _pageIndex == 0
                ? $"Welcome to QSurfer {_version}"
            : "QSurfer quick start";
        SubheadingText.Text = _isUpdate
            ? "A quick summary of changes in this update."
            : _pageIndex == 0
            ? "A zero-to-ready guide for searching and browsing shared files."
            : "Set up the essentials, then use Help when you want the complete reference.";
        PageIndicator.Text = _isUpdate ? "Update complete" : $"{_pageIndex + 1} of {_pages.Length}";
        BackButton.IsEnabled = _pageIndex > 0;
        NextButton.Content = _pageIndex == _pages.Length - 1 ? (_isUpdate ? "Close" : "Finish") : "Next";
    }
}
