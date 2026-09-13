using QSurfer.Core.Models;
using QSurfer.Core.Services;
using Xunit;

namespace QSurfer.Core.Tests;

public sealed class LiveNasSmokeTests
{
    [Fact]
    [Trait("Category", "LiveNas")]
    public async Task ConfiguredNasAcceptsAReadOnlyNameSearch()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("QSURFER_LIVE_NAS"), "1", StringComparison.Ordinal))
        {
            return;
        }

        var config = ConfigStore.Load();
        Assert.False(string.IsNullOrWhiteSpace(config.Host), "No NAS host is configured for this workstation.");
        Assert.False(string.IsNullOrWhiteSpace(config.User), "No NAS user is configured for this workstation.");
        Assert.False(string.IsNullOrWhiteSpace(config.Password), "No NAS password is configured for this workstation.");

        // Keep the live probe bounded even if the workstation normally permits a longer search.
        config.Behavior.SearchTimeoutSeconds = Math.Min(config.Behavior.SearchTimeoutSeconds, 30);
        var query = Environment.GetEnvironmentVariable("QSURFER_LIVE_QUERY");
        query = string.IsNullOrWhiteSpace(query) ? "a" : query.Trim();

        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(35));
        using var client = new QsirchClient(config);
        await client.EnsureAuthenticatedAsync(cancellation.Token);

        var results = await client.SearchAsync(
            query,
            new FileTypeFilter { Name = "All types", IncludeAllFiles = true, IncludeFolders = true },
            limit: 5,
            offset: 0,
            sortBy: null,
            sortDir: "desc",
            batchReceived: null,
            cancellationToken: cancellation.Token);

        Assert.NotNull(results);
    }
}
