using QSurfer.Avalonia.ViewModels;
using QSurfer.Core.Models;
using Xunit;

namespace QSurfer.Core.Tests;

public sealed class SearchScopeTests
{
    [Fact]
    public void SelectingParentThenChildExcludesOnlyTheChildBranch()
    {
        var tab = CreateTab();

        tab.ToggleScopeFolder(@"\Shared\AA Criminal");
        tab.ToggleScopeFolder(@"\Shared\AA Criminal\Closed");

        Assert.Equal("folder", tab.SelectedScope.Key);
        Assert.Equal([@"Shared\AA Criminal"], tab.ScopePaths);
        Assert.Equal([@"Shared\AA Criminal\Closed"], tab.ExcludedScopePaths);
    }

    [Fact]
    public void ClearingScopeRemovesIncludedAndExcludedFolders()
    {
        var tab = CreateTab();
        tab.ToggleScopeFolder(@"\Shared\AA Criminal");
        tab.ToggleScopeFolder(@"\Shared\AA Criminal\Closed");

        tab.ClearScopeFolder();

        Assert.Equal("all", tab.SelectedScope.Key);
        Assert.Empty(tab.ScopePaths);
        Assert.Empty(tab.ExcludedScopePaths);
    }

    private static SearchTabViewModel CreateTab()
    {
        var fileTypes = new[]
        {
            new FileTypeFilter { Name = "All types", IncludeAllFiles = true, IncludeFolders = true },
        };
        return new SearchTabViewModel(
            1,
            fileTypes,
            _ => Task.CompletedTask,
            _ => Task.CompletedTask,
            _ => Task.CompletedTask,
            _ => Task.CompletedTask,
            _ => Task.CompletedTask,
            _ => Task.CompletedTask,
            _ => Task.CompletedTask,
            _ => Task.CompletedTask);
    }
}
