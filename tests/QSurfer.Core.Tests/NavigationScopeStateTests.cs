using QSurfer.Avalonia.ViewModels;
using QSurfer.Core.Models;
using Xunit;

namespace QSurfer.Core.Tests;

public sealed class NavigationScopeStateTests
{
    [Fact]
    public async Task NewTabClearsTheNavigationScopePaintForItsOwnEmptyScope()
    {
        using var window = CreateWindow(out var node);

        await window.ToggleNavigationSearchScopeAsync(node);
        Assert.True(node.IsScopeSelected);

        await window.NewSearchTabCommand.ExecuteAsync();

        Assert.Empty(window.SelectedSearchTab!.ScopePaths);
        Assert.False(node.IsScopeSelected);
    }

    [Fact]
    public async Task InactiveTabScopeChangesCannotRepaintTheActiveTabsNavigationScope()
    {
        using var window = CreateWindow(out var node);
        var firstTab = window.SelectedSearchTab!;
        await window.ToggleNavigationSearchScopeAsync(node);

        await window.NewSearchTabCommand.ExecuteAsync();
        Assert.False(node.IsScopeSelected);

        firstTab.ToggleScopeFolder(@"\Shared\Other");

        Assert.False(node.IsScopeSelected);
    }

    [Fact]
    public void ScopeEntriesCanBeRemovedIndividually()
    {
        using var window = CreateWindow(out _);
        var tab = window.SelectedSearchTab!;
        tab.SetScopeFolders(
        [
            @"Shared\AA Criminal",
            @"Shared\AA Civil",
            @"Shared\AA Estates",
            @"Shared\AA Family",
            @"Shared\AA Forms",
        ]);

        Assert.Equal(
            new[]
            {
                @"Shared\AA Criminal",
                @"Shared\AA Civil",
                @"Shared\AA Estates",
                @"Shared\AA Family",
                @"Shared\AA Forms",
            },
            tab.ScopePaths);
        Assert.Equal(5, tab.ScopeDisplayEntries.Count);

        tab.RemoveScopeEntry(tab.ScopeDisplayEntries[2]);

        Assert.Equal(4, tab.ScopePaths.Count);
        Assert.DoesNotContain(@"Shared\AA Estates", tab.ScopePaths);
    }

    [Fact]
    public void AddingAScopeEntryKeepsExistingFoldersAndIgnoresDuplicates()
    {
        using var window = CreateWindow(out _);
        var tab = window.SelectedSearchTab!;
        tab.SetScopeFolder(@"Shared\AA Criminal");

        Assert.True(tab.AddScopeFolder(@"Shared\AA Estates"));
        Assert.False(tab.AddScopeFolder(@"Shared\AA Criminal"));
        Assert.Equal(
            new[] { @"Shared\AA Criminal", @"Shared\AA Estates" },
            tab.ScopePaths);
    }

    [Fact]
    public void AppendingTheNextAcceptedAddressKeepsTheExistingScope()
    {
        using var window = CreateWindow(out _);
        var tab = window.SelectedSearchTab!;
        tab.SetScopeFolder(@"Shared\AA Criminal");

        tab.BeginScopeAppend();
        Assert.True(tab.IsScopeAppendPending);
        Assert.Equal(2, tab.ScopeDisplayEntries.Count);
        Assert.True(tab.ScopeDisplayEntries[^1].IsPending);
        Assert.Equal("Type a new path to add to this query", tab.ScopeDisplayEntries[^1].Text);

        Assert.True(tab.ApplyAddressScope(@"Shared\AA Estates"));
        Assert.False(tab.IsScopeAppendPending);
        Assert.Equal(
            new[] { @"Shared\AA Criminal", @"Shared\AA Estates" },
            tab.ScopePaths);
    }

    [Fact]
    public void AcceptingAnExistingAddressScopeDoesNotReplaceOtherFolders()
    {
        using var window = CreateWindow(out _);
        var tab = window.SelectedSearchTab!;
        tab.SetScopeFolders([@"Shared\AA Criminal", @"Shared\AA Estates"]);

        Assert.False(tab.ApplyAddressScope(@"Shared\AA Criminal"));
        Assert.Equal(
            new[] { @"Shared\AA Criminal", @"Shared\AA Estates" },
            tab.ScopePaths);
    }

    [Fact]
    public void ScopeEntryToggleMovesAFolderBetweenIncludeAndExclude()
    {
        using var window = CreateWindow(out _);
        var tab = window.SelectedSearchTab!;
        tab.SetScopeFolder(@"Shared\AA Criminal");

        tab.SetScopeEntryIncluded(tab.ScopeDisplayEntries[0], false);

        Assert.Empty(tab.ScopePaths);
        Assert.Equal(new[] { @"Shared\AA Criminal" }, tab.ExcludedScopePaths);
        Assert.True(tab.ScopeDisplayEntries[0].IsExcluded);

        tab.SetScopeEntryIncluded(tab.ScopeDisplayEntries[0], true);

        Assert.Equal(new[] { @"Shared\AA Criminal" }, tab.ScopePaths);
        Assert.Empty(tab.ExcludedScopePaths);
        Assert.True(tab.ScopeDisplayEntries[0].IsIncluded);
    }

