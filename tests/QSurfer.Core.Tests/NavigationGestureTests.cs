using Avalonia.Input;
using QSurfer.Avalonia;
using Xunit;

namespace QSurfer.Core.Tests;

public sealed class NavigationGestureTests
{
    [Fact]
    public void ShiftAltClickMatchesTheDefaultExcludeGesture()
    {
        Assert.True(MainWindow.MatchesNavigationScopeGesture(
            KeyModifiers.Shift | KeyModifiers.Alt,
            "Shift+Alt"));
    }

    [Fact]
    public void ShiftOnlyDoesNotMatchTheExcludeGesture()
    {
        Assert.False(MainWindow.MatchesNavigationScopeGesture(KeyModifiers.Shift, "Shift+Alt"));
    }
}
