using System.Net;
using System.Text;
using System.Text.Json;
using QSurfer.Core.Models;
using QSurfer.Core.Services;
using Xunit;

namespace QSurfer.Core.Tests;

public sealed class SearchProviderConfigTests
{
    [Theory]
    [InlineData("name:\"ORCHID 731\"", false)]
    [InlineData("ORCHID 731", true)]
    public async Task QIndexerSearchUsesTheSameFieldModeAsQsirch(string query, bool expectContent)
    {
        var handler = new RecordingHandler();
        using var client = new QSurferSearchServiceClient(new AppConfig
        {
            SearchService = new SearchServiceConnection
            {
                Host = "127.0.0.1",
                Port = 41973,
                Token = "test-token",
            },
        }, handler);

        await client.SearchAsync(
            query,
            new FileTypeFilter
            {
                Name = "All types",
                IncludeFolders = true,
                IncludeAllFiles = true,
            },
            10,
            0,
            "relevance",
            "desc",
            null,
            CancellationToken.None);

        var body = JsonDocument.Parse(handler.RequestBody!);
        var fields = body.RootElement.GetProperty("filters").GetProperty("match_fields")
            .EnumerateArray().Select(value => value.GetString()).ToList();
        Assert.Equal(expectContent, fields.Contains("content", StringComparer.Ordinal));
        Assert.Equal(
            expectContent ? ["name", "path", "extension", "content"] : ["name"],
            fields);
        Assert.Equal("ORCHID 731", body.RootElement.GetProperty("query").GetString());
    }

    [Fact]
    public async Task QIndexerUsesExistingExactAndContentFiltersWithoutChangingTheQsirchQuery()
    {
        var handler = new RecordingHandler();
        using var client = new QSurferSearchServiceClient(new AppConfig
        {
            SearchService = new SearchServiceConnection { Host = "127.0.0.1", Port = 41973, Token = "test-token" },
        }, handler);

        await client.SearchAsync(
            "name:\"settlement agreement\"",
            new FileTypeFilter { Name = "All types", IncludeAllFiles = true },
            10,
            0,
            "relevance",
            "desc",
            null,
            CancellationToken.None,
            options: new SearchProviderQueryOptions(ExactMatch: true, SearchContents: true));

        var filters = JsonDocument.Parse(handler.RequestBody!).RootElement.GetProperty("filters");
        Assert.Equal("exact", filters.GetProperty("match_mode").GetString());
        Assert.Equal(["name", "path", "extension", "content"], filters.GetProperty("match_fields")
            .EnumerateArray().Select(value => value.GetString()));
        Assert.Equal("settlement agreement", JsonDocument.Parse(handler.RequestBody!).RootElement.GetProperty("query").GetString());
    }

    [Fact]
    public async Task QIndexerSendsStructuredBooleanTermsWithoutSplittingPhrases()
    {
        var handler = new RecordingHandler();
        using var client = new QSurferSearchServiceClient(new AppConfig
        {
            SearchService = new SearchServiceConnection { Host = "127.0.0.1", Port = 41973, Token = "test-token" },
        }, handler);

        await client.SearchAsync(
            "agreement",
            new FileTypeFilter { Name = "All types", IncludeAllFiles = true },
            10,
            0,
            "relevance",
            "desc",
            null,
            CancellationToken.None,
            options: new SearchProviderQueryOptions(
                ExactMatch: false,
                SearchContents: true,
                RequiredTerms: ["settlement agreement"],
                AnyTerms: ["amended agreement", "renewal"],
                ExcludedTerms: ["draft copy"]));

        var boolean = JsonDocument.Parse(handler.RequestBody!).RootElement.GetProperty("filters").GetProperty("boolean");
        var fields = JsonDocument.Parse(handler.RequestBody!).RootElement.GetProperty("filters").GetProperty("match_fields")
            .EnumerateArray().Select(value => value.GetString()).ToList();
        Assert.Equal(["content"], fields);
        Assert.Equal(["settlement agreement"], boolean.GetProperty("all").EnumerateArray().Select(value => value.GetString()));
        Assert.Equal(["amended agreement", "renewal"], boolean.GetProperty("any").EnumerateArray().Select(value => value.GetString()));
        Assert.Equal(["draft copy"], boolean.GetProperty("not").EnumerateArray().Select(value => value.GetString()));
    }