    [Fact]
    public void ReplacingTheScopeClearsAnEarlierExclusionForThatFolder()
    {
        using var window = CreateWindow(out _);
        var tab = window.SelectedSearchTab!;
        tab.AddResults(
        [
            new SearchResult { Name = "Known.docx", Path = @"Shared\Known.docx" },
        ]);

        tab.SetScopeFolder(@"Shared");
        tab.SetScopeEntryIncluded(tab.ScopeDisplayEntries[0], false);
        tab.SetScopeFolder(@"Shared");

        Assert.Equal(new[] { @"Shared" }, tab.ScopePaths);
        Assert.Empty(tab.ExcludedScopePaths);
        Assert.Single(tab.Results);
    }

    [Fact]
    public void ScopeEntryToggleKeepsTheFolderInItsStableDisplayPosition()
    {
        using var window = CreateWindow(out _);
        var tab = window.SelectedSearchTab!;
        tab.SetScopeFolders([@"Shared\Alpha", @"Shared\Zeta"]);

        tab.SetScopeEntryIncluded(tab.ScopeDisplayEntries[1], false);

        Assert.Equal(@"Shared\Alpha", tab.ScopeDisplayEntries[0].Path);
        Assert.Equal(@"Shared\Zeta", tab.ScopeDisplayEntries[1].Path);
        Assert.True(tab.ScopeDisplayEntries[1].IsExcluded);
    }

    [Fact]
    public void ScopeEntryToggleImmediatelyRepaintsExistingResults()
    {
        using var window = CreateWindow(out _);
        var tab = window.SelectedSearchTab!;
        tab.AddResults(
        [
            new SearchResult { Name = "Included.docx", Path = @"Shared\\Included\\Included.docx" },
            new SearchResult { Name = "Other.docx", Path = @"Shared\\Other\\Other.docx" },
        ]);
        tab.SetScopeFolders([@"Shared\\Included", @"Shared\\Other"]);

        tab.SetScopeEntryIncluded(tab.ScopeDisplayEntries.Single(entry => entry.Path == @"Shared\\Included"), false);

        Assert.Single(tab.Results);
        Assert.Equal("Other.docx", tab.Results[0].Name);
    }

    [Fact]
    public void ScopeEntryToggleMatchesTheNasPathWhenAResultDisplaysThroughAMappedDrive()
    {
        using var window = CreateWindow(out _);
        var tab = window.SelectedSearchTab!;
        tab.AddResults(
        [
            new SearchResult
            {
                Name = "Included.docx",
                Path = @"unmapped-source\\Included\\Included.docx",
                ResolvedPath = @"\\Shared\\Included\\Included.docx",
                WindowsPath = @"X:\\Included\\Included.docx",
            },
            new SearchResult
            {
                Name = "Other.docx",
                Path = @"unmapped-source\\Other\\Other.docx",
                ResolvedPath = @"\\Shared\\Other\\Other.docx",
                WindowsPath = @"X:\\Other\\Other.docx",
            },
        ]);
        tab.SetScopeFolders([@"Shared\\Included", @"Shared\\Other"]);

        tab.SetScopeEntryIncluded(tab.ScopeDisplayEntries.Single(entry => entry.Path == @"Shared\\Included"), false);

        Assert.Single(tab.Results);
        Assert.Equal("Other.docx", tab.Results[0].Name);
    }

    [Fact]
    public void ScopeEntriesShowTheLoadedResultCountForEachFolder()
    {
        using var window = CreateWindow(out _);
        var tab = window.SelectedSearchTab!;
        tab.SetScopeFolders([@"Shared\\Included", @"Shared\\Other"]);
        tab.AddResults(
        [
            new SearchResult { Name = "First.docx", Path = @"Shared\\Included\\First.docx" },
            new SearchResult { Name = "Second.docx", Path = @"Shared\\Included\\Nested\\Second.docx" },
            new SearchResult { Name = "Other.docx", Path = @"Shared\\Other\\Other.docx" },
        ]);

        Assert.Equal(2, tab.ScopeDisplayEntries.Single(entry => entry.Path == @"Shared\\Included").ResultCount);
        Assert.Equal(1, tab.ScopeDisplayEntries.Single(entry => entry.Path == @"Shared\\Other").ResultCount);
    }

    private static MainWindowViewModel CreateWindow(out NavigationTreeNode node)
    {
        var config = new AppConfig();
        config.PathMappings.Add(new PathMapping { ShareRoot = @"\Shared", MappedRoot = @"X:\" });
        var window = new MainWindowViewModel(config, startBackgroundWork: false);
        node = new NavigationTreeNode { Name = "AA Criminal", FullPath = @"X:\AA Criminal" };
        window.NavigationRoots.Add(node);
        return window;
    }
}
