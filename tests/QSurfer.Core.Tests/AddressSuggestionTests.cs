using QSurfer.Avalonia.ViewModels;
using QSurfer.Core.Models;
using Xunit;

namespace QSurfer.Core.Tests;

public sealed class AddressSuggestionTests
{
    [Fact]
    public void AddressSuggestionsMatchImmediateFoldersIncludingSpaces()
    {
        var root = Path.Combine(Path.GetTempPath(), "QSurfer-address-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "AA Criminal"));
        Directory.CreateDirectory(Path.Combine(root, "AA Civil"));
        Directory.CreateDirectory(Path.Combine(root, "Archive"));

        try
        {
            using var window = new MainWindowViewModel(new AppConfig(), startBackgroundWork: false);
            var suggestions = window.FindAddressSuggestions(Path.Combine(root, "AA Cr"), CancellationToken.None);

            var suggestion = Assert.Single(suggestions);
            Assert.Equal("AA Criminal", suggestion.Name);
            Assert.Equal(Path.Combine(root, "AA Criminal"), suggestion.Path);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void AddressSuggestionsCompleteEachTypedFolderSegment()
    {
        var root = Path.Combine(Path.GetTempPath(), "QSurfer-address-tests", Guid.NewGuid().ToString("N"));
        var active = Directory.CreateDirectory(Path.Combine(root, "AA Criminal", "Active"));
        Directory.CreateDirectory(Path.Combine(active.FullName, "Forms"));
        Directory.CreateDirectory(Path.Combine(active.FullName, "Former cases"));

        try
        {
            using var window = new MainWindowViewModel(new AppConfig(), startBackgroundWork: false);
            var suggestions = window.FindAddressSuggestions(
                Path.Combine(root, "AA Criminal", "Active", "Fo"),
                CancellationToken.None);

            Assert.Equal(
                new[] { "Forms", "Former cases" }.OrderBy(name => name, StringComparer.OrdinalIgnoreCase),
                suggestions.Select(suggestion => suggestion.Name).OrderBy(name => name, StringComparer.OrdinalIgnoreCase));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void AddressSuggestionsListImmediateChildrenAlphabeticallyAfterTrailingSeparator()
    {
        var root = Path.Combine(Path.GetTempPath(), "QSurfer-address-tests", Guid.NewGuid().ToString("N"));
        var parent = Directory.CreateDirectory(Path.Combine(root, "AA Criminal"));
        Directory.CreateDirectory(Path.Combine(parent.FullName, "Zoning"));
        Directory.CreateDirectory(Path.Combine(parent.FullName, "Active"));
        Directory.CreateDirectory(Path.Combine(parent.FullName, "Briefs"));

        try
        {
            using var window = new MainWindowViewModel(new AppConfig(), startBackgroundWork: false);
            var suggestions = window.FindAddressSuggestions(parent.FullName + Path.DirectorySeparatorChar, CancellationToken.None);

            Assert.Equal(new[] { "Active", "Briefs", "Zoning" }, suggestions.Select(suggestion => suggestion.Name));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void AcceptingAddressSuggestionUpdatesLocationAndClosesThePopup()
    {
        using var window = new MainWindowViewModel(new AppConfig(), startBackgroundWork: false);
        window.AddressSuggestions.Add(new BrowserPathSuggestion("AA Criminal", @"X:\AA Criminal"));
        window.IsAddressSuggestionsOpen = true;

        window.AcceptAddressSuggestion(window.AddressSuggestions[0]);

        Assert.Equal(@"X:\AA Criminal", window.BrowserLocation);
        Assert.Single(window.AddressSuggestions);
        Assert.False(window.IsAddressSuggestionsOpen);
    }

    [Fact]
    public async Task RapidAddressChangesLeaveTheLatestSuggestionRequestUsable()
    {
        var root = Path.Combine(Path.GetTempPath(), "QSurfer-address-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "AA Criminal"));

        try
        {
            using var window = new MainWindowViewModel(new AppConfig(), startBackgroundWork: false);
            var first = window.RefreshAddressSuggestionsAsync(Path.Combine(root, "AA"));
            await Task.Delay(20);
            var second = window.RefreshAddressSuggestionsAsync(Path.Combine(root, "AA Cr"));

            await Task.WhenAll(first, second);

            var suggestion = Assert.Single(window.AddressSuggestions);
            Assert.Equal("AA Criminal", suggestion.Name);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