    [Fact]
    public void NasAndSearchServiceConnectionsSurviveMachineConfigurationFlow()
    {
        var config = new AppConfig
        {
            Host = "nas.example.local",
            Port = 443,
            Ssl = true,
            User = "qsirch",
            Password = "nas-password",
            SearchService = new SearchServiceConnection
            {
                Enabled = true,
                Host = "127.0.0.1",
                Port = 41973,
                Ssl = false,
                Token = "test-token",
            },
        };

        config.CaptureCurrentHost();
        config.ApplyCurrentHost();

        Assert.Equal(SearchProviders.Qsirch, config.SearchProvider);
        Assert.Equal("nas.example.local", config.Host);
        Assert.Equal("qsirch", config.User);
        Assert.Equal("nas-password", config.Password);
        Assert.True(config.SearchService.Enabled);
        Assert.Equal("127.0.0.1", config.SearchService.Host);
        Assert.Equal("test-token", config.SearchService.Token);
    }

    [Fact]
    public void QsirchCanBeDisabledWithoutDiscardingStoredConnection()
    {
        var config = new AppConfig
        {
            QsirchEnabled = false,
            Host = "nas.example.local",
            User = "qsirch",
            Password = "nas-password",
        };

        config.CaptureCurrentHost();
        config.ApplyCurrentHost();

        Assert.False(config.QsirchEnabled);
        Assert.Equal("nas.example.local", config.Host);
        Assert.Equal("qsirch", config.User);
        Assert.Equal("nas-password", config.Password);
    }

    [Fact]
    public void LegacyStandaloneConnectionMigratesIntoCompanionService()
    {
        var config = new AppConfig
        {
            SearchProvider = SearchProviders.Standalone,
            Host = "127.0.0.1",
            Port = 41973,
            Ssl = false,
            Password = "test-token",
        };

        config.MigrateLegacySettings();

        Assert.Equal(SearchProviders.Qsirch, config.SearchProvider);
        Assert.Empty(config.Host);
        Assert.Empty(config.Password);
        Assert.True(config.SearchService.Enabled);
        Assert.Equal("127.0.0.1", config.SearchService.Host);
        Assert.Equal(41973, config.SearchService.Port);
        Assert.False(config.SearchService.Ssl);
        Assert.Equal("test-token", config.SearchService.Token);
    }

    [Fact]
    public void QsirchRecognizesStoredCompactNasScopesButNotLocalDrives()
    {
        var config = new AppConfig
        {
            PathMappings =
            [
                new PathMapping
                {
                    ShareRoot = @"\\fileserver\Shared",
                    MappedRoot = @"X:\",
                },
            ],
        };
        using var client = new QsirchClient(config);

        Assert.True(client.CanSearchScope(new SearchProviderScope([@"X:\AA CRIMINAL"], [])));
        Assert.True(client.CanSearchScope(new SearchProviderScope([@"shared\AA CRIMINAL"], [])));
        Assert.False(client.CanSearchScope(new SearchProviderScope([@"D:\Cases"], [])));
    }

