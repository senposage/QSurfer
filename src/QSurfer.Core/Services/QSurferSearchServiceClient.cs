using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using QSurfer.Core.Models;

namespace QSurfer.Core.Services;

public sealed class QSurferSearchServiceClient : ISearchProvider
{
    private static readonly Regex NameQuery = new(
        @"^name:""(?<query>.*)""$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex ModifiedClause = new(
        "\\s+modified:(?<from>\\d{4}-\\d{2}-\\d{2})\\.\\.(?<to>\\d{4}-\\d{2}-\\d{2})\\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private readonly HttpClient _http;
    private readonly AppConfig _config;
    private readonly string _baseUrl;
    private readonly SemaphoreSlim _rootsGate = new(1, 1);
    private readonly object _availabilityLock = new();
    private IReadOnlyList<SearchServiceRoot>? _roots;
    private Exception? _recentAvailabilityFailure;
    private DateTimeOffset _retryAfterUtc;

    public QSurferSearchServiceClient(AppConfig config)
        : this(config, null)
    {
    }

    public QSurferSearchServiceClient(AppConfig config, HttpMessageHandler? handler)
    {
        _config = config;
        var service = config.SearchService;
        _baseUrl = $"{(service.Ssl ? "https" : "http")}://{NasIdentity.NormalizeHost(service.Host)}:{service.Port}/v1";
        _http = handler == null
            ? CreateHttpClient(config, service)
            : new HttpClient(handler, disposeHandler: true)
            {
                Timeout = TimeSpan.FromSeconds(Math.Clamp(config.Behavior.SearchTimeoutSeconds, 15, 300)),
            };
        AppLogger.Info("qindexer", $"configured endpoint=\"{_baseUrl}\" bearerConfigured={!string.IsNullOrWhiteSpace(service.Token)}");
        if (!string.IsNullOrWhiteSpace(service.Token))
        {
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", service.Token);
        }
    }

    public string ProviderName => "QIndexer";

    public async Task EnsureAvailableAsync(CancellationToken cancellationToken, SearchProviderScope? scope = null)
    {
        ThrowIfRecentlyUnavailable();
        var uri = $"{_baseUrl}/health";
        try
        {
            AppLogger.Info("qindexer", $"GET endpoint=\"{uri}\"");
            using var response = await _http.GetAsync(uri, cancellationToken);
            AppLogger.Info("qindexer", $"GET endpoint=\"{uri}\" status={(int)response.StatusCode}");
            if (!response.IsSuccessStatusCode)
            {
                throw await CreateRequestExceptionAsync(response, cancellationToken);
            }
            ClearAvailabilityFailure();
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            RememberAvailabilityFailure(ex);
            throw;
        }
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
        ThrowIfRecentlyUnavailable();
        var (searchText, modifiedAfter, modifiedBefore, metadataOnly) = ParseLegacyQuery(query);
        var extensions = typeFilter.Extensions ?? [];
        var serviceScope = await PrepareScopeAsync(scope, cancellationToken);
        var request = new SearchServiceRequest
        {
            SearchId = Guid.NewGuid().ToString("N"),
            Query = searchText,
            Limit = Math.Clamp(limit, 1, 500),
            Offset = Math.Max(0, offset),
            Sort = string.IsNullOrWhiteSpace(sortBy) ? "relevance" : sortBy,
            SortDirection = sortDirection,
            IncludePaths = serviceScope.IncludePaths,
            ExcludePaths = serviceScope.ExcludePaths,
            Filters = new SearchServiceFilters
            {
                Extensions = typeFilter.IncludeAllFiles ? [] : extensions,
                ModifiedAfter = modifiedAfter,
                ModifiedBefore = modifiedBefore,
                ScopeAliases = BuildScopeAliases(),
                // Ordinary content searches mirror Qsirch's broad bare-query behavior.
                // Structured Boolean content searches are intentionally content-only:
                // allowing name or path would make folders match their own labels.
                MatchFields = options?.SearchContents == true && options.HasBooleanTerms
                    ? ["content"]
                    : options?.SearchContents == true || !metadataOnly
                        ? ["name", "path", "extension", "content"]
                        : ["name"],
                MatchMode = options?.ExactMatch == true
                    ? "exact"
                    : "prefix",
                Boolean = options?.HasBooleanTerms == true
                    ? new SearchServiceBooleanFilter
                    {
                        All = options.RequiredTerms ?? [],
                        Any = options.AnyTerms ?? [],
                        Not = options.ExcludedTerms ?? [],
                    }
                    : null,
            },
        };

        // A relevance sort without a query is invalid for protocol 1.1.
        if (string.IsNullOrWhiteSpace(searchText) && string.Equals(request.Sort, "relevance", StringComparison.OrdinalIgnoreCase))
        {
            request.Sort = "modified";
        }

        var response = await SendAsync<SearchServiceResponse>("search", request, cancellationToken);
        var results = (response.Results ?? []).Select(result => ToSearchResult(result, serviceScope.Roots))
            .Where(result => (result.IsFolder && typeFilter.IncludeFolders) ||
                             (!result.IsFolder && (typeFilter.IncludeAllFiles || extensions.Contains(result.Extension, StringComparer.OrdinalIgnoreCase))))
            .ToList();
        if (batchReceived != null && results.Count > 0)
        {
            await batchReceived(results);
        }
        var contentMatches = results.Count(result => itemHasMatchedField(result, "content"));
        AppLogger.Info("search-service", $"search returned={results.Count} contentMatches={contentMatches} offset={offset} hasMore={response.HasMore} query=\"{searchText}\"");
        return results;
    }

    private static bool itemHasMatchedField(SearchResult result, string field) =>
        result.Raw.ValueKind == JsonValueKind.Object &&
        result.Raw.TryGetProperty("matched_fields", out var fields) &&
        fields.ValueKind == JsonValueKind.Array &&
        fields.EnumerateArray().Any(value => string.Equals(value.GetString(), field, StringComparison.OrdinalIgnoreCase));

    public async Task<IReadOnlyList<SearchResult>> SearchDirectoriesAsync(
        string query,
        int limit,
        CancellationToken cancellationToken,
        SearchProviderScope? scope = null)
    {
        ThrowIfRecentlyUnavailable();
        var serviceScope = await PrepareScopeAsync(scope, cancellationToken);
        var response = await SendAsync<SearchServiceResponse>("directories", new SearchServiceRequest
        {
            SearchId = Guid.NewGuid().ToString("N"),
            Query = query.Trim(),
            Limit = Math.Clamp(limit, 1, 100),
            Sort = "name",
            SortDirection = "asc",
            IncludePaths = serviceScope.IncludePaths,
            ExcludePaths = serviceScope.ExcludePaths,
            Filters = new SearchServiceFilters { ScopeAliases = BuildScopeAliases() },
        }, cancellationToken);
        return (response.Results ?? [])
            .Select(result => ToSearchResult(result, serviceScope.Roots))
            .ToList();
    }

    public Task<byte[]?> ThumbnailAsync(SearchResult result, CancellationToken cancellationToken) => Task.FromResult<byte[]?>(null);

    public void Dispose()
    {
        _rootsGate.Dispose();
        _http.Dispose();
    }

    private async Task<ServiceScopeContext> PrepareScopeAsync(SearchProviderScope? scope, CancellationToken cancellationToken)
    {
        if (scope is not { HasPaths: true })
        {
            return new ServiceScopeContext([], [], []);
        }

        try
        {
            var roots = await GetRootsAsync(cancellationToken);
            var includePaths = scope.IncludePaths
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(NormalizedPath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var excludePaths = scope.ExcludePaths
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(NormalizedPath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            AppLogger.Info("qindexer", $"scope forwarded include={includePaths.Count} exclude={excludePaths.Count} aliases={BuildScopeAliases().Count}");
            return new ServiceScopeContext(includePaths, excludePaths, roots);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            AppLogger.Warn("qindexer", $"scope root translation unavailable; retaining client-side scope filtering error=\"{ex.Message}\"");
            return new ServiceScopeContext([], [], []);
        }
    }

    private void ThrowIfRecentlyUnavailable()
    {
        lock (_availabilityLock)
        {
            if (_recentAvailabilityFailure != null && DateTimeOffset.UtcNow < _retryAfterUtc)
            {
                var remaining = Math.Ceiling((_retryAfterUtc - DateTimeOffset.UtcNow).TotalSeconds);
                throw new InvalidOperationException($"QIndexer is temporarily unavailable. Try again in {remaining:n0} seconds.", _recentAvailabilityFailure);
            }
        }
    }

    private void RememberAvailabilityFailure(Exception failure)
    {
        lock (_availabilityLock)
        {
            _recentAvailabilityFailure = failure;
            _retryAfterUtc = DateTimeOffset.UtcNow.AddSeconds(10);
        }
    }

    private void ClearAvailabilityFailure()
    {
        lock (_availabilityLock)
        {
            _recentAvailabilityFailure = null;
            _retryAfterUtc = DateTimeOffset.MinValue;
        }
    }

    private async Task<IReadOnlyList<SearchServiceRoot>> GetRootsAsync(CancellationToken cancellationToken)
    {
        if (_roots != null)
        {
            return _roots;
        }

        await _rootsGate.WaitAsync(cancellationToken);
        try
        {
            if (_roots != null)
            {
                return _roots;
            }

            var uri = $"{_baseUrl}/roots";
            AppLogger.Info("qindexer", $"GET endpoint=\"{uri}\"");
            using var response = await _http.GetAsync(uri, cancellationToken);
            AppLogger.Info("qindexer", $"GET endpoint=\"{uri}\" status={(int)response.StatusCode}");
            if (!response.IsSuccessStatusCode)
            {
                throw await CreateRequestExceptionAsync(response, cancellationToken);
            }
            var payload = await response.Content.ReadFromJsonAsync<SearchServiceRootsResponse>(cancellationToken: cancellationToken)
                          ?? throw new InvalidOperationException("QIndexer returned an empty roots response.");
            _roots = payload.Roots ?? [];
            return _roots;
        }
        finally
        {
            _rootsGate.Release();
        }
    }

    private IReadOnlyList<SearchServiceScopeAlias> BuildScopeAliases()
    {
        var aliases = new Dictionary<string, SearchServiceScopeAlias>(StringComparer.OrdinalIgnoreCase);

        void Add(string path, string target, string platform)
        {
            path = NormalizedPath(path);
            target = NormalizedPath(target);
            if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(target) ||
                string.Equals(path, target, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            aliases.TryAdd(path, new SearchServiceScopeAlias { Path = path, Target = target, Platform = platform });
        }

        foreach (var mapping in _config.PathMappings)
        {
            var mapped = mapping.MappedRoot;
            var share = NasIdentity.NormalizeShareRoot(mapping.ShareRoot, _config.Host);
            var shareName = share.Trim('\\').Split('\\', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
            Add(share, mapped, OperatingSystem.IsWindows() ? "windows-unc" : "linux-mount");
            if (!string.IsNullOrWhiteSpace(shareName))
            {
                Add("\\" + shareName, mapped, "windows-compact-share");
            }
        }

        if (OperatingSystem.IsWindows())
        {
            foreach (var drive in PathMapper.DiscoverWindowsDriveMappings())
            {
                Add(drive.NetworkPath, drive.DriveRoot, "windows-unc");
                Add(drive.QsirchShareRoot, drive.DriveRoot, "windows-compact-share");
            }
        }

        return aliases.Values.ToList();
    }

    private async Task<T> SendAsync<T>(string endpoint, object request, CancellationToken cancellationToken)
    {
        var uri = $"{_baseUrl}/{endpoint}";
        AppLogger.Info("qindexer", $"POST endpoint=\"{uri}\"");
        using var response = await _http.PostAsJsonAsync(uri, request, cancellationToken);
        AppLogger.Info("qindexer", $"POST endpoint=\"{uri}\" status={(int)response.StatusCode}");
        if (!response.IsSuccessStatusCode)
        {
            throw await CreateRequestExceptionAsync(response, cancellationToken);
        }

        return await response.Content.ReadFromJsonAsync<T>(cancellationToken: cancellationToken)
               ?? throw new InvalidOperationException("The QSurfer Search Service returned an empty response.");
    }

    private static async Task<Exception> CreateRequestExceptionAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var error = await response.Content.ReadFromJsonAsync<SearchServiceErrorResponse>(cancellationToken: cancellationToken);
            if (!string.IsNullOrWhiteSpace(error?.Error?.Message))
            {
                return new InvalidOperationException($"Search service: {error.Error.Message}");
            }
        }
        catch (JsonException)
        {
            // The status text below is a useful fallback for a reverse proxy or an old service build.
        }
        return new HttpRequestException($"Search service request failed: {(int)response.StatusCode} {response.ReasonPhrase}", null, response.StatusCode);
    }

    private static (string Query, string? ModifiedAfter, string? ModifiedBefore, bool MetadataOnly) ParseLegacyQuery(string value)
    {
        var query = value.Trim();
        string? modifiedAfter = null;
        string? modifiedBefore = null;
        var dateMatch = ModifiedClause.Match(query);
        if (dateMatch.Success)
        {
            modifiedAfter = dateMatch.Groups["from"].Value + "T00:00:00Z";
            modifiedBefore = dateMatch.Groups["to"].Value + "T23:59:59Z";
            query = query[..dateMatch.Index].Trim();
        }

        var nameMatch = NameQuery.Match(query);
        var metadataOnly = nameMatch.Success;
        if (nameMatch.Success)
        {
            query = nameMatch.Groups["query"].Value.Replace("\\\"", "\"");
        }
        else if (query.Length >= 2 && query[0] == '"' && query[^1] == '"')
        {
            query = query[1..^1].Replace("\\\"", "\"");
        }
        return (query, modifiedAfter, modifiedBefore, metadataOnly);
    }

    private static SearchResult ToSearchResult(SearchServiceResult item, IEnumerable<SearchServiceRoot> roots)
    {
        var raw = JsonSerializer.SerializeToElement(item);
        var result = new SearchResult
        {
            Name = item.Name ?? "",
            Extension = item.Extension ?? "",
            Path = item.Path ?? "",
            Type = item.Kind ?? (item.IsFolder ? "Folder" : "File"),
            Size = item.Size,
            Modified = item.ModifiedAt?.ToLocalTime().ToString("g") ?? "",
            IsFolder = item.IsFolder,
            Raw = raw,
        };
        result.ResolvedPath = item.DisplayPath ?? TranslateCanonicalPath(item.Path ?? "", roots) ?? "";
        return result;
    }

    private static string? TranslateCanonicalPath(string input, IEnumerable<SearchServiceRoot> roots)
    {
        var match = roots
            .Where(root => IsPathWithin(input, root.CanonicalPath))
            .OrderByDescending(root => NormalizedPath(root.CanonicalPath).Length)
            .FirstOrDefault();
        if (match == null)
        {
            return null;
        }

        var preferredAlias = match.Aliases.FirstOrDefault(alias =>
            OperatingSystem.IsWindows()
                ? string.Equals(alias.Platform, "windows-drive", StringComparison.OrdinalIgnoreCase) ||
                  string.Equals(alias.Platform, "windows-unc", StringComparison.OrdinalIgnoreCase)
                : string.Equals(alias.Platform, "linux-mount", StringComparison.OrdinalIgnoreCase))
            ?? match.Aliases.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(preferredAlias?.Path))
        {
            return null;
        }

        var path = NormalizedPath(input);
        var canonical = NormalizedPath(match.CanonicalPath);
        var remainder = path.Length == canonical.Length ? "" : path[canonical.Length..].TrimStart('\\');
        var alias = NormalizedPath(preferredAlias.Path);
        return string.IsNullOrWhiteSpace(remainder) ? alias : alias + "\\" + remainder;
    }

    private static bool IsPathWithin(string path, string root)
    {
        var normalizedPath = NormalizedPath(path);
        var normalizedRoot = NormalizedPath(root);
        return !string.IsNullOrWhiteSpace(normalizedRoot) &&
               (normalizedPath.Equals(normalizedRoot, StringComparison.OrdinalIgnoreCase) ||
                normalizedPath.StartsWith(normalizedRoot + "\\", StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizedPath(string path) => (path ?? "")
        .Replace('/', '\\')
        .Trim()
        .TrimEnd('\\');

    private static HttpClient CreateHttpClient(AppConfig config, SearchServiceConnection service)
    {
        var handler = new HttpClientHandler();
        if (service.Ssl && !service.SslVerify)
        {
            handler.ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
        }
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(Math.Clamp(config.Behavior.SearchTimeoutSeconds, 15, 300)) };
    }

    private sealed class SearchServiceRequest
    {
        [JsonPropertyName("search_id")]
        public string SearchId { get; init; } = "";
        [JsonPropertyName("query")]
        public string Query { get; init; } = "";
        [JsonPropertyName("filters")]
        public SearchServiceFilters Filters { get; init; } = new();
        [JsonPropertyName("limit")]
        public int Limit { get; init; }
        [JsonPropertyName("offset")]
        public int Offset { get; init; }
        [JsonPropertyName("sort")]
        public string Sort { get; set; } = "relevance";
        [JsonPropertyName("sort_direction")]
        public string SortDirection { get; init; } = "desc";
        [JsonPropertyName("include_paths")]
        public IReadOnlyList<string> IncludePaths { get; init; } = [];
        [JsonPropertyName("exclude_paths")]
        public IReadOnlyList<string> ExcludePaths { get; init; } = [];
    }

    private sealed class SearchServiceFilters
    {
        [JsonPropertyName("extensions")]
        public IEnumerable<string> Extensions { get; init; } = [];
        [JsonPropertyName("modified_after")]
        public string? ModifiedAfter { get; init; }
        [JsonPropertyName("modified_before")]
        public string? ModifiedBefore { get; init; }
        [JsonPropertyName("match_fields")]
        public IReadOnlyList<string> MatchFields { get; init; } = [];
        [JsonPropertyName("match_mode")]
        public string MatchMode { get; init; } = "prefix";
        [JsonPropertyName("boolean")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public SearchServiceBooleanFilter? Boolean { get; init; }
        [JsonPropertyName("scope_aliases")]
        public IReadOnlyList<SearchServiceScopeAlias> ScopeAliases { get; init; } = [];
    }

    private sealed class SearchServiceBooleanFilter
    {
        [JsonPropertyName("all")]
        public IReadOnlyList<string> All { get; init; } = [];
        [JsonPropertyName("any")]
        public IReadOnlyList<string> Any { get; init; } = [];
        [JsonPropertyName("not")]
        public IReadOnlyList<string> Not { get; init; } = [];
    }

    private sealed class SearchServiceResponse
    {
        [JsonPropertyName("results")]
        public List<SearchServiceResult>? Results { get; init; } = [];
        [JsonPropertyName("has_more")]
        public bool HasMore { get; init; }
    }

    private sealed class SearchServiceRootsResponse
    {
        [JsonPropertyName("roots")]
        public List<SearchServiceRoot>? Roots { get; init; } = [];
    }

    private sealed class SearchServiceRoot
    {
        [JsonPropertyName("root_id")]
        public string RootId { get; init; } = "";
        [JsonPropertyName("canonical_path")]
        public string CanonicalPath { get; init; } = "";
        [JsonPropertyName("aliases")]
        public List<SearchServiceRootAlias> Aliases { get; init; } = [];
    }

    private sealed class SearchServiceRootAlias
    {
        [JsonPropertyName("alias_id")]
        public string AliasId { get; init; } = "";
        [JsonPropertyName("platform")]
        public string Platform { get; init; } = "";
        [JsonPropertyName("path")]
        public string Path { get; init; } = "";
    }

    private sealed record ServiceScopeContext(
        IReadOnlyList<string> IncludePaths,
        IReadOnlyList<string> ExcludePaths,
        IReadOnlyList<SearchServiceRoot> Roots);

    private sealed class SearchServiceResult
    {
        [JsonPropertyName("name")]
        public string? Name { get; init; }
        [JsonPropertyName("extension")]
        public string? Extension { get; init; }
        [JsonPropertyName("path")]
        public string? Path { get; init; }
        [JsonPropertyName("display_path")]
        public string? DisplayPath { get; init; }
        [JsonPropertyName("kind")]
        public string? Kind { get; init; }
        [JsonPropertyName("is_folder")]
        public bool IsFolder { get; init; }
        [JsonPropertyName("size")]
        public long Size { get; init; }
        [JsonPropertyName("modified_at")]
        public DateTimeOffset? ModifiedAt { get; init; }
        [JsonPropertyName("matched_fields")]
        public List<string> MatchedFields { get; init; } = [];
        [JsonPropertyName("highlights")]
        public Dictionary<string, List<string>> Highlights { get; init; } = [];
    }

    private sealed class SearchServiceScopeAlias
    {
        [JsonPropertyName("path")]
        public string Path { get; init; } = "";
        [JsonPropertyName("target")]
        public string Target { get; init; } = "";
        [JsonPropertyName("platform")]
        public string Platform { get; init; } = "";
    }

    private sealed class SearchServiceErrorResponse
    {
        [JsonPropertyName("error")]
        public SearchServiceError? Error { get; init; }
    }

    private sealed class SearchServiceError
    {
        [JsonPropertyName("message")]
        public string? Message { get; init; }
    }
}
