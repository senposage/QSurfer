using QSurfer.Core.Models;
using Xunit;

namespace QSurfer.Core.Tests;

public sealed class BehaviorConfigTests
{
    [Fact]
    public void NewConfigurationsLimitBulkTabOpensToFifteen()
    {
        Assert.Equal(15, new BehaviorConfig().MaxBulkOpenTabs);
    }

    [Fact]
    public void ScopeReplacementConfirmationIsEnabledByDefault()
    {
        Assert.True(new BehaviorConfig().ConfirmScopeReplacement);
    }

    [Fact]
    public void HostSettingsPreserveTheBulkTabOpenLimit()
    {
        var config = new AppConfig();
        config.Behavior.MaxBulkOpenTabs = 42;

        config.CaptureCurrentHost();
        config.ApplyCurrentHost();

        Assert.Equal(42, config.Behavior.MaxBulkOpenTabs);
    }

    [Fact]
    public void HostSettingsPreserveScopeGesturePreferencesAndFirstRunGuideState()
    {
        var config = new AppConfig();
        config.Behavior.NavigationScopeIncludeGesture = "Ctrl";
        config.Behavior.NavigationScopeExcludeGesture = "Ctrl+Alt";
        config.Behavior.ConfirmScopeReplacement = false;
        config.Behavior.LastSeenFirstRunGuideVersion = "1.1.0";

        config.CaptureCurrentHost();
        config.ApplyCurrentHost();

        Assert.Equal("Ctrl", config.Behavior.NavigationScopeIncludeGesture);
        Assert.Equal("Ctrl+Alt", config.Behavior.NavigationScopeExcludeGesture);
        Assert.False(config.Behavior.ConfirmScopeReplacement);
        Assert.Equal("1.1.0", config.Behavior.LastSeenFirstRunGuideVersion);
    }
}
