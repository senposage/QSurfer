using QSurfer.Avalonia.ViewModels;
using QSurfer.Core.Models;
using Xunit;

namespace QSurfer.Core.Tests;

public sealed class WorkspaceTransferTests
{
    [Fact]
    public void MovingATabToAnotherWindowPreservesBrowseAndSearchState()
    {
        using var source = CreateWindowModel();
        using var destination = CreateWindowModel();
        var tab = Assert.IsType<SearchTabViewModel>(source.SelectedSearchTab);

        tab.Query = "legal agreement";
        tab.ExactMatch = true;
        tab.SearchContents = true;
        tab.DateFrom = new DateTime(2026, 1, 1);
        tab.DateTo = new DateTime(2026, 2, 1);
        tab.ToggleScopeFolder(@"X:\AA Civil");
        tab.ToggleExcludedScopeFolder(@"X:\AA Civil\Archive");
        source.BrowserLocation = @"X:\AA Civil";
        tab.SetBrowseLocation(@"X:\AA Civil");
        tab.SetWorkspaceMode(browsing: true);
        var expectedTitle = tab.Title;

        Assert.True(source.MoveSearchTabTo(tab, destination));

        var moved = Assert.IsType<SearchTabViewModel>(destination.SelectedSearchTab);
        Assert.Equal("legal agreement", moved.Query);
        Assert.True(moved.ExactMatch);
        Assert.True(moved.SearchContents);
        Assert.Equal(new DateTime(2026, 1, 1), moved.DateFrom);
        Assert.Equal(new DateTime(2026, 2, 1), moved.DateTo);
        Assert.Equal([@"X:\AA Civil"], moved.ScopePaths);
        Assert.Equal([@"X:\AA Civil\Archive"], moved.ExcludedScopePaths);
        Assert.True(moved.IsBrowsing);
        Assert.Equal(expectedTitle, moved.Title);
    }

    private static MainWindowViewModel CreateWindowModel() =>
        new(new AppConfig(), startBackgroundWork: false);
}
