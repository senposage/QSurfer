using System.Net;
using System.Net.Http;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using QSurfer.Core.Models;

namespace QSurfer.Core.Services;

public sealed class QsirchClient(AppConfig config) : IDisposable
{
    private const int MaxThumbnailBytes = 4 * 1024 * 1024;
    private readonly HttpClient _http = CreateHttpClient(config);
    private readonly SemaphoreSlim _loginGate = new(1, 1);
    private readonly QsirchSessionStore _sessionStore = new(config);
    private readonly string _baseUrl = $"{(config.Ssl ? "https" : "http")}://{config.Host}:{config.Port}";
    private bool _loggedIn;
    private string _sid = "";
    private DateTimeOffset _nextLoginAttemptUtc;

    public async Task<IReadOnlyList<SearchResult>> SearchAsync(string query, FileTypeFilter typeFilter, CancellationToken cancellationToken)
    {
        return await SearchAsync(query, typeFilter, 500, 0, null, cancellationToken);
    }

    public async Task<IReadOnlyList<SearchResult>> SearchAsync(
        string query,
        FileTypeFilter typeFilter,
        Func<IReadOnlyList<SearchResult>, Task>? batchReceived,
        CancellationToken cancellationToken)
    {
        return await SearchAsync(query, typeFilter, 500, 0, null, "desc", batchReceived, cancellationToken);
    }

    public async Task<IReadOnlyList<SearchResult>> SearchAsync(
        string query,
        FileTypeFilter typeFilter,
        int limit,
        int offset,
        Func<IReadOnlyList<SearchResult>, Task>? batchReceived,
        CancellationToken cancellationToken)
    {
        return await SearchAsync(query, typeFilter, limit, offset, null, "desc", batchReceived, cancellationToken);
    }

    public async Task<IReadOnlyList<SearchResult>> SearchAsync(
        string query,
        FileTypeFilter typeFilter,
        int limit,
        int offset,
        string? sortBy,
        string sortDir,
        Func<IReadOnlyList<SearchResult>, Task>? batchReceived,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(config.Host) || string.IsNullOrWhiteSpace(config.User) || string.IsNullOrWhiteSpace(config.Password))
        {
            throw new InvalidOperationException("Configure the NAS connection in config.json first.");
        }

        var stopwatch = Stopwatch.StartNew();
        limit = Math.Clamp(limit, 1, 500);
        offset = Math.Max(0, offset);
        var serverQuery = string.IsNullOrWhiteSpace(typeFilter.Category) ||
                          typeFilter.Category.Equals("All", StringComparison.OrdinalIgnoreCase)
            ? query
            : $"{query} category:{typeFilter.Category}";
        var url = $"{_baseUrl}/qsirch/latest/api/search?q={Uri.EscapeDataString(serverQuery)}&limit={limit}&offset={offset}&advanced_mode=0&store_history=0";
        if (!string.IsNullOrWhiteSpace(sortBy) && !sortBy.Equals("relevance", StringComparison.OrdinalIgnoreCase))
        {
            url += $"&sort_by={Uri.EscapeDataString(sortBy)}&sort_dir={Uri.EscapeDataString(sortDir)}";
        }
        var searchMethod = HttpMethod.Get;
        HttpRequestMessage CreateSearchRequest()
        {
            return new HttpRequestMessage(searchMethod, url);
        }

