using QSurfer.Core.Services;
using Xunit;

namespace QSurfer.Core.Tests;

public sealed class NasIdentityTests
{
    [Theory]
    [InlineData("https://nas.example.local:443", "nas.example.local")]
    [InlineData(@"\\nas.example.local\Shared", "nas.example.local")]
    [InlineData(" nas.example.local ", "nas.example.local")]
    public void NormalizeHostRemovesProtocolPathAndWhitespace(string source, string expected)
    {
        Assert.Equal(expected, NasIdentity.NormalizeHost(source));
    }

    [Fact]
    public void NormalizeShareRootUsesConfiguredFqdnForMatchingShortName()
    {
        var result = NasIdentity.NormalizeShareRoot(@"\\DRK-NAS\Shared", "drk-nas.example.local");

        Assert.Equal(@"\\drk-nas.example.local\Shared", result);
    }

    [Fact]
    public void HostsMatchShortAndFullyQualifiedNames()
    {
        Assert.True(NasIdentity.HostsMatch("drk-nas", "DRK-NAS.example.local"));
    }
}
