using QSurfer.Core.Models;

namespace QSurfer.Core.Services;

/// <summary>
/// Queries every configured index independently. A healthy provider remains
/// useful when its companion is offline, slow, or being rebuilt.
/// </summary>
public sealed class CompositeSearchProvider(IEnumerable<ISearchProvider> providers) : ISearchProvider
{
    private static readonly TimeSpan AvailabilityProbeWindow = TimeSpan.FromMilliseconds(750);
    private readonly IReadOnlyList<ISearchProvider> _providers = providers.ToList();
    private readonly object _searchCacheLock = new();
    private readonly Dictionary<SearchCacheKey, ProviderSearchCache> _searchCaches = [];
    private readonly object _availabilityLock = new();
    private readonly Dictionary<string, IReadOnlyList<ISearchProvider>> _readyProvidersByScope = [];

    public string ProviderName => string.Join(" + ", _providers.Select(provider => provider.ProviderName));

    public async Task EnsureAvailableAsync(CancellationToken cancellationToken, SearchProviderScope? scope = null)
    {
        var providers = ProvidersForScope(scope);
        if (providers.Count == 0)
        {
            throw new InvalidOperationException("No configured search provider indexes the selected folders.");
        }

        var pending = providers
            .Select(provider => CheckAvailabilityAsync(provider, cancellationToken, scope))
            .ToList();
        var checks = new List<ProviderResult>();
        var deadline = Task.Delay(AvailabilityProbeWindow, cancellationToken);

        // A slow remote provider must not block a responsive local indexer. Gather
        // providers that prove they are ready during a short probe, then start the
        // search against them while the slow provider remains a later retry.
        while (pending.Count > 0)
        {
            var completed = await Task.WhenAny(pending.Cast<Task>().Append(deadline));
            if (ReferenceEquals(completed, deadline))
            {
                break;
            }

            var check = await (Task<ProviderResult>)completed;
            pending.Remove((Task<ProviderResult>)completed);
            checks.Add(check);
        }

        var ready = checks.Where(check => check.Success).ToList();
        if (ready.Count > 0)
        {
            RememberReadyProviders(scope, ready.Select(check => check.ProviderInstance));
            foreach (var failure in checks.Where(check => !check.Success))
            {
                AppLogger.Warn("search", $"optional provider unavailable provider=\"{failure.Provider}\" error=\"{failure.Error!.Message}\"");
            }
            foreach (var delayedCheck in pending)
            {
                _ = LogDeferredAvailabilityAsync(delayedCheck);
            }
            return;
        }

        if (pending.Count > 0)
        {
            checks.AddRange(await Task.WhenAll(pending));
            ready = checks.Where(check => check.Success).ToList();
            if (ready.Count > 0)
            {
                RememberReadyProviders(scope, ready.Select(check => check.ProviderInstance));
                foreach (var failure in checks.Where(check => !check.Success))
                {
                    AppLogger.Warn("search", $"optional provider unavailable provider=\"{failure.Provider}\" error=\"{failure.Error!.Message}\"");
                }
                return;
            }
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
        SearchProviderScope? scope = null,
        SearchProviderQueryOptions? options = null)
    {
        var requested = Math.Max(1, offset + limit);
        var providers = ReadyProvidersForScope(scope);
        if (providers.Count == 0)
        {
            throw new InvalidOperationException("No configured search provider indexes the selected folders.");
        }
        var searches = await Task.WhenAll(providers.Select(provider => LoadCachedWindowAsync(
            provider, query, typeFilter, requested, sortBy, sortDirection, cancellationToken, scope, options, offset == 0)));
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
        var providers = ReadyProvidersForScope(scope);
        if (providers.Count == 0)
        {
            return [];
        }
        var searches = await Task.WhenAll(providers.Select(async provider =>
        {
            try
            {
                return new ProviderResult(provider, await provider.SearchDirectoriesAsync(query, limit, cancellationToken, scope), null);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                return new ProviderResult(provider, [], ex);
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
            return new ProviderResult(provider, [], null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return new ProviderResult(provider, [], ex);
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
        SearchProviderQueryOptions? options,
        bool refresh)
    {
        var cache = GetSearchCache(provider, query, typeFilter, sortBy, sortDirection, scope, options, refresh);
        if (cache.Failure != null)
        {
            return new ProviderResult(provider, [], cache.Failure);
        }

        try
        {
            await cache.Gate.WaitAsync(cancellationToken);
            try
            {
                if (cache.Failure != null)
                {
                    return new ProviderResult(provider, [], cache.Failure);
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
                        scope,
                        options);
                    cache.Results.AddRange(page);
                    if (page.Count < pageSize)
                    {
                        cache.IsComplete = true;
                    }
                }

                return new ProviderResult(provider, cache.Results.Take(requested).ToList(), null);
            }
            finally
            {
                cache.Gate.Release();
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            cache.Failure = ex;
            return new ProviderResult(provider, [], ex);
        }
    }

    private ProviderSearchCache GetSearchCache(
        ISearchProvider provider,
        string query,
        FileTypeFilter typeFilter,
        string? sortBy,
        string sortDirection,
        SearchProviderScope? scope,
        SearchProviderQueryOptions? options,
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
            string.Join('|', scope?.ExcludePaths ?? []),
            options?.ExactMatch ?? false,
            options?.SearchContents ?? false,
            string.Join('|', options?.RequiredTerms ?? []),
            string.Join('|', options?.AnyTerms ?? []),
            string.Join('|', options?.ExcludedTerms ?? []));

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

    private IReadOnlyList<ISearchProvider> ReadyProvidersForScope(SearchProviderScope? scope)
    {
        var eligible = ProvidersForScope(scope);
        lock (_availabilityLock)
        {
            return _readyProvidersByScope.TryGetValue(ScopeKey(scope), out var ready)
                ? eligible.Where(ready.Contains).ToList()
                : eligible;
        }
    }

    private void RememberReadyProviders(SearchProviderScope? scope, IEnumerable<ISearchProvider> providers)
    {
        lock (_availabilityLock)
        {
            _readyProvidersByScope[ScopeKey(scope)] = providers.Distinct().ToList();
        }
    }

    private static string ScopeKey(SearchProviderScope? scope) => string.Join('|', [
        string.Join('\u001f', scope?.IncludePaths ?? []),
        string.Join('\u001f', scope?.ExcludePaths ?? []),
    ]);

    private static async Task LogDeferredAvailabilityAsync(Task<ProviderResult> check)
    {
        try
        {
            var result = await check;
            if (!result.Success)
            {
                AppLogger.Warn("search", $"optional provider unavailable provider=\"{result.Provider}\" error=\"{result.Error!.Message}\"");
            }
        }
        catch (OperationCanceledException)
        {
            // The user started a new search before the slow provider finished.
        }
    }

    private sealed record ProviderResult(ISearchProvider ProviderInstance, IReadOnlyList<SearchResult> Results, Exception? Error)
    {
        public string Provider => ProviderInstance.ProviderName;
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
        string ExcludePaths,
        bool ExactMatch,
        bool SearchContents,
        string RequiredTerms,
        string AnyTerms,
        string ExcludedTerms);
}
