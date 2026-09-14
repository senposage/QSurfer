using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using System.Globalization;
using System.Diagnostics;
using System.Runtime.InteropServices;
using QSurfer.Core.Models;
using QSurfer.Core.Services;
using QSurfer.Avalonia.Services;
using QSurfer.Avalonia.ViewModels;

namespace QSurfer.Avalonia;

public sealed partial class MainWindow : Window
{
    private const int PreviewSelectionDelayMilliseconds = 240;
    private const int TabDragHoldDelayMilliseconds = 250;
    private const double WideSearchLayoutWidth = 1160;
    private const double WideFilterLayoutWidth = 1500;
    private readonly MainWindowViewModel _viewModel;
    private readonly bool _isDetachedWindow;
    private bool _exitRequested;
    private CancellationTokenSource? _previewCancellation;
    private ShellPreviewHost? _nativePreviewHost;
    private Bitmap? _imagePreview;
    private string? _activePreviewKey;
    private IReadOnlyList<SearchResult> _contextResults = [];
    private DataGrid? _contextResultsGrid;
    private bool _controlKeyDown;
    private bool _applyingAddressCompletion;
    private string? _acceptedAddressSuggestionText;
    private string? _dismissedInlineAddressText;
    private AddressEditState _addressEditState = AddressEditState.Empty;
    private int _addressSuggestionIndex = -1;
    private IReadOnlyList<BrowserPathSuggestion> _addressSuggestionCycle = [];
    private bool _sidebarRecentSearchPrimaryPressed;
    private bool _popupRecentSearchPrimaryPressed;
    private bool _resultContextClick;
    private bool _browserContextClick;
    private bool _favoriteContextClick;
    private GridLength _workspaceSidebarWidth = new(238, GridUnitType.Pixel);
    private GridLength _workspacePreviewWidth = new(280, GridUnitType.Pixel);
    private bool _workspaceSidebarVisible;
    private bool _workspacePreviewVisible;
    private bool _workspaceLayoutInitialized;
    private bool _whatsNewQueued;
    private double? _browserHorizontalOffset;
    private DataGrid? _browserResultsGrid;
    private ListBox? _browserResultsList;
    private SearchTabViewModel? _tabDragCandidate;
    private Point _tabDragStart;
    private DateTime _tabDragPressedAtUtc;
    private bool _tabDetachStarted;
    private Border? _searchTabDropTarget;
    private ListBox? _searchTabList;
    private Border? _tabDragGhost;
    private TextBlock? _tabDragGhostTitle;
    private DispatcherTimer? _tabTearOffWatcher;
    private SearchTabViewModel? _watchedTearOffTab;
    private TabDragPreviewWindow? _tabTearOffPreview;
    private bool _tearOffPreviewIsOutside;
    private static TabDragSession? _activeTabDrag;
    private Popup? _addressSuggestionsPopup;
    private ScrollViewer? _addressSuggestionsScrollViewer;

    public MainWindow() : this(new MainWindowViewModel(), isDetachedWindow: false)
    {
    }

    internal MainWindow(MainWindowViewModel viewModel, bool isDetachedWindow = true, DetachedWindowLayout? inheritedLayout = null)
    {
        _viewModel = viewModel;
        _isDetachedWindow = isDetachedWindow;
        InitializeComponent();
        var addressTextBox = this.FindControl<TextBox>("AddressTextBox");
        var addressSuggestionsPopup = this.FindControl<Popup>("AddressSuggestionsPopup");
        _addressSuggestionsPopup = addressSuggestionsPopup;
        _addressSuggestionsScrollViewer = addressSuggestionsPopup?.Child?.FindControl<ScrollViewer>("AddressSuggestionsScrollViewer");
        if (addressTextBox != null && addressSuggestionsPopup != null)
        {
            addressSuggestionsPopup.PlacementTarget = addressTextBox;
        }
        _searchTabDropTarget = this.FindControl<Border>("SearchTabDropTarget");
        _searchTabList = this.FindControl<ListBox>("SearchTabList");
        _tabDragGhost = this.FindControl<Border>("TabDragGhost");
        _tabDragGhostTitle = this.FindControl<TextBlock>("TabDragGhostTitle");
        foreach (var dropTarget in new Control?[] { _searchTabDropTarget, _searchTabList }.OfType<Control>())
        {
            DragDrop.SetAllowDrop(dropTarget, true);
            DragDrop.AddDragOverHandler(dropTarget, SearchTabDragOver);
            DragDrop.AddDragLeaveHandler(dropTarget, SearchTabDragLeave);
            DragDrop.AddDropHandler(dropTarget, SearchTabDrop);
        }
        DataContext = _viewModel;
        _viewModel.IsFavoritesVisible = inheritedLayout?.FavoritesVisible ?? _viewModel.IsFavoritesVisible;
        _viewModel.IsPreviewVisible = inheritedLayout?.PreviewVisible ?? _viewModel.IsPreviewVisible;
        _workspaceSidebarWidth = new GridLength(Math.Clamp(inheritedLayout?.FavoritesPaneWidth ?? _viewModel.Config.Behavior.FavoritesPaneWidth, 120, 900), GridUnitType.Pixel);
        _workspacePreviewWidth = new GridLength(Math.Clamp(inheritedLayout?.PreviewPaneWidth ?? _viewModel.Config.Behavior.PreviewPaneWidth, 220, 900), GridUnitType.Pixel);
        ApplyWindowBehavior();
        KeyDown += MainWindow_KeyDown;
        KeyUp += (_, args) => _controlKeyDown = args.KeyModifiers.HasFlag(KeyModifiers.Control);
        _viewModel.PropertyChanged += ViewModel_PropertyChanged;
        Opened += (_, _) =>
        {
            ApplyResponsiveCommandLayout();
            ApplyWorkspaceLayout();
            ApplyDetailColumnVisibility();
            ApplyWindowChrome();
            _ = RequestPreviewForInheritedSelectionAsync();
            if (!_isDetachedWindow)
            {
                _ = _viewModel.ReloadPinnedSearchesAsync();
            }
        };
        SizeChanged += (_, _) => ApplyResponsiveCommandLayout();
        Activated += (_, _) =>
        {
            ApplyWindowChrome();
            QueueWhatsNew();
        };
        Closed += (_, _) =>
        {
            StopTabTearOffWatcher();
            CloseTabTearOffPreview();
            SaveWorkspacePreferences(captureDividerSizes: false);
            _viewModel.PropertyChanged -= ViewModel_PropertyChanged;
            ClearNativePreview();
            _viewModel.Dispose();
        };
        Closing += MainWindow_Closing;
        PropertyChanged += MainWindow_PropertyChanged;
    }

    internal AppConfig Config => _viewModel.Config;

    private void QueueWhatsNew()
    {
        if (_whatsNewQueued)
        {
            return;
        }

        _whatsNewQueued = true;
        Dispatcher.UIThread.Post(async () => await ShowWhatsNewIfNeededAsync(), DispatcherPriority.Background);
    }

    private async Task ShowWhatsNewIfNeededAsync()
    {
        if (_isDetachedWindow)
        {
            return;
        }

        var version = typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "1.0";
        if (string.Equals(_viewModel.Config.Behavior.LastSeenFirstRunGuideVersion, version, StringComparison.Ordinal))
        {
            return;
        }

        var dialog = new WhatsNewWindow(version);
        dialog.Opened += (_, _) => Dispatcher.UIThread.Post(dialog.Activate, DispatcherPriority.Background);
        dialog.Closed += (_, _) =>
        {
            _viewModel.Config.Behavior.LastSeenFirstRunGuideVersion = version;
            ConfigStore.Save(_viewModel.Config);
        };

        // The first-run guide must be easy to find, but it must never trap the
        // owner window behind a modal dialog or prevent the user moving QSurfer.
        dialog.Show(this);
        await Task.CompletedTask;
    }

    internal void SetStatus(string message) => _viewModel.StatusMessage(message);

    internal void HideToTray()
    {
        if (Application.Current is App app)
        {
            app.EnsureTrayIconForBehavior();
        }
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
            FindVisualControl<Control>("BooleanFilterToggle"),
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
            FindVisualControl<Control>("SuppressFolderDatesFilter"),
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
        var booleanFilterToggle = controls[2]!;
        var typeFilterToggle = controls[3]!;
        var datePresetFilter = controls[4]!;
        var fromFilterLabel = controls[5]!;
        var dateFromFilter = controls[6]!;
        var toFilterLabel = controls[7]!;
        var dateToFilter = controls[8]!;
        var clearFiltersButton = controls[9]!;
        var scopeFilterLabel = controls[10]!;
        var scopeFilter = controls[11]!;
        var arrangeFilterLabel = controls[12]!;
        var arrangeFilter = controls[13]!;
        var suppressFolderDatesFilter = controls[14]!;
        var viewFilterLabel = controls[15]!;
        var viewFilter = controls[16]!;
        var loadMoreButton = controls[17]!;
        var scopeIndicator = FindVisualControl<Border>("ScopeIndicator");

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
            SetGridRows(filterControlsGrid, GridLength.Auto, GridLength.Auto);
            SetGridColumns(filterControlsGrid, Enumerable.Repeat(GridLength.Auto, 18).ToArray());
            filterControlsGrid.RowSpacing = 0;
            typeFilterToggle.Width = 152;
            datePresetFilter.Width = 130;
            dateFromFilter.Width = 132;
            dateToFilter.Width = 132;

            SetGridPosition(exactMatchFilter, 0, 0);
            SetGridPosition(searchContentsFilter, 0, 1);
            SetGridPosition(booleanFilterToggle, 0, 2);
            SetGridPosition(typeFilterToggle, 0, 3);
            SetGridPosition(datePresetFilter, 0, 4);
            SetGridPosition(fromFilterLabel, 0, 5);
            SetGridPosition(dateFromFilter, 0, 6);
            SetGridPosition(toFilterLabel, 0, 7);
            SetGridPosition(dateToFilter, 0, 8);
            SetGridPosition(clearFiltersButton, 0, 9);
            SetGridPosition(scopeFilterLabel, 0, 10);
            SetGridPosition(scopeFilter, 0, 11);
            SetGridPosition(arrangeFilterLabel, 0, 12);
            SetGridPosition(arrangeFilter, 0, 13);
            SetGridPosition(suppressFolderDatesFilter, 0, 14);
            SetGridPosition(viewFilterLabel, 0, 15);
            SetGridPosition(viewFilter, 0, 16);
            SetGridPosition(loadMoreButton, 0, 17);
            if (scopeIndicator != null)
            {
                SetGridPosition(scopeIndicator, 1, 0);
                Grid.SetColumnSpan(scopeIndicator, 18);
            }
            return;
        }

