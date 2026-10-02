using Avalonia.Controls;
using Avalonia.Interactivity;

namespace QSurfer.Avalonia;

public sealed partial class WhatsNewWindow : Window
{
    private readonly string _version;
    private readonly Control[] _allPages;
    private readonly Control[] _pages;
    private readonly bool _isUpdate;
    private int _pageIndex;

    public WhatsNewWindow(string version, bool isUpdate)
    {
        InitializeComponent();
        _version = version;
        _isUpdate = isUpdate;
        _allPages = [WelcomePage, ConnectionPage, SearchPage, BrowsePage, RecoveryPage, UpdatePage];
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
        foreach (var page in _allPages)
        {
            page.IsVisible = false;
        }

        _pages[_pageIndex].IsVisible = true;

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
