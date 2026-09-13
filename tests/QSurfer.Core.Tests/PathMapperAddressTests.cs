using QSurfer.Core.Models;
using QSurfer.Core.Services;
using Xunit;

namespace QSurfer.Core.Tests;

public sealed class PathMapperAddressTests
{
    [Fact]
    public void ShortShareAddressUsesConfiguredNasHostWhenNoDriveMappingExists()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var config = new AppConfig { Host = "qsurfer-autocomplete-test" };
        var mapper = new PathMapper(config);

        var resolved = mapper.ResolveBrowserPath(@"\QSurferAutocompleteTest\AA Criminal");

        Assert.Equal(@"\\qsurfer-autocomplete-test\QSurferAutocompleteTest\AA Criminal", resolved);
        Assert.Equal(@"\QSurferAutocompleteTest\AA Criminal", mapper.DisplayBrowserPath(resolved));
    }
}
