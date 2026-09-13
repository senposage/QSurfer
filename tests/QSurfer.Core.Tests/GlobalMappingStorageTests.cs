using QSurfer.Core.Models;
using Xunit;

namespace QSurfer.Core.Tests;

public sealed class GlobalMappingStorageTests
{
    [Fact]
    public void GlobalMappingsLiveInTheDeploymentConfigWhileLocalMappingsStayWithTheWorkstation()
    {
        var config = new AppConfig
        {
            PathMappings =
            [
                new PathMapping { ShareRoot = @"\Shared", MappedRoot = @"X:\", IsGlobal = true },
            ],
            Hosts =
            {
                [AppConfig.CurrentHostKey] = new HostConfig
                {
                    PathMappings =
                    [
                        new PathMapping { ShareRoot = @"\Shared\Legal", MappedRoot = @"L:\" },
                    ],
                },
            },
        };

        config.ApplyCurrentHost();

        Assert.Collection(
            config.PathMappings,
            mapping =>
            {
                Assert.Equal(@"\\Shared\Legal", mapping.ShareRoot);
                Assert.False(mapping.IsGlobal);
            },
            mapping =>
            {
                Assert.Equal(@"\Shared", mapping.ShareRoot);
                Assert.True(mapping.IsGlobal);
            });

        config.CaptureCurrentHost();

        var local = Assert.Single(config.Hosts[AppConfig.CurrentHostKey].PathMappings);
        Assert.Equal(@"\\Shared\Legal", local.ShareRoot);
        Assert.False(local.IsGlobal);
        var global = Assert.Single(config.PathMappings);
        Assert.Equal(@"\Shared", global.ShareRoot);
        Assert.True(global.IsGlobal);
    }
}