    [Fact]
    public async Task QIndexerDoesNotAttemptSearchAfterItsAvailabilityCheckFailed()
    {
        var handler = new UnavailableHandler();
        using var client = new QSurferSearchServiceClient(new AppConfig
        {
            SearchService = new SearchServiceConnection { Host = "127.0.0.1", Port = 41973, Token = "test-token" },
        }, handler);

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.EnsureAvailableAsync(CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.SearchAsync(
            "test", new FileTypeFilter { Name = "All", IncludeAllFiles = true }, 20, 0, "name", "asc", null, CancellationToken.None));

        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task QIndexerForwardsRawScopeWithKnownPathAliases()
    {
        var handler = new ScopeAliasRecordingHandler();
        using var client = new QSurferSearchServiceClient(new AppConfig
        {
            Host = "fileserver.example.local",
            SearchService = new SearchServiceConnection { Host = "127.0.0.1", Port = 41973, Token = "test-token" },
            PathMappings =
            [
                new PathMapping { ShareRoot = @"\\fileserver.example.local\Shared", MappedRoot = @"X:\" },
            ],
        }, handler);

        await client.SearchAsync(
            "test",
            new FileTypeFilter { Name = "All", IncludeAllFiles = true },
            10,
            0,
            "name",
            "asc",
            null,
            CancellationToken.None,
            new SearchProviderScope([@"X:\AA CRIMINAL"], []));

        var body = JsonDocument.Parse(handler.SearchRequestBody!);
        Assert.Equal(@"X:\AA CRIMINAL", body.RootElement.GetProperty("include_paths")[0].GetString());
        var aliases = body.RootElement.GetProperty("filters").GetProperty("scope_aliases").EnumerateArray().ToList();
        Assert.Contains(aliases, alias =>
            alias.GetProperty("path").GetString() == @"\Shared" &&
            alias.GetProperty("target").GetString() == @"X:");
        Assert.Contains(aliases, alias =>
            alias.GetProperty("path").GetString() == @"\\fileserver.example.local\Shared" &&
            alias.GetProperty("target").GetString() == @"X:");
    }

    [Fact]
    public async Task QIndexerPreservesServiceMatchedFieldsForContentSearchDiagnostics()
    {
        using var client = new QSurferSearchServiceClient(new AppConfig
        {
            SearchService = new SearchServiceConnection { Host = "127.0.0.1", Port = 41973, Token = "test-token" },
        }, new MatchedFieldsHandler());

        var results = await client.SearchAsync(
            "needle",
            new FileTypeFilter { Name = "All", IncludeAllFiles = true },
            10,
            0,
            "modified",
            "desc",
            null,
            CancellationToken.None);

        var fields = Assert.Single(results).Raw.GetProperty("matched_fields")
            .EnumerateArray()
            .Select(value => value.GetString());
        Assert.Equal(["content"], fields);
    }

    [Fact]
    public void QsirchNasPathInNameFieldUsesOnlyItsLeafName()
    {
        using var document = JsonDocument.Parse("""
            {"name":"\\Shared\\AA CRIMINAL\\Active\\Brief.docx","extension":"docx","path":"\\Shared\\AA CRIMINAL\\Active\\Brief.docx","type":"File"}
            """);

        var result = QsirchClient.ResultFromJson(document.RootElement);

        Assert.Equal("Brief.docx", result.Name);
        Assert.Equal("Brief.docx", result.FileName);
        Assert.Equal(@"\Shared\AA CRIMINAL\Active\Brief.docx", result.Path);
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public string? RequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"results\":[],\"has_more\":false}", Encoding.UTF8, "application/json"),
            };
        }
    }

    private sealed class UnavailableHandler : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            {
                Content = new StringContent("{\"error\":{\"message\":\"offline\"}}", Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class MatchedFieldsHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    {"results":[{"name":"content-only.docx","extension":"docx","path":"X:\\Cases\\content-only.docx","kind":"File","matched_fields":["content"]}],"has_more":false}
                    """, Encoding.UTF8, "application/json"),
            });
    }

    private sealed class ScopeAliasRecordingHandler : HttpMessageHandler
    {
        public string? SearchRequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri?.AbsolutePath.EndsWith("/roots", StringComparison.Ordinal) == true)
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"roots\":[]}", Encoding.UTF8, "application/json"),
                };
            }

            SearchRequestBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"results\":[],\"has_more\":false}", Encoding.UTF8, "application/json"),
            };
        }
    }
}