        SetGridRows(filterControlsGrid, GridLength.Auto, GridLength.Auto, GridLength.Auto);
        SetGridColumns(filterControlsGrid, Enumerable.Repeat(GridLength.Auto, 10).ToArray());
        filterControlsGrid.RowSpacing = 8;
        typeFilterToggle.Width = 140;
        datePresetFilter.Width = 116;
        dateFromFilter.Width = 116;
        dateToFilter.Width = 116;

        SetGridPosition(exactMatchFilter, 0, 0);
        SetGridPosition(searchContentsFilter, 0, 1);
        SetGridPosition(booleanFilterToggle, 0, 2);
        SetGridPosition(typeFilterToggle, 0, 3);
        SetGridPosition(datePresetFilter, 0, 4);
        SetGridPosition(fromFilterLabel, 0, 5);
        SetGridPosition(dateFromFilter, 0, 6);
        SetGridPosition(toFilterLabel, 0, 7);
        SetGridPosition(dateToFilter, 0, 8);
        SetGridPosition(clearFiltersButton, 0, 9);
        SetGridPosition(scopeFilterLabel, 1, 0);
        SetGridPosition(scopeFilter, 1, 1);
        SetGridPosition(arrangeFilterLabel, 1, 2);
        SetGridPosition(arrangeFilter, 1, 3);
        SetGridPosition(suppressFolderDatesFilter, 1, 4);
        SetGridPosition(viewFilterLabel, 1, 5);
        SetGridPosition(viewFilter, 1, 6);
        SetGridPosition(loadMoreButton, 1, 7);
        if (scopeIndicator != null)
        {
            SetGridPosition(scopeIndicator, 2, 0);
            Grid.SetColumnSpan(scopeIndicator, 10);
        }
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
        _viewModel.DismissAddressSuggestions();
        _viewModel.ReturnToSearchResults();
        this.GetVisualDescendants().OfType<TextBox>()
            .FirstOrDefault(textBox => textBox.Classes.Contains("search-box"))
            ?.Focus();
    }

    private void SearchTabGotFocus(object? sender, GotFocusEventArgs e)
    {
        _viewModel.DismissAddressSuggestions();
        _viewModel.ReturnToSearchResults();
    }

    private void BrowserLocationGotFocus(object? sender, GotFocusEventArgs e)
    {
        _viewModel.EnterBrowseMode();
        if (sender is TextBox box)
        {
            _addressEditState = AddressEditState.From(box.Text ?? "", box.SelectionStart, box.SelectionEnd);
            if (!string.IsNullOrWhiteSpace(box.Text) && !IsWindowsDriveRoot(box.Text))
            {
                _ = _viewModel.RefreshAddressSuggestionsAsync(box.Text);
            }
            else
            {
                ClearAddressSuggestionState();
            }
        }
    }

    private void BrowserLocationLostFocus(object? sender, RoutedEventArgs e)
    {
        // Light dismiss closes clicks outside the popup. Keep an address session
        // alive long enough for a suggestion row to dispatch its normal Click.
    }

    private void AddressSuggestionsScrollViewerLoaded(object? sender, RoutedEventArgs e)
    {
        _addressSuggestionsScrollViewer = sender as ScrollViewer;
        AppLogger.Info("browse", $"address popup scrollbar loaded viewer={_addressSuggestionsScrollViewer?.GetHashCode()}");
    }

    private async void BrowserLocationTextChanged(object? sender, TextChangedEventArgs e)
    {
        if (sender is not TextBox box || !box.IsFocused || _applyingAddressCompletion)
        {
            return;
        }

        var typedText = box.Text ?? "";
        if (string.Equals(typedText, _acceptedAddressSuggestionText, StringComparison.Ordinal))
        {
            // Accepting a suggestion writes the resolved location back through the
            // binding. That is not a new edit and must not reopen the popup.
            _acceptedAddressSuggestionText = null;
            ClearAddressSuggestionState();
            return;
        }

        _acceptedAddressSuggestionText = null;
        var normalizedText = NormalizeDrivePathSeparators(typedText);
        if (!string.Equals(typedText, normalizedText, StringComparison.Ordinal))
        {
            _applyingAddressCompletion = true;
            try
            {
                box.Text = normalizedText;
                box.CaretIndex = Math.Min(box.CaretIndex, normalizedText.Length);
                box.SelectionStart = box.CaretIndex;
                box.SelectionEnd = box.CaretIndex;
            }
            finally
            {
                _applyingAddressCompletion = false;
            }

            return;
        }

        var isBacktracking = string.Equals(
            typedText,
            _dismissedInlineAddressText,
            StringComparison.Ordinal) || _addressEditState.IsBacktracking(
            typedText,
            box.SelectionStart,
            box.SelectionEnd);

        if (IsBareWindowsDriveDesignator(typedText))
        {
            if (!isBacktracking)
            {
                // Queue this behind pending text input so a user who already typed the
                // separator is never left with a doubled backslash.
                Dispatcher.UIThread.Post(() =>
                {
                    if (!box.IsFocused || !IsBareWindowsDriveDesignator(box.Text ?? ""))
                    {
                        return;
                    }

                    _applyingAddressCompletion = true;
                    try
                    {
                        box.Text += "\\";
                        box.CaretIndex = box.Text?.Length ?? 0;
                        box.SelectionStart = box.CaretIndex;
                        box.SelectionEnd = box.CaretIndex;
                    }
                    finally
                    {
                        _applyingAddressCompletion = false;
                    }
                }, DispatcherPriority.Input);
            }

            _addressEditState = AddressEditState.From(typedText, box.SelectionStart, box.SelectionEnd);
            ClearAddressSuggestionState();
            return;
        }

        SetAddressGhostText(box, null);
        var isDismissedInlineText = string.Equals(
            typedText,
            _dismissedInlineAddressText,
            StringComparison.Ordinal);
        isBacktracking |= isDismissedInlineText;
        if (!isDismissedInlineText)
        {
            _dismissedInlineAddressText = null;
        }
        _addressEditState = AddressEditState.From(typedText, box.SelectionStart, box.SelectionEnd);
        if (!isBacktracking)
        {
            _addressSuggestionIndex = -1;
            _addressSuggestionCycle = [];
        }
        if (string.IsNullOrWhiteSpace(typedText))
        {
            _addressEditState = AddressEditState.Empty;
            _dismissedInlineAddressText = null;
            _viewModel.RestoreSettledBrowserLocation();
            return;
        }

        if (IsWindowsDriveRoot(typedText))
        {
            ClearAddressSuggestionState();
            return;
        }

        _viewModel.BeginAddressNavigation();
        await _viewModel.RefreshAddressSuggestionsAsync(typedText);
        if (!box.IsFocused || !string.Equals(box.Text, typedText, StringComparison.Ordinal))
        {
            return;
        }

        CaptureAddressSuggestionCycle();
        if (isBacktracking || _addressSuggestionCycle.Count == 0 ||
            _addressSuggestionCycle[0] is not { } suggestion ||
            !TryCompleteCurrentAddressSegment(typedText, suggestion, out var completedText))
        {
            return;
        }

        SetAddressGhostText(box, completedText[typedText.Length..]);
        _dismissedInlineAddressText = null;
    }

    private static bool TryCompleteCurrentAddressSegment(
        string typedText,
        BrowserPathSuggestion suggestion,
        out string completedText)
    {
        completedText = "";
        var typed = typedText.Trim();
        if (string.IsNullOrWhiteSpace(typed) ||
            !suggestion.Path.StartsWith(typed, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var remaining = suggestion.Path[typed.Length..];
        if (remaining.Length == 0 || remaining.IndexOfAny(['\\', '/']) >= 0)
        {
            return false;
        }

        completedText = suggestion.Path;
        return true;
    }

    private static bool IsBareWindowsDriveDesignator(string value) =>
        OperatingSystem.IsWindows() &&
        value.Length == 2 &&
        char.IsAsciiLetter(value[0]) &&
        value[1] == ':';

    private static bool IsWindowsDriveRoot(string? value) =>
        OperatingSystem.IsWindows() &&
        value is { Length: 3 } &&
        char.IsAsciiLetter(value[0]) &&
        value[1] == ':' &&
        value[2] == '\\';

    private void ClearAddressSuggestionState()
    {
        _addressSuggestionIndex = -1;
        _addressSuggestionCycle = [];
        _viewModel.DismissAddressSuggestions();
    }

    private void SuppressAcceptedAddressSuggestionRefresh(string path)
    {
        _acceptedAddressSuggestionText = path;
        ClearAddressSuggestionState();
    }

    private static string NormalizeDrivePathSeparators(string value)
    {
        if (!OperatingSystem.IsWindows() || value.Length < 4 ||
            !char.IsAsciiLetter(value[0]) || value[1] != ':' || value[2] != '\\')
        {
            return value;
        }

        return value[0..3] + value[3..].Replace("\\\\", "\\");
    }

    private readonly record struct AddressEditState(
        string Text,
        int LeftCharacterCount,
        int RightCharacterCount)
    {
        public static AddressEditState Empty { get; } = new("", 0, 0);

        public static AddressEditState From(string text, int selectionStart, int selectionEnd)
        {
            var start = Math.Clamp(Math.Min(selectionStart, selectionEnd), 0, text.Length);
            var end = Math.Clamp(Math.Max(selectionStart, selectionEnd), start, text.Length);
            return new AddressEditState(text, start, text.Length - end);
        }

        public bool IsBacktracking(string nextText, int nextSelectionStart, int nextSelectionEnd)
        {
            if (string.IsNullOrEmpty(Text))
            {
                return false;
            }

            var nextStart = Math.Clamp(Math.Min(nextSelectionStart, nextSelectionEnd), 0, nextText.Length);
            var nextEnd = Math.Clamp(Math.Max(nextSelectionStart, nextSelectionEnd), nextStart, nextText.Length);
            var nextLeft = nextStart;
            var nextRight = nextText.Length - nextEnd;
            return nextText.Length < Text.Length &&
                   (nextLeft < LeftCharacterCount || nextRight < RightCharacterCount);
        }
    }

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
        if (_resultContextClick)
        {
            return;
        }

        if (e.AddedItems.OfType<SearchResult>().LastOrDefault() is { } result)
        {
            await RequestNativePreviewAsync(result);
        }
    }

    private void ArrangeFilter_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox { DataContext: SearchTabViewModel tab } ||
            e.AddedItems.OfType<ResultSortMode>().LastOrDefault() is not { } mode)
        {
            return;
        }

        tab.SelectedSortMode = mode;
        ClearDetailsGridSorts();
        AppLogger.Info("sort", $"arrange selected tab=\"{tab.Title}\" mode=\"{mode.Key}\" visible={tab.Results.Count}");
        // Selection bindings are normally two-way, but this explicit collection reset
        // guarantees that a user choice immediately redraws the visible rows.
        tab.RefreshVisibleResults(forceRepaint: true);
    }

    private void ClearDetailsGridSorts()
    {
        var grid = FindDetailsGrid();
        if (grid == null)
        {
            return;
        }

        foreach (var column in grid.Columns)
        {
            column.ClearSort();
        }
    }

    private async void FavoriteSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_favoriteContextClick)
        {
            return;
        }

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
        if (sender is not TextBox box)
        {
            return;
        }

        if (e.Key is Key.Down or Key.Up or Key.Enter)
        {
            AppLogger.Info(
                "browse",
                $"address key key={e.Key} suggestions={_viewModel.AddressSuggestions.Count} index={_addressSuggestionIndex} focused={box.IsFocused}");
        }

        if (e.Key == Key.Right && TryAcceptAddressGhost(box))
        {
            e.Handled = true;
            return;
        }

        if (e.Key == Key.OemBackslash && box.SelectionEnd > box.SelectionStart)
        {
            _applyingAddressCompletion = true;
            try
            {
                box.Text = (box.Text ?? "") + "\\";
                box.CaretIndex = box.Text.Length;
                _addressEditState = AddressEditState.From(box.Text, box.SelectionStart, box.SelectionEnd);
                _dismissedInlineAddressText = null;
                SetAddressGhostText(box, null);
            }
            finally
            {
                _applyingAddressCompletion = false;
            }

            _viewModel.BeginAddressNavigation();
            await _viewModel.RefreshAddressSuggestionsAsync(box.Text ?? "", skipDebounce: true);
            CaptureAddressSuggestionCycle();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Tab)
        {
            if (_addressSuggestionCycle.Count == 0 &&
                (box.Text ?? "").EndsWith('\\'))
            {
                await _viewModel.RefreshAddressSuggestionsAsync(box.Text ?? "", skipDebounce: true);
                CaptureAddressSuggestionCycle();
            }

            if (_addressSuggestionCycle.Count > 0)
            {
                var direction = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? -1 : 1;
                _addressSuggestionIndex += direction;
                if (_addressSuggestionIndex < 0)
                {
                    _addressSuggestionIndex = _addressSuggestionCycle.Count - 1;
                }
                else if (_addressSuggestionIndex >= _addressSuggestionCycle.Count)
                {
                    _addressSuggestionIndex = 0;
                }

                ApplyAddressSuggestionToTextBox(
                    box,
                    _addressSuggestionCycle[_addressSuggestionIndex]);
            }

            box.Focus();
            e.Handled = true;
            return;
        }

        if ((e.Key is Key.Down or Key.Up) && _viewModel.AddressSuggestions.Count > 0)
        {
            MoveAddressSuggestionSelection(e.Key == Key.Up ? -1 : 1);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape)
        {
            _viewModel.DismissAddressSuggestions();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Enter)
        {
            // This handler awaits the confirmation dialog. Mark the key handled before
            // awaiting so Enter cannot bubble into the tab's search handler while the
            // dialog is open and change the workspace state underneath this operation.
            e.Handled = true;
            if (!await ConfirmScopeReplacementAsync(_viewModel.BrowserLocation))
            {
                return;
            }

            if (TryAcceptSelectedAddressSuggestion(box))
            {
                await _viewModel.BrowseAddressAsync(setSearchScope: true);
                return;
            }

            await _viewModel.BrowseAddressAsync(setSearchScope: true);
        }
    }

    private bool TryAcceptSelectedAddressSuggestion(TextBox box)
    {
        if (_addressSuggestionIndex < 0 || _addressSuggestionIndex >= _viewModel.AddressSuggestions.Count)
        {
            return false;
        }

        var suggestion = _viewModel.AddressSuggestions[_addressSuggestionIndex];
        SuppressAcceptedAddressSuggestionRefresh(suggestion.Path);
        _viewModel.AcceptAddressSuggestion(suggestion);
        ApplyAddressSuggestionToTextBox(box, suggestion);
        box.Focus();
        return true;
    }

    private async Task<bool> ConfirmScopeReplacementAsync(string candidateAddress)
    {
        if (!_viewModel.RequiresScopeReplacementConfirmationForAddress(candidateAddress) ||
            _viewModel.SelectedSearchTab is not { } tab)
        {
            return true;
        }

        var count = tab.ScopePaths.Count;
        var confirmation = new ConfirmationWindow(
            "Replace search folders?",
            $"This will replace the {count} selected search folders with one folder. Choose + beside Search folders to add another folder instead.",
            "Replace");
        return await confirmation.ShowDialog<bool?>(this) == true;
    }

    private void UpdateAddressSuggestionHighlight()
    {
        for (var index = 0; index < _viewModel.AddressSuggestions.Count; index++)
        {
            _viewModel.AddressSuggestions[index].IsKeyboardSelected = index == _addressSuggestionIndex;
        }

        ScrollAddressSuggestionIntoView();
    }

    private void ScrollAddressSuggestionIntoView()
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_addressSuggestionIndex < 0)
            {
                return;
            }

            if (_addressSuggestionsScrollViewer == null || _addressSuggestionsScrollViewer.Viewport.Height <= 0)
            {
                AppLogger.Info(
                    "browse",
                    $"address popup scroll skipped viewer={(_addressSuggestionsScrollViewer is null ? "missing" : "unmeasured")} index={_addressSuggestionIndex} viewport={_addressSuggestionsScrollViewer?.Viewport}");
                return;
            }

            const double rowHeight = 60;
            var selectedTop = _addressSuggestionIndex * rowHeight;
            var selectedBottom = selectedTop + rowHeight;
            var offset = _addressSuggestionsScrollViewer.Offset;
            var viewportBottom = offset.Y + _addressSuggestionsScrollViewer.Viewport.Height;
            var nextOffset = selectedTop < offset.Y
                ? selectedTop
                : selectedBottom > viewportBottom
                    ? selectedBottom - _addressSuggestionsScrollViewer.Viewport.Height
                    : offset.Y;

            AppLogger.Info(
                "browse",
                $"address popup scroll index={_addressSuggestionIndex} viewer={_addressSuggestionsScrollViewer.GetHashCode()} viewport={_addressSuggestionsScrollViewer.Viewport} extent={_addressSuggestionsScrollViewer.Extent} offsetBefore={offset} requestedY={nextOffset:F1}");

            if (Math.Abs(nextOffset - offset.Y) > 0.5)
            {
                _addressSuggestionsScrollViewer.Offset = new Vector(offset.X, Math.Max(0, nextOffset));
                AppLogger.Info("browse", $"address popup scroll applied offsetAfter={_addressSuggestionsScrollViewer.Offset}");
            }
        }, DispatcherPriority.Render);
    }

    private void MoveAddressSuggestionSelection(int direction)
    {
        if (_viewModel.AddressSuggestions.Count == 0)
        {
            return;
        }

        var nextIndex = _addressSuggestionIndex + direction;
        if (nextIndex < 0)
        {
            nextIndex = _viewModel.AddressSuggestions.Count - 1;
        }
        else if (nextIndex >= _viewModel.AddressSuggestions.Count)
        {
            nextIndex = 0;
        }

        _addressSuggestionIndex = nextIndex;
        UpdateAddressSuggestionHighlight();
    }

    private bool TryAcceptAddressGhost(TextBox box)
    {
        var ghostBox = this.FindControl<TextBlock>("AddressGhostText");
        if (ghostBox == null || string.IsNullOrEmpty(ghostBox.Text))
        {
            return false;
        }

        var ghostText = ghostBox.Text;

        _applyingAddressCompletion = true;
        try
        {
            box.Text = (box.Text ?? "") + ghostText;
            box.CaretIndex = box.Text.Length;
            box.SelectionStart = box.Text.Length;
            box.SelectionEnd = box.Text.Length;
            _addressEditState = AddressEditState.From(box.Text, box.Text.Length, box.Text.Length);
            _dismissedInlineAddressText = null;
            SetAddressGhostText(box, null);
            return true;
        }
        finally
        {
            _applyingAddressCompletion = false;
        }
    }

    private void SetAddressGhostText(TextBox box, string? ghostText)
    {
        var ghost = this.FindControl<TextBlock>("AddressGhostText");
        if (ghost == null)
        {
            return;
        }

        ghost.Text = ghostText;
        ghost.IsVisible = !string.IsNullOrEmpty(ghostText);
        if (string.IsNullOrEmpty(ghostText))
        {
            return;
        }

        var typedText = box.Text ?? "";
        var typeface = new Typeface(box.FontFamily, box.FontStyle, box.FontWeight, box.FontStretch);
        var typedLayout = new FormattedText(
            typedText,
            CultureInfo.CurrentUICulture,
            box.FlowDirection,
            typeface,
            box.FontSize,
            box.Foreground);
        ghost.Margin = new Thickness(box.Padding.Left + typedLayout.WidthIncludingTrailingWhitespace + 3, 0, 0, 0);
    }

    private void CaptureAddressSuggestionCycle() =>
        _addressSuggestionCycle = _viewModel.AddressSuggestions.ToList();

    private void ApplyAddressSuggestionToTextBox(TextBox box, BrowserPathSuggestion suggestion)
    {
        _applyingAddressCompletion = true;
        try
        {
            box.Text = suggestion.Path;
            box.CaretIndex = suggestion.Path.Length;
            box.SelectionStart = suggestion.Path.Length;
            box.SelectionEnd = suggestion.Path.Length;
            _addressEditState = AddressEditState.From(suggestion.Path, box.SelectionStart, box.SelectionEnd);
            _dismissedInlineAddressText = null;
            SetAddressGhostText(box, null);
        }
        finally
        {
            _applyingAddressCompletion = false;
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
        if (_browserContextClick)
        {
            return;
        }

        var item = e.AddedItems.OfType<BrowserItem>().LastOrDefault();
        if (item == null)
        {
            // DataGrid can raise an empty selection event while it is reconciling a
            // current row. Do not cancel the preview that was just requested for it.
            if (_viewModel.SelectedBrowserItem == null)
            {
                ClearNativePreview();
            }
            return;
        }

        _viewModel.SelectedBrowserItem = item;
        await RequestNativePreviewForSelectedBrowserItemAsync();
    }

    private void BrowserItemPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _browserResultsGrid = sender as DataGrid;
        _browserResultsList = sender as ListBox;
        _browserHorizontalOffset = BrowserScrollViewer(sender as Control)?.Offset.X;
        _browserContextClick = e.GetCurrentPoint(sender as Visual).Properties.IsRightButtonPressed;
        if (!_browserContextClick)
        {
            return;
        }

        if (e.Source is Control { DataContext: BrowserItem item })
        {
            if (_browserResultsGrid?.SelectedItems is { } gridItems && !gridItems.Contains(item))
            {
                gridItems.Clear();
                gridItems.Add(item);
            }
            else if (_browserResultsList?.SelectedItems is { } listItems && !listItems.Contains(item))
            {
                listItems.Clear();
                listItems.Add(item);
            }
            _viewModel.SelectedBrowserItem = item;
            return;
        }

        _browserResultsGrid?.SelectedItems?.Clear();
        _browserResultsList?.SelectedItems?.Clear();
        _viewModel.SelectedBrowserItem = null;
        ClearNativePreview();
    }

    private void BrowserItemPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        _browserContextClick = false;
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

    private void BrowserDetailsGrid_LayoutUpdated(object? sender, EventArgs e)
    {
        if (sender is not DataGrid grid || !grid.IsVisible || grid.Columns.Count < 2)
        {
            return;
        }

        var guides = FindWorkspaceControl<Canvas>("BrowserColumnGuides");
        if (guides == null)
        {
            return;
        }

        // Canvas does not size to its children. Match the grid so guides span its empty area too.
        guides.Width = grid.Bounds.Width;
        guides.Height = grid.Bounds.Height;

        var requiredGuides = grid.Columns.Count - 1;
        while (guides.Children.Count < requiredGuides)
        {
            var guide = new Border
            {
                Width = 1,
                Background = new SolidColorBrush(Color.Parse("#FF454C57")),
                IsHitTestVisible = false
            };
            if (TryGetResource("QSurfer.SurfaceBorder", ActualThemeVariant, out var resource) && resource is IBrush brush)
            {
                guide.Background = brush;
            }
            guides.Children.Add(guide);
        }
        while (guides.Children.Count > requiredGuides)
        {
            guides.Children.RemoveAt(guides.Children.Count - 1);
        }

        var left = 0d;
        for (var index = 0; index < requiredGuides; index++)
        {
            left += grid.Columns[index].ActualWidth;
            var guide = guides.Children[index];
            guide.Height = grid.Bounds.Height;
            Canvas.SetLeft(guide, Math.Round(left - 0.5));
            Canvas.SetTop(guide, 0);
        }
    }

    private void SidebarRecentSearchPointerPressed(object? sender, PointerPressedEventArgs e) =>
        _sidebarRecentSearchPrimaryPressed = e.GetCurrentPoint(sender as Visual).Properties.IsLeftButtonPressed;

    private void PopupRecentSearchPointerPressed(object? sender, PointerPressedEventArgs e) =>
        _popupRecentSearchPrimaryPressed = e.GetCurrentPoint(sender as Visual).Properties.IsLeftButtonPressed;

    private async void PopupRecentSearchPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        var point = e.GetCurrentPoint(sender as Visual);
        if (!_popupRecentSearchPrimaryPressed ||
            point.Properties.PointerUpdateKind != PointerUpdateKind.LeftButtonReleased ||
            sender is not ListBox { SelectedItem: string query } list)
        {
            return;
        }

        _popupRecentSearchPrimaryPressed = false;
        await _viewModel.RunRecentSearchAsync(query);
        list.SelectedItem = null;
    }

    private async void SidebarRecentSearchPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        var point = e.GetCurrentPoint(sender as Visual);
        if (!_sidebarRecentSearchPrimaryPressed ||
            point.Properties.PointerUpdateKind != PointerUpdateKind.LeftButtonReleased ||
            sender is not ListBox { SelectedItem: string query } list)
        {
            return;
        }

        _sidebarRecentSearchPrimaryPressed = false;
        await _viewModel.RunRecentSearchAsync(query);
        list.SelectedItem = null;
    }

    private async void RemoveRecentSearch_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string query })
        {
            await _viewModel.RemoveRecentSearchAsync(query);
        }
    }

    private async void AddressSuggestion_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: BrowserPathSuggestion suggestion })
        {
            return;
        }

        _addressSuggestionIndex = _viewModel.AddressSuggestions.IndexOf(suggestion);
        AppLogger.Info("browse", $"address suggestion selected path=\"{suggestion.Path}\"");
        if (!await ConfirmScopeReplacementAsync(suggestion.Path))
        {
            return;
        }
        SuppressAcceptedAddressSuggestionRefresh(suggestion.Path);
        await _viewModel.SelectAddressSuggestionAsync(suggestion);
        this.FindControl<TextBox>("AddressTextBox")?.Focus();
    }


    private void ClearFolderScope_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: SearchTabViewModel tab })
        {
            Dispatcher.UIThread.Post(tab.ClearScopeFolder, DispatcherPriority.Background);
        }
    }

    private void ArmFolderScopeAppend_Click(object? sender, RoutedEventArgs e) =>
        Dispatcher.UIThread.Post(_viewModel.BeginFolderScopeAppend, DispatcherPriority.Background);

    private void RemoveFolderScopeEntry_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { Tag: ScopeDisplayEntry entry })
        {
            var tab = _viewModel.SelectedSearchTab;
            if (tab != null)
            {
                Dispatcher.UIThread.Post(() => tab.RemoveScopeEntry(entry), DispatcherPriority.Background);
            }
        }
    }

    private void ToggleScopeEntryInclusion_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is ToggleSwitch { Tag: ScopeDisplayEntry entry, IsChecked: bool included })
        {
            var tab = _viewModel.SelectedSearchTab;
            if (tab != null)
            {
                Dispatcher.UIThread.Post(() => tab.SetScopeEntryIncluded(entry, included), DispatcherPriority.Background);
            }
        }
    }

    private async void BrowserOpen_Click(object? sender, RoutedEventArgs e) => await _viewModel.OpenSelectedBrowserItemAsync();

    private async void BrowserOpenAllInNewTabs_Click(object? sender, RoutedEventArgs e) =>
        await _viewModel.OpenBrowserItemsInNewTabsAsync(SelectedBrowserItems());

    private async void BrowserShowAll_Click(object? sender, RoutedEventArgs e) =>
        await _viewModel.ShowBrowserItemsInFileManagerAsync(SelectedBrowserItems());

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

    private async void EmptyRecycleBin_Click(object? sender, RoutedEventArgs e)
    {
        var confirm = new ConfirmationWindow(
            "Empty Recycle Bin",
            "Permanently delete all items in this local Recycle Bin? This cannot be undone.",
            "Empty Recycle Bin");
        if (await confirm.ShowDialog<bool?>(this) == true)
        {
            await _viewModel.EmptyLocalRecycleBinAsync();
        }
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
            if (item.Tag as string == "versions")
            {
                item.IsVisible = selectionCount == 1 && _viewModel.SupportsVersionHistory(_viewModel.SelectedBrowserItemAsResult());
                continue;
            }
            item.IsEnabled = (item.Tag as string) switch
            {
                "new-tabs" or "show-all" => selectionCount > 1,
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
        if (_browserResultsGrid != null)
        {
            return (_browserResultsGrid.SelectedItems ?? Array.Empty<object>())
                .OfType<BrowserItem>()
                .DistinctBy(item => item.FullPath, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        if (_browserResultsList != null)
        {
            return (_browserResultsList.SelectedItems ?? Array.Empty<object>())
                .OfType<BrowserItem>()
                .DistinctBy(item => item.FullPath, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        return _viewModel.SelectedBrowserItem is { } item ? [item] : [];
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
        NavigationTreeNode? node = null;
        try
        {
            if (IsTreeExpander(e.Source) || NavigationNodeFromSource(e.Source) is not { } selectedNode)
            {
                return;
            }

            node = selectedNode;

            if (MatchesNavigationScopeGesture(e.KeyModifiers, _viewModel.Config.Behavior.NavigationScopeExcludeGesture))
            {
                await _viewModel.ToggleNavigationSearchExclusionAsync(node);
            }
            else if (MatchesNavigationScopeGesture(e.KeyModifiers, _viewModel.Config.Behavior.NavigationScopeIncludeGesture))
            {
                await _viewModel.ToggleNavigationSearchScopeAsync(node);
            }
            else
            {
                await _viewModel.NavigateToFolderAsync(node);
            }
        }
        catch (Exception ex)
        {
            // Async event handlers must not allow a transient mapped-drive failure to
            // reach Avalonia's dispatcher as an unhandled application exception.
            AppLogger.Error("browse", ex, $"navigation click failed folder=\"{node?.FullPath ?? ""}\"");
        }
    }

    internal static bool MatchesNavigationScopeGesture(KeyModifiers modifiers, string configuredGesture)
    {
        if (string.IsNullOrWhiteSpace(configuredGesture))
        {
            return false;
        }

        var expected = KeyModifiers.None;
        foreach (var token in configuredGesture.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            expected |= token.ToUpperInvariant() switch
            {
                "SHIFT" => KeyModifiers.Shift,
                "CTRL" or "CONTROL" => KeyModifiers.Control,
                "ALT" => KeyModifiers.Alt,
                _ => KeyModifiers.None,
            };
        }

        const KeyModifiers relevantModifiers = KeyModifiers.Shift | KeyModifiers.Control | KeyModifiers.Alt;
        return (modifiers & relevantModifiers) == expected;
    }

    private async void NavigationOpen_Click(object? sender, RoutedEventArgs e)
    {
        if (NavigationNodeFromMenu(sender) is { } node) await _viewModel.NavigateToFolderAsync(node);
    }

    private async void NavigationOpenInNewTab_Click(object? sender, RoutedEventArgs e)
    {
        if (NavigationNodeFromMenu(sender) is not { } node) return;
        await _viewModel.OpenBrowserItemsInNewTabsAsync([CreateNavigationBrowserItem(node)]);
    }

    private async void NavigationSearchHere_Click(object? sender, RoutedEventArgs e)
    {
        if (NavigationNodeFromMenu(sender) is { } node) await _viewModel.ToggleNavigationSearchScopeAsync(node);
    }

    private async void NavigationShow_Click(object? sender, RoutedEventArgs e)
    {
        if (NavigationNodeFromMenu(sender) is not { } node) return;
        await _viewModel.ShowBrowserItemsInFileManagerAsync([CreateNavigationBrowserItem(node)]);
    }

    private async void NavigationOpenSelectedInNewTabs_Click(object? sender, RoutedEventArgs e) =>
        await _viewModel.OpenBrowserItemsInNewTabsAsync(_viewModel.SelectedNavigationScopeItems());

    private async void NavigationShowSelected_Click(object? sender, RoutedEventArgs e) =>
        await _viewModel.ShowBrowserItemsInFileManagerAsync(_viewModel.SelectedNavigationScopeItems());

    private async void NavigationCopyPath_Click(object? sender, RoutedEventArgs e)
    {
        if (NavigationNodeFromMenu(sender) is not { FullPath: { Length: > 0 } } node) return;
        var clipboard = GetTopLevel(this)?.Clipboard;
        if (clipboard != null) await clipboard.SetTextAsync(node.FullPath);
    }

    private static NavigationTreeNode? NavigationNodeFromMenu(object? sender) =>
        sender is MenuItem { CommandParameter: NavigationTreeNode node } ? node : null;

    private static BrowserItem CreateNavigationBrowserItem(NavigationTreeNode node) =>
        new(node.Name, node.FullPath, true, 0, DateTime.MinValue);

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
        Func<string, HotkeyRegistrationResult>? configureHotkey = OperatingSystem.IsWindows() && app != null
            ? app.ConfigureGlobalHotkey
            : null;
        var settings = new SettingsWindow(_viewModel.Config, configureHotkey);
        await settings.ShowDialog(this);
        if (settings.Saved)
        {
            _viewModel.ReloadConnection();
            ApplyWindowBehavior();
            ApplyWorkspaceLayout();
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
        var previewKey = GetPreviewKey(result);
        if (string.Equals(_activePreviewKey, previewKey, StringComparison.OrdinalIgnoreCase) &&
            (_previewCancellation != null || _nativePreviewHost != null || _imagePreview != null))
        {
            return;
        }

        ClearNativePreview();
        if (!_viewModel.IsPreviewVisible || result.IsFolder || ShellPreviewHost.IsVideoFile(result.Extension))
        {
            return;
        }

        var cancellation = new CancellationTokenSource();
        var token = cancellation.Token;
        _previewCancellation = cancellation;
        _activePreviewKey = previewKey;
        var stopwatch = Stopwatch.StartNew();
        AppLogger.Info("preview", $"request started result=\"{result.Name}\"");
        // A mapped-drive probe can block briefly when SMB is busy. Keep that work and
        // the handler lookup off the UI thread so search paint batches stay responsive.
        try
        {
            var path = await Task.Run(() => _viewModel.ResolveWindowsPath(result), token);
            if (string.IsNullOrWhiteSpace(path))
            {
                AppLogger.Warn("preview", $"request could not resolve path result=\"{result.Name}\" elapsed={stopwatch.ElapsedMilliseconds}ms");
                return;
            }

            var previewPath = await PreviewFileStager.StageIfRemoteAsync(path, token);

            await Task.Delay(PreviewSelectionDelayMilliseconds, token);
            if (token.IsCancellationRequested || !IsCurrentPreviewResult(result))
            {
                return;
            }

            if (IsImageFile(result.Extension))
            {
                var imagePreview = await Task.Run(() => new Bitmap(previewPath), token);
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (token.IsCancellationRequested || !IsCurrentPreviewResult(result))
                    {
                        imagePreview.Dispose();
                        return;
                    }

                    _imagePreview = imagePreview;
                    _viewModel.SetNativePreviewHost(new Image
                    {
                        Source = imagePreview,
                        Stretch = Stretch.Uniform,
                    });
                    AppLogger.Info("preview", $"image preview attached result=\"{result.Name}\" elapsed={stopwatch.ElapsedMilliseconds}ms");
                }, DispatcherPriority.Input);
                return;
            }

            if (OperatingSystem.IsLinux() && LinuxDocumentPreviewService.Supports(result.Extension))
            {
                var documentPreview = await LinuxDocumentPreviewService.TryRenderAsync(previewPath, result.Extension, token);
                if (documentPreview == null)
                {
                    return;
                }

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (token.IsCancellationRequested || !IsCurrentPreviewResult(result))
                    {
                        documentPreview.Dispose();
                        return;
                    }

                    _imagePreview = documentPreview;
                    _viewModel.SetNativePreviewHost(new Border
                    {
                        Background = Brushes.White,
                        Child = new Image
                        {
                            Source = documentPreview,
                            Stretch = Stretch.Uniform,
                        },
                    });
                    AppLogger.Info("preview", $"Linux document preview attached result=\"{result.Name}\" elapsed={stopwatch.ElapsedMilliseconds}ms");
                }, DispatcherPriority.Input);
                return;
            }

            var handlerClassId = await Task.Run(() => ShellPreviewHost.TryResolveHandlerClassId(previewPath), token);
            if (handlerClassId is not { } value)
            {
                return;
            }

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (token.IsCancellationRequested || !IsCurrentPreviewResult(result))
                {
                    return;
                }

                var host = ShellPreviewHost.Create(previewPath, value);
                host.PreviewFailed += NativePreviewFailed;
                _nativePreviewHost = host;
                _viewModel.SetNativePreviewHost(host);
                AppLogger.Info("preview", $"native preview attached result=\"{result.Name}\" elapsed={stopwatch.ElapsedMilliseconds}ms");
            }, DispatcherPriority.Input);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            AppLogger.Error("preview", ex, $"preview preparation failed result=\"{result.Name}\"");
        }
        finally
        {
            if (ReferenceEquals(_previewCancellation, cancellation))
            {
                _previewCancellation = null;
            }
            cancellation.Dispose();
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

    private static bool IsImageFile(string extension) => ImageExtensions.Contains(extension.Trim().TrimStart('.'));

    private static string GetPreviewKey(SearchResult result) =>
        string.IsNullOrWhiteSpace(result.WindowsPath)
            ? result.DisplayPath
            : result.WindowsPath;

    private void ClearNativePreview()
    {
        _previewCancellation?.Cancel();
        _previewCancellation = null;
        _activePreviewKey = null;
        var host = _nativePreviewHost;
        _nativePreviewHost = null;
        _viewModel.SetNativePreviewHost(null);
        var imagePreview = _imagePreview;
        _imagePreview = null;
        imagePreview?.Dispose();
        if (host != null)
        {
            host.PreviewFailed -= NativePreviewFailed;
            host.Dispose();
        }
    }

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "bmp", "gif", "jpeg", "jpg", "png", "tif", "tiff", "webp",
    };

    private void NativePreviewFailed(object? sender, PreviewFailureEventArgs e)
    {
        if (!ReferenceEquals(sender, _nativePreviewHost))
        {
            return;
        }
        ClearNativePreview();
        _viewModel.StatusMessage(e.Message);
    }

    private async Task RequestPreviewForInheritedSelectionAsync()
    {
        if (!_viewModel.IsPreviewVisible)
        {
            return;
        }

        if (_viewModel.SelectedSearchTab?.SelectedResult is { } result)
        {
            await RequestNativePreviewAsync(result);
            return;
        }

        await RequestNativePreviewForSelectedBrowserItemAsync();
    }

    private void MainWindow_Closing(object? sender, WindowClosingEventArgs e)
    {
        if (!_isDetachedWindow && !_exitRequested && _viewModel.Config.Behavior.ExitToTray)
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
        if (e.PropertyName is nameof(MainWindowViewModel.IsFavoritesVisible) or nameof(MainWindowViewModel.IsNavigationPaneVisible) or nameof(MainWindowViewModel.IsPreviewVisible) or nameof(MainWindowViewModel.HasRecentSearches))
        {
            Dispatcher.UIThread.Post(ApplyWorkspaceLayout, DispatcherPriority.Render);
            if (e.PropertyName == nameof(MainWindowViewModel.IsPreviewVisible))
            {
                if (_viewModel.IsPreviewVisible)
                {
                    _ = RequestPreviewForInheritedSelectionAsync();
                }
                else
                {
                    ClearNativePreview();
                }
            }
        }
        else if (e.PropertyName == nameof(MainWindowViewModel.IsNavigationVisible))
        {
            Dispatcher.UIThread.Post(ApplyResponsiveCommandLayout, DispatcherPriority.Loaded);
        }
    }

    private void ApplyWorkspaceLayout()
    {
        var state = GetWorkspaceLayoutState();
        var workspace = FindWorkspaceControl<Grid>("WorkspaceGrid");
        var sidebar = FindWorkspaceControl<Border>("WorkspaceSidebar");
        var favorites = FindWorkspaceControl<Grid>("WorkspaceFavorites");
        var recentSearches = FindWorkspaceControl<Grid>("WorkspaceRecentSearches");
        var navigation = FindWorkspaceControl<Grid>("WorkspaceNavigation");
        var sidebarGrid = FindWorkspaceControl<Grid>("WorkspaceSidebarGrid");
        var favoritesSplitter = FindWorkspaceControl<GridSplitter>("WorkspaceFavoritesSplitter");
        var recentsSplitter = FindWorkspaceControl<GridSplitter>("WorkspaceRecentsSplitter");
        var leftSplitter = FindWorkspaceControl<GridSplitter>("WorkspaceLeftSplitter");
        var previewSplitter = FindWorkspaceControl<GridSplitter>("WorkspacePreviewSplitter");
        var preview = FindWorkspaceControl<Border>("WorkspacePreview");
        if (workspace?.ColumnDefinitions.Count != 5 || sidebar == null || favorites == null || recentSearches == null || navigation == null ||
            sidebarGrid?.RowDefinitions.Count != 5 || favoritesSplitter == null || recentsSplitter == null || leftSplitter == null ||
            previewSplitter == null || preview == null)
        {
            return;
        }

        sidebar.IsVisible = state.ShowSidebar;
        leftSplitter.IsVisible = state.ShowSidebar;
        preview.IsVisible = state.ShowPreview;
        previewSplitter.IsVisible = state.ShowPreview;
        ApplyWorkspaceColumn(workspace.ColumnDefinitions[0], workspace.ColumnDefinitions[1], state.ShowSidebar, ref _workspaceSidebarWidth, ref _workspaceSidebarVisible, !_workspaceLayoutInitialized);
        ApplyWorkspaceColumn(workspace.ColumnDefinitions[4], workspace.ColumnDefinitions[3], state.ShowPreview, ref _workspacePreviewWidth, ref _workspacePreviewVisible, !_workspaceLayoutInitialized);

        var rows = sidebarGrid.RowDefinitions;
        var showRecents = state.ShowRecentSearches;
        favorites.IsVisible = state.ShowFavorites;
        recentSearches.IsVisible = showRecents;
        navigation.IsVisible = state.ShowNavigation;
        Grid.SetRow(navigation, 4);

        SetSidebarRow(rows[0], state.ShowFavorites, _viewModel.Config.Behavior.FavoritesSectionHeight, 64, fillAvailable: !showRecents && !state.ShowNavigation);
        SetSidebarRow(rows[2], showRecents, _viewModel.Config.Behavior.RecentSearchesSectionHeight, 52, fillAvailable: !state.ShowFavorites && !state.ShowNavigation);
        SetSidebarRow(rows[4], state.ShowNavigation, 0, 96, fillAvailable: true);

        if (state.ShowFavorites && showRecents)
        {
            rows[1].Height = new GridLength(6, GridUnitType.Pixel);
            favoritesSplitter.IsVisible = true;
        }
        else if (state.ShowFavorites && state.ShowNavigation && !showRecents)
        {
            rows[1].Height = new GridLength(6, GridUnitType.Pixel);
            favoritesSplitter.IsVisible = true;
            Grid.SetRow(navigation, 2);
            SetSidebarRow(rows[2], true, 0, 96, fillAvailable: true);
            SetSidebarRow(rows[4], false, 0, 0, fillAvailable: false);
        }
        else
        {
            rows[1].Height = new GridLength(0, GridUnitType.Pixel);
            favoritesSplitter.IsVisible = false;
        }

        if (showRecents && state.ShowNavigation)
        {
            rows[3].Height = new GridLength(6, GridUnitType.Pixel);
            recentsSplitter.IsVisible = true;
            Grid.SetRow(navigation, 4);
        }
        else
        {
            rows[3].Height = new GridLength(0, GridUnitType.Pixel);
            recentsSplitter.IsVisible = false;
        }

        Grid.SetRow(favorites, 0);
        Grid.SetRow(recentSearches, 2);

        sidebarGrid.InvalidateMeasure();
        sidebarGrid.InvalidateArrange();
        _workspaceLayoutInitialized = true;
    }

    private static void SetSidebarRow(RowDefinition row, bool isVisible, double preferredHeight, double minHeight, bool fillAvailable)
    {
        row.MinHeight = isVisible ? minHeight : 0;
        row.Height = !isVisible
            ? new GridLength(0, GridUnitType.Pixel)
            : fillAvailable
                ? new GridLength(1, GridUnitType.Star)
                : new GridLength(preferredHeight, GridUnitType.Pixel);
    }

    private T? FindWorkspaceControl<T>(string name) where T : Control =>
        this.GetVisualDescendants().OfType<T>().FirstOrDefault(control => control.Name == name);

    private WorkspaceLayoutState GetWorkspaceLayoutState() =>
        new(
            _viewModel.IsFavoritesVisible,
            _viewModel.HasRecentSearches,
            _viewModel.IsNavigationPaneVisible,
            _viewModel.IsPreviewVisible);

    private static void ApplyWorkspaceColumn(
        ColumnDefinition contentColumn,
        ColumnDefinition splitterColumn,
        bool isVisible,
        ref GridLength preferredWidth,
        ref bool wasVisible,
        bool force)
    {
        if (isVisible && (!wasVisible || force))
        {
            contentColumn.Width = preferredWidth;
            splitterColumn.Width = new GridLength(6, GridUnitType.Pixel);
        }
        else if (!isVisible && (wasVisible || force))
        {
            contentColumn.Width = new GridLength(0, GridUnitType.Pixel);
            splitterColumn.Width = new GridLength(0, GridUnitType.Pixel);
        }

        wasVisible = isVisible;
    }

    private void SaveWorkspacePreferences(bool captureDividerSizes)
    {
        if (captureDividerSizes)
        {
            var workspace = FindWorkspaceControl<Grid>("WorkspaceGrid");
            if (workspace?.ColumnDefinitions.Count == 5)
            {
                var sidebarWidth = workspace.ColumnDefinitions[0].ActualWidth;
                var previewWidth = workspace.ColumnDefinitions[4].ActualWidth;
                if (_workspaceSidebarVisible && sidebarWidth >= 120)
                {
                    _workspaceSidebarWidth = new GridLength(sidebarWidth, GridUnitType.Pixel);
                    _viewModel.Config.Behavior.FavoritesPaneWidth = (int)Math.Round(sidebarWidth);
                }
                if (_workspacePreviewVisible && previewWidth >= 220)
                {
                    _workspacePreviewWidth = new GridLength(previewWidth, GridUnitType.Pixel);
                    _viewModel.Config.Behavior.PreviewPaneWidth = (int)Math.Round(previewWidth);
                }
            }

            var favorites = FindWorkspaceControl<Grid>("WorkspaceFavorites");
            var recentSearches = FindWorkspaceControl<Grid>("WorkspaceRecentSearches");
            if (_viewModel.IsFavoritesVisible && favorites?.Bounds.Height >= 64)
            {
                _viewModel.Config.Behavior.FavoritesSectionHeight = (int)Math.Round(favorites.Bounds.Height);
            }
            if (_viewModel.HasRecentSearches && recentSearches?.Bounds.Height >= 52)
            {
                _viewModel.Config.Behavior.RecentSearchesSectionHeight = (int)Math.Round(recentSearches.Bounds.Height);
            }
        }

        ConfigStore.Save(_viewModel.Config);
    }

    private void WorkspaceSplitter_PointerReleased(object? sender, PointerReleasedEventArgs e) =>
        Dispatcher.UIThread.Post(() => SaveWorkspacePreferences(captureDividerSizes: true), DispatcherPriority.Background);

    private void WorkspaceSplitter_PointerCaptureLost(object? sender, PointerCaptureLostEventArgs e) =>
        Dispatcher.UIThread.Post(() => SaveWorkspacePreferences(captureDividerSizes: true), DispatcherPriority.Background);

    private DetachedWindowLayout CaptureDetachedWindowLayout()
    {
        var workspace = FindWorkspaceControl<Grid>("WorkspaceGrid");
        var sidebarWidth = workspace?.ColumnDefinitions.Count == 5
            ? workspace.ColumnDefinitions[0].ActualWidth
            : _workspaceSidebarWidth.Value;
        var previewWidth = workspace?.ColumnDefinitions.Count == 5
            ? workspace.ColumnDefinitions[4].ActualWidth
            : _workspacePreviewWidth.Value;
        return new DetachedWindowLayout(
            _viewModel.IsFavoritesVisible,
            _viewModel.IsPreviewVisible,
            Math.Clamp(sidebarWidth, 120, 900),
            Math.Clamp(previewWidth, 220, 900));
    }

    private readonly record struct WorkspaceLayoutState(bool ShowFavorites, bool ShowRecentSearches, bool ShowNavigation, bool ShowPreview)
    {
        public bool ShowSidebar => ShowFavorites || ShowRecentSearches || ShowNavigation;
    }

    internal sealed record DetachedWindowLayout(
        bool FavoritesVisible,
        bool PreviewVisible,
        double FavoritesPaneWidth,
        double PreviewPaneWidth);

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

    private void SearchTabPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(sender as Visual).Properties.IsLeftButtonPressed ||
            sender is not Control { DataContext: SearchTabViewModel tab } ||
            IsTabButton(e.Source))
        {
            return;
        }

        _tabDragCandidate = tab;
        _tabDragStart = e.GetPosition(this);
        _tabDragPressedAtUtc = DateTime.UtcNow;
        _tabDetachStarted = false;
        HideTabDragGhost();
        e.Pointer.Capture(sender as IInputElement);
    }

    private void SearchTabPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_tabDragCandidate is not { } tab || _tabDetachStarted || sender is not IInputElement input)
        {
            return;
        }

        var tabList = _searchTabList;
        if (tabList == null)
        {
            return;
        }

        var current = e.GetPosition(this);
        var movedEnough = Math.Abs(current.X - _tabDragStart.X) > 12 || Math.Abs(current.Y - _tabDragStart.Y) > 12;
        var heldLongEnough = DateTime.UtcNow - _tabDragPressedAtUtc >= TimeSpan.FromMilliseconds(TabDragHoldDelayMilliseconds);
        if (!movedEnough || !heldLongEnough)
        {
            return;
        }

        if (_isDetachedWindow)
        {
            ShowTabDragGhost(tab, current);
        }

        // Windows uses a small native cursor watcher so its tear-off preview can
        // follow the pointer outside the source window. Other platforms can use
        // Avalonia's cross-platform drag/drop flow directly.
        if (!_isDetachedWindow && OperatingSystem.IsWindows())
        {
            StartTabTearOffWatcher(tab);
            return;
        }

        if (!OperatingSystem.IsWindows())
        {
            _tabDetachStarted = true;
            _ = BeginTabDragAsync(tab, e);
            return;
        }

        var stripPosition = e.GetPosition(tabList);
        var outsideStrip = stripPosition.Y < -24 || stripPosition.Y > tabList.Bounds.Height + 24;
        if (!outsideStrip)
        {
            return;
        }

        _tabDetachStarted = true;
        _ = BeginTabDragAsync(tab, e);
    }

    private async Task BeginTabDragAsync(SearchTabViewModel tab, PointerEventArgs e)
    {
        HideTabDragGhost();
        var session = new TabDragSession(this, tab, _isDetachedWindow && _viewModel.SearchTabs.Count == 1);
        _activeTabDrag = session;
        try
        {
            var data = new DataTransfer();
            data.Add(DataTransferItem.CreateText("QSurfer tab"));
            var effect = await DragDrop.DoDragDropAsync(e, data, DragDropEffects.Move);
            if (effect != DragDropEffects.Move && ReferenceEquals(_activeTabDrag, session))
            {
                MoveTabToNewWindow(tab, null);
            }
        }
        catch (Exception exception)
        {
            AppLogger.Error("tabs", exception, "tab drag failed");
            _viewModel.StatusMessage("Could not move this tab.");
        }
        finally
        {
            if (ReferenceEquals(_activeTabDrag, session))
            {
                _activeTabDrag = null;
            }
            _tabDragCandidate = null;
            _tabDetachStarted = false;
        }
    }

    private void SearchTabDragOver(object? sender, DragEventArgs e)
    {
        if (_activeTabDrag is { } session && !ReferenceEquals(session.SourceWindow, this))
        {
            _searchTabDropTarget?.Classes.Add("drag-over");
            e.DragEffects = DragDropEffects.Move;
            e.Handled = true;
        }
    }

    private void SearchTabDragLeave(object? sender, DragEventArgs e) => _searchTabDropTarget?.Classes.Remove("drag-over");

    private void SearchTabDrop(object? sender, DragEventArgs e)
    {
        _searchTabDropTarget?.Classes.Remove("drag-over");
        if (_activeTabDrag is not { } session || ReferenceEquals(session.SourceWindow, this))
        {
            return;
        }

        if (!session.SourceWindow._viewModel.MoveSearchTabTo(session.Tab, _viewModel))
        {
            return;
        }

        e.DragEffects = DragDropEffects.Move;
        e.Handled = true;
        if (session.CloseSourceWhenTransferred)
        {
            session.SourceWindow.CloseTransferredWindow();
        }
    }

    private void SearchTabPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        // The external preview completes on the cursor watcher after the button is up.
        if (_tabTearOffPreview != null && _watchedTearOffTab != null)
        {
            return;
        }

        ClearTabDragState(e);
    }

    private void SearchTabPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        // Showing the floating preview transfers native pointer capture away from the
        // source window. The watcher owns the rest of that external drag gesture.
        if (_tabTearOffPreview != null && _watchedTearOffTab != null)
        {
            return;
        }

        _tabDragCandidate = null;
        _tabDetachStarted = false;
        StopTabTearOffWatcher();
        HideTabDragGhost();
    }

    private void ClearTabDragState(PointerEventArgs e)
    {
        _tabDragCandidate = null;
        _tabDetachStarted = false;
        StopTabTearOffWatcher();
        HideTabDragGhost();
        if (e.Pointer.Captured is IInputElement captured && ReferenceEquals(captured, e.Source))
        {
            e.Pointer.Capture(null);
        }
    }

    private void ShowTabDragGhost(SearchTabViewModel tab, Point position)
    {
        if (_tabDragGhost == null)
        {
            return;
        }

        if (_tabDragGhostTitle != null)
        {
            _tabDragGhostTitle.Text = tab.Title;
        }

        _tabDragGhost.IsVisible = true;
        Canvas.SetLeft(_tabDragGhost, Math.Clamp(position.X + 14, 8, Math.Max(8, Bounds.Width - 330)));
        Canvas.SetTop(_tabDragGhost, Math.Clamp(position.Y + 14, 8, Math.Max(8, Bounds.Height - 42)));
    }

    private void HideTabDragGhost()
    {
        if (_tabDragGhost != null)
        {
            _tabDragGhost.IsVisible = false;
        }
    }

    private void StartTabTearOffWatcher(SearchTabViewModel tab)
    {
        if (ReferenceEquals(_watchedTearOffTab, tab))
        {
            return;
        }

        _watchedTearOffTab = tab;
        _tabTearOffWatcher ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _tabTearOffWatcher.Tick -= TabTearOffWatcher_Tick;
        _tabTearOffWatcher.Tick += TabTearOffWatcher_Tick;
        _tabTearOffWatcher.Start();
        if (GetCursorPos(out var cursor))
        {
            ShowTabTearOffPreview(tab, cursor, isOutsideSource: false);
        }
    }

    private void StopTabTearOffWatcher()
    {
        _tabTearOffWatcher?.Stop();
        _watchedTearOffTab = null;
    }

    private void TabTearOffWatcher_Tick(object? sender, EventArgs e)
    {
        if (_watchedTearOffTab is not { } tab)
        {
            StopTabTearOffWatcher();
            HideTabDragGhost();
            return;
        }

        if (!GetCursorPos(out var cursor))
        {
            return;
        }

        if ((GetAsyncKeyState(0x01) & 0x8000) == 0)
        {
            var shouldDetach = _tearOffPreviewIsOutside;
            CloseTabTearOffPreview();
            StopTabTearOffWatcher();
            HideTabDragGhost();
            if (shouldDetach)
            {
                MoveTabToNewWindow(tab, new PixelPoint(cursor.X - 180, cursor.Y - 24));
            }
            return;
        }

        var handle = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (handle == IntPtr.Zero || !GetWindowRect(handle, out var bounds))
        {
            return;
        }

        HideTabDragGhost();
        var cursorIsInsideWindow = cursor.X >= bounds.Left && cursor.X < bounds.Right && cursor.Y >= bounds.Top && cursor.Y < bounds.Bottom;
        ShowTabTearOffPreview(tab, cursor, !cursorIsInsideWindow);
    }

    private void CloseTabTearOffPreview()
    {
        if (_tabTearOffPreview == null)
        {
            return;
        }

        _tabTearOffPreview.Close();
        _tabTearOffPreview = null;
        _tearOffPreviewIsOutside = false;
    }

    private void ShowTabTearOffPreview(SearchTabViewModel tab, NativePoint cursor, bool isOutsideSource)
    {
        _tabTearOffPreview ??= new TabDragPreviewWindow(tab.Title);
        _tearOffPreviewIsOutside = isOutsideSource;
        _tabTearOffPreview.Update(tab.Title, isOutsideSource);
        _tabTearOffPreview.MoveTo(new PixelPoint(cursor.X + 18, cursor.Y + 18));
        if (!_tabTearOffPreview.IsVisible)
        {
            _tabTearOffPreview.Show();
        }
    }

    private void MoveTabToNewWindow_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: SearchTabViewModel tab })
        {
            return;
        }

        MoveTabToNewWindow(tab, null);
    }

    private void MoveTabToNewWindow(SearchTabViewModel tab, PixelPoint? screenPosition)
    {
        try
        {
            var detachedWindow = new MainWindow(_viewModel.DetachSearchTab(tab), inheritedLayout: CaptureDetachedWindowLayout());
            if (screenPosition is { } position)
            {
                detachedWindow.Position = position;
            }
            detachedWindow.Show();
        }
        catch (Exception exception)
        {
            AppLogger.Error("tabs", exception, "could not move tab to a new window");
            _viewModel.StatusMessage("Could not move this tab to a new window.");
        }
    }

    private static bool IsTabButton(object? source) => source is Visual visual &&
        visual.GetVisualAncestors().Append(visual).OfType<Button>().Any();

    private void CloseTransferredWindow()
    {
        _exitRequested = true;
        Close();
    }

    private sealed record TabDragSession(MainWindow SourceWindow, SearchTabViewModel Tab, bool CloseSourceWhenTransferred);

    private async void SaveSearchAs_Click(object? sender, RoutedEventArgs e)
    {
        var tab = sender is Control { DataContext: SearchTabViewModel directTab }
            ? directTab
            : (sender as MenuItem)?.Parent is ContextMenu { PlacementTarget.DataContext: SearchTabViewModel placedTab }
                ? placedTab
                : null;
        if (tab == null || string.IsNullOrWhiteSpace(tab.Query))
        {
            return;
        }

        await SaveSearchWithNamePromptAsync(tab, tab.Query, promptForName: true);
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
        if (sender is not Control { DataContext: SearchTabViewModel tab } ||
            e.Source is not Control { DataContext: SearchResult result })
        {
            return;
        }

        var point = e.GetCurrentPoint(sender as Visual);
        _resultContextClick = point.Properties.IsRightButtonPressed;
        if (result.IsFolder && point.Properties.IsLeftButtonPressed &&
            e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            tab.SetScopeFolder(result.Path);
            _viewModel.StatusMessage($"Searching this tab in {result.FileName}. Clear filters to remove the folder scope.");
            e.Handled = true;
            return;
        }

        if (!_resultContextClick)
        {
            return;
        }
        tab.SelectedResult = result;
    }

    private async void OverwriteSavedSearch_Click(object? sender, RoutedEventArgs e)
    {
        var tab = sender is Control { Tag: SearchTabViewModel taggedTab }
            ? taggedTab
            : sender is Control { DataContext: SearchTabViewModel directTab }
                ? directTab
                : (sender as MenuItem)?.Parent is ContextMenu { PlacementTarget.DataContext: SearchTabViewModel placedTab }
                    ? placedTab
                    : null;
        if (tab != null)
        {
            await _viewModel.OverwriteSavedSearchAsync(tab);
        }
    }

    private async void SaveSearch_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { Tag: SearchTabViewModel tab })
        {
            if (tab.HasSavedSearchSource)
            {
                await _viewModel.OverwriteSavedSearchAsync(tab);
            }
            else
            {
                await SaveSearchWithNamePromptAsync(tab, tab.Query, promptForName: false);
            }
        }
    }

    private async Task SaveSearchWithNamePromptAsync(
        SearchTabViewModel tab,
        string suggestedName,
        bool promptForName)
    {
        while (true)
        {
            var name = suggestedName;
            if (promptForName)
            {
                var dialog = new TextEntryWindow("Save search", "Name this saved search", "Save", suggestedName);
                dialog.Topmost = Topmost;
                if (await dialog.ShowDialog<bool?>(this) != true)
                {
                    return;
                }

                name = dialog.Value.Trim();
                if (string.IsNullOrWhiteSpace(name))
                {
                    return;
                }
            }

            var existing = await _viewModel.FindSavedSearchByNameAsync(name);
            if (existing is null)
            {
                await _viewModel.SaveSearchAsync(tab, name);
                return;
            }

            var confirmation = new ConfirmationWindow(
                "Saved search already exists",
                $"A saved search named '{existing.Name}' already exists. Overwrite it, or choose another name.",
                "Overwrite",
                "Pick another name")
            {
                Topmost = Topmost,
            };
            if (await confirmation.ShowDialog<bool?>(this) == true)
            {
                await _viewModel.OverwriteSavedSearchAsync(tab, existing);
                return;
            }

            suggestedName = name;
            promptForName = true;
        }
    }

    private void ResultPointerReleased(object? sender, PointerReleasedEventArgs e) => _resultContextClick = false;

    private void FavoritePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _favoriteContextClick = e.GetCurrentPoint(sender as Visual).Properties.IsRightButtonPressed;
        if (e.Source is not Control { DataContext: FavoriteTreeNode node })
        {
            _viewModel.SelectedFavoriteNode = null;
            e.Handled = true;
            return;
        }

        if (_favoriteContextClick)
        {
            _viewModel.SelectedFavoriteNode = node;
        }
    }

    private void FavoritePointerReleased(object? sender, PointerReleasedEventArgs e) => _favoriteContextClick = false;

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
            if (item.Tag as string is "new-tabs" or "show-all")
            {
                item.IsVisible = _contextResults.Count > 1;
                item.IsEnabled = _contextResults.Count > 1;
                continue;
            }
            if (item.Tag as string == "show")
            {
                item.IsVisible = hasResult && !_contextResults[0].IsFolder;
                continue;
            }
            if (item.Tag as string == "scope")
            {
                item.IsVisible = hasResult && _contextResults.Count == 1 && _contextResults[0].IsFolder;
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

        if (!_viewModel.SupportsVersionHistory(result))
        {
            _viewModel.StatusMessage("Version history is available only for NAS files and folders.");
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

    private void ResultScopeFolder_Click(object? sender, RoutedEventArgs e)
    {
        var result = ContextResult();
        if (result is not { IsFolder: true } || _viewModel.SelectedSearchTab is not { } tab)
        {
            return;
        }

        tab.SetScopeFolder(result.Path);
        _viewModel.StatusMessage($"Searching this tab in {result.FileName}. Clear filters to remove the folder scope.");
    }

    private async void ResultOpenAllInNewTabs_Click(object? sender, RoutedEventArgs e)
    {
        var items = _contextResults
            .Select(CreateBrowserItem)
            .OfType<BrowserItem>()
            .ToList();
        await _viewModel.OpenBrowserItemsInNewTabsAsync(items);
    }

    private async void ResultShowAll_Click(object? sender, RoutedEventArgs e)
    {
        var items = _contextResults
            .Select(CreateBrowserItem)
            .OfType<BrowserItem>()
            .ToList();
        await _viewModel.ShowBrowserItemsInFileManagerAsync(items);
    }

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

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr handle, out NativeRect rectangle);

    private readonly record struct NativePoint(int X, int Y);

    private readonly record struct NativeRect(int Left, int Top, int Right, int Bottom);
}