        AppLogger.Info("qsirch", $"search request method={searchMethod} host=\"{config.Host}\" port={config.Port} ssl={config.Ssl} verify={config.SslVerify} timeout={_http.Timeout.TotalSeconds:n0}s streaming=True query=\"{query}\" serverQuery=\"{serverQuery}\" type=\"{typeFilter.Name}\" category=\"{typeFilter.Category}\" limit={limit} offset={offset} sort=\"{sortBy ?? "relevance"}:{sortDir}\"");
        using var response = await SendWithSessionRecoveryAsync(CreateSearchRequest, "search", stopwatch, cancellationToken, HttpCompletionOption.ResponseHeadersRead);
        AppLogger.Info("qsirch", $"search response status={(int)response.StatusCode} elapsed={stopwatch.ElapsedMilliseconds}ms");
        response.EnsureSuccessStatusCode();
        List<SearchResult> results;
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            results = await ReadSearchItemsAsync(stream, typeFilter, batchReceived, stopwatch, cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            AppLogger.Warn("qsirch", $"search body timed out after {stopwatch.ElapsedMilliseconds}ms timeout={_http.Timeout.TotalSeconds:n0}s host=\"{config.Host}\" port={config.Port} ssl={config.Ssl}");
            throw new TimeoutException($"Qsirch search timed out after {_http.Timeout.TotalSeconds:n0} seconds.");
        }
        AppLogger.Info("qsirch", $"search parsed returned={results.Count} limit={limit} offset={offset} sort=\"{sortBy ?? "relevance"}:{sortDir}\" elapsed={stopwatch.ElapsedMilliseconds}ms");
        return results;
    }

    public async Task<IReadOnlyList<SearchResult>> SearchDirectoriesAsync(string query, int limit, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(config.Host) || string.IsNullOrWhiteSpace(config.User) || string.IsNullOrWhiteSpace(config.Password))
        {
            throw new InvalidOperationException("Configure the NAS connection in config.json first.");
        }

        var stopwatch = Stopwatch.StartNew();
        limit = Math.Clamp(limit, 1, 100);
        var url = $"{_baseUrl}/qsirch/latest/api/search-dirs?q={Uri.EscapeDataString(query)}&limit={limit}";
        AppLogger.Info("qsirch", $"directory search request host=\"{config.Host}\" port={config.Port} ssl={config.Ssl} verify={config.SslVerify} query=\"{query}\" limit={limit}");
        using var response = await SendWithSessionRecoveryAsync(
            () => new HttpRequestMessage(HttpMethod.Get, url),
            "directory search",
            stopwatch,
            cancellationToken);
        AppLogger.Info("qsirch", $"directory search response status={(int)response.StatusCode} elapsed={stopwatch.ElapsedMilliseconds}ms");
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (!document.RootElement.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
        {
            AppLogger.Warn("qsirch", $"directory search response had no items array elapsed={stopwatch.ElapsedMilliseconds}ms");
            return [];
        }

        var results = new List<SearchResult>();
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items.EnumerateArray())
        {
            var path = GetString(item, "name").Trim();
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            var normalizedPath = path.Replace('/', '\\').TrimEnd('\\');
            if (!seenPaths.Add(normalizedPath))
            {
                continue;
            }

            var name = Path.GetFileName(normalizedPath);
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            results.Add(new SearchResult
            {
                Name = name,
                Path = path,
                Type = "Folder",
                IsFolder = true,
                Raw = item.Clone(),
            });
        }

        AppLogger.Info("qsirch", $"directory search parsed returned={results.Count} limit={limit} elapsed={stopwatch.ElapsedMilliseconds}ms");
        return results;
    }

    private static async Task<List<SearchResult>> ReadSearchItemsAsync(
        Stream stream,
        FileTypeFilter typeFilter,
        Func<IReadOnlyList<SearchResult>, Task>? batchReceived,
        Stopwatch stopwatch,
        CancellationToken cancellationToken)
    {
        var results = new List<SearchResult>();
        var pendingBatch = new List<SearchResult>(10);
        var buffer = new List<byte>(64 * 1024);
        var readBuffer = new byte[16 * 1024];
        var parseIndex = 0;
        var itemsStarted = false;
        var objectStart = -1;
        var depth = 0;
        var inString = false;
        var escaped = false;
        var firstItemLogged = false;

        while (true)
        {
            var read = await stream.ReadAsync(readBuffer, cancellationToken);
            if (read == 0)
            {
                break;
            }
            buffer.AddRange(readBuffer.Take(read));

            if (!itemsStarted)
            {
                var start = FindItemsArrayStart(buffer, parseIndex);
                if (start < 0)
                {
                    parseIndex = Math.Max(0, buffer.Count - 16);
                    continue;
                }
                itemsStarted = true;
                parseIndex = start;
            }

            while (parseIndex < buffer.Count)
            {
                var current = buffer[parseIndex];
                if (inString)
                {
                    if (escaped)
                    {
                        escaped = false;
                    }
                    else if (current == (byte)'\\')
                    {
                        escaped = true;
                    }
                    else if (current == (byte)'"')
                    {
                        inString = false;
                    }
                    parseIndex++;
                    continue;
                }

                if (current == (byte)'"')
                {
                    inString = true;
                    parseIndex++;
                    continue;
                }
                if (current == (byte)'{')
                {
                    if (depth == 0)
                    {
                        objectStart = parseIndex;
                    }
                    depth++;
                }
                else if (current == (byte)'}' && depth > 0)
                {
                    depth--;
                    if (depth == 0 && objectStart >= 0)
                    {
                        var objectBytes = buffer.GetRange(objectStart, parseIndex - objectStart + 1).ToArray();
                        using var doc = JsonDocument.Parse(objectBytes);
                        var result = ResultFromJson(doc.RootElement.Clone());
                        if ((result.IsFolder && typeFilter.IncludeFolders) ||
                            (!result.IsFolder && (typeFilter.IncludeAllFiles || typeFilter.Extensions.Contains(result.Extension, StringComparer.OrdinalIgnoreCase))))
                        {
                            results.Add(result);
                            pendingBatch.Add(result);
                            if (!firstItemLogged)
                            {
                                firstItemLogged = true;
                                AppLogger.Info("qsirch", $"search first item elapsed={stopwatch.ElapsedMilliseconds}ms");
                            }
                            if (pendingBatch.Count >= 10 && batchReceived != null)
                            {
                                await batchReceived(pendingBatch.ToList());
                                pendingBatch.Clear();
                            }
                        }
                        objectStart = -1;
                    }
                }
                else if (current == (byte)']' && depth == 0)
                {
                    parseIndex++;
                    break;
                }
                parseIndex++;
            }
        }

        if (!itemsStarted)
        {
            AppLogger.Warn("qsirch", $"search response had no items array elapsed={stopwatch.ElapsedMilliseconds}ms");
        }
        if (pendingBatch.Count > 0 && batchReceived != null)
        {
            await batchReceived(pendingBatch);
        }
        return results;
    }

    private static int FindItemsArrayStart(IReadOnlyList<byte> buffer, int start)
    {
        var pattern = "\"items\""u8;
        for (var i = Math.Max(0, start); i <= buffer.Count - pattern.Length; i++)
        {
            var matches = true;
            for (var j = 0; j < pattern.Length; j++)
            {
                if (buffer[i + j] != pattern[j])
                {
                    matches = false;
                    break;
                }
            }
            if (!matches)
            {
                continue;
            }

            var index = i + pattern.Length;
            while (index < buffer.Count && char.IsWhiteSpace((char)buffer[index]))
            {
                index++;
            }
            if (index >= buffer.Count || buffer[index] != (byte)':')
            {
                continue;
            }
            index++;
            while (index < buffer.Count && char.IsWhiteSpace((char)buffer[index]))
            {
                index++;
            }
            if (index < buffer.Count && buffer[index] == (byte)'[')
            {
                return index + 1;
            }
        }
        return -1;
    }

    public async Task<byte[]?> ThumbnailAsync(SearchResult result, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(config.Host) || string.IsNullOrWhiteSpace(config.User) || string.IsNullOrWhiteSpace(config.Password))
        {
            return null;
        }

        var action = ThumbnailAction(result);
        if (string.IsNullOrWhiteSpace(action))
        {
            AppLogger.Info("qsirch", $"thumbnail skipped no action path=\"{result.DisplayPath}\" name=\"{result.FileName}\"");
            return null;
        }

        using var response = await SendWithSessionRecoveryAsync(
            () => new HttpRequestMessage(HttpMethod.Get, action),
            "thumbnail",
            Stopwatch.StartNew(),
            cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            AppLogger.Warn("qsirch", $"thumbnail response status={(int)response.StatusCode} path=\"{result.DisplayPath}\" name=\"{result.FileName}\"");
            return null;
        }
        var contentType = response.Content.Headers.ContentType?.MediaType ?? "";
        if (!contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
        {
            AppLogger.Warn("qsirch", $"thumbnail response non-image contentType=\"{contentType}\" path=\"{result.DisplayPath}\" name=\"{result.FileName}\"");
            return null;
        }
        var contentLength = response.Content.Headers.ContentLength;
        if (contentLength is > MaxThumbnailBytes)
        {
            AppLogger.Warn("qsirch", $"thumbnail skipped too large bytes={contentLength} limit={MaxThumbnailBytes} path=\"{result.DisplayPath}\" name=\"{result.FileName}\"");
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var capacity = contentLength.HasValue && contentLength.Value > 0 && contentLength.Value <= MaxThumbnailBytes
            ? (int)contentLength.Value
            : 0;
        using var buffer = new MemoryStream(capacity);
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > MaxThumbnailBytes)
            {
                AppLogger.Warn("qsirch", $"thumbnail skipped after limit bytes={buffer.Length + read} limit={MaxThumbnailBytes} path=\"{result.DisplayPath}\" name=\"{result.FileName}\"");
                return null;
            }
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    private async Task LoginAsync(CancellationToken cancellationToken)
    {
        if (_loggedIn)
        {
            return;
        }

        await _loginGate.WaitAsync(cancellationToken);
        try
        {
            if (_loggedIn)
            {
                return;
            }

            ResumeLocalSession();
            if (_loggedIn)
            {
                return;
            }

            if (DateTimeOffset.UtcNow < _nextLoginAttemptUtc)
            {
                var remaining = Math.Ceiling((_nextLoginAttemptUtc - DateTimeOffset.UtcNow).TotalSeconds);
                throw new InvalidOperationException($"Qsirch session is unavailable. Waiting {remaining:n0} seconds before another sign-in attempt.");
            }

            var stopwatch = Stopwatch.StartNew();
            AppLogger.Info("qsirch", $"login request host=\"{config.Host}\" port={config.Port} ssl={config.Ssl} verify={config.SslVerify} userSet={!string.IsNullOrWhiteSpace(config.User)}");
            var encodedPassword = Convert.ToBase64String(Encoding.UTF8.GetBytes(config.Password));
            using var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["user"] = config.User,
                ["pwd"] = encodedPassword,
            });
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/cgi-bin/authLogin.cgi") { Content = content };
            using var response = await SendWithTimeoutLoggingAsync(request, "login", stopwatch, cancellationToken);
            AppLogger.Info("qsirch", $"login response status={(int)response.StatusCode} elapsed={stopwatch.ElapsedMilliseconds}ms");
            response.EnsureSuccessStatusCode();
            var xml = XDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            if (xml.Root?.Element("authPassed")?.Value != "1")
            {
                throw new InvalidOperationException("QNAP authentication failed.");
            }
            var sessionId = xml.Root?.Element("authSid")?.Value ?? "";
            if (string.IsNullOrWhiteSpace(sessionId))
            {
                throw new InvalidOperationException("QNAP did not return an authSid.");
            }
            ApplySession(sessionId);
            _sessionStore.Save(sessionId);
            _nextLoginAttemptUtc = DateTimeOffset.MinValue;
            AppLogger.Info("qsirch", $"login success elapsed={stopwatch.ElapsedMilliseconds}ms");
        }
        finally
        {
            _loginGate.Release();
        }
    }

    public static SearchResult ResultFromJson(JsonElement element)
    {
        var name = GetName(element);
        var ext = GetString(element, "extension").TrimStart('.');
        var path = GetPath(element);
        if (string.IsNullOrWhiteSpace(name))
        {
            var nameFromPath = Path.GetFileName(path.TrimEnd('\\', '/'));
            if (!string.IsNullOrWhiteSpace(ext) && nameFromPath.EndsWith("." + ext, StringComparison.OrdinalIgnoreCase))
            {
                name = nameFromPath;
            }
        }
        var type = GetString(element, "type");
        var result = new SearchResult
        {
            Name = name,
            Extension = ext,
            Path = path,
            Type = type,
            Size = GetLong(element, "size"),
            Modified = GetString(element, "modified"),
            Raw = element,
        };
        result.IsFolder = IsFolder(result, element);
        if (!result.IsFolder && !result.HasUsableFileName)
        {
            var keys = element.ValueKind == JsonValueKind.Object
                ? string.Join(',', element.EnumerateObject().Select(property => property.Name))
                : "";
            AppLogger.Warn("qsirch", $"result missing filename extension=\"{ext}\" path=\"{path}\" keys=\"{keys}\"");
        }
        return result;
    }

    private async Task<HttpResponseMessage> SendWithTimeoutLoggingAsync(
        HttpRequestMessage request,
        string operation,
        Stopwatch stopwatch,
        CancellationToken cancellationToken,
        HttpCompletionOption completionOption = HttpCompletionOption.ResponseContentRead)
    {
        try
        {
            return await _http.SendAsync(request, completionOption, cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            AppLogger.Warn("qsirch", $"{operation} timed out after {stopwatch.ElapsedMilliseconds}ms timeout={_http.Timeout.TotalSeconds:n0}s host=\"{config.Host}\" port={config.Port} ssl={config.Ssl}");
            throw new TimeoutException($"Qsirch {operation} timed out after {_http.Timeout.TotalSeconds:n0} seconds.");
        }
    }

    private async Task<HttpResponseMessage> SendWithSessionRecoveryAsync(
        Func<HttpRequestMessage> requestFactory,
        string operation,
        Stopwatch stopwatch,
        CancellationToken cancellationToken,
        HttpCompletionOption completionOption = HttpCompletionOption.ResponseContentRead)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            await LoginAsync(cancellationToken);
            var sessionId = _sid;
            using var request = requestFactory();
            var response = await SendWithTimeoutLoggingAsync(request, operation, stopwatch, cancellationToken, completionOption);
            if (response.StatusCode != HttpStatusCode.Unauthorized)
            {
                return response;
            }

            response.Dispose();
            InvalidateSession(sessionId);
            if (attempt == 0)
            {
                AppLogger.Warn("qsirch", $"{operation} response status=401; session rejected, signing in once and retrying");
                continue;
            }

            _nextLoginAttemptUtc = DateTimeOffset.UtcNow.AddSeconds(45);
            AppLogger.Warn("qsirch", $"{operation} response status=401 after a fresh sign-in; pausing retries for 45s to prevent a login loop");
            throw new InvalidOperationException("Qsirch rejected the refreshed session. Search retries are paused briefly to prevent repeated sign-ins.");
        }

        throw new InvalidOperationException("Unable to recover the Qsirch session.");
    }

    private void InvalidateSession(string expiredSessionId)
    {
        if (!_sid.Equals(expiredSessionId, StringComparison.Ordinal))
        {
            return;
        }

        _loggedIn = false;
        _sid = "";
        _http.DefaultRequestHeaders.Remove("Cookie");
        _sessionStore.Clear();
    }

    private void ResumeLocalSession()
    {
        var sessionId = _sessionStore.Read();
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return;
        }

        ApplySession(sessionId);
        AppLogger.Info("qsirch", "resumed encrypted local Qsirch session");
    }

    private void ApplySession(string sessionId)
    {
        _sid = sessionId;
        _http.DefaultRequestHeaders.Remove("Cookie");
        _http.DefaultRequestHeaders.Add("Cookie", $"NAS_SID={sessionId}");
        _loggedIn = true;
    }

    private static HttpClient CreateHttpClient(AppConfig config)
    {
        var handler = new HttpClientHandler { CookieContainer = new CookieContainer() };
        if (config.Ssl && !config.SslVerify)
        {
            handler.ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
        }
        var timeoutSeconds = Math.Clamp(config.Behavior.SearchTimeoutSeconds, 15, 300);
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(timeoutSeconds) };
    }

    private string ThumbnailAction(SearchResult result)
    {
        if (result.Raw.ValueKind != JsonValueKind.Object ||
            !result.Raw.TryGetProperty("actions", out var actions) ||
            !actions.TryGetProperty("thumbnail", out var thumbnail) ||
            thumbnail.ValueKind == JsonValueKind.Null ||
            string.IsNullOrWhiteSpace(thumbnail.ToString()))
        {
            return "";
        }

        var action = thumbnail.ToString() ?? "";
        return action.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? action : _baseUrl + action;
    }

    private static bool IsFolder(SearchResult result, JsonElement element)
    {
        var metadata = new[] { result.Type, GetString(element, "kind"), GetString(element, "file_type"), GetString(element, "container_type") };
        if (metadata.Any(x => x.Equals("folder", StringComparison.OrdinalIgnoreCase) || x.Equals("directory", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }
        if (!string.IsNullOrWhiteSpace(result.Extension))
        {
            return false;
        }
        return result.Path.EndsWith('\\') || result.Path.EndsWith('/') || (!string.IsNullOrWhiteSpace(result.Name) && !result.Name.Contains('.'));
    }

    private static string GetPath(JsonElement element)
    {
        return GetPreviewInfoValue(element, "path") ?? GetString(element, "path");
    }

    private static string GetName(JsonElement element)
    {
        foreach (var property in new[] { "name", "filename", "file_name" })
        {
            var value = GetString(element, property);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
            value = GetPreviewInfoValue(element, property);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }
        return "";
    }

    private static string? GetPreviewInfoValue(JsonElement element, string key)
    {
        if (!element.TryGetProperty("preview", out var preview) ||
            !preview.TryGetProperty("info", out var info) ||
            info.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var item in info.EnumerateArray())
        {
            if (GetString(item, "key").Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                return GetString(item, "value");
            }
        }
        return null;
    }

    private static string GetString(JsonElement element, string name)
    {
        if (element.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null)
        {
            return value.ToString() ?? "";
        }

        foreach (var property in element.EnumerateObject())
        {
            if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && property.Value.ValueKind != JsonValueKind.Null)
            {
                return property.Value.ToString() ?? "";
            }
        }

        return "";
    }

    private static long GetLong(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            return 0;
        }
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number))
        {
            return number;
        }
        return long.TryParse(value.ToString(), out var parsed) ? parsed : 0;
    }

    public void Dispose()
    {
        _loginGate.Dispose();
        _http.Dispose();
    }
}
