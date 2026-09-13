using QSurfer.Core.Models;

namespace QSurfer.Core.Services;

// Keeps QSurfer's search UI independent from a particular index implementation.
public interface ISearchProvider : IDisposable
{
    string ProviderName { get; }

    Task EnsureAvailableAsync(CancellationToken cancellationToken, SearchProviderScope? scope = null);

    Task<IReadOnlyList<SearchResult>> SearchAsync(
        string query,
        FileTypeFilter typeFilter,
        int limit,
        int offset,
        string? sortBy,
        string sortDirection,
        Func<IReadOnlyList<SearchResult>, Task>? batchReceived,
        CancellationToken cancellationToken,
        SearchProviderScope? scope = null);

    Task<IReadOnlyList<SearchResult>> SearchDirectoriesAsync(
        string query,
        int limit,
        CancellationToken cancellationToken,
        SearchProviderScope? scope = null);

    Task<byte[]?> ThumbnailAsync(SearchResult result, CancellationToken cancellationToken);
}

public sealed record SearchProviderScope(IReadOnlyList<string> IncludePaths, IReadOnlyList<string> ExcludePaths)
{
    public static readonly SearchProviderScope Empty = new([], []);
    public bool HasPaths => IncludePaths.Count > 0 || ExcludePaths.Count > 0;
}

// Lets a provider decline a scope it can prove does not belong to its index.
// The composite uses this to avoid needlessly waking Qsirch for local disks.
public interface IScopeAwareSearchProvider
{
    bool CanSearchScope(SearchProviderScope scope);
}

public static class SearchProviders
{
    public const string Qsirch = "qsirch";
    public const string Standalone = "qsurfer-search-service";

    public static bool IsStandalone(string? provider) =>
        string.Equals(provider, Standalone, StringComparison.OrdinalIgnoreCase);

    public static string Normalize(string? provider) =>
        IsStandalone(provider) ? Standalone : Qsirch;
}
