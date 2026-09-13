using QSurfer.Core.Models;
using QSurfer.Core.Services;
using Xunit;

namespace QSurfer.Core.Tests;

public sealed class CompositeSearchProviderTests
{
    [Fact]
    public async Task SearchUsesHealthyProviderWhenCompanionFails()
    {
        using var provider = new CompositeSearchProvider([
            new StubProvider("Qsirch", [Result("NAS result", @"\Shared\NAS result.docx")]),
            new StubProvider("QIndexer", [], new TimeoutException("indexer unavailable")),
        ]);

        var results = await provider.SearchAsync("result", AllFiles, 20, 0, "name", "asc", null, CancellationToken.None);

        var result = Assert.Single(results);
        Assert.Equal("NAS result", result.Name);
    }

    [Fact]
    public async Task SearchMergesAndDeduplicatesProviderResults()
    {
        using var provider = new CompositeSearchProvider([
            new StubProvider("Qsirch", [Result("Shared", @"\Shared\Shared.docx")]),
            new StubProvider("QIndexer", [Result("Shared", @"\Shared\Shared.docx"), Result("Indexed", @"\Shared\Indexed.pdf")]),
        ]);

        var results = await provider.SearchAsync("", AllFiles, 20, 0, "name", "asc", null, CancellationToken.None);

        Assert.Equal(["Indexed", "Shared"], results.Select(result => result.Name));
    }

    [Fact]
    public async Task SearchUsesPathAsAStableTieBreakerAcrossProviders()
    {
        using var provider = new CompositeSearchProvider([
            new StubProvider("Qsirch", [Result("Same", @"\Shared\Zulu\Same.docx")]),
            new StubProvider("QIndexer", [Result("Same", @"\Shared\Alpha\Same.docx")]),
        ]);

        var results = await provider.SearchAsync("same", AllFiles, 20, 0, "modified", "desc", null, CancellationToken.None);

        Assert.Equal([@"\Shared\Alpha\Same.docx", @"\Shared\Zulu\Same.docx"], results.Select(result => result.Path));
    }

    [Fact]
    public async Task LocalOnlyScopeSkipsProviderThatDoesNotIndexIt()
    {
        var nas = new StubProvider("Qsirch", [Result("NAS result", @"\Shared\NAS result.docx")], acceptsScope: false);
        var local = new StubProvider("QIndexer", [Result("Local result", @"D:\Cases\Local result.docx")]);
        using var provider = new CompositeSearchProvider([nas, local]);

        var results = await provider.SearchAsync(
            "result",
            AllFiles,
            20,
            0,
            "name",
            "asc",
            null,
            CancellationToken.None,
            new SearchProviderScope([@"D:\"], []));

        Assert.Equal(0, nas.SearchRequests);
        Assert.Equal(1, local.SearchRequests);
        Assert.Equal("Local result", Assert.Single(results).Name);
    }

    [Fact]
    public async Task PagingExtendsEachProviderWindowWithoutRepeatingEarlierRows()
    {
        var nas = new StubProvider("Qsirch", Enumerable.Range(0, 80)
            .Select(index => Result($"NAS {index:D3}", $@"\Shared\NAS {index:D3}.docx"))
            .ToList());
        using var provider = new CompositeSearchProvider([nas]);

        await provider.SearchAsync("result", AllFiles, 20, 0, "name", "asc", null, CancellationToken.None);
        await provider.SearchAsync("result", AllFiles, 20, 20, "name", "asc", null, CancellationToken.None);
        await provider.SearchAsync("result", AllFiles, 20, 0, "name", "asc", null, CancellationToken.None);

        Assert.Equal([0, 20, 0], nas.SearchOffsets);
    }

    [Fact]
    public async Task PagingDoesNotRetryAnOptionalProviderThatAlreadyFailedForThisSearch()
    {
        var nas = new StubProvider("Qsirch", Enumerable.Range(0, 60)
            .Select(index => Result($"NAS {index:D3}", $@"\Shared\NAS {index:D3}.docx"))
            .ToList());
        var unavailable = new StubProvider("QIndexer", [], new HttpRequestException("offline"));
        using var provider = new CompositeSearchProvider([nas, unavailable]);

        await provider.SearchAsync("result", AllFiles, 20, 0, "name", "asc", null, CancellationToken.None);
        await provider.SearchAsync("result", AllFiles, 20, 20, "name", "asc", null, CancellationToken.None);
        await provider.SearchAsync("result", AllFiles, 20, 40, "name", "asc", null, CancellationToken.None);

        Assert.Equal(1, unavailable.SearchRequests);
        Assert.Equal([0, 20, 40], nas.SearchOffsets);
    }

    private static readonly FileTypeFilter AllFiles = new() { Name = "All", IncludeAllFiles = true, IncludeFolders = true };

    private static SearchResult Result(string name, string path) => new()
    {
        Name = name,
        Path = path,
        Extension = Path.GetExtension(path).TrimStart('.'),
        Modified = "2026-09-10 12:00",
    };

    private sealed class StubProvider(
        string name,
        IReadOnlyList<SearchResult> results,
        Exception? failure = null,
        bool acceptsScope = true) : ISearchProvider, IScopeAwareSearchProvider
    {
        public string ProviderName => name;
        public int SearchRequests { get; private set; }
        public List<int> SearchOffsets { get; } = [];

        public Task EnsureAvailableAsync(CancellationToken cancellationToken, SearchProviderScope? scope = null) => failure == null
            ? Task.CompletedTask
            : Task.FromException(failure);

        public bool CanSearchScope(SearchProviderScope scope) => acceptsScope;

        public Task<IReadOnlyList<SearchResult>> SearchAsync(string query, FileTypeFilter typeFilter, int limit, int offset, string? sortBy, string sortDirection, Func<IReadOnlyList<SearchResult>, Task>? batchReceived, CancellationToken cancellationToken, SearchProviderScope? scope = null)
        {
            SearchRequests++;
            SearchOffsets.Add(offset);
            if (failure != null)
            {
                return Task.FromException<IReadOnlyList<SearchResult>>(failure);
            }
            return Task.FromResult<IReadOnlyList<SearchResult>>(results.Skip(offset).Take(limit).ToList());
        }

        public Task<IReadOnlyList<SearchResult>> SearchDirectoriesAsync(string query, int limit, CancellationToken cancellationToken, SearchProviderScope? scope = null) =>
            Task.FromResult<IReadOnlyList<SearchResult>>([]);

        public Task<byte[]?> ThumbnailAsync(SearchResult result, CancellationToken cancellationToken) => Task.FromResult<byte[]?>(null);

        public void Dispose()
        {
        }
    }
}
