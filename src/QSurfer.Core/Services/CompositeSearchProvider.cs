using QSurfer.Core.Models;

namespace QSurfer.Core.Services;

/// <summary>
/// Queries every configured index independently. A healthy provider remains
/// useful when its companion is offline, slow, or being rebuilt.
/// </summary>
public sealed class CompositeSearchProvider(IEnumerable<ISearchProvider> providers) : ISearchProvider
{
    private readonly IReadOnlyList<ISearchProvider> _providers = providers.ToList();
    private readonly object _searchCacheLock = new();
    private readonly Dictionary<SearchCacheKey, ProviderSearchCache> _searchCaches = [];

    public string ProviderName => string.Join(" + ", _providers.Select(provider => provider.ProviderName));

    public async Task EnsureAvailableAsync(CancellationToken cancellationToken, SearchProviderScope? scope = null)
    {
        var providers = ProvidersForScope(scope);
        if (providers.Count == 0)
        {
            throw new InvalidOperationException("No configured search provider indexes the selected folders.");
        }
        var checks = await Task.WhenAll(providers.Select(provider => CheckAvailabilityAsync(provider, cancellationToken, scope)));
        if (checks.Any(check => check.Success))
        {
            foreach (var failure in checks.Where(check => !check.Success))
            {
                AppLogger.Warn("search", $"optional provider unavailable provider=\"{failure.Provider}\" error=\"{failure.Error!.Message}\"");
            }
            return;
        }

        throw new InvalidOperationException($"No configured search provider is available. {string.Join(" ", checks.Select(check => $"{check.Provider}: {check.Error?.Message}"))}");
    }

    public async Task<IReadOnlyList<SearchResult>> SearchAsync(
        string query,
        FileTypeFilter typeFilter,
        int limit,
        int offset,
        string? sortBy,
        string sortDirection,
        Func<IReadOnlyList<SearchResult>, Task>? batchReceived,
        CancellationToken cancellationToken,
        SearchProviderScope? scope = null)
    {
        var requested = Math.Max(1, offset + limit);
        var providers = ProvidersForScope(scope);
        if (providers.Count == 0)
        {
            throw new InvalidOperationException("No configured search provider indexes the selected folders.");
        }
        var searches = await Task.WhenAll(providers.Select(provider => LoadCachedWindowAsync(
            provider, query, typeFilter, requested, sortBy, sortDirection, cancellationToken, scope, offset == 0)));
        var successful = searches.Where(search => search.Error == null).ToList();
        if (successful.Count == 0)
        {
            throw new InvalidOperationException($"All configured search providers failed. {string.Join(" ", searches.Select(search => $"{search.Provider}: {search.Error?.Message}"))}");
        }

        foreach (var failure in searches.Where(search => search.Error != null))
        {
            AppLogger.Warn("search", $"optional provider search failed provider=\"{failure.Provider}\" error=\"{failure.Error!.Message}\"");
        }

        var results = Sort(Merge(successful.SelectMany(search => search.Results)), sortBy, sortDirection)
            .Skip(offset)
            .Take(Math.Max(1, limit))
            .ToList();
        if (batchReceived != null && results.Count > 0)
        {
            await batchReceived(results);
        }
        return results;
    }

