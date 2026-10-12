using QSurfer.Core.Models;

namespace QSurfer.Core.Services;

/// <summary>Represents a deliberately unconfigured search surface without creating an invalid network URI.</summary>
public sealed class UnavailableSearchProvider(string message) : ISearchProvider
{
    public string ProviderName => "Unavailable";

    public Task EnsureAvailableAsync(CancellationToken cancellationToken, SearchProviderScope? scope = null) =>
        Task.FromException(new InvalidOperationException(message));

    public Task<IReadOnlyList<SearchResult>> SearchAsync(
        string query,
        FileTypeFilter typeFilter,
        int limit,
        int offset,
        string? sortBy,
        string sortDirection,
        Func<IReadOnlyList<SearchResult>, Task>? batchReceived,
        CancellationToken cancellationToken,
        SearchProviderScope? scope = null,
        SearchProviderQueryOptions? options = null) =>
        Task.FromException<IReadOnlyList<SearchResult>>(new InvalidOperationException(message));

    public Task<IReadOnlyList<SearchResult>> SearchDirectoriesAsync(
        string query,
        int limit,
        CancellationToken cancellationToken,
        SearchProviderScope? scope = null) =>
        Task.FromException<IReadOnlyList<SearchResult>>(new InvalidOperationException(message));

    public Task<byte[]?> ThumbnailAsync(SearchResult result, CancellationToken cancellationToken) => Task.FromResult<byte[]?>(null);

    public void Dispose()
    {
    }
}
