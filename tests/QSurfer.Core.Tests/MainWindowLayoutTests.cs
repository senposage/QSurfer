using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using QSurfer.Avalonia;
using QSurfer.Avalonia.ViewModels;
using QSurfer.Core.Models;
using Xunit;

namespace QSurfer.Core.Tests;

public sealed class MainWindowLayoutTests
{
    [AvaloniaFact]
    public void NavigationSidebarUsesSpaceAndPreviewStaysCollapsedWhenDisabled()
    {
        var window = CreateWindow(width: 1280, height: 820, showNavigation: true, showPreview: false);
        try
        {
            var sidebar = Require<Border>(window, "WorkspaceSidebar");
            var preview = Require<Border>(window, "WorkspacePreview");
            var workspace = Require<Grid>(window, "WorkspaceGrid");
            var navigation = Require<Grid>(window, "WorkspaceNavigation");

            Assert.True(sidebar.IsVisible);
            Assert.True(navigation.IsVisible);
            Assert.True(sidebar.Bounds.Width >= 120);
            Assert.True(navigation.Bounds.Height >= 96);
            Assert.False(preview.IsVisible);
            Assert.True(preview.Bounds.Width <= 1);
            Assert.True(workspace.Bounds.Width >= 900);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void PreviewAndSidebarRespectTheirMinimumWidthsAtTheSmallestSupportedWindow()
    {
        var window = CreateWindow(width: 900, height: 620, showNavigation: true, showPreview: true);
        try
        {
            var sidebar = Require<Border>(window, "WorkspaceSidebar");
            var preview = Require<Border>(window, "WorkspacePreview");
            var workspace = Require<Grid>(window, "WorkspaceGrid");

            Assert.True(sidebar.IsVisible);
            Assert.True(preview.IsVisible);
            Assert.True(sidebar.Bounds.Width >= 120);
            Assert.True(preview.Bounds.Width >= 220);
            Assert.True(workspace.ColumnDefinitions[2].ActualWidth >= 340);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void NavigationReceivesTheAvailableSidebarHeightWhenFavoritesAreHidden()
    {
        var window = CreateWindow(width: 1280, height: 820, showNavigation: true, showPreview: false, showFavorites: false);
        try
        {
            var sidebar = Require<Border>(window, "WorkspaceSidebar");
            var favorites = Require<Grid>(window, "WorkspaceFavorites");
            var navigation = Require<Grid>(window, "WorkspaceNavigation");

            Assert.False(favorites.IsVisible);
            Assert.True(navigation.IsVisible);
            Assert.True(navigation.Bounds.Height >= sidebar.Bounds.Height * 0.9);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void CompactWindowMovesSearchAndFilterControlsOntoSeparateRows()
    {
        var window = CreateWindow(width: 900, height: 620, showNavigation: false, showPreview: false);
        try
        {
            var addressBand = Require<Grid>(window, "SearchAddressBand");
            var filters = Require<Grid>(window, "FilterControlsGrid");
            var searchControls = Require<Grid>(window, "SearchControlsGroup");
            var scopeFilter = Require<Control>(window, "ScopeFilter");

            Assert.Equal(2, addressBand.RowDefinitions.Count);
            Assert.Equal(3, filters.RowDefinitions.Count);
            Assert.Equal(1, Grid.GetRow(searchControls));
            Assert.Equal(1, Grid.GetRow(scopeFilter));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void WideWindowReturnsSearchAndFilterControlsToSingleRows()
    {
        var window = CreateWindow(width: 1600, height: 820, showNavigation: false, showPreview: false);
        try
        {
            var addressBand = Require<Grid>(window, "SearchAddressBand");
            var filters = Require<Grid>(window, "FilterControlsGrid");
            var searchControls = Require<Grid>(window, "SearchControlsGroup");
            var scopeFilter = Require<Control>(window, "ScopeFilter");

            Assert.Single(addressBand.RowDefinitions);
            Assert.Equal(2, filters.RowDefinitions.Count);
            Assert.Equal(0, Grid.GetRow(searchControls));
            Assert.Equal(0, Grid.GetRow(scopeFilter));
        }
        finally
        {
            window.Close();
        }
    }

    private static MainWindow CreateWindow(double width, double height, bool showNavigation, bool showPreview, bool showFavorites = true)
    {
        var config = new AppConfig();
        config.Behavior.ShowNavigationPane = showNavigation;
        config.Behavior.PreviewPane = showPreview;
        config.Behavior.FavoritesPaneWidth = 238;
        config.Behavior.PreviewPaneWidth = 280;

        var viewModel = new MainWindowViewModel(config, startBackgroundWork: false);
        viewModel.IsFavoritesVisible = showFavorites;
        var window = new MainWindow(viewModel, isDetachedWindow: true)
        {
            Width = width,
            Height = height,
        };
        window.Show();
        window.Measure(new Size(width, height));
        window.Arrange(new Rect(0, 0, width, height));
        window.UpdateLayout();
        return window;
    }

    private static T Require<T>(Control root, string name) where T : Control =>
        Assert.IsAssignableFrom<T>(root.GetVisualDescendants().OfType<Control>().FirstOrDefault(control => control.Name == name));
}
