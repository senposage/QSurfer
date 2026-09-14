using QSurfer.Avalonia.ViewModels;
using QSurfer.Core.Models;
using Xunit;

namespace QSurfer.Core.Tests;

public sealed class SearchTabRegressionTests
{
    [Fact]
    public void RestoringParentExclusionPreservesAnExplicitlyIncludedChild()
    {
        using var tab = CreateTab();

        tab.RestoreScope(
            "folder",
            "",
            [@"X:\Client Files"],
            [@"X:\"]);

        Assert.Equal(["X:\\Client Files"], tab.ScopePaths);
        Assert.Equal(["X:"], tab.ExcludedScopePaths);
    }

    [Fact]
    public void DirectExclusionFiltersOneBranchWithoutAnIncludedFolder()
    {
        using var tab = CreateTab();
        tab.AddResults(
        [
            Result("Visible", @"Shared\AA Civil\Visible.pdf"),
            Result("Hidden", @"Shared\AA Criminal\Hidden.pdf"),
        ]);

        tab.ToggleExcludedScopeFolder(@"\Shared\AA Criminal");

        Assert.True(tab.HasFolderScope);
        Assert.Empty(tab.ScopePaths);
        Assert.Equal([@"Shared\AA Criminal"], tab.ExcludedScopePaths);
        Assert.Equal(["Visible.pdf"], tab.Results.Select(result => result.FileName));
    }

    [Fact]
    public void DirectExclusionCanBeRemovedWithoutChangingIncludedScopes()
    {
        using var tab = CreateTab();
        tab.ToggleScopeFolder(@"\Shared\AA Civil");
        tab.ToggleExcludedScopeFolder(@"\Shared\AA Civil\Archive");

        tab.ToggleExcludedScopeFolder(@"\Shared\AA Civil\Archive");

        Assert.Equal([@"Shared\AA Civil"], tab.ScopePaths);
        Assert.Empty(tab.ExcludedScopePaths);
        Assert.True(tab.HasFolderScope);
    }

    [Fact]
    public void RestoredStandaloneExclusionFiltersMatchingBranch()
    {
        using var tab = CreateTab();
        tab.AddResults(
        [
            Result("Keep", @"Shared\Open\Keep.pdf"),
            Result("Skip", @"Shared\Archive\Skip.pdf"),
        ]);

        tab.RestoreScope("folder", "", [], [@"\Shared\Archive"]);

        Assert.True(tab.HasFolderScope);
        Assert.Empty(tab.ScopePaths);
        Assert.Equal([@"Shared\Archive"], tab.ExcludedScopePaths);
        Assert.Equal(["Keep.pdf"], tab.Results.Select(result => result.FileName));
    }

    [Fact]
    public void DisabledParentKeepsAnExplicitlyIncludedChild()
    {
        using var tab = CreateTab();
        tab.AddResults(
        [
            Result("Parent", @"X:\Parent.pdf"),
            Result("Child", @"X:\Client Files\Child.pdf"),
        ]);

        tab.SetScopeFolders([@"X:\", @"X:\Client Files"]);
        var root = Assert.Single(tab.ScopeDisplayEntries, entry => entry.Path.Equals("X:", StringComparison.OrdinalIgnoreCase));
        tab.SetScopeEntryIncluded(root, included: false);

        Assert.Equal([@"X:\Client Files"], tab.ScopePaths);
        Assert.Equal(["X:"], tab.ExcludedScopePaths);
        Assert.Equal(["Child.pdf"], tab.Results.Select(result => result.FileName));
    }

    [Fact]
    public void EquivalentMappedAndCanonicalProviderPathsAreDeduplicated()
    {
        using var tab = CreateTab();
        tab.AddResults(
        [
            new SearchResult
            {
                Name = "Agreement.docx",
                Path = @"Shared\Legal\Agreement.docx",
                WindowsPath = @"X:\Legal\Agreement.docx",
            },
        ]);

        var duplicate = new SearchResult
        {
            Name = "Agreement.docx",
            Path = @"D:\Indexer\Shared\Legal\Agreement.docx",
            ResolvedPath = @"X:\Legal\Agreement.docx",
        };

        Assert.True(tab.ContainsResult(duplicate));
    }

    [Fact]
    public void FutureDateIsClampedToToday()
    {
        using var tab = CreateTab();

        tab.DateTo = DateTime.Today.AddDays(30);

        Assert.Equal(DateTime.Today, tab.DateTo);
    }

    [Fact]
    public void FolderGroupsPutFoldersFirstAndFilesByRecentness()
    {
        using var tab = CreateTab();
        tab.AddResults(
        [
            new SearchResult { Name = "Old", Extension = "pdf", Path = @"Shared\Old.pdf", Modified = "2024-01-01" },
            new SearchResult { Name = "Zulu", IsFolder = true, Path = @"Shared\Zulu" },
            new SearchResult { Name = "Alpha", IsFolder = true, Path = @"Shared\Alpha" },
            new SearchResult { Name = "New", Extension = "pdf", Path = @"Shared\New.pdf", Modified = "2025-01-01" },
        ]);

        tab.ApplySortSpecification("folder:asc");

        Assert.Equal(["Alpha", "Zulu", "New.pdf", "Old.pdf"], tab.Results.Select(result => result.FileName));
    }

    [Fact]
    public void SuppressingFolderDatesKeepsFoldersAlphabeticalBeforeDatedFiles()
    {
        using var tab = CreateTab();
        tab.AddResults(
        [
            new SearchResult { Name = "Zulu", IsFolder = true, Path = @"Shared\Zulu", Modified = "2026-09-12" },
            new SearchResult { Name = "Alpha", IsFolder = true, Path = @"Shared\Alpha", Modified = "2020-01-01" },
            new SearchResult { Name = "Old", Extension = "pdf", Path = @"Shared\Old.pdf", Modified = "2024-01-01" },
            new SearchResult { Name = "New", Extension = "pdf", Path = @"Shared\New.pdf", Modified = "2025-01-01" },
        ]);

        tab.ApplySortSpecification("recent:desc");
        tab.SuppressFolderDates = true;

        Assert.Equal(["Alpha", "Zulu", "New.pdf", "Old.pdf"], tab.Results.Select(result => result.FileName));
    }

    [Fact]
    public void TiedRecentResultsUsePathAsAStableFinalTieBreaker()
    {
        using var tab = CreateTab();
        tab.AddResults(
        [
            new SearchResult { Name = "Same", Extension = "pdf", Path = @"Shared\Zulu\Same.pdf", Modified = "2025-01-01" },
            new SearchResult { Name = "Same", Extension = "pdf", Path = @"Shared\Alpha\Same.pdf", Modified = "2025-01-01" },
        ]);

        tab.ApplySortSpecification("recent:desc");

        Assert.Equal([@"Shared\Alpha\Same.pdf", @"Shared\Zulu\Same.pdf"], tab.Results.Select(result => result.Path));
    }

    [Fact]
    public async Task SearchCommandAcceptsANewQueryWhileThePreviousSearchIsRunning()
    {
        var firstSearchStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstSearch = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var executions = 0;
        using var tab = CreateTab(async _ =>
        {
            executions++;
            if (executions == 1)
            {
                firstSearchStarted.SetResult();
                await releaseFirstSearch.Task;
            }
        });

        tab.Query = "first";
        var firstSearch = tab.SearchCommand.ExecuteAsync();
        await firstSearchStarted.Task;

        tab.Query = "replacement";
        Assert.True(tab.SearchCommand.CanExecute(null));
        await tab.SearchCommand.ExecuteAsync();

        releaseFirstSearch.SetResult();
        await firstSearch;
        Assert.Equal(2, executions);
    }

    private static SearchResult Result(string name, string path) => new()
    {
        Name = name,
        Extension = "pdf",
        Path = path,
        Modified = "2025-01-01",
    };

    private static SearchTabViewModel CreateTab(Func<SearchTabViewModel, Task>? search = null)
    {
        var fileTypes = new[]
        {
            new FileTypeFilter { Name = "All types", IncludeAllFiles = true, IncludeFolders = true },
        };
        return new SearchTabViewModel(
            1,
            fileTypes,
            search ?? (_ => Task.CompletedTask),
            _ => Task.CompletedTask,
            _ => Task.CompletedTask,
            _ => Task.CompletedTask,
            _ => Task.CompletedTask,
            _ => Task.CompletedTask,
            _ => Task.CompletedTask,
            _ => Task.CompletedTask);
    }
}
