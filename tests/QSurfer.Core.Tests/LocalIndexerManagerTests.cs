using System.Text.RegularExpressions;
using QSurfer.Core.Services;
using Xunit;

namespace QSurfer.Core.Tests;

public sealed class LocalIndexerManagerTests
{
    [Fact]
    public void ManagedConfigUsesOnlyLoopbackTokenFilesAndFixedVolumeRoots()
    {
        var config = LocalIndexerManager.RenderConfig(
            43111,
            43112,
            @"C:\Users\Test\AppData\Local\QSurfer\QIndexer\data",
            [new LocalIndexerVolume("local-a1b2", "Local disk C:", @"C:\", "volume-guid")]);

        Assert.Contains("127.0.0.1:43111", config);
        Assert.Contains("127.0.0.1:43112", config);
        Assert.Contains("token_file: \"secrets/search.token\"", config);
        Assert.Contains("token_file: \"secrets/admin.token\"", config);
        Assert.Contains("path: \"C:\\\\\"", config);
        Assert.Contains("id: \"local-a1b2\"", config);
        Assert.Contains("**/$RECYCLE.BIN/**", config);
        Assert.DoesNotContain("0.0.0.0", config);
        Assert.DoesNotContain("::", config);

        var ports = LocalIndexerManager.ParseLoopbackPorts(config);
        Assert.Equal((43111, 43112), ports);

        // QIndexer's admin UI persists ordinary YAML scalars without quote
        // marks. QSurfer must still reconnect after an admin-side edit.
        var adminSavedConfig = config.Replace("\"127.0.0.1:43111\"", "127.0.0.1:43111")
            .Replace("\"127.0.0.1:43112\"", "127.0.0.1:43112")
            .Replace("  bind:", "    bind:");
        Assert.Equal((43111, 43112), LocalIndexerManager.ParseLoopbackPorts(adminSavedConfig));
    }

    [Fact]
    public void ManagedTokensAreThirtyTwoRandomBytesEncodedForUrls()
    {
        var first = LocalIndexerManager.GenerateToken();
        var second = LocalIndexerManager.GenerateToken();

        Assert.Equal(43, first.Length);
        Assert.Matches(new Regex("^[A-Za-z0-9_-]+$"), first);
        Assert.NotEqual(first, second);
    }

    [Fact]
    public void ManagedConfigRejectsSharedPorts()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => LocalIndexerManager.RenderConfig(43111, 43111, @"C:\index", []));
    }
}
