using QSurfer.Core.Services;
using Xunit;

namespace QSurfer.Core.Tests;

public sealed class LocalIndexerManagerTests
{
    [Fact]
    public void QIndexerOwnedConfigParsesLoopbackPortsAfterAdminEdits()
    {
        const string config = """
            server:
              bind: "127.0.0.1:43111"
            management:
              bind: "127.0.0.1:43112"
            """;

        var ports = LocalIndexerManager.ParseLoopbackPorts(config);
        Assert.Equal((43111, 43112), ports);

        // QIndexer's admin UI persists ordinary YAML scalars without quote
        // marks. QSurfer must still reconnect after an admin-side edit.
        var adminSavedConfig = config.Replace("\"127.0.0.1:43111\"", "127.0.0.1:43111")
            .Replace("\"127.0.0.1:43112\"", "127.0.0.1:43112")
            .Replace("  bind:", "    bind:");
        Assert.Equal((43111, 43112), LocalIndexerManager.ParseLoopbackPorts(adminSavedConfig));
    }
}
