using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using QSurfer.Core.Models;
using QSurfer.Core.Services;
using QSurfer.Avalonia.Services;
using QSurfer.Avalonia.ViewModels;

namespace QSurfer.Avalonia;

public sealed partial class MainWindow : Window
{
    private const int PreviewSelectionDelayMilliseconds = 240;
    private const double WideSearchLayoutWidth = 1160;
    private const double WideFilterLayoutWidth = 1500;
    private readonly MainWindowViewModel _viewModel = new();
    private bool _exitRequested;
    private CancellationTokenSource? _previewCancellation;
    private ShellPreviewHost? _nativePreviewHost;
    private IReadOnlyList<SearchResult> _contextResults = [];
    private DataGrid? _contextResultsGrid;
    private bool _controlKeyDown;
    private GridLength _favoritesPaneWidth = new(238, GridUnitType.Pixel);
    private GridLength _previewPaneWidth = new(280, GridUnitType.Pixel);
    private double? _browserHorizontalOffset;
    private DataGrid? _browserResultsGrid;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;
        _favoritesPaneWidth = new GridLength(Math.Clamp(_viewModel.Config.Behavior.FavoritesPaneWidth, 160, 600), GridUnitType.Pixel);
        _previewPaneWidth = new GridLength(Math.Clamp(_viewModel.Config.Behavior.PreviewPaneWidth, 220, 700), GridUnitType.Pixel);
        ApplyWindowBehavior();
        KeyDown += MainWindow_KeyDown;
        KeyUp += (_, args) => _controlKeyDown = args.KeyModifiers.HasFlag(KeyModifiers.Control);
        _viewModel.PropertyChanged += ViewModel_PropertyChanged;
        Opened += (_, _) =>
        {
            ApplyResponsiveCommandLayout();
            UpdateSidePaneColumns();
            ApplyFavoritesNavigationSplit();
            ApplyDetailColumnVisibility();
            ApplyWindowChrome();
        };
        SizeChanged += (_, _) => ApplyResponsiveCommandLayout();
        Activated += (_, _) => ApplyWindowChrome();
        Closed += (_, _) =>
        {
            PersistPaneLayout();
            _viewModel.PropertyChanged -= ViewModel_PropertyChanged;
            ClearNativePreview();
            _viewModel.Dispose();
        };
        Closing += MainWindow_Closing;
        PropertyChanged += MainWindow_PropertyChanged;
    }

    internal AppConfig Config => _viewModel.Config;

    internal void SetStatus(string message) => _viewModel.StatusMessage(message);

    internal void HideToTray()
    {
        Hide();
        AppLogger.Info("app", "main window hidden to tray");
    }

    internal void RestoreFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
        AppLogger.Info("app", "main window restored from tray");
    }

    private void ApplyResponsiveCommandLayout()
    {
        var searchAddressBand = FindVisualControl<Grid>("SearchAddressBand");
        var addressControlsGroup = FindVisualControl<Grid>("AddressControlsGroup");
        var addressSearchDivider = FindVisualControl<Border>("AddressSearchDivider");
        var searchControlsGroup = FindVisualControl<Grid>("SearchControlsGroup");
        var filterControlsGrid = FindVisualControl<Grid>("FilterControlsGrid");
        var navigateBackButton = FindVisualControl<Button>("NavigateBackButton");
        var navigateForwardButton = FindVisualControl<Button>("NavigateForwardButton");
        var navigateUpButton = FindVisualControl<Button>("NavigateUpButton");
        var addressModeLabel = FindVisualControl<TextBlock>("AddressModeLabel");
        var addressTextBox = FindVisualControl<TextBox>("AddressTextBox");
        var controls = new[]
        {
            FindVisualControl<Control>("ExactMatchFilter"),
            FindVisualControl<Control>("SearchContentsFilter"),
            FindVisualControl<Control>("TypeFilterToggle"),
            FindVisualControl<Control>("DatePresetFilter"),
            FindVisualControl<Control>("FromFilterLabel"),
            FindVisualControl<Control>("DateFromFilter"),
            FindVisualControl<Control>("ToFilterLabel"),
            FindVisualControl<Control>("DateToFilter"),
            FindVisualControl<Control>("ClearFiltersButton"),
            FindVisualControl<Control>("ScopeFilterLabel"),
            FindVisualControl<Control>("ScopeFilter"),
            FindVisualControl<Control>("ArrangeFilterLabel"),
            FindVisualControl<Control>("ArrangeFilter"),
            FindVisualControl<Control>("ViewFilterLabel"),
            FindVisualControl<Control>("ViewFilter"),
            FindVisualControl<Control>("LoadMoreButton"),
        };
        if (searchAddressBand is null || addressControlsGroup is null || addressSearchDivider is null ||
            searchControlsGroup is null || filterControlsGrid is null || navigateBackButton is null ||
            navigateForwardButton is null || navigateUpButton is null || addressModeLabel is null ||
            addressTextBox is null || controls.Any(control => control is null))
        {
            return;
        }

        var exactMatchFilter = controls[0]!;
        var searchContentsFilter = controls[1]!;
        var typeFilterToggle = controls[2]!;
        var datePresetFilter = controls[3]!;
        var fromFilterLabel = controls[4]!;
        var dateFromFilter = controls[5]!;
        var toFilterLabel = controls[6]!;
        var dateToFilter = controls[7]!;
        var clearFiltersButton = controls[8]!;
        var scopeFilterLabel = controls[9]!;
        var scopeFilter = controls[10]!;
        var arrangeFilterLabel = controls[11]!;
        var arrangeFilter = controls[12]!;
        var viewFilterLabel = controls[13]!;
        var viewFilter = controls[14]!;
        var loadMoreButton = controls[15]!;

        var isBrowsing = _viewModel.IsNavigationVisible;
        var useWideSearchLayout = Bounds.Width >= WideSearchLayoutWidth;
        if (useWideSearchLayout)
        {
            SetGridRows(searchAddressBand, GridLength.Auto);
            SetGridColumns(
                searchAddressBand,
                isBrowsing
                    ? [new GridLength(2, GridUnitType.Star), new GridLength(14), new GridLength(3, GridUnitType.Star)]
                    : [GridLength.Auto, new GridLength(14), new GridLength(1, GridUnitType.Star)]);
        }
        else
        {
            SetGridRows(searchAddressBand, GridLength.Auto, GridLength.Auto);
            SetGridColumns(searchAddressBand, new GridLength(1, GridUnitType.Star));
        }
        searchAddressBand.RowSpacing = useWideSearchLayout ? 0 : 8;
        Grid.SetRow(addressControlsGroup, 0);
        Grid.SetColumn(addressControlsGroup, 0);
        Grid.SetRow(searchControlsGroup, useWideSearchLayout ? 0 : 1);
        Grid.SetColumn(searchControlsGroup, useWideSearchLayout ? 2 : 0);
        Grid.SetRow(addressSearchDivider, 0);
        Grid.SetColumn(addressSearchDivider, 1);
        addressSearchDivider.IsVisible = useWideSearchLayout;
        addressControlsGroup.HorizontalAlignment = isBrowsing ? HorizontalAlignment.Stretch : HorizontalAlignment.Left;
        navigateBackButton.IsVisible = isBrowsing;
        navigateForwardButton.IsVisible = isBrowsing;
        navigateUpButton.IsVisible = isBrowsing;
        addressModeLabel.IsVisible = !isBrowsing;
        addressTextBox.MaxWidth = isBrowsing ? double.PositiveInfinity : 260;
        addressTextBox.Watermark = isBrowsing ? "Address" : "Browse address";

        var useWideFilterLayout = Bounds.Width >= WideFilterLayoutWidth;
        if (useWideFilterLayout)
        {
            SetGridRows(filterControlsGrid, GridLength.Auto);
            SetGridColumns(filterControlsGrid, Enumerable.Repeat(GridLength.Auto, 16).ToArray());
            filterControlsGrid.RowSpacing = 0;
            typeFilterToggle.Width = 152;
            datePresetFilter.Width = 130;
            dateFromFilter.Width = 132;
            dateToFilter.Width = 132;

            SetGridPosition(exactMatchFilter, 0, 0);
            SetGridPosition(searchContentsFilter, 0, 1);
            SetGridPosition(typeFilterToggle, 0, 2);
            SetGridPosition(datePresetFilter, 0, 3);
            SetGridPosition(fromFilterLabel, 0, 4);
            SetGridPosition(dateFromFilter, 0, 5);
            SetGridPosition(toFilterLabel, 0, 6);
            SetGridPosition(dateToFilter, 0, 7);
            SetGridPosition(clearFiltersButton, 0, 8);
            SetGridPosition(scopeFilterLabel, 0, 9);
            SetGridPosition(scopeFilter, 0, 10);
            SetGridPosition(arrangeFilterLabel, 0, 11);
            SetGridPosition(arrangeFilter, 0, 12);
            SetGridPosition(viewFilterLabel, 0, 13);
            SetGridPosition(viewFilter, 0, 14);
            SetGridPosition(loadMoreButton, 0, 15);
            return;
        }

        SetGridRows(filterControlsGrid, GridLength.Auto, GridLength.Auto);
        SetGridColumns(filterControlsGrid, Enumerable.Repeat(GridLength.Auto, 9).ToArray());
        filterControlsGrid.RowSpacing = 8;
        typeFilterToggle.Width = 140;
        datePresetFilter.Width = 116;
        dateFromFilter.Width = 116;
        dateToFilter.Width = 116;

        SetGridPosition(exactMatchFilter, 0, 0);
        SetGridPosition(searchContentsFilter, 0, 1);
        SetGridPosition(typeFilterToggle, 0, 2);
        SetGridPosition(datePresetFilter, 0, 3);
        SetGridPosition(fromFilterLabel, 0, 4);
        SetGridPosition(dateFromFilter, 0, 5);
        SetGridPosition(toFilterLabel, 0, 6);
        SetGridPosition(dateToFilter, 0, 7);
        SetGridPosition(clearFiltersButton, 0, 8);
        SetGridPosition(scopeFilterLabel, 1, 0);
        SetGridPosition(scopeFilter, 1, 1);
        SetGridPosition(arrangeFilterLabel, 1, 2);
        SetGridPosition(arrangeFilter, 1, 3);
        SetGridPosition(viewFilterLabel, 1, 4);
        SetGridPosition(viewFilter, 1, 5);
        SetGridPosition(loadMoreButton, 1, 6);
    }

    private T? FindVisualControl<T>(string name) where T : Control =>
        this.GetVisualDescendants().OfType<T>().FirstOrDefault(control => control.Name == name);

    private static void SetGridPosition(Control control, int row, int column)
    {
        Grid.SetRow(control, row);
        Grid.SetColumn(control, column);
    }

    private static void SetGridRows(Grid grid, params GridLength[] heights)
    {
        grid.RowDefinitions.Clear();
        foreach (var height in heights)
        {
            grid.RowDefinitions.Add(new RowDefinition(height));
        }
    }

    private static void SetGridColumns(Grid grid, params GridLength[] widths)
    {
        grid.ColumnDefinitions.Clear();
        foreach (var width in widths)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition(width));
        }
    }

    internal void ExitApplication()
    {
        _exitRequested = true;
        Close();
    }

    private async void SearchTabKeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is Control { DataContext: SearchTabViewModel tab } &&
            e.Key == Key.Enter && tab.SearchCommand.CanExecute(null))
        {
            await tab.SearchCommand.ExecuteAsync();
            e.Handled = true;
        }
    }

    private async void MainWindow_KeyDown(object? sender, KeyEventArgs e)
    {
        _controlKeyDown = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        if (e.Key == Key.F1)
        {
            await OpenHelpAsync();
            e.Handled = true;
            return;
        }
        if (IsTextInputFocused() && !MatchesShortcut(_viewModel.Config.Behavior.KeyboardShortcuts.FocusSearch, e))
        {
            return;
        }

        var shortcuts = _viewModel.Config.Behavior.KeyboardShortcuts;
        if (MatchesShortcut(shortcuts.FocusSearch, e))
        {
            FocusSearchBox();
        }
        else if (MatchesShortcut(shortcuts.Refresh, e))
        {
            if (_viewModel.IsNavigationVisible && _viewModel.RefreshBrowserCommand.CanExecute(null))
            {
                await _viewModel.RefreshBrowserCommand.ExecuteAsync();
            }
            else if (_viewModel.SelectedSearchTab?.SearchCommand.CanExecute(null) == true)
            {
                await _viewModel.SelectedSearchTab.SearchCommand.ExecuteAsync();
            }
            else
            {
                return;
            }
        }
        else if (MatchesShortcut(shortcuts.Back, e) && _viewModel.IsNavigationVisible && _viewModel.NavigateBackCommand.CanExecute(null))
        {
            await _viewModel.NavigateBackCommand.ExecuteAsync();
        }
        else if (MatchesShortcut(shortcuts.Forward, e) && _viewModel.IsNavigationVisible && _viewModel.NavigateForwardCommand.CanExecute(null))
        {
            await _viewModel.NavigateForwardCommand.ExecuteAsync();
        }
        else if (MatchesShortcut(shortcuts.Up, e) && _viewModel.IsNavigationVisible && _viewModel.NavigateUpCommand.CanExecute(null))
        {
            await _viewModel.NavigateUpCommand.ExecuteAsync();
        }
        else if (MatchesShortcut(shortcuts.Open, e))
        {
            if (_viewModel.IsNavigationVisible && _viewModel.OpenBrowserItemCommand.CanExecute(null))
            {
                await _viewModel.OpenBrowserItemCommand.ExecuteAsync();
            }
            else if (_viewModel.SelectedSearchTab?.OpenCommand.CanExecute(null) == true)
            {
                await _viewModel.SelectedSearchTab.OpenCommand.ExecuteAsync();
            }
            else
            {
                return;
            }
        }
        else if (MatchesShortcut(shortcuts.CopyPath, e))
        {
            if (!await CopySelectedItemAsync()) return;
        }
        else if (_viewModel.IsNavigationVisible && MatchesShortcut(shortcuts.Cut, e))
        {
            if (SelectedBrowserItems().Count == 0) return;
            CopySelectedBrowserItems(cut: true);
        }
        else if (_viewModel.IsNavigationVisible && MatchesShortcut(shortcuts.Paste, e))
        {
            await PasteBrowserItemsAsync();
        }
        else if (_viewModel.IsNavigationVisible && MatchesShortcut(shortcuts.Rename, e))
        {
            await PromptRenameBrowserItemAsync();
        }
        else if (_viewModel.IsNavigationVisible && MatchesShortcut(shortcuts.Delete, e))
        {
            await ConfirmDeleteBrowserItemsAsync();
        }
        else if (_viewModel.IsNavigationVisible && MatchesShortcut(shortcuts.NewFolder, e))
        {
            await PromptNewBrowserFolderAsync();
        }
        else if (MatchesShortcut(shortcuts.Favorite, e))
        {
            if (_viewModel.IsNavigationVisible && _viewModel.ToggleBrowserFavoriteCommand.CanExecute(null))
            {
                await _viewModel.ToggleBrowserFavoriteCommand.ExecuteAsync();
            }
            else if (_viewModel.SelectedSearchTab?.SelectedResult is { } result)
            {
                await _viewModel.ToggleSearchResultFavoriteAsync(result);
            }
            else
            {
                return;
            }
        }
        else
        {
            return;
        }

        e.Handled = true;
    }

    private static bool MatchesShortcut(string setting, KeyEventArgs args) =>
        KeyboardShortcut.TryParse(setting, out var shortcut, out _) && shortcut.Matches(args);

    private bool IsTextInputFocused() => this.GetVisualDescendants().OfType<TextBox>().Any(textBox => textBox.IsFocused);

    private void FocusSearchBox()
    {
        _viewModel.ReturnToSearchResults();
        this.GetVisualDescendants().OfType<TextBox>()
            .FirstOrDefault(textBox => textBox.Classes.Contains("search-box"))
            ?.Focus();
    }

    private void SearchTabGotFocus(object? sender, GotFocusEventArgs e) => _viewModel.ReturnToSearchResults();

    private void BrowserLocationGotFocus(object? sender, GotFocusEventArgs e) => _viewModel.EnterBrowseMode();

    private async void ToggleTabWorkspace_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: SearchTabViewModel tab })
        {
            _viewModel.SelectedSearchTab = tab;
            await _viewModel.ToggleWorkspaceModeAsync();
        }
    }

    private async void RecentSearchSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is not ListBox { SelectedItem: string query })
        {
            return;
        }
        await _viewModel.RunRecentSearchAsync(query);
        if (sender is ListBox list)
        {
            list.SelectedItem = null;
        }
    }

    private void RecentSearchPopup_Closed(object? sender, EventArgs e) => _viewModel.IsRecentSearchesOpen = false;

    private async void RecentSearchToggle_Click(object? sender, RoutedEventArgs e) =>
        await _viewModel.ToggleRecentSearchesAsync();

    private async void SearchResultSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is Control { DataContext: SearchTabViewModel tab } && tab.SelectedResult is { } result)
        {
            await RequestNativePreviewAsync(result);
        }
    }

    private async void FavoriteSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_viewModel.SelectedFavoriteNode?.Result is { } result)
        {
            await RequestNativePreviewAsync(result);
        }
        else
        {
            ClearNativePreview();
        }
    }

    private async void BrowserLocationKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && _viewModel.BrowseCommand.CanExecute(null))
        {
            await _viewModel.BrowseCommand.ExecuteAsync();
            e.Handled = true;
        }
    }

    private async void SearchResultDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is Control { DataContext: SearchTabViewModel tab })
        {
            await tab.OpenCommand.ExecuteAsync();
        }
    }

    private async void BrowserItemDoubleTapped(object? sender, TappedEventArgs e)
    {
        await _viewModel.OpenSelectedBrowserItemAsync();
    }

    private async void BrowserItemSelected(object? sender, SelectionChangedEventArgs e)
    {
        var item = e.AddedItems.OfType<BrowserItem>().LastOrDefault();
        if (item == null)
        {
            ClearNativePreview();
            return;
        }

        _viewModel.SelectedBrowserItem = item;
        await RequestNativePreviewForSelectedBrowserItemAsync();
    }

    private void BrowserItemPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _browserResultsGrid = sender as DataGrid;
        _browserHorizontalOffset = BrowserScrollViewer(sender as Control)?.Offset.X;
        if (!e.GetCurrentPoint(sender as Visual).Properties.IsRightButtonPressed)
        {
            return;
        }

        if (e.Source is Control { DataContext: BrowserItem item })
        {
            if (_browserResultsGrid != null && !_browserResultsGrid.SelectedItems.Contains(item))
            {
                _browserResultsGrid.SelectedItems.Clear();
                _browserResultsGrid.SelectedItems.Add(item);
            }
            _viewModel.SelectedBrowserItem = item;
            _ = RequestNativePreviewForSelectedBrowserItemAsync();
            return;
        }

        _browserResultsGrid?.SelectedItems.Clear();
        _viewModel.SelectedBrowserItem = null;
        ClearNativePreview();
    }

    private void BrowserItemPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_browserHorizontalOffset is not { } horizontalOffset) return;
        _browserHorizontalOffset = null;
        Dispatcher.UIThread.Post(() =>
        {
            var scrollViewer = BrowserScrollViewer(sender as Control);
            if (scrollViewer != null && Math.Abs(scrollViewer.Offset.X - horizontalOffset) > 0.5)
            {
                scrollViewer.Offset = new Vector(horizontalOffset, scrollViewer.Offset.Y);
            }
        }, DispatcherPriority.Render);
    }

    private static ScrollViewer? BrowserScrollViewer(Control? control) => control?
        .GetVisualDescendants()
        .OfType<ScrollViewer>()
        .FirstOrDefault();

    private async void BrowserOpen_Click(object? sender, RoutedEventArgs e) => await _viewModel.OpenSelectedBrowserItemAsync();

    private async void RestoreRecycle_Click(object? sender, RoutedEventArgs e)
    {
        var items = SelectedBrowserItems();
        if (items.Count == 0)
        {
            _viewModel.StatusMessage("Select one or more Recycle Bin items to restore.");
            return;
        }

        var restoreTargets = NasFileBrowser.GetRecycleRestoreTargets(items);
        if (restoreTargets.Count != items.Count)
        {
            _viewModel.StatusMessage("One or more selected items do not have a recoverable original location.");
            return;
        }

        var filesToReplace = restoreTargets
            .Where(target => !target.Item.IsFolder && File.Exists(target.DestinationPath))
            .ToList();
        if (filesToReplace.Count > 0)
        {
            var message = filesToReplace.Count == 1
                ? $"Restore {items.Count:n0} item(s)? The live file \"{filesToReplace[0].Item.Name}\" will be retained as a hidden, read-only .qsurfer copy."
                : $"Restore {items.Count:n0} item(s)? {filesToReplace.Count:n0} live files will be retained as hidden, read-only .qsurfer copies.";
            var confirm = new ConfirmationWindow("Restore from Recycle Bin", message, "Restore");
            if (await confirm.ShowDialog<bool?>(this) != true)
            {
                return;
            }
        }

        await _viewModel.RestoreRecycleItemsAsync(items, replaceExistingFiles: filesToReplace.Count > 0);
    }

    private async void BrowserBreadcrumb_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: BrowserBreadcrumb breadcrumb })
        {
            await _viewModel.NavigateToBreadcrumbAsync(breadcrumb);
        }
    }

    private void BrowserContextMenu_Opened(object? sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu menu) return;
        var selectionCount = SelectedBrowserItems().Count;
        foreach (var item in menu.Items.OfType<MenuItem>())
        {
            item.IsEnabled = (item.Tag as string) switch
            {
                "paste" => !string.IsNullOrWhiteSpace(_viewModel.BrowserLocation),
                "new-folder" => !string.IsNullOrWhiteSpace(_viewModel.BrowserLocation),
                "rename" or "shortcut" or "properties" => selectionCount == 1,
                "cut" or "copy" or "delete" => selectionCount > 0,
                _ => selectionCount == 1,
            };
        }
    }

    private void BrowserCut_Click(object? sender, RoutedEventArgs e) => CopySelectedBrowserItems(cut: true);

    private async void BrowserCopy_Click(object? sender, RoutedEventArgs e) => await CopySelectedItemAsync();

    private void CopySelectedBrowserItems(bool cut)
    {
        var items = SelectedBrowserItems();
        if (items.Count > 0)
        {
            _viewModel.CopyBrowserItems(items, cut);
        }
    }

    private async void BrowserPaste_Click(object? sender, RoutedEventArgs e) => await PasteBrowserItemsAsync();

    private async Task PasteBrowserItemsAsync()
    {
        if (!_viewModel.CanPasteBrowserItems)
        {
            var externalItems = await GetClipboardBrowserItemsAsync();
            if (externalItems.Count > 0)
            {
                _viewModel.CopyBrowserItems(externalItems, cut: false);
            }
        }
        if (_viewModel.PasteBrowserItemsCommand.CanExecute(null))
        {
            await _viewModel.PasteBrowserItemsCommand.ExecuteAsync();
        }
    }

    private async void BrowserNewFolder_Click(object? sender, RoutedEventArgs e) => await PromptNewBrowserFolderAsync();

    private async Task PromptNewBrowserFolderAsync()
    {
        var dialog = new TextEntryWindow("New folder", "Folder name", "Create", "New folder");
        if (await dialog.ShowDialog<bool?>(this) == true)
        {
            await _viewModel.CreateBrowserFolderAsync(dialog.Value);
        }
    }

    private async void BrowserRename_Click(object? sender, RoutedEventArgs e) => await PromptRenameBrowserItemAsync();

    private async Task PromptRenameBrowserItemAsync()
    {
        if (SelectedBrowserItems() is not [var item]) return;
        var dialog = new TextEntryWindow("Rename", "New name", "Rename", item.Name);
        if (await dialog.ShowDialog<bool?>(this) == true)
        {
            await _viewModel.RenameBrowserItemAsync(item, dialog.Value);
        }
    }

    private async void BrowserDelete_Click(object? sender, RoutedEventArgs e) => await ConfirmDeleteBrowserItemsAsync();

    private async Task ConfirmDeleteBrowserItemsAsync()
    {
        var items = SelectedBrowserItems();
        if (items.Count == 0) return;
        var description = items.Count == 1
            ? $"Delete the {(items[0].IsFolder ? "folder" : "file")} \"{items[0].Name}\"?"
            : $"Delete {items.Count:n0} selected items?";
        var dialog = new ConfirmationWindow("Delete", description, "Delete");
        if (await dialog.ShowDialog<bool?>(this) == true)
        {
            await _viewModel.DeleteBrowserItemsAsync(items);
        }
    }

    private async void BrowserShortcut_Click(object? sender, RoutedEventArgs e)
    {
        if (SelectedBrowserItems() is [var item])
        {
            await _viewModel.CreateBrowserShortcutAsync(item);
        }
    }

    private async void BrowserCopyPath_Click(object? sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedBrowserItemAsResult() is { } result)
        {
            await CopyPathAsync(result);
        }
    }

    private async void BrowserVersionHistory_Click(object? sender, RoutedEventArgs e) =>
        await OpenVersionHistoryAsync(_viewModel.SelectedBrowserItemAsResult(), openedFromExplorer: true);

    private async Task<bool> CopySelectedItemAsync()
    {
        var items = _viewModel.IsNavigationVisible
            ? SelectedBrowserItems()
            : CreateBrowserItem(_viewModel.SelectedSearchTab?.SelectedResult) is { } searchItem ? [searchItem] : [];
        if (items.Count == 0)
        {
            return false;
        }

        _viewModel.CopyBrowserItems(items, cut: false);
        var clipboard = GetTopLevel(this)?.Clipboard;
        var storageProvider = GetTopLevel(this)?.StorageProvider;
        if (clipboard == null || storageProvider == null)
        {
            return true;
        }

        try
        {
            var storageItems = new List<IStorageItem>();
            foreach (var selectedItem in items)
            {
                IStorageItem? storageItem;
                if (selectedItem.IsFolder)
                {
                    storageItem = await storageProvider.TryGetFolderFromPathAsync(selectedItem.FullPath);
                }
                else
                {
                    storageItem = await storageProvider.TryGetFileFromPathAsync(selectedItem.FullPath);
                }
                if (storageItem != null)
                {
                    storageItems.Add(storageItem);
                }
            }
            if (storageItems.Count == 0)
            {
                _viewModel.StatusMessage("The selected item could not be prepared for the Windows clipboard.");
                return true;
            }
            await clipboard.SetFilesAsync(storageItems);
            return true;
        }
        catch (Exception ex)
        {
            AppLogger.Error("clipboard", ex, $"file copy failed count={items.Count}");
            _viewModel.StatusMessage("The item is ready to paste in QSurfer, but Windows could not update the clipboard.");
            return true;
        }
    }

    private IReadOnlyList<BrowserItem> SelectedBrowserItems()
    {
        if (_browserResultsGrid == null)
        {
            return _viewModel.SelectedBrowserItem is { } item ? [item] : [];
        }

        return _browserResultsGrid.SelectedItems
            .OfType<BrowserItem>()
            .DistinctBy(item => item.FullPath, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private async Task<IReadOnlyList<BrowserItem>> GetClipboardBrowserItemsAsync()
    {
        var clipboard = GetTopLevel(this)?.Clipboard;
        if (clipboard == null)
        {
            return [];
        }

        try
        {
            var files = await clipboard.TryGetFilesAsync() ?? [];
            return files
                .Select(StorageProviderExtensions.TryGetLocalPath)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(CreateBrowserItem)
                .Where(item => item != null)
                .Cast<BrowserItem>()
                .DistinctBy(item => item.FullPath, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex)
        {
            AppLogger.Error("clipboard", ex, "read file clipboard failed");
            _viewModel.StatusMessage("Windows could not read files from the clipboard.");
            return [];
        }
    }

    private static BrowserItem? CreateBrowserItem(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            var isFolder = Directory.Exists(path);
            var name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (string.IsNullOrWhiteSpace(name))
            {
                name = path;
            }
            if (isFolder)
            {
                var info = new DirectoryInfo(path);
                return new BrowserItem(name, path, true, 0, info.LastWriteTime);
            }

            var file = new FileInfo(path);
            return file.Exists ? new BrowserItem(name, path, false, file.Length, file.LastWriteTime) : null;
        }
        catch
        {
            return null;
        }
    }

    private BrowserItem? CreateBrowserItem(SearchResult? result)
    {
        if (result == null)
        {
            return null;
        }

        var path = _viewModel.ResolveWindowsPath(result);
        if (string.IsNullOrWhiteSpace(path))
        {
            _viewModel.StatusMessage("QSurfer could not resolve a Windows path for the selected result.");
            return null;
        }

        var modified = DateTime.TryParse(result.Modified, out var parsedModified) ? parsedModified : DateTime.MinValue;
        return new BrowserItem(result.Name, path, result.IsFolder, result.Size, modified);
    }

    private void BrowserProperties_Click(object? sender, RoutedEventArgs e)
    {
        if (SelectedBrowserItems() is [var item])
        {
            _viewModel.ShowBrowserItemProperties(item);
        }
    }

    private async void BrowserFavorite_Click(object? sender, RoutedEventArgs e)
    {
        if (_viewModel.ToggleBrowserFavoriteCommand.CanExecute(null))
        {
            await _viewModel.ToggleBrowserFavoriteCommand.ExecuteAsync();
        }
    }

    private async void NavigationTreePointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (IsTreeExpander(e.Source) || NavigationNodeFromSource(e.Source) is not { } node)
        {
            return;
        }

        await _viewModel.NavigateToFolderAsync(node);
    }

    private static bool IsTreeExpander(object? source) =>
        source is ToggleButton || source is Visual visual && visual.GetVisualAncestors().OfType<ToggleButton>().Any();

    private static NavigationTreeNode? NavigationNodeFromSource(object? source)
    {
        if (source is StyledElement { DataContext: NavigationTreeNode node })
        {
            return node;
        }

        return source is Visual visual
            ? visual.GetVisualAncestors()
                .OfType<StyledElement>()
                .Select(element => element.DataContext)
                .OfType<NavigationTreeNode>()
                .FirstOrDefault()
            : null;
    }

    private async void FavoriteDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (_viewModel.OpenFavoriteCommand.CanExecute(null))
        {
            await _viewModel.OpenFavoriteCommand.ExecuteAsync();
        }
    }

    private async void Settings_Click(object? sender, RoutedEventArgs e)
    {
        var app = Application.Current as App;
        Func<string, HotkeyRegistrationResult>? configureHotkey = app == null ? null : app.ConfigureGlobalHotkey;
        var settings = new SettingsWindow(_viewModel.Config, configureHotkey);
        await settings.ShowDialog(this);
        if (settings.Saved)
        {
            _viewModel.ReloadConnection();
            ApplyWindowBehavior();
            ApplyDetailColumnVisibility();
            if (_viewModel.IsNavigationVisible && !string.IsNullOrWhiteSpace(_viewModel.BrowserLocation))
            {
                await _viewModel.RefreshBrowserCommand.ExecuteAsync();
            }
            if (settings.ClearHistoryRequested)
            {
                await _viewModel.ClearCurrentUserHistoryAsync(settings.ClearStarredRequested);
            }
            if (settings.ResetDatabaseRequested)
            {
                await _viewModel.ResetCurrentUserHistoryAsync();
            }
        }
    }

    private async void Help_Click(object? sender, RoutedEventArgs e)
    {
        await OpenHelpAsync();
    }

    private Task OpenHelpAsync() => new HelpWindow().ShowDialog(this);

    private void ApplyWindowBehavior()
    {
        Topmost = _viewModel.Config.AlwaysOnTop;
        ShowInTaskbar = _viewModel.Config.Behavior.ShowInTaskbar;
        ThemeColorService.Apply(_viewModel.Config.Behavior.ThemeColors, _viewModel.Config.Behavior.UseWindowsAccentColor);
        Application.Current!.RequestedThemeVariant = _viewModel.Config.Behavior.Theme.ToLowerInvariant() switch
        {
            "light" => ThemeVariant.Light,
            "dark" => ThemeVariant.Dark,
            _ => ThemeVariant.Default,
        };
        ApplyWindowChrome();
    }

    private void ApplyWindowChrome() =>
        WindowChromeService.Apply(this, _viewModel.Config.Behavior.ThemeColors);

    private async Task RequestNativePreviewAsync(SearchResult result)
    {
        ClearNativePreview();
        if (!_viewModel.IsPreviewVisible || result.IsFolder || ShellPreviewHost.IsVideoFile(result.Extension))
        {
            return;
        }

        var path = _viewModel.ResolveWindowsPath(result);
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        var cancellation = new CancellationTokenSource();
        _previewCancellation = cancellation;
        try
        {
            await Task.Delay(PreviewSelectionDelayMilliseconds, cancellation.Token);
            if (cancellation.IsCancellationRequested || !IsCurrentPreviewResult(result))
            {
                return;
            }

            var host = ShellPreviewHost.TryCreate(path);
            if (host == null)
            {
                return;
            }

            host.PreviewFailed += NativePreviewFailed;
            _nativePreviewHost = host;
            _viewModel.SetNativePreviewHost(host);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private Task RequestNativePreviewForSelectedBrowserItemAsync() =>
        _viewModel.SelectedBrowserItemAsResult() is { } result
            ? RequestNativePreviewAsync(result)
            : Task.CompletedTask;

    private bool IsCurrentPreviewResult(SearchResult result) =>
        ReferenceEquals(_viewModel.SelectedSearchTab?.SelectedResult, result) ||
        ReferenceEquals(_viewModel.SelectedFavoriteNode?.Result, result) ||
        string.Equals(_viewModel.SelectedBrowserItem?.FullPath, result.WindowsPath, StringComparison.OrdinalIgnoreCase);

    private void ClearNativePreview()
    {
        _previewCancellation?.Cancel();
        _previewCancellation?.Dispose();
        _previewCancellation = null;
        var host = _nativePreviewHost;
        _nativePreviewHost = null;
        _viewModel.SetNativePreviewHost(null);
        if (host != null)
        {
            host.PreviewFailed -= NativePreviewFailed;
            host.Dispose();
        }
    }

    private void NativePreviewFailed(object? sender, PreviewFailureEventArgs e)
    {
        if (!ReferenceEquals(sender, _nativePreviewHost))
        {
            return;
        }
        ClearNativePreview();
        _viewModel.StatusMessage(e.Message);
    }

    private void MainWindow_Closing(object? sender, WindowClosingEventArgs e)
    {
        if (!_exitRequested && _viewModel.Config.Behavior.ExitToTray)
        {
            e.Cancel = true;
            HideToTray();
        }
    }

    private void MainWindow_PropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == WindowStateProperty && WindowState == WindowState.Minimized && _viewModel.Config.Behavior.MinimizeToTray)
        {
            HideToTray();
        }
    }

    private void ViewModel_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainWindowViewModel.IsFavoritesVisible) or nameof(MainWindowViewModel.IsPreviewVisible))
        {
            UpdateSidePaneColumns();
        }
        else if (e.PropertyName == nameof(MainWindowViewModel.IsNavigationVisible))
        {
            Dispatcher.UIThread.Post(ApplyResponsiveCommandLayout, DispatcherPriority.Loaded);
        }
        else if (e.PropertyName == nameof(MainWindowViewModel.SelectedSearchTab))
        {
            Dispatcher.UIThread.Post(UpdateSidePaneColumns, DispatcherPriority.Loaded);
        }
    }

    private void UpdateSidePaneColumns()
    {
        var grid = this.GetVisualDescendants()
            .OfType<Grid>()
            .FirstOrDefault(control => control.Name == "ResultLayoutGrid");
        if (grid?.ColumnDefinitions.Count != 5)
        {
            return;
        }

        var columns = grid.ColumnDefinitions;
        SetSidePaneColumns(columns[0], columns[1], _viewModel.IsFavoritesVisible, ref _favoritesPaneWidth);
        SetSidePaneColumns(columns[4], columns[3], _viewModel.IsPreviewVisible, ref _previewPaneWidth);
    }

    private void ApplyFavoritesNavigationSplit()
    {
        var grid = this.FindControl<Grid>("FavoritesNavigationGrid");
        if (grid?.RowDefinitions.Count != 3)
        {
            return;
        }

        var favoritesWeight = Math.Clamp(_viewModel.Config.Behavior.FavoritesNavigationSplit, 0.2, 0.8);
        grid.RowDefinitions[0].Height = new GridLength(favoritesWeight, GridUnitType.Star);
        grid.RowDefinitions[2].Height = new GridLength(1 - favoritesWeight, GridUnitType.Star);
    }

    private void PersistPaneLayout()
    {
        var layout = this.FindControl<Grid>("ResultLayoutGrid");
        if (layout?.ColumnDefinitions.Count == 5)
        {
            var favoritesWidth = layout.ColumnDefinitions[0].ActualWidth;
            var previewWidth = layout.ColumnDefinitions[4].ActualWidth;
            if (favoritesWidth >= 160)
            {
                _viewModel.Config.Behavior.FavoritesPaneWidth = (int)Math.Round(favoritesWidth);
            }
            if (previewWidth >= 220)
            {
                _viewModel.Config.Behavior.PreviewPaneWidth = (int)Math.Round(previewWidth);
            }
        }

        var split = this.FindControl<Grid>("FavoritesNavigationGrid");
        if (split?.RowDefinitions.Count == 3)
        {
            var favoritesHeight = split.RowDefinitions[0].ActualHeight;
            var navigationHeight = split.RowDefinitions[2].ActualHeight;
            if (favoritesHeight > 0 && navigationHeight > 0)
            {
                _viewModel.Config.Behavior.FavoritesNavigationSplit = Math.Clamp(favoritesHeight / (favoritesHeight + navigationHeight), 0.2, 0.8);
            }
        }

        ConfigStore.Save(_viewModel.Config);
    }

    private static void SetSidePaneColumns(ColumnDefinition paneColumn, ColumnDefinition splitterColumn, bool isVisible, ref GridLength savedWidth)
    {
        if (isVisible)
        {
            paneColumn.Width = savedWidth;
            splitterColumn.Width = new GridLength(6, GridUnitType.Pixel);
            return;
        }

        if (paneColumn.Width.Value > 0)
        {
            savedWidth = paneColumn.Width;
        }
        paneColumn.Width = new GridLength(0, GridUnitType.Pixel);
        splitterColumn.Width = new GridLength(0, GridUnitType.Pixel);
    }

    private void CloseSearchTab_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: SearchTabViewModel tab })
        {
            _viewModel.CloseSearchTab(tab);
        }
    }

    private void ToggleTabPin_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: SearchTabViewModel tab })
        {
            _viewModel.ToggleTabPin(tab);
        }
    }

    private async void SaveSearchAs_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: SearchTabViewModel tab } || string.IsNullOrWhiteSpace(tab.Query))
        {
            return;
        }

        var dialog = new TextEntryWindow("Save search", "Name this saved search", "Save", tab.Query);
        if (await dialog.ShowDialog<bool?>(this) == true)
        {
            await _viewModel.SaveSearchAsync(tab, dialog.Value);
        }
    }

    private void TypeFilterPopup_Closed(object? sender, EventArgs e)
    {
        if (sender is Popup { PlacementTarget.DataContext: SearchTabViewModel tab })
        {
            tab.IsTypeFilterOpen = false;
        }
    }

    private void ApplyTypeFilters_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: SearchTabViewModel tab })
        {
            tab.IsTypeFilterOpen = false;
        }
    }

    private async void ToggleFavoriteResult_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: SearchResult result })
        {
            await _viewModel.ToggleSearchResultFavoriteAsync(result);
        }
    }

    private void ResultPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(sender as Visual).Properties.IsRightButtonPressed ||
            sender is not Control { DataContext: SearchTabViewModel tab } ||
            e.Source is not Control { DataContext: SearchResult result })
        {
            return;
        }
        tab.SelectedResult = result;
    }

    private void FavoritePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is not Control { DataContext: FavoriteTreeNode node })
        {
            _viewModel.SelectedFavoriteNode = null;
            e.Handled = true;
            return;
        }

        if (e.GetCurrentPoint(sender as Visual).Properties.IsRightButtonPressed)
        {
            _viewModel.SelectedFavoriteNode = node;
        }
    }

    private void ResultContextMenu_Opened(object? sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu menu)
        {
            return;
        }
        _contextResultsGrid = menu.PlacementTarget as DataGrid;
        _contextResults = _contextResultsGrid is { } grid
            ? grid.SelectedItems.OfType<SearchResult>().ToList()
            : [];
        if (_contextResults.Count == 0 && _viewModel.SelectedSearchTab?.SelectedResult is { } selected)
        {
            _contextResults = [selected];
        }
        var hasResult = _contextResults.Count > 0;
        foreach (var item in menu.Items.OfType<MenuItem>())
        {
            item.IsEnabled = hasResult;
            if (item.Tag as string == "show")
            {
                item.IsVisible = hasResult && !_contextResults[0].IsFolder;
            }
        }
        UpdateColumnMenu(menu, _contextResultsGrid);
    }

    private static void UpdateColumnMenu(ContextMenu menu, DataGrid? grid)
    {
        var columnsMenu = menu.Items.OfType<MenuItem>().FirstOrDefault(item => item.Tag as string == "columns");
        if (columnsMenu == null || grid == null)
        {
            return;
        }

        foreach (var item in columnsMenu.Items.OfType<MenuItem>())
        {
            var key = (item.Tag as string)?.Replace("column:", "", StringComparison.Ordinal);
            var column = grid.Columns.FirstOrDefault(candidate => candidate.SortMemberPath == key);
            item.IsChecked = column?.IsVisible == true;
        }
    }

    private void ToggleDetailColumn_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string tag } item || !tag.StartsWith("column:", StringComparison.Ordinal))
        {
            return;
        }

        var grid = _contextResultsGrid ?? FindDetailsGrid();
        var key = tag[7..];
        var column = grid?.Columns.FirstOrDefault(candidate => candidate.SortMemberPath == key);
        if (grid == null || column == null)
        {
            return;
        }

        if (column.IsVisible && grid.Columns.Count(candidate => candidate.IsVisible) == 1)
        {
            item.IsChecked = true;
            return;
        }

        column.IsVisible = item.IsChecked;
        _viewModel.Config.Behavior.VisibleDetailColumns = grid.Columns
            .Where(candidate => candidate.IsVisible && !string.IsNullOrWhiteSpace(candidate.SortMemberPath))
            .Select(candidate => candidate.SortMemberPath!)
            .ToList();
        ConfigStore.Save(_viewModel.Config);
    }

    private void DetailsGrid_Sorting(object? sender, DataGridColumnEventArgs e)
    {
        if (sender is not DataGrid { DataContext: SearchTabViewModel tab } ||
            string.IsNullOrWhiteSpace(e.Column.SortMemberPath))
        {
            return;
        }

        tab.ApplyColumnSort(e.Column.SortMemberPath, _controlKeyDown);
        e.Handled = true;
    }

    private void ApplyDetailColumnVisibility()
    {
        var grid = FindDetailsGrid();
        if (grid == null)
        {
            return;
        }

        var visible = new HashSet<string>(_viewModel.Config.Behavior.VisibleDetailColumns, StringComparer.OrdinalIgnoreCase);
        if (visible.Count == 0)
        {
            visible.Add("name");
        }
        foreach (var column in grid.Columns)
        {
            if (!string.IsNullOrWhiteSpace(column.SortMemberPath))
            {
                column.IsVisible = visible.Contains(column.SortMemberPath);
            }
        }
    }

    private DataGrid? FindDetailsGrid() => this.GetVisualDescendants()
        .OfType<DataGrid>()
        .FirstOrDefault(grid => grid.Name == "DetailsResultsGrid");

    private void FavoriteContextMenu_Opened(object? sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu menu)
        {
            return;
        }
        var node = _viewModel.SelectedFavoriteNode;
        if (node == null)
        {
            menu.Items.OfType<Control>().ToList().ForEach(item => item.IsVisible = false);
            return;
        }
        var isResult = node?.Result != null;
        var isSavedSearch = node?.SavedSearch != null;
        var isGroup = node?.IsFolder == true && !node.FolderPath.StartsWith("__", StringComparison.Ordinal);
        foreach (var item in menu.Items.OfType<MenuItem>())
        {
            item.IsVisible = (item.Tag as string) switch
            {
                "open" => isResult || isSavedSearch,
                "show" => isResult && !node!.Result!.IsFolder,
                "copy" or "versions" or "group" => isResult,
                "remove" => isResult || isSavedSearch,
                "delete-group" => isGroup,
                _ => true,
            };
        }
        foreach (var separator in menu.Items.OfType<Separator>())
        {
            separator.IsVisible = isResult;
        }
    }

    private async void ResultOpen_Click(object? sender, RoutedEventArgs e)
    {
        if (ContextResult() is { } result)
        {
            await _viewModel.OpenSearchResultAsync(result);
        }
    }

    private async void ResultShow_Click(object? sender, RoutedEventArgs e)
    {
        if (ContextResult() is { } result)
        {
            await _viewModel.ShowSearchResultAsync(result);
        }
    }

    private async void ResultCopyPath_Click(object? sender, RoutedEventArgs e)
    {
        if (ContextResult() is { } result)
        {
            await CopyPathAsync(result);
        }
    }

    private void ResultProperties_Click(object? sender, RoutedEventArgs e)
    {
        if (ContextResult() is { } result)
        {
            _viewModel.ShowSearchResultProperties(result);
        }
    }

    private async void ResultVersionHistory_Click(object? sender, RoutedEventArgs e) =>
        await OpenVersionHistoryAsync(ContextResult(), openedFromExplorer: false);

    private async void ResultFavorite_Click(object? sender, RoutedEventArgs e)
    {
        if (_contextResults.Count > 0)
        {
            await _viewModel.SetSearchResultsFavoriteAsync(_contextResults, _contextResults.Any(result => !result.IsFavorite));
        }
    }

    private async void ResultAddToGroup_Click(object? sender, RoutedEventArgs e)
    {
        if (_contextResults.Count > 0)
        {
            await EditFavoriteGroupsAsync(_contextResults);
        }
    }

    private async void FavoriteOpen_Click(object? sender, RoutedEventArgs e) => await _viewModel.OpenFavoriteNodeAsync(_viewModel.SelectedFavoriteNode);
    private async void FavoriteShow_Click(object? sender, RoutedEventArgs e) => await _viewModel.ShowFavoriteNodeAsync(_viewModel.SelectedFavoriteNode);

    private async void FavoriteCopyPath_Click(object? sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedFavoriteNode?.Result is { } result)
        {
            await CopyPathAsync(result);
        }
    }

    private async void FavoriteAddToGroup_Click(object? sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedFavoriteNode?.Result is { } result)
        {
            await EditFavoriteGroupsAsync([result]);
        }
    }

    private async void FavoriteVersionHistory_Click(object? sender, RoutedEventArgs e) =>
        await OpenVersionHistoryAsync(_viewModel.SelectedFavoriteNode?.Result, openedFromExplorer: false);

    private async void VersionHistory_Click(object? sender, RoutedEventArgs e)
    {
        var result = _viewModel.IsNavigationVisible
            ? _viewModel.SelectedBrowserItemAsResult()
            : _viewModel.SelectedSearchTab?.SelectedResult ?? _viewModel.SelectedFavoriteNode?.Result;
        await OpenVersionHistoryAsync(result, openedFromExplorer: _viewModel.IsNavigationVisible);
    }

    private async Task OpenVersionHistoryAsync(SearchResult? result, bool openedFromExplorer)
    {
        if (result == null)
        {
            _viewModel.StatusMessage("Select a file or folder to view its earlier versions.");
            return;
        }

        var path = _viewModel.ResolveWindowsPath(result);
        if (string.IsNullOrWhiteSpace(path))
        {
            _viewModel.StatusMessage("QSurfer could not resolve a Windows path for version history.");
            return;
        }

        await new VersionHistoryWindow(path, result.IsFolder, _viewModel.Config, openedFromExplorer).ShowDialog(this);
    }

    private async void FavoriteRemove_Click(object? sender, RoutedEventArgs e) => await _viewModel.RemoveFavoriteNodeAsync(_viewModel.SelectedFavoriteNode);
    private async void FavoriteDeleteGroup_Click(object? sender, RoutedEventArgs e) => await _viewModel.RemoveFavoriteGroupAsync(_viewModel.SelectedFavoriteNode);

    private async Task EditFavoriteGroupsAsync(IReadOnlyList<SearchResult> results)
    {
        if (results.Count == 0)
        {
            return;
        }
        var groups = await _viewModel.GetFavoriteGroupDataAsync(results[0]);
        var picker = new FavoriteGroupsWindow(groups.Groups, groups.SelectedGroups);
        await picker.ShowDialog(this);
        if (picker.Saved)
        {
            await _viewModel.SaveFavoriteGroupsAsync(results, picker.SelectedGroups);
        }
    }

    private SearchResult? ContextResult() => _contextResults.FirstOrDefault() ?? _viewModel.SelectedSearchTab?.SelectedResult;

    private async Task CopyPathAsync(SearchResult result)
    {
        var path = _viewModel.ResolveWindowsPath(result);
        var clipboard = GetTopLevel(this)?.Clipboard;
        if (clipboard == null || string.IsNullOrWhiteSpace(path))
        {
            _viewModel.StatusMessage("QSurfer could not copy a path for that item.");
            return;
        }

        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                await clipboard.SetTextAsync(path);
                _viewModel.StatusMessage("Copied full path");
                return;
            }
            catch when (attempt < 2)
            {
                await Task.Delay(80);
            }
            catch (Exception ex)
            {
                AppLogger.Error("clipboard", ex, "copy path failed");
            }
        }

        _viewModel.StatusMessage("Windows could not open the clipboard. Try again.");
    }
}