    public async Task<IReadOnlyList<SearchResult>> SearchDirectoriesAsync(
        string query,
        int limit,
        CancellationToken cancellationToken,
        SearchProviderScope? scope = null)
    {
        var providers = ProvidersForScope(scope);
        if (providers.Count == 0)
        {
            return [];
        }
        var searches = await Task.WhenAll(providers.Select(async provider =>
        {
            try
            {
                return new ProviderResult(provider.ProviderName, await provider.SearchDirectoriesAsync(query, limit, cancellationToken, scope), null);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                return new ProviderResult(provider.ProviderName, [], ex);
            }
        }));
        var successful = searches.Where(search => search.Error == null).ToList();
        if (successful.Count == 0)
        {
            throw new InvalidOperationException($"All configured directory providers failed. {string.Join(" ", searches.Select(search => $"{search.Provider}: {search.Error?.Message}"))}");
        }

        foreach (var failure in searches.Where(search => search.Error != null))
        {
            AppLogger.Warn("search", $"optional provider directory search failed provider=\"{failure.Provider}\" error=\"{failure.Error!.Message}\"");
        }
        return Merge(successful.SelectMany(search => search.Results))
            .OrderBy(result => result.FileName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(ResultKey, StringComparer.OrdinalIgnoreCase)
            .Take(Math.Max(1, limit))
            .ToList();
    }

    public async Task<byte[]?> ThumbnailAsync(SearchResult result, CancellationToken cancellationToken)
    {
        foreach (var provider in _providers)
        {
            try
            {
                var thumbnail = await provider.ThumbnailAsync(result, cancellationToken);
                if (thumbnail is { Length: > 0 })
                {
                    return thumbnail;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                AppLogger.Warn("search", $"optional provider thumbnail failed provider=\"{provider.ProviderName}\" error=\"{ex.Message}\"");
            }
        }
        return null;
    }

    public void Dispose()
    {
        lock (_searchCacheLock)
        {
            foreach (var cache in _searchCaches.Values)
            {
                cache.Dispose();
            }
            _searchCaches.Clear();
        }

        foreach (var provider in _providers)
        {
            provider.Dispose();
        }
    }

    private IReadOnlyList<ISearchProvider> ProvidersForScope(SearchProviderScope? scope)
    {
        if (scope is not { HasPaths: true })
        {
            return _providers;
        }

        var eligible = _providers
            .Where(provider => provider is not IScopeAwareSearchProvider scoped || scoped.CanSearchScope(scope))
            .ToList();
        foreach (var skipped in _providers.Except(eligible))
        {
            AppLogger.Info("search", $"provider skipped provider=\"{skipped.ProviderName}\" reason=\"scope has no matching indexed root\"");
        }
        return eligible;
    }

    private static async Task<ProviderResult> CheckAvailabilityAsync(
        ISearchProvider provider,
        CancellationToken cancellationToken,
        SearchProviderScope? scope)
    {
        try
        {
            await provider.EnsureAvailableAsync(cancellationToken, scope);
            return new ProviderResult(provider.ProviderName, [], null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return new ProviderResult(provider.ProviderName, [], ex);
        }
    }

    private async Task<ProviderResult> LoadCachedWindowAsync(
        ISearchProvider provider,
        string query,
        FileTypeFilter typeFilter,
        int requested,
        string? sortBy,
        string sortDirection,
        CancellationToken cancellationToken,
        SearchProviderScope? scope,
        bool refresh)
    {
        var cache = GetSearchCache(provider, query, typeFilter, sortBy, sortDirection, scope, refresh);
        if (cache.Failure != null)
        {
            return new ProviderResult(provider.ProviderName, [], cache.Failure);
        }

        try
        {
            await cache.Gate.WaitAsync(cancellationToken);
            try
            {
                if (cache.Failure != null)
                {
                    return new ProviderResult(provider.ProviderName, [], cache.Failure);
                }

                while (!cache.IsComplete && cache.Results.Count < requested)
                {
                    var pageSize = Math.Min(500, requested - cache.Results.Count);
                    var page = await provider.SearchAsync(
                        query,
                        typeFilter,
                        pageSize,
                        cache.Results.Count,
                        sortBy,
                        sortDirection,
                        null,
                        cancellationToken,
                        scope);
                    cache.Results.AddRange(page);
                    if (page.Count < pageSize)
                    {
                        cache.IsComplete = true;
                    }
                }

                return new ProviderResult(provider.ProviderName, cache.Results.Take(requested).ToList(), null);
            }
            finally
            {
                cache.Gate.Release();
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            cache.Failure = ex;
            return new ProviderResult(provider.ProviderName, [], ex);
        }
    }

    private ProviderSearchCache GetSearchCache(
        ISearchProvider provider,
        string query,
        FileTypeFilter typeFilter,
        string? sortBy,
        string sortDirection,
        SearchProviderScope? scope,
        bool refresh)
    {
        var key = new SearchCacheKey(
            provider,
            query,
            typeFilter.Name,
            typeFilter.Category,
            string.Join('|', typeFilter.Extensions),
            typeFilter.IncludeFolders,
            typeFilter.IncludeAllFiles,
            sortBy ?? "",
            sortDirection,
            string.Join('|', scope?.IncludePaths ?? []),
            string.Join('|', scope?.ExcludePaths ?? []));

        lock (_searchCacheLock)
        {
            if (!refresh && _searchCaches.TryGetValue(key, out var cache))
            {
                return cache;
            }

            cache = new ProviderSearchCache();
            _searchCaches[key] = cache;
            return cache;
        }
    }

    private static IEnumerable<SearchResult> Merge(IEnumerable<SearchResult> source) =>
        source.DistinctBy(ResultKey, StringComparer.OrdinalIgnoreCase);

    private static string ResultKey(SearchResult result) =>
        $"{result.Path.Replace('/', '\\').TrimEnd('\\')}\0{result.FileName}";

    private static IEnumerable<SearchResult> Sort(IEnumerable<SearchResult> source, string? sortBy, string sortDirection)
    {
        var descending = !string.Equals(sortDirection, "asc", StringComparison.OrdinalIgnoreCase);
        return sortBy?.ToLowerInvariant() switch
        {
            "name" => descending
                ? source.OrderByDescending(result => result.FileName, StringComparer.OrdinalIgnoreCase).ThenBy(ResultKey, StringComparer.OrdinalIgnoreCase)
                : source.OrderBy(result => result.FileName, StringComparer.OrdinalIgnoreCase).ThenBy(ResultKey, StringComparer.OrdinalIgnoreCase),
            "size" => descending
                ? source.OrderByDescending(result => result.Size).ThenBy(result => result.FileName, StringComparer.OrdinalIgnoreCase).ThenBy(ResultKey, StringComparer.OrdinalIgnoreCase)
                : source.OrderBy(result => result.Size).ThenBy(result => result.FileName, StringComparer.OrdinalIgnoreCase).ThenBy(ResultKey, StringComparer.OrdinalIgnoreCase),
            "modified" => descending
                ? source.OrderByDescending(result => result.ModifiedDate).ThenBy(result => result.FileName, StringComparer.OrdinalIgnoreCase).ThenBy(ResultKey, StringComparer.OrdinalIgnoreCase)
                : source.OrderBy(result => result.ModifiedDate).ThenBy(result => result.FileName, StringComparer.OrdinalIgnoreCase).ThenBy(ResultKey, StringComparer.OrdinalIgnoreCase),
            _ => source.OrderBy(ResultKey, StringComparer.OrdinalIgnoreCase),
        };
    }

    private sealed record ProviderResult(string Provider, IReadOnlyList<SearchResult> Results, Exception? Error)
    {
        public bool Success => Error == null;
    }

    private sealed class ProviderSearchCache : IDisposable
    {
        public List<SearchResult> Results { get; } = [];
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public bool IsComplete { get; set; }
        public Exception? Failure { get; set; }

        public void Dispose() => Gate.Dispose();
    }

    private sealed record SearchCacheKey(
        ISearchProvider Provider,
        string Query,
        string TypeName,
        string TypeCategory,
        string Extensions,
        bool IncludeFolders,
        bool IncludeAllFiles,
        string SortBy,
        string SortDirection,
        string IncludePaths,
        string ExcludePaths);
}
