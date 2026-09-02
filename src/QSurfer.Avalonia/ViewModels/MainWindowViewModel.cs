using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Avalonia.Threading;
using Avalonia.Media.Imaging;
using QSurfer.Core.Models;
using QSurfer.Core.Services;
using QSurfer.Avalonia.Services;

namespace QSurfer.Avalonia.ViewModels;

public sealed class MainWindowViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly AppConfig _config = ConfigStore.Load();
    private readonly IReadOnlyList<FileTypeFilter> _fileTypes;
    private readonly HistoryStore _history;
    private QsirchClient _client;
    private PathMapper _mapper;
    private ResultRules _rules;
    private readonly NasFileBrowser _browser = new();
    private ObservableCollection<BrowserItem> _browserItems = [];
    private readonly Dictionary<SearchTabViewModel, BrowserTabState> _browserTabs = [];
    private readonly Dictionary<string, Bitmap> _iconCache = new(StringComparer.OrdinalIgnoreCase);
    private Bitmap? _recycleBinIcon;
    private readonly HashSet<string> _iconLoadsInFlight = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _iconLoadGate = new(3, 3);
    private CancellationTokenSource? _browseCancellation;
    private readonly List<string> _browserHistory = [];
    private readonly List<BrowserItem> _browserClipboard = [];
    private bool _browserClipboardIsCut;
    private string _status = "Ready";
    private SearchTabViewModel? _selectedSearchTab;
    private BrowserItem? _selectedBrowserItem;
    private FavoriteTreeNode? _selectedFavoriteNode;
    private string _browserLocation = "";
    private bool _isFavoritesVisible = true;
    private bool _isPreviewVisible;
    private bool _isNavigationVisible;
    private int _browserHistoryIndex = -1;
    private string _previewTitle = "Select a result";
    private string _previewDescription = "Choose a file or folder to see its details here.";
    private string _previewLocation = "";
    private object? _nativePreviewHost;
    private bool _isRecentSearchesOpen;
    private bool _isNasNavigationOnline;
    private bool _isLoadingNasNavigation;
    private string _nasNavigationStatus = "NAS navigation is waiting for a connection.";
    private readonly DispatcherTimer _nasNavigationRetryTimer;
    private int _nextTabNumber = 1;

    public MainWindowViewModel()
    {
        _client = new QsirchClient(_config);
        _mapper = new PathMapper(_config);
        _rules = new ResultRules(_config);
        _history = new HistoryStore(_config);
        _isPreviewVisible = _config.Behavior.PreviewPane;
        _fileTypes =
        [
            new FileTypeFilter { Name = "All types", IncludeAllFiles = true, IncludeFolders = true },
            new FileTypeFilter { Name = "Folders", IncludeFolders = true },
            new FileTypeFilter { Name = "Word", Extensions = ["doc", "docx", "docm", "rtf"] },
            new FileTypeFilter { Name = "Excel", Extensions = ["xls", "xlsx", "xlsm", "csv"] },
            new FileTypeFilter { Name = "PowerPoint", Extensions = ["ppt", "pptx", "pptm"] },
            new FileTypeFilter { Name = "PDF", Extensions = ["pdf"] },
            new FileTypeFilter { Name = "Images", Extensions = ["jpg", "jpeg", "png", "gif", "bmp", "tif", "tiff"] },
            new FileTypeFilter { Name = "Media", Extensions = ["mp3", "mp4", "mov", "avi", "mkv", "wav"] },
            new FileTypeFilter { Name = "Text", Extensions = ["txt", "log", "xml", "json", "html", "htm"] },
        ];

        _browserItems.CollectionChanged += BrowserItemsCollectionChanged;
        _nasNavigationRetryTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        _nasNavigationRetryTimer.Tick += NasNavigationRetryTimer_Tick;
        NewSearchTabCommand = new AsyncCommand(NewSearchTabAsync);
        BrowseCommand = new AsyncCommand(BrowseAtLocationAsync, () => !string.IsNullOrWhiteSpace(BrowserLocation));
        RefreshBrowserCommand = new AsyncCommand(() => BrowseAsync(), () => !string.IsNullOrWhiteSpace(BrowserLocation));
        NavigateUpCommand = new AsyncCommand(NavigateUpAsync, () => NasFileBrowser.GetParentFolder(BrowserLocation) != null);
        OpenBrowserItemCommand = new AsyncCommand(OpenSelectedBrowserItemAsync, () => SelectedBrowserItem != null);
        ToggleBrowserFavoriteCommand = new AsyncCommand(ToggleSelectedBrowserItemFavoriteAsync, () => SelectedBrowserItem != null);
        PasteBrowserItemsCommand = new AsyncCommand(PasteBrowserItemsAsync, () => CanPasteBrowserItems && !string.IsNullOrWhiteSpace(BrowserLocation));
        NavigateBackCommand = new AsyncCommand(() => NavigateHistoryAsync(-1), () => CanNavigateBack);
        NavigateForwardCommand = new AsyncCommand(() => NavigateHistoryAsync(1), () => CanNavigateForward);
        RefreshFavoritesCommand = new AsyncCommand(RefreshFavoritesAsync);
        ToggleFavoritesCommand = new AsyncCommand(ToggleFavoritesAsync);
        TogglePreviewCommand = new AsyncCommand(TogglePreviewAsync);
        OpenFavoriteCommand = new AsyncCommand(
            OpenSelectedFavoriteAsync,
            () => SelectedFavoriteNode?.Result != null || SelectedFavoriteNode?.SavedSearch != null);

        RestorePinnedTabs();
        if (SearchTabs.Count == 0)
        {
            AddSearchTab();
        }
        SelectedSearchTab = SearchTabs[0];
        EnsureNavigationRoots();
        _ = LoadNasNavigationRootAsync();
        _nasNavigationRetryTimer.Start();
        _ = RefreshFavoritesAsync();
        _ = RefreshRecentSearchesAsync();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<SearchTabViewModel> SearchTabs { get; } = [];
    public ObservableCollection<BrowserItem> BrowserItems
    {
        get => _browserItems;
        private set
        {
            if (ReferenceEquals(_browserItems, value))
            {
                return;
            }

            _browserItems.CollectionChanged -= BrowserItemsCollectionChanged;
            _browserItems = value;
            _browserItems.CollectionChanged += BrowserItemsCollectionChanged;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasBrowserItems));
            OnPropertyChanged(nameof(HasNoBrowserItems));
        }
    }
    public ObservableCollection<NavigationTreeNode> NavigationRoots { get; } = [];
    public ObservableCollection<BrowserBreadcrumb> BrowserBreadcrumbs { get; } = [];
    public ObservableCollection<FavoriteTreeNode> FavoriteTree { get; } = [];
    public ObservableCollection<string> RecentSearches { get; } = [];
    public AsyncCommand NewSearchTabCommand { get; }
    public AsyncCommand BrowseCommand { get; }
    public AsyncCommand RefreshBrowserCommand { get; }
    public AsyncCommand NavigateUpCommand { get; }
    public AsyncCommand OpenBrowserItemCommand { get; }
    public AsyncCommand ToggleBrowserFavoriteCommand { get; }
    public AsyncCommand PasteBrowserItemsCommand { get; }
    public AsyncCommand NavigateBackCommand { get; }
    public AsyncCommand NavigateForwardCommand { get; }
    public AsyncCommand RefreshFavoritesCommand { get; }
    public AsyncCommand ToggleFavoritesCommand { get; }
    public AsyncCommand TogglePreviewCommand { get; }
    public AsyncCommand OpenFavoriteCommand { get; }
    public AppConfig Config => _config;
    public bool HasBrowserItems => BrowserItems.Count > 0;
    public bool HasNoBrowserItems => !HasBrowserItems;
    public bool CanNavigateBack => _browserHistoryIndex > 0;
    public bool CanNavigateForward => _browserHistoryIndex >= 0 && _browserHistoryIndex < _browserHistory.Count - 1;
    public bool CanPasteBrowserItems => _browserClipboard.Count > 0;
    public bool IsConnectionConfigured => !string.IsNullOrWhiteSpace(_config.Host) &&
                                         !string.IsNullOrWhiteSpace(_config.User) &&
                                         !string.IsNullOrWhiteSpace(_config.Password);
    public bool NeedsConnection => !IsConnectionConfigured;
    public string CurrentWindowsUser
    {
        get
        {
            var user = GlobalRuleAuthorization.GetCurrentUserStatus();
            return $"{user.UserName} | {user.DisplayStatus}";
        }
    }

    public string ConnectionSummary => IsConnectionConfigured
        ? $"NAS: {_config.Host}:{_config.Port}"
        : "NAS: not configured";
    public string NasConnectionStatus => IsConnectionConfigured
        ? $"{ConnectionSummary} | {NasNavigationStatus}"
        : ConnectionSummary;
    public string NasNavigationStatus
    {
        get => _nasNavigationStatus;
        private set
        {
            if (SetField(ref _nasNavigationStatus, value))
            {
                OnPropertyChanged(nameof(NasConnectionStatus));
            }
        }
    }
    public bool IsNasNavigationOnline
    {
        get => _isNasNavigationOnline;
        private set => SetField(ref _isNasNavigationOnline, value);
    }
    public bool ShowNasNavigationStatus => !IsNasNavigationOnline;

    public SearchTabViewModel? SelectedSearchTab
    {
        get => _selectedSearchTab;
        set
        {
            var previousTab = _selectedSearchTab;
            if (!SetField(ref _selectedSearchTab, value))
            {
                return;
            }

            if (previousTab != null)
            {
                _browserTabs[previousTab] = CaptureBrowserTab();
            }

            if (value != null)
            {
                RestoreBrowserTab(value);
                IsNavigationVisible = value.IsBrowsing;
                Status = value.Status;
            }
        }
    }

    public string Status
    {
        get => _status;
        private set => SetField(ref _status, value);
    }

    public BrowserItem? SelectedBrowserItem
    {
        get => _selectedBrowserItem;
        set
        {
            if (!SetField(ref _selectedBrowserItem, value))
            {
                return;
            }
            OpenBrowserItemCommand.RaiseCanExecuteChanged();
            ToggleBrowserFavoriteCommand.RaiseCanExecuteChanged();
            SetPreview(value is { } item ? CreateSearchResult(item) : null);
        }
    }

    public FavoriteTreeNode? SelectedFavoriteNode
    {
        get => _selectedFavoriteNode;
        set
        {
            if (!SetField(ref _selectedFavoriteNode, value))
            {
                return;
            }

            SetPreview(value?.Result);
            OpenFavoriteCommand.RaiseCanExecuteChanged();
        }
    }

    public bool IsFavoritesVisible
    {
        get => _isFavoritesVisible;
        set => SetField(ref _isFavoritesVisible, value);
    }

    public bool IsPreviewVisible
    {
        get => _isPreviewVisible;
        set => SetField(ref _isPreviewVisible, value);
    }

    public bool IsNavigationVisible
    {
        get => _isNavigationVisible;
        private set
        {
            if (!SetField(ref _isNavigationVisible, value))
            {
                return;
            }

            OnPropertyChanged(nameof(IsSearchContentVisible));
            OnPropertyChanged(nameof(IsRecycleBinView));
            SelectedSearchTab?.SetWorkspaceMode(value);
        }
    }

    public bool IsSearchContentVisible => !IsNavigationVisible;
    public bool IsRecycleBinView => IsNavigationVisible && IsRecycleFolderPath(BrowserLocation);

    public string PreviewTitle
    {
        get => _previewTitle;
        private set => SetField(ref _previewTitle, value);
    }

    public string PreviewDescription
    {
        get => _previewDescription;
        private set => SetField(ref _previewDescription, value);
    }

    public string PreviewLocation
    {
        get => _previewLocation;
        private set => SetField(ref _previewLocation, value);
    }

    public object? NativePreviewHost
    {
        get => _nativePreviewHost;
        private set => SetField(ref _nativePreviewHost, value);
    }

    public bool IsRecentSearchesOpen
    {
        get => _isRecentSearchesOpen;
        set => SetField(ref _isRecentSearchesOpen, value);
    }

    public string BrowserLocation
    {
        get => _browserLocation;
        set
        {
            if (!SetField(ref _browserLocation, value))
            {
                return;
            }
            BrowseCommand.RaiseCanExecuteChanged();
            RefreshBrowserCommand.RaiseCanExecuteChanged();
            NavigateUpCommand.RaiseCanExecuteChanged();
            OnPropertyChanged(nameof(IsRecycleBinView));
        }
    }

    public async Task NavigateToFolderAsync(NavigationTreeNode node)
    {
        if (node.IsPlaceholder || string.IsNullOrWhiteSpace(node.FullPath))
        {
            return;
        }

        try
        {
            var resolvedPath = _mapper.ResolveBrowserPath(node.FullPath);
            AppLogger.Info("browse", $"navigation requested folder=\"{node.FullPath}\" resolved=\"{resolvedPath}\"");

            IsFavoritesVisible = true;
            IsNavigationVisible = true;
            BrowserLocation = resolvedPath;
            await BrowseAsync();
            await LoadNavigationChildrenAsync(node);
            node.IsExpanded = true;
        }
        catch (Exception ex)
        {
            AppLogger.Error("browse", ex, $"navigation failed folder=\"{node.FullPath}\"");
            Status = $"Could not open {NavigationTreeDisplayName(node.FullPath)}";
            if (SelectedSearchTab is { } tab)
            {
                tab.Status = Status;
            }
        }
    }

    public async Task NavigateToBreadcrumbAsync(BrowserBreadcrumb breadcrumb)
    {
        if (string.IsNullOrWhiteSpace(breadcrumb.FullPath))
        {
            return;
        }

        IsNavigationVisible = true;
        BrowserLocation = breadcrumb.FullPath;
        await BrowseAsync();
    }

    public async Task LoadNavigationChildrenAsync(NavigationTreeNode node)
    {
        if (_config.Behavior.FlattenRecycleBin && IsRecycleBinRootPath(node.FullPath))
        {
            node.Children.Clear();
            node.ChildrenLoaded = true;
            return;
        }

        if (node.ChildrenLoaded || string.IsNullOrWhiteSpace(node.FullPath))
        {
            return;
        }

        var isNasRoot = IsNasNavigationRoot(node.FullPath);
        try
        {
            var listing = await _browser.BrowseAsync(node.FullPath);
            node.Children.Clear();
            foreach (var item in listing.Items.Where(item => item.IsFolder))
            {
                if (!_config.Behavior.ShowRecoverySystemFolders &&
                    (IsSnapshotFolder(item.Name) || (IsRecycleFolder(item.Name) && !node.IsShareRoot)))
                {
                    continue;
                }
                if (!_config.Behavior.ShowQSurferSafetyCopies && IsQSurferSafetyCopy(item))
                {
                    continue;
                }

                var isRecoveryFolder = node.IsShareRoot && IsRecycleFolder(item.Name);
                var child = CreateNavigationNode(
                    isRecoveryFolder ? "Recycle Bin" : NavigationTreeDisplayName(item.FullPath),
                    item.FullPath,
                    isRecoveryFolder,
                    isNasRoot);
                node.Children.Add(child);
                if (ShouldRestoreExpanded(child.FullPath))
                {
                    child.IsExpanded = true;
                }
            }
            node.ChildrenLoaded = true;
            if (isNasRoot)
            {
                SetNasNavigationState(true, "Online");
            }
            AppLogger.Info("browse", $"navigation folder=\"{node.FullPath}\" folders={node.Children.Count}");
        }
        catch (Exception ex)
        {
            if (isNasRoot)
            {
                // Leave the node expandable and its placeholder intact so a VPN or NAS
                // reconnect can fill the same tree without rebuilding the UI.
                node.ChildrenLoaded = false;
                node.EnsurePlaceholder();
                SetNasNavigationState(false, "Unavailable - retrying automatically");
            }
            else
            {
                // Network drives can appear late or briefly disconnect. Keep the node
                // retryable rather than leaving it permanently empty after one failed read.
                node.ChildrenLoaded = false;
                node.EnsurePlaceholder();
            }
            AppLogger.Warn("browse", $"navigation tree unavailable folder=\"{node.FullPath}\" reason=\"{ex.Message}\"");
        }
    }

    public void ReloadConnection()
    {
        _client.Dispose();
        _client = new QsirchClient(_config);
        _mapper = new PathMapper(_config);
        _rules = new ResultRules(_config);
        RefreshNavigationRoots();
        _ = LoadNasNavigationRootAsync();
        IsPreviewVisible = _config.Behavior.PreviewPane;
        OnPropertyChanged(nameof(IsConnectionConfigured));
        OnPropertyChanged(nameof(NeedsConnection));
        OnPropertyChanged(nameof(ConnectionSummary));
        OnPropertyChanged(nameof(NasConnectionStatus));
        Status = IsConnectionConfigured ? "Connection settings saved" : "Add NAS connection details to search";
    }

    public void StatusMessage(string message) => Status = message;

    public void ReturnToSearchResults() => IsNavigationVisible = false;

    public void EnterBrowseMode() => IsNavigationVisible = true;

    public async Task ToggleWorkspaceModeAsync()
    {
        if (IsNavigationVisible)
        {
            ReturnToSearchResults();
            return;
        }

        if (string.IsNullOrWhiteSpace(BrowserLocation))
        {
            var root = NavigationRoots.FirstOrDefault(node => node.FullPath.StartsWith("\\\\", StringComparison.Ordinal))
                ?? NavigationRoots.FirstOrDefault();
            if (root == null)
            {
                Status = "Choose a folder from the navigation pane first.";
                return;
            }
            BrowserLocation = root.FullPath;
        }

        IsNavigationVisible = true;
        await BrowseAsync();
    }

    public string ResolveWindowsPath(SearchResult result)
    {
        var path = _mapper.TryResolve(result) ?? result.WindowsPath;
        return string.IsNullOrWhiteSpace(path) ? "" : _mapper.ResolveBrowserPath(path);
    }

    public void SetNativePreviewHost(object? host) => NativePreviewHost = host;

    public async Task RunRecentSearchAsync(string query)
    {
        var tab = SelectedSearchTab ?? AddSearchTab();
        SelectedSearchTab = tab;
        tab.Query = query;
        IsRecentSearchesOpen = false;
        if (tab.SearchCommand.CanExecute(null))
        {
            await tab.SearchCommand.ExecuteAsync();
        }
    }

    public async Task ToggleRecentSearchesAsync()
    {
        if (IsRecentSearchesOpen)
        {
            IsRecentSearchesOpen = false;
            return;
        }

        await RefreshRecentSearchesAsync();
        IsRecentSearchesOpen = true;
    }

    public async Task ClearCurrentUserHistoryAsync(bool clearStarred)
    {
        await Task.Run(() => _history.ClearCurrentMachine(clearStarred));
        await RefreshFavoritesAsync();
        Status = clearStarred ? "Saved data cleared" : "Saved results cleared; favorites kept";
    }

    public async Task ResetCurrentUserHistoryAsync()
    {
        await Task.Run(_history.Reset);
        await RefreshFavoritesAsync();
        Status = "Saved data reset";
    }

    public void CloseSearchTab(SearchTabViewModel tab)
    {
        if (tab.IsPinned)
        {
            Status = "Unpin this tab before closing it.";
            return;
        }

        var index = SearchTabs.IndexOf(tab);
        if (index < 0)
        {
            return;
        }
        tab.Dispose();
        SearchTabs.RemoveAt(index);
        if (SearchTabs.Count == 0)
        {
            AddSearchTab();
        }
        SelectedSearchTab = SearchTabs[Math.Min(index, SearchTabs.Count - 1)];
        _browserTabs.Remove(tab);
        PersistPinnedTabs();
    }

    public void ToggleTabPin(SearchTabViewModel tab)
    {
        tab.IsPinned = !tab.IsPinned;
    }

    private Task NewSearchTabAsync()
    {
        SelectedSearchTab = AddSearchTab();
        return Task.CompletedTask;
    }

    private SearchTabViewModel AddSearchTab()
    {
        var tab = new SearchTabViewModel(
            _nextTabNumber++,
            _fileTypes,
            SearchTabAsync,
            LoadMoreSearchTabAsync,
            StopSearchTabAsync,
            ClearSearchTabAsync,
            OpenSelectedSearchResultAsync,
            BrowseSelectedSearchResultAsync,
            ToggleSelectedSearchResultFavoriteAsync,
            SaveCurrentSearchAsync);
        tab.PinChanged += (_, _) => PersistPinnedTabs();
        tab.PropertyChanged += SearchTabPropertyChanged;
        tab.SearchContents = _config.Behavior.SearchContents;
        tab.ExactMatch = false;
        tab.SelectedViewMode = tab.ViewModes.FirstOrDefault(view => view.Key.Equals(_config.Behavior.ResultView, StringComparison.OrdinalIgnoreCase)) ?? tab.ViewModes[0];
        tab.SelectedSortMode = tab.SortModes.FirstOrDefault(sort => sort.Key.Equals(_config.Behavior.ResultSort, StringComparison.OrdinalIgnoreCase)) ?? tab.SortModes[0];
        SearchTabs.Add(tab);
        return tab;
    }

    private void RestorePinnedTabs()
    {
        foreach (var saved in _config.PinnedTabs.Where(tab => !string.IsNullOrWhiteSpace(tab.Query)))
        {
            var tab = AddSearchTab();
            tab.Query = saved.Query;
            tab.SelectedFileType = _fileTypes[Math.Clamp(saved.TypeIndex, 0, _fileTypes.Count - 1)];
            tab.ApplyTypeSelection(saved.TypeNames);
            tab.SelectedViewMode = tab.ViewModes.FirstOrDefault(view => view.Key.Equals(saved.ViewKey, StringComparison.OrdinalIgnoreCase)) ?? tab.SelectedViewMode;
            tab.ApplySortSpecification(saved.SortValue);
            tab.DateFrom = saved.DateFrom;
            tab.DateTo = saved.DateTo ?? DateTime.Today;
            tab.ExactMatch = saved.ExactMatch;
            tab.SearchContents = saved.SearchContents;
            tab.IsPinned = true;
        }
    }

    private async Task SearchTabAsync(SearchTabViewModel tab)
    {
        var query = tab.Query.Trim();
        if (string.IsNullOrWhiteSpace(query))
        {
            return;
        }

        tab.CancelSearch();
        tab.SearchCancellation?.Dispose();
        tab.SearchCancellation = new CancellationTokenSource();
        var token = tab.SearchCancellation.Token;
        var version = ++tab.SearchVersion;

        _ = RecordRecentSearchAsync(query);

        tab.ResetResults();
        tab.IsSearching = true;
        SetTabStatus(tab, "Searching...");
        try
        {
            await _client.EnsureAuthenticatedAsync(token);
            var starredKeys = await Task.Run(_history.StarredKeys, token);
            var typeFilter = _fileTypes[0];
            var resultLimit = Math.Clamp(_config.Behavior.MaxSearchResults, 50, 5000);
            var firstPageSize = Math.Clamp(_config.Behavior.FirstPageSize, 5, 500);
            var nextPageSize = Math.Clamp(_config.Behavior.NextPageSize, 10, 500);
            var serverQuery = BuildServerQuery(query, tab.SearchContents);
            if (tab.HasDateRange)
            {
                var from = tab.DateFrom?.ToString("yyyy-MM-dd") ?? "1900-01-01";
                var to = tab.DateTo?.ToString("yyyy-MM-dd") ?? DateTime.Today.ToString("yyyy-MM-dd");
                serverQuery += $" modified:{from}..{to}";
            }

            var (serverSortBy, serverSortDirection) = ServerSortFor(tab);
            var recentCutoff = DateTime.Today.AddDays(-30);
            SetTabStatus(tab, tab.HasDateRange ? "Loading selected date range..." : "Loading recent results...");

            var recentFiles = _client.SearchAsync(
                serverQuery,
                typeFilter,
                firstPageSize,
                0,
                "modified",
                "desc",
                batch => Dispatcher.UIThread.InvokeAsync(() => AddVisibleResults(
                    tab,
                    tab.HasDateRange ? batch : RecentResults(batch, recentCutoff),
                    token,
                    version,
                    starredKeys)).GetTask(),
                token);

            var folders = typeFilter.IncludeFolders
                ? _client.SearchDirectoriesAsync(query, 100, token)
                : Task.FromResult<IReadOnlyList<SearchResult>>([]);

            _ = PaintDirectoryResultsAsync(tab, folders, token, version, starredKeys);
            await recentFiles;

            IReadOnlyList<SearchResult> firstPage;
            if (tab.HasDateRange)
            {
                firstPage = recentFiles.Result;
            }
            else
            {
                SetTabStatus(tab, $"Searching {tab.Results.Count:n0} results...");
                firstPage = await _client.SearchAsync(
                    serverQuery,
                    typeFilter,
                    firstPageSize,
                    0,
                    serverSortBy,
                    serverSortDirection,
                    batch => Dispatcher.UIThread.InvokeAsync(() => AddVisibleResults(tab, batch, token, version, starredKeys)).GetTask(),
                    token);
            }

            var offset = firstPage.Count;
            var pagingComplete = firstPage.Count == 0;

            // Paint the first small page immediately, then keep paging in the background.
            // The visible limit prevents a broad search from running indefinitely.
            while (!pagingComplete && tab.Results.Count < resultLimit)
            {
                token.ThrowIfCancellationRequested();
                if (tab.SearchVersion != version)
                {
                    return;
                }

                SetTabStatus(tab, $"Searching {tab.Results.Count:n0} results...");
                var page = await _client.SearchAsync(
                    serverQuery,
                    typeFilter,
                    nextPageSize,
                    offset,
                    serverSortBy,
                    serverSortDirection,
                    batch => Dispatcher.UIThread.InvokeAsync(() => AddVisibleResults(tab, batch, token, version, starredKeys)).GetTask(),
                    token);
                offset += page.Count;
                pagingComplete = page.Count == 0;
                AppLogger.Info("search", $"QSurfer tab=\"{tab.Title}\" page offset={offset - page.Count} count={page.Count} visible={tab.Results.Count} limit={resultLimit}");
            }

            tab.NextOffset = offset;
            tab.SetCanLoadMore(!pagingComplete);
            SetTabStatus(tab, $"Ready {tab.Results.Count:n0} results");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            SetTabStatus(tab, "Search stopped");
        }
        catch (Exception ex)
        {
            AppLogger.Error("search", ex, $"QSurfer search failed tab=\"{tab.Title}\" query=\"{query}\"");
            SetTabStatus(tab, ex.Message);
        }
        finally
        {
            if (tab.SearchVersion == version)
            {
                tab.IsSearching = false;
            }
        }
    }

    private Task StopSearchTabAsync(SearchTabViewModel tab)
    {
        tab.CancelSearch();
        SetTabStatus(tab, "Stopping search...");
        return Task.CompletedTask;
    }

    private async Task LoadMoreSearchTabAsync(SearchTabViewModel tab)
    {
        if (string.IsNullOrWhiteSpace(tab.Query) || !tab.CanLoadMore)
        {
            return;
        }

        tab.CancelSearch();
        tab.SearchCancellation?.Dispose();
        tab.SearchCancellation = new CancellationTokenSource();
        var token = tab.SearchCancellation.Token;
        var version = ++tab.SearchVersion;
        tab.IsSearching = true;
        SetTabStatus(tab, "Loading more results...");
        try
        {
            var query = tab.Query.Trim();
            var serverQuery = BuildServerQuery(query, tab.SearchContents);
            var starredKeys = await Task.Run(_history.StarredKeys, token);
            var pageSize = Math.Clamp(_config.Behavior.NextPageSize, 10, 500);
            var page = await _client.SearchAsync(
                serverQuery,
                _fileTypes[0],
                pageSize,
                tab.NextOffset,
                batch => Dispatcher.UIThread.InvokeAsync(() => AddVisibleResults(tab, batch, token, version, starredKeys)).GetTask(),
                token);
            tab.NextOffset += page.Count;
            tab.SetCanLoadMore(page.Count >= pageSize);
            SetTabStatus(tab, page.Count == 0 ? $"Ready {tab.Results.Count:n0} results" : $"Ready {tab.Results.Count:n0} results");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            SetTabStatus(tab, "Load more stopped");
        }
        catch (Exception ex)
        {
            AppLogger.Error("search", ex, $"QSurfer load more failed tab=\"{tab.Title}\"");
            SetTabStatus(tab, ex.Message);
        }
        finally
        {
            if (tab.SearchVersion == version)
            {
                tab.IsSearching = false;
            }
        }
    }

    private Task ClearSearchTabAsync(SearchTabViewModel tab)
    {
        if (tab.IsPinned)
        {
            SetTabStatus(tab, "Unpin this tab before clearing it.");
            return Task.CompletedTask;
        }

        tab.CancelSearch();
        tab.SearchVersion++;
        tab.ResetResults();
        tab.Query = "";
        tab.IsSearching = false;
        SetTabStatus(tab, "Ready");
        return Task.CompletedTask;
    }

    private Task ToggleFavoritesAsync()
    {
        IsFavoritesVisible = !IsFavoritesVisible;
        return Task.CompletedTask;
    }

    private Task TogglePreviewAsync()
    {
        IsPreviewVisible = !IsPreviewVisible;
        return Task.CompletedTask;
    }

    private async Task RefreshFavoritesAsync()
    {
        try
        {
            var expandedGroups = FavoriteTree
                .SelectMany(node => FlattenFavoriteNodes([node]))
                .Where(node => node.IsFolder && node.IsExpanded)
                .Select(node => node.FolderPath)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var hasExistingTree = FavoriteTree.Count > 0;
            var selectedNodeKey = FavoriteNodeKey(SelectedFavoriteNode);
            var snapshot = await Task.Run(() =>
                (Favorites: _history.Favorites(), SavedSearches: _history.SavedSearches()));
            var refreshedTree = BuildFavoritesTree(
                snapshot.Favorites,
                snapshot.SavedSearches,
                hasExistingTree ? expandedGroups : null);

            FavoriteTree.Clear();
            foreach (var node in refreshedTree)
            {
                FavoriteTree.Add(node);
            }
            SelectedFavoriteNode = FindFavoriteNode(refreshedTree, selectedNodeKey);
        }
        catch (Exception ex)
        {
            AppLogger.Error("favorites", ex, "QSurfer favorites refresh failed");
            Status = "Could not load favorites.";
        }
    }

    private async Task RefreshRecentSearchesAsync()
    {
        var searches = await Task.Run(() => _history.RecentSearches());
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            RecentSearches.Clear();
            foreach (var search in searches)
            {
                RecentSearches.Add(search);
            }
        });
    }

    private async Task RecordRecentSearchAsync(string query)
    {
        await Task.Run(() => _history.RecordSearch(query));
        await RefreshRecentSearchesAsync();
    }

    private async Task OpenSelectedFavoriteAsync()
    {
        if (SelectedFavoriteNode?.SavedSearch is { } savedSearch)
        {
            var tab = SelectedSearchTab ?? AddSearchTab();
            SelectedSearchTab = tab;
            tab.Query = savedSearch.Query;
            if (tab.SearchCommand.CanExecute(null))
            {
                await tab.SearchCommand.ExecuteAsync();
            }
            return;
        }

        if (SelectedFavoriteNode?.Result is { } result)
        {
            if (result.IsFolder)
            {
                await BrowseResultFolderAsync(result);
                return;
            }
            await OpenPathAsync(ResolveWindowsPath(result));
        }
    }

    public async Task OpenFavoriteNodeAsync(FavoriteTreeNode? node)
    {
        if (node == null)
        {
            return;
        }

        SelectedFavoriteNode = node;
        await OpenSelectedFavoriteAsync();
    }

    public async Task ShowFavoriteNodeAsync(FavoriteTreeNode? node)
    {
        if (node?.Result is not { } result)
        {
            return;
        }

        await ShowSearchResultAsync(result);
    }

    public async Task RemoveFavoriteNodeAsync(FavoriteTreeNode? node)
    {
        if (node?.Result is { } result)
        {
            await SetSearchResultFavoriteAsync(result, false);
            return;
        }

        if (node?.SavedSearch is { } savedSearch)
        {
            await Task.Run(() => _history.DeleteSavedSearch(savedSearch.Id));
            await RefreshFavoritesAsync();
            Status = "Saved search removed";
        }
    }

    public async Task RemoveFavoriteGroupAsync(FavoriteTreeNode? node)
    {
        if (node == null || string.IsNullOrWhiteSpace(node.FolderPath) || node.FolderPath.StartsWith("__", StringComparison.Ordinal))
        {
            return;
        }

        var group = node.FolderPath;
        var favorites = await Task.Run(() => _history.Favorites(group));
        await Task.Run(() =>
        {
            foreach (var result in favorites)
            {
                var remainingGroups = result.Groups.Where(item => !item.Equals(group, StringComparison.OrdinalIgnoreCase));
                _history.SetGroups(result, remainingGroups);
            }
        });
        await RefreshFavoritesAsync();
        Status = $"Removed Favorites group {node.Name}";
    }

    private async Task OpenSelectedSearchResultAsync(SearchTabViewModel tab)
    {
        var result = tab.SelectedResult;
        if (result == null)
        {
            return;
        }

        if (result.IsFolder)
        {
            await BrowseResultFolderAsync(result);
            return;
        }

        await OpenPathAsync(ResolveWindowsPath(result));
    }

    public async Task OpenSearchResultAsync(SearchResult result)
    {
        var tab = SelectedSearchTab;
        if (tab == null)
        {
            return;
        }

        tab.SelectedResult = result;
        await OpenSelectedSearchResultAsync(tab);
    }

    private async Task BrowseResultFolderAsync(SearchResult result)
    {
        var path = ResolveWindowsPath(result);
        if (string.IsNullOrWhiteSpace(path))
        {
            Status = "QSurfer could not resolve a Windows path for the selected folder.";
            return;
        }

        IsNavigationVisible = true;
        BrowserLocation = path;
        await BrowseAsync();
    }

    private async Task BrowseSelectedSearchResultAsync(SearchTabViewModel tab)
    {
        var result = tab.SelectedResult;
        if (result == null)
        {
            return;
        }

        var path = ResolveWindowsPath(result);
        if (string.IsNullOrWhiteSpace(path) || !PathRootIsAvailable(path))
        {
            path = _mapper.TryResolveUnc(result) ?? path;
        }
        if (string.IsNullOrWhiteSpace(path))
        {
            SetTabStatus(tab, "QSurfer could not resolve a network path for the selected result.");
            return;
        }

        try
        {
            var arguments = result.IsFolder
                ? $"\"{path}\""
                : $"/select,\"{path}\"";
            Process.Start(new ProcessStartInfo("explorer.exe", arguments) { UseShellExecute = true });
            SetTabStatus(tab, "Showing location");
        }
        catch (Exception ex)
        {
            AppLogger.Error("browse", ex, $"show location failed path=\"{path}\"");
            SetTabStatus(tab, ex.Message);
        }
        await Task.CompletedTask;
    }

    public async Task ShowSearchResultAsync(SearchResult result)
    {
        var tab = SelectedSearchTab;
        if (tab == null)
        {
            return;
        }

        tab.SelectedResult = result;
        await BrowseSelectedSearchResultAsync(tab);
    }

    public void ShowSearchResultProperties(SearchResult result)
    {
        var path = ResolveWindowsPath(result);
        if (string.IsNullOrWhiteSpace(path))
        {
            Status = "QSurfer could not resolve a Windows path for the selected result.";
            return;
        }
        try
        {
            _browser.ShowProperties(new BrowserItem(
                result.FileName,
                path,
                result.IsFolder,
                result.Size,
                result.ModifiedDate ?? DateTime.MinValue));
        }
        catch (Exception ex)
        {
            AppLogger.Error("browse", ex, $"properties failed path=\"{path}\"");
            Status = ex.Message;
        }
    }

    private async Task ToggleSelectedSearchResultFavoriteAsync(SearchTabViewModel tab)
    {
        var result = tab.SelectedResult;
        if (result == null)
        {
            return;
        }

        var makeFavorite = !result.IsFavorite;
        try
        {
            await Task.Run(() => _history.SetStarred(result, makeFavorite));
            result.IsFavorite = makeFavorite;
            await RefreshFavoritesAsync();
            SetTabStatus(tab, makeFavorite ? "Added to favorites" : "Removed from favorites");
        }
        catch (Exception ex)
        {
            AppLogger.Error("favorites", ex, "QSurfer favorite update failed");
            SetTabStatus(tab, "Could not update favorites.");
        }
    }

    public async Task ToggleSearchResultFavoriteAsync(SearchResult result)
    {
        var tab = SelectedSearchTab;
        if (tab == null)
        {
            return;
        }

        tab.SelectedResult = result;
        await ToggleSelectedSearchResultFavoriteAsync(tab);
    }

    public SearchResult? SelectedBrowserItemAsResult() => SelectedBrowserItem is { } item
        ? CreateSearchResult(item)
        : null;

    private async Task ToggleSelectedBrowserItemFavoriteAsync()
    {
        var result = SelectedBrowserItemAsResult();
        if (result == null)
        {
            return;
        }

        var makeFavorite = await Task.Run(() => !_history.IsStarred(result));
        await SetSearchResultFavoriteAsync(result, makeFavorite);
    }

    public async Task SetSearchResultFavoriteAsync(SearchResult result, bool favorite)
    {
        try
        {
            await Task.Run(() => _history.SetStarred(result, favorite));
            result.IsFavorite = favorite;
            if (!favorite)
            {
                result.Groups = [];
            }
            await RefreshFavoritesAsync();
            if (SelectedSearchTab is { } tab)
            {
                SetTabStatus(tab, favorite ? "Added to favorites" : "Removed from favorites");
            }
            else
            {
                Status = favorite ? "Added to favorites" : "Removed from favorites";
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("favorites", ex, "QSurfer favorite update failed");
            Status = "Could not update favorites.";
        }
    }

    public async Task SetSearchResultsFavoriteAsync(IEnumerable<SearchResult> source, bool favorite)
    {
        var results = source.DistinctBy(HistoryStore.ResultKey, StringComparer.OrdinalIgnoreCase).ToList();
        if (results.Count == 0)
        {
            return;
        }

        try
        {
            await Task.Run(() => _history.SetStarred(results, favorite));
            foreach (var result in results)
            {
                result.IsFavorite = favorite;
                if (!favorite)
                {
                    result.Groups = [];
                }
            }
            await RefreshFavoritesAsync();
            Status = favorite ? $"Added {results.Count:n0} item(s) to Favorites" : $"Removed {results.Count:n0} item(s) from Favorites";
        }
        catch (Exception ex)
        {
            AppLogger.Error("favorites", ex, "QSurfer multi-favorite update failed");
            Status = "Could not update favorites.";
        }
    }

    public async Task<(IReadOnlyList<string> Groups, IReadOnlyList<string> SelectedGroups)> GetFavoriteGroupDataAsync(SearchResult result)
    {
        return await Task.Run(() =>
            ((IReadOnlyList<string>)_history.FavoriteGroups(), (IReadOnlyList<string>)_history.GroupsFor(result)));
    }

    public async Task SaveFavoriteGroupsAsync(IReadOnlyList<SearchResult> results, IReadOnlyList<string> groups)
    {
        if (results.Count == 0)
        {
            return;
        }

        await Task.Run(() => _history.SetGroups(results, groups));
        foreach (var result in results)
        {
            result.IsFavorite = true;
            result.Groups = groups.ToList();
        }
        await RefreshFavoritesAsync();
        Status = groups.Count == 0 ? "Favorite updated" : "Favorite groups updated";
    }

    public async Task SaveSearchAsync(SearchTabViewModel tab, string name)
    {
        var query = tab.Query.Trim();
        var displayName = name.Trim();
        if (string.IsNullOrWhiteSpace(query) || string.IsNullOrWhiteSpace(displayName))
        {
            return;
        }

        var saved = await Task.Run(() => _history.SaveSearch(displayName, query));
        if (!saved)
        {
            SetTabStatus(tab, "Could not save search.");
            return;
        }
        await RefreshFavoritesAsync();
        SetTabStatus(tab, $"Saved search: {displayName}");
    }

    private Task SaveCurrentSearchAsync(SearchTabViewModel tab) => SaveSearchAsync(tab, tab.Query);

    private async Task PaintDirectoryResultsAsync(
        SearchTabViewModel tab,
        Task<IReadOnlyList<SearchResult>> folders,
        CancellationToken token,
        int version,
        ISet<string> starredKeys)
    {
        try
        {
            var results = await folders;
            await Dispatcher.UIThread.InvokeAsync(() => AddVisibleResults(tab, results, token, version, starredKeys));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            AppLogger.Info("search", $"QSurfer directory search canceled tab=\"{tab.Title}\"");
        }
        catch (Exception ex)
        {
            AppLogger.Warn("search", $"QSurfer directory search skipped tab=\"{tab.Title}\" error=\"{ex.Message}\"");
        }
    }

    private void AddVisibleResults(SearchTabViewModel tab, IEnumerable<SearchResult> source, CancellationToken token, int version, ISet<string>? starredKeys = null)
    {
        if (token.IsCancellationRequested || tab.SearchVersion != version)
        {
            return;
        }

        var added = 0;
        var hidden = 0;
        var duplicates = 0;
        var accepted = new List<SearchResult>();
        foreach (var result in source)
        {
            if ((!_config.Behavior.ShowRecoverySystemFolders && IsRecoverySystemResult(result)) ||
                (!_config.Behavior.ShowQSurferSafetyCopies && IsQSurferSafetyCopy(result)) ||
                _rules.IsHidden(result))
            {
                hidden++;
                continue;
            }
            if (tab.ContainsResult(result))
            {
                duplicates++;
                continue;
            }

            try
            {
                result.WindowsPath = _mapper.TryResolve(result) ?? "";
                result.IsFavorite = starredKeys?.Contains(HistoryStore.ResultKey(result)) == true;
            }
            catch (Exception ex)
            {
                AppLogger.Warn("path", $"result path unavailable name=\"{result.FileName}\" path=\"{result.Path}\" error=\"{ex.Message}\"");
                result.WindowsPath = "";
            }
            accepted.Add(result);
            added++;
        }

        tab.AddResults(accepted);
        QueueResultIcons(accepted, token);

        if (added > 0)
        {
            SetTabStatus(tab, $"Searching {tab.Results.Count:n0} results");
        }
        AppLogger.Info("paint", $"QSurfer tab=\"{tab.Title}\" batch added={added} hidden={hidden} duplicates={duplicates} visible={tab.Results.Count}");
    }

    private void SetTabStatus(SearchTabViewModel tab, string status)
    {
        tab.Status = status;
        if (ReferenceEquals(tab, SelectedSearchTab))
        {
            Status = status;
        }
    }

    private void QueueResultIcons(IEnumerable<SearchResult> results, CancellationToken token)
    {
        foreach (var result in results)
        {
            if (token.IsCancellationRequested || result.IconSource != null)
            {
                continue;
            }

            var key = IconCacheKey(result);
            if (_iconCache.TryGetValue(key, out var cached))
            {
                result.IconSource = cached;
                continue;
            }

            lock (_iconLoadsInFlight)
            {
                if (!_iconLoadsInFlight.Add(key))
                {
                    continue;
                }
            }
            _ = LoadResultIconAsync(result, key, token);
        }
    }

    private async Task LoadResultIconAsync(SearchResult result, string key, CancellationToken token)
    {
        var enteredGate = false;
        try
        {
            await _iconLoadGate.WaitAsync(token);
            enteredGate = true;
            Bitmap? icon = null;
            if (_config.Behavior.UseQsirchThumbnails && result.HasThumbnailAction)
            {
                try
                {
                    var thumbnail = await _client.ThumbnailAsync(result, token);
                    if (thumbnail is { Length: > 0 })
                    {
                        using var stream = new MemoryStream(thumbnail);
                        icon = new Bitmap(stream);
                    }
                }
                catch
                {
                    icon = null;
                }
            }

            icon ??= await Task.Run(() => WindowsShellIconService.FileTypeIcon(result.Extension, result.IsFolder), token);
            if (icon == null)
            {
                return;
            }

            await Dispatcher.UIThread.InvokeAsync(() => ApplyCachedIcon(key, result, icon));
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            AppLogger.Warn("icons", $"shell icon failed extension=\"{result.Extension}\" error=\"{ex.Message}\"");
        }
        finally
        {
            lock (_iconLoadsInFlight)
            {
                _iconLoadsInFlight.Remove(key);
            }
            if (enteredGate)
            {
                _iconLoadGate.Release();
            }
        }
    }

    private void ApplyCachedIcon(string key, SearchResult result, Bitmap icon)
    {
        _iconCache[key] = icon;
        result.IconSource = icon;
        foreach (var candidate in SearchTabs.SelectMany(tab => tab.Results).Append(result).Distinct())
        {
            if (candidate.IconSource == null &&
                !(_config.Behavior.UseQsirchThumbnails && candidate.HasThumbnailAction) &&
                IconCacheKey(candidate).Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                candidate.IconSource = icon;
            }
        }
    }

    private string IconCacheKey(SearchResult result) =>
        _config.Behavior.UseQsirchThumbnails && result.HasThumbnailAction
            ? $"thumbnail|{result.Path}|{result.FileName}"
            : result.IsFolder
                ? "__folder__"
                : string.IsNullOrWhiteSpace(result.Extension) ? "__file__" : "." + result.Extension.TrimStart('.').ToLowerInvariant();

    private void QueueBrowserIcons(IEnumerable<BrowserItem> items, CancellationToken token)
    {
        foreach (var item in items)
        {
            if (token.IsCancellationRequested || item.IconSource != null)
            {
                continue;
            }

            var key = IconCacheKey(item);
            if (_iconCache.TryGetValue(key, out var cached))
            {
                item.IconSource = cached;
                continue;
            }

            lock (_iconLoadsInFlight)
            {
                if (!_iconLoadsInFlight.Add(key))
                {
                    continue;
                }
            }
            _ = LoadBrowserIconAsync(item, key, token);
        }
    }

    private async Task LoadBrowserIconAsync(BrowserItem item, string key, CancellationToken token)
    {
        var enteredGate = false;
        try
        {
            await _iconLoadGate.WaitAsync(token);
            enteredGate = true;
            var icon = await Task.Run(() => WindowsShellIconService.FileTypeIcon(Path.GetExtension(item.Name), item.IsFolder), token);
            if (icon != null)
            {
                await Dispatcher.UIThread.InvokeAsync(() => ApplyCachedBrowserIcon(key, item, icon));
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            AppLogger.Warn("icons", $"browser shell icon failed name=\"{item.Name}\" error=\"{ex.Message}\"");
        }
        finally
        {
            lock (_iconLoadsInFlight)
            {
                _iconLoadsInFlight.Remove(key);
            }
            if (enteredGate)
            {
                _iconLoadGate.Release();
            }
        }
    }

    private void ApplyCachedBrowserIcon(string key, BrowserItem item, Bitmap icon)
    {
        _iconCache[key] = icon;
        item.IconSource = icon;
        foreach (var candidate in BrowserItems.Where(candidate => candidate.IconSource == null && IconCacheKey(candidate).Equals(key, StringComparison.OrdinalIgnoreCase)))
        {
            candidate.IconSource = icon;
        }
    }

    private static string IconCacheKey(BrowserItem item) =>
        item.IsFolder
            ? "__folder__"
            : string.IsNullOrWhiteSpace(Path.GetExtension(item.Name)) ? "__file__" : Path.GetExtension(item.Name).ToLowerInvariant();

    private static (string? SortBy, string SortDirection) ServerSortFor(SearchTabViewModel tab) =>
        tab.PrimarySortKey switch
        {
            "modified" => ("modified", "desc"),
            "name" => ("name", "asc"),
            "size" => ("size", "desc"),
            _ => (null, "desc"),
        };

    // Qsirch's bare query searches indexed document content as well as filenames.
    // The explicit name field keeps the default search fast and filename-only.
    private static string BuildServerQuery(string query, bool searchContents) =>
        searchContents ? query : $"name:\"{query.Replace("\"", "\\\"")}\"";

    private static IReadOnlyList<SearchResult> RecentResults(IEnumerable<SearchResult> results, DateTime cutoff) =>
        results.Where(result => result.ModifiedDate is { } modified && modified.Date >= cutoff).ToList();

    private void SearchTabPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not SearchTabViewModel tab)
        {
            return;
        }

        if (e.PropertyName == nameof(SearchTabViewModel.SelectedResult) && tab.SelectedResult is { } result)
        {
            SetPreview(result);
        }
        if (e.PropertyName == nameof(SearchTabViewModel.Query) &&
            string.IsNullOrWhiteSpace(tab.Query) &&
            _config.Behavior.ClearResultsWithQuery &&
            !tab.IsPinned)
        {
            _ = ClearSearchTabAsync(tab);
        }
        if (e.PropertyName == nameof(SearchTabViewModel.SearchContents) &&
            !string.IsNullOrWhiteSpace(tab.Query) &&
            !tab.IsSearching)
        {
            _ = tab.SearchCommand.ExecuteAsync();
        }
        if (tab.IsPinned && e.PropertyName is nameof(SearchTabViewModel.Query) or
            nameof(SearchTabViewModel.SelectedViewMode) or
            nameof(SearchTabViewModel.SelectedSortMode) or
            nameof(SearchTabViewModel.SortSpecification) or
            nameof(SearchTabViewModel.DateFrom) or
            nameof(SearchTabViewModel.DateTo) or
            nameof(SearchTabViewModel.ExactMatch) or
            nameof(SearchTabViewModel.SearchContents))
        {
            PersistPinnedTabs();
        }
    }

    private void SetPreview(SearchResult? result)
    {
        if (result == null)
        {
            PreviewTitle = "Select a result";
            PreviewDescription = "Choose a file or folder to see its details here.";
            PreviewLocation = "";
            return;
        }

        PreviewTitle = result.FileName;
        PreviewDescription = result.IsFolder
            ? "Folder"
            : string.IsNullOrWhiteSpace(result.Kind) ? "File" : result.Kind;
        PreviewLocation = string.IsNullOrWhiteSpace(result.WindowsPath)
            ? result.DisplayPath
            : result.WindowsPath;
    }

    private async Task NavigateUpAsync()
    {
        var parent = NasFileBrowser.GetParentFolder(BrowserLocation);
        if (parent == null)
        {
            return;
        }

        BrowserLocation = parent;
        await BrowseAsync();
    }

    private async Task NavigateHistoryAsync(int direction)
    {
        var targetIndex = _browserHistoryIndex + direction;
        if (targetIndex < 0 || targetIndex >= _browserHistory.Count)
        {
            return;
        }

        _browserHistoryIndex = targetIndex;
        BrowserLocation = _browserHistory[_browserHistoryIndex];
        UpdateNavigationHistoryCommands();
        await BrowseAsync(addToHistory: false);
    }

    private async Task BrowseAsync(bool addToHistory = true)
    {
        var browserTab = SelectedSearchTab;
        if (browserTab == null)
        {
            return;
        }

        var location = _mapper.ResolveBrowserPath((BrowserLocation ?? "").Trim());
        if (string.IsNullOrWhiteSpace(location))
        {
            return;
        }

        if (!location.Equals(BrowserLocation, StringComparison.OrdinalIgnoreCase))
        {
            BrowserLocation = location;
        }

        _browseCancellation?.Cancel();
        _browseCancellation?.Dispose();
        _browseCancellation = new CancellationTokenSource();
        var token = _browseCancellation.Token;

        SelectedBrowserItem = null;
        var flattenRecycleBin = _config.Behavior.FlattenRecycleBin && IsRecycleBinRootPath(location);
        if (flattenRecycleBin)
        {
            // A recursive scan can take a while on a large NAS Recycle Bin. Clear the
            // prior directory contents so only actual deleted files are ever shown here.
            BrowserItems = [];
        }

        Status = flattenRecycleBin ? "Scanning Recycle Bin..." : "Loading folder...";
        browserTab.Status = Status;
        try
        {
            DirectoryReadResult listing;
            if (flattenRecycleBin)
            {
                IProgress<IReadOnlyList<BrowserItem>> progress = new Progress<IReadOnlyList<BrowserItem>>(batch =>
                {
                    if (token.IsCancellationRequested || !ReferenceEquals(browserTab, SelectedSearchTab))
                    {
                        return;
                    }

                    var visibleBatch = batch
                        .Where(item => ShouldShowBrowserItem(item, location))
                        .ToList();
                    if (visibleBatch.Count == 0)
                    {
                        return;
                    }

                    foreach (var item in visibleBatch)
                    {
                        BrowserItems.Add(item);
                    }
                    QueueBrowserIcons(visibleBatch, token);
                    Status = $"Scanning Recycle Bin... {BrowserItems.Count:n0} files found";
                    browserTab.Status = Status;
                });
                listing = await _browser.BrowseRecycleBinAsync(location, progress.Report, token);
            }
            else
            {
                listing = await _browser.BrowseAsync(location, token);
            }
            if (token.IsCancellationRequested || !ReferenceEquals(browserTab, SelectedSearchTab))
            {
                return;
            }

            BrowserLocation = listing.FolderPath;
            browserTab.SetBrowseLocation(BrowserLocation);
            UpdateBrowserBreadcrumbs(listing.FolderPath);
            // Swap completed folder data in one step. Clearing a shared observable
            // collection while hidden browser views retain a selection can crash Avalonia.
            var visibleItems = listing.Items
                .Where(item => ShouldShowBrowserItem(item, listing.FolderPath))
                .ToList();
            BrowserItems = new ObservableCollection<BrowserItem>(visibleItems);
            QueueBrowserIcons(BrowserItems, token);
            Status = listing.SkippedCount == 0
                ? $"Ready {BrowserItems.Count:n0} items"
                : $"Ready {BrowserItems.Count:n0} items, {listing.SkippedCount:n0} unavailable";
            browserTab.Status = Status;
            if (addToHistory)
            {
                RecordBrowserLocation(listing.FolderPath);
            }
            AppLogger.Info("browse", $"folder=\"{listing.FolderPath}\" items={listing.Items.Count} skipped={listing.SkippedCount}");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            AppLogger.Error("browse", ex, $"failed folder=\"{location}\"");
            browserTab.Status = ex.Message;
            if (ReferenceEquals(browserTab, SelectedSearchTab))
            {
                Status = ex.Message;
            }
        }
    }

    private async Task BrowseAtLocationAsync()
    {
        IsNavigationVisible = true;
        await BrowseAsync();
    }

    private void EnsureNavigationRoots()
    {
        var nasRoot = NasNavigationRoot(_config.Host);
        if (!string.IsNullOrWhiteSpace(nasRoot) &&
            !NavigationRoots.Any(node => node.FullPath.Equals(nasRoot, StringComparison.OrdinalIgnoreCase)))
        {
            NavigationRoots.Add(CreateNavigationNode(NavigationDisplayName(nasRoot), nasRoot));
        }

        foreach (var (name, path) in LocalNavigationFolders())
        {
            if (NavigationRoots.Any(node => node.FullPath.Equals(path, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }
            NavigationRoots.Add(CreateNavigationNode(name, path));
        }

        var roots = PathMapper.DiscoverWindowsPathMappings()
            .Select(mapping => NormalizeNavigationRoot(mapping.MappedRoot))
            .Concat(_config.PathMappings
            .Where(mapping => !MappingBelongsToConfiguredNas(mapping))
            .Select(mapping => NormalizeNavigationRoot(mapping.MappedRoot)))
            .Where(path => !string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (!string.IsNullOrWhiteSpace(BrowserLocation) && Path.IsPathFullyQualified(BrowserLocation) &&
            !roots.Any(path => BrowserLocation.StartsWith(path, StringComparison.OrdinalIgnoreCase)))
        {
            roots.Add(BrowserLocation);
        }

        foreach (var root in roots)
        {
            if (NavigationRoots.Any(node => node.FullPath.Equals(root, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }
            NavigationRoots.Add(CreateNavigationNode(NavigationDisplayName(root), root));
        }
    }

    private void RefreshNavigationRoots()
    {
        NavigationRoots.Clear();
        EnsureNavigationRoots();
        AppLogger.Info("browse", $"navigation roots refreshed count={NavigationRoots.Count} host=\"{_config.Host}\"");
    }

    private async Task LoadNasNavigationRootAsync()
    {
        var nasRoot = NasNavigationRoot(_config.Host);
        if (string.IsNullOrWhiteSpace(nasRoot))
        {
            SetNasNavigationState(false, "NAS navigation is not configured.");
            return;
        }

        if (_isLoadingNasNavigation)
        {
            return;
        }

        var root = NavigationRoots.FirstOrDefault(node =>
            node.FullPath.Equals(nasRoot, StringComparison.OrdinalIgnoreCase));
        if (root != null)
        {
            if (root.ChildrenLoaded)
            {
                SetNasNavigationState(true, "Online");
                return;
            }

            _isLoadingNasNavigation = true;
            SetNasNavigationState(false, "Checking NAS folders...");
            root.EnsurePlaceholder();
            try
            {
                await LoadNavigationChildrenAsync(root);
                FlattenNasNavigationRoot(root);
            }
            finally
            {
                _isLoadingNasNavigation = false;
            }
        }
    }

    private async void NasNavigationRetryTimer_Tick(object? sender, EventArgs e)
    {
        if (!IsNasNavigationOnline)
        {
            await LoadNasNavigationRootAsync();
        }
    }

    private string NavigationTreeDisplayName(string path)
    {
        var name = NavigationDisplayName(path);
        var resolved = NormalizeNavigationRoot(_mapper.ResolveBrowserPath(path));
        if (IsDriveRoot(resolved) && !path.Equals(resolved, StringComparison.OrdinalIgnoreCase))
        {
            return $"{name} ({resolved})";
        }

        return name;
    }

    private void FlattenNasNavigationRoot(NavigationTreeNode root)
    {
        if (!root.ChildrenLoaded)
        {
            return;
        }

        var shares = root.Children.Where(node => !node.IsPlaceholder).ToList();
        if (shares.Count == 0)
        {
            return;
        }

        var rootIndex = NavigationRoots.IndexOf(root);
        if (rootIndex < 0)
        {
            return;
        }

        // Shares resolve to their mapped drive when one exists. Replace the generic
        // mapped-drive root with the populated NAS share so its recovery entry stays attached.
        var duplicateRoots = NavigationRoots
            .Where(node => !ReferenceEquals(node, root) && shares.Any(share =>
                share.FullPath.Equals(node.FullPath, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        var insertionIndex = rootIndex - duplicateRoots.Count(node => NavigationRoots.IndexOf(node) < rootIndex);

        NavigationRoots.Remove(root);
        foreach (var duplicate in duplicateRoots)
        {
            NavigationRoots.Remove(duplicate);
        }

        insertionIndex = Math.Clamp(insertionIndex, 0, NavigationRoots.Count);
        for (var index = 0; index < shares.Count; index++)
        {
            NavigationRoots.Insert(insertionIndex + index, shares[index]);
        }
    }

    private NavigationTreeNode CreateNavigationNode(
        string name,
        string path,
        bool isRecoveryFolder = false,
        bool isShareRoot = false)
    {
        var node = new NavigationTreeNode
        {
            Name = name,
            FullPath = _mapper.ResolveBrowserPath(path),
            IsRecoveryFolder = isRecoveryFolder,
            IsShareRoot = isShareRoot,
            IconSource = isRecoveryFolder ? RecycleBinIcon() : null,
            ExpandAsync = LoadNavigationChildrenAsync,
        };
        if (!_config.Behavior.FlattenRecycleBin || !IsRecycleBinRootPath(node.FullPath))
        {
            node.EnsurePlaceholder();
        }
        node.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(NavigationTreeNode.IsExpanded))
            {
                RecordNavigationExpansion(node);
            }
        };
        if (ShouldRestoreExpanded(node.FullPath))
        {
            node.IsExpanded = true;
        }
        return node;
    }

    private bool IsNasNavigationRoot(string path)
    {
        var root = NasNavigationRoot(_config.Host);
        return !string.IsNullOrWhiteSpace(root) && root.Equals(path, StringComparison.OrdinalIgnoreCase);
    }

    private void SetNasNavigationState(bool online, string status)
    {
        IsNasNavigationOnline = online;
        NasNavigationStatus = status;
        OnPropertyChanged(nameof(ShowNasNavigationStatus));
    }

    private List<string> NavigationExpandedPaths
    {
        get
        {
            _config.Behavior ??= new BehaviorConfig();
            return _config.Behavior.NavigationExpandedPaths ??= [];
        }
    }

    private bool ShouldRestoreExpanded(string path) =>
        NavigationExpandedPaths.Any(saved => string.Equals(saved, path, StringComparison.OrdinalIgnoreCase));

    private void RecordNavigationExpansion(NavigationTreeNode node)
    {
        if (node.IsPlaceholder || string.IsNullOrWhiteSpace(node.FullPath))
        {
            return;
        }

        var expandedPaths = NavigationExpandedPaths;
        expandedPaths.RemoveAll(path => string.Equals(path, node.FullPath, StringComparison.OrdinalIgnoreCase));
        if (node.IsExpanded)
        {
            expandedPaths.Add(node.FullPath);
        }
    }

    private bool MappingBelongsToConfiguredNas(PathMapping mapping)
    {
        var nasRoot = NasNavigationRoot(_config.Host)?.TrimStart('\\');
        var rawShareRoot = (mapping.ShareRoot ?? "").Trim();
        var shareRoot = rawShareRoot.Trim('\\', '/');
        if (string.IsNullOrWhiteSpace(nasRoot) || string.IsNullOrWhiteSpace(shareRoot))
        {
            return false;
        }

        return !rawShareRoot.StartsWith(@"\\", StringComparison.Ordinal) ||
               shareRoot.StartsWith(nasRoot + "\\", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsDriveRoot(string path) =>
        path.Length == 3 && path[1] == ':' && (path[2] == '\\' || path[2] == '/');

    private static bool IsRecycleFolder(string name) =>
        name.Equals("@Recycle", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("@RecycleBin", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("#recycle", StringComparison.OrdinalIgnoreCase);

    private static bool IsSnapshotFolder(string name) =>
        name.Equals("@Recently-Snapshot", StringComparison.OrdinalIgnoreCase);

    private static bool IsRecoverySystemFolder(string name) =>
        IsRecycleFolder(name) || IsSnapshotFolder(name);

    private static bool IsRecoverySystemResult(SearchResult result) =>
        IsRecoverySystemFolder(result.FileName) ||
        result.Path.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries).Any(IsRecoverySystemFolder);

    private static bool IsRecycleFolderPath(string path) =>
        path.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries).Any(IsRecycleFolder);

    private static bool IsRecycleBinRootPath(string path)
    {
        var trimmed = (path ?? "").Trim().TrimEnd('\\', '/');
        return IsRecycleFolder(Path.GetFileName(trimmed));
    }

    private static bool IsQSurferSafetyCopy(SearchResult result) =>
        IsQSurferSafetyCopyName(result.FileName, result.IsFolder);

    private static bool IsQSurferSafetyCopy(BrowserItem item) =>
        IsQSurferSafetyCopyName(item.Name, item.IsFolder);

    private static bool IsQSurferSafetyCopyName(string name, bool isFolder) => isFolder
        ? name.EndsWith("@qsurfer", StringComparison.OrdinalIgnoreCase)
        : name.EndsWith(".qsurfer", StringComparison.OrdinalIgnoreCase) ||
          Path.GetFileNameWithoutExtension(name).EndsWith(".qsurfer", StringComparison.OrdinalIgnoreCase);

    private bool ShouldShowBrowserItem(BrowserItem item, string folderPath) =>
        (_config.Behavior.ShowRecoverySystemFolders || !IsRecoverySystemFolder(item.Name)) &&
        (_config.Behavior.ShowQSurferSafetyCopies || !IsQSurferSafetyCopy(item)) &&
        (item.IsFolder || !item.Name.StartsWith('~') || _config.Behavior.ShowHiddenTemporaryFiles);

    private Bitmap? RecycleBinIcon() => _recycleBinIcon ??= WindowsShellIconService.RecycleBinIcon();

    private void RecordBrowserLocation(string location)
    {
        if (_browserHistoryIndex >= 0 && _browserHistory[_browserHistoryIndex].Equals(location, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }
        if (_browserHistoryIndex < _browserHistory.Count - 1)
        {
            _browserHistory.RemoveRange(_browserHistoryIndex + 1, _browserHistory.Count - _browserHistoryIndex - 1);
        }
        _browserHistory.Add(location);
        _browserHistoryIndex = _browserHistory.Count - 1;
        UpdateNavigationHistoryCommands();
    }

    private BrowserTabState CaptureBrowserTab() => new(
        BrowserLocation,
        BrowserItems,
        BrowserBreadcrumbs.ToList(),
        _browserHistory.ToList(),
        _browserHistoryIndex,
        SelectedBrowserItem);

    private void RestoreBrowserTab(SearchTabViewModel tab)
    {
        if (!_browserTabs.TryGetValue(tab, out var state))
        {
            state = new BrowserTabState("", [], [], [], -1, null);
            _browserTabs[tab] = state;
        }

        BrowserItems = state.Items;
        BrowserBreadcrumbs.Clear();
        foreach (var breadcrumb in state.Breadcrumbs)
        {
            BrowserBreadcrumbs.Add(breadcrumb);
        }

        _browserHistory.Clear();
        _browserHistory.AddRange(state.History);
        _browserHistoryIndex = state.HistoryIndex;
        BrowserLocation = state.Location;
        SelectedBrowserItem = state.SelectedItem;
        UpdateNavigationHistoryCommands();
    }

    private void UpdateBrowserBreadcrumbs(string folderPath)
    {
        var segments = new List<(string Name, string FullPath)>();
        var normalized = folderPath.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (normalized.StartsWith(@"\\", StringComparison.Ordinal))
        {
            var current = @"\\";
            foreach (var part in normalized.TrimStart('\\', '/').Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries))
            {
                current = current == @"\\" ? current + part : current + "\\" + part;
                segments.Add((part, current));
            }
        }
        else
        {
            var root = Path.GetPathRoot(folderPath);
            if (!string.IsNullOrWhiteSpace(root))
            {
                var rootPath = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                segments.Add((root, root));
                var remainder = folderPath[root.Length..].Trim(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var current = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                foreach (var part in remainder.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
                {
                    current += Path.DirectorySeparatorChar + part;
                    segments.Add((part, current));
                }
            }
        }

        BrowserBreadcrumbs.Clear();
        for (var index = 0; index < segments.Count; index++)
        {
            var segment = segments[index];
            BrowserBreadcrumbs.Add(new BrowserBreadcrumb(segment.Name, segment.FullPath, index == segments.Count - 1));
        }
    }

    private void UpdateNavigationHistoryCommands()
    {
        OnPropertyChanged(nameof(CanNavigateBack));
        OnPropertyChanged(nameof(CanNavigateForward));
        NavigateBackCommand.RaiseCanExecuteChanged();
        NavigateForwardCommand.RaiseCanExecuteChanged();
    }

    private static string NavigationDisplayName(string path)
    {
        var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return Path.GetFileName(trimmed) is { Length: > 0 } name ? name : trimmed;
    }

    private static string? NasNavigationRoot(string host)
    {
        var value = (host ?? "").Trim().Trim('\\', '/');
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            value = uri.Host;
        }
        else if (value.Contains(':', StringComparison.Ordinal))
        {
            value = value[..value.IndexOf(':')];
        }

        return string.IsNullOrWhiteSpace(value) || value.IndexOfAny(['\\', '/', '?', '#']) >= 0
            ? null
            : @"\\" + value;
    }

    private static IEnumerable<(string Name, string Path)> LocalNavigationFolders()
    {
        var folders = new (string Name, Environment.SpecialFolder Folder)[]
        {
            ("Desktop", Environment.SpecialFolder.DesktopDirectory),
            ("Documents", Environment.SpecialFolder.MyDocuments),
            ("Pictures", Environment.SpecialFolder.MyPictures),
            ("Music", Environment.SpecialFolder.MyMusic),
            ("Videos", Environment.SpecialFolder.MyVideos),
            ("Downloads", Environment.SpecialFolder.UserProfile),
        };

        foreach (var (name, folder) in folders.OrderBy(folder => folder.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            var path = Environment.GetFolderPath(folder);
            if (name == "Downloads" && !string.IsNullOrWhiteSpace(path))
            {
                path = Path.Combine(path, "Downloads");
            }
            if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
            {
                yield return (name, path);
            }
        }
    }

    private static string NormalizeNavigationRoot(string path)
    {
        var trimmed = (path ?? "").Trim();
        return trimmed.Length == 3 && trimmed[1] == ':' && (trimmed[2] == '\\' || trimmed[2] == '/')
            ? trimmed
            : trimmed.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    public async Task OpenSelectedBrowserItemAsync()
    {
        var item = SelectedBrowserItem;
        if (item == null)
        {
            return;
        }

        if (item.IsFolder)
        {
            BrowserLocation = item.FullPath;
            await BrowseAsync();
            return;
        }

        await OpenPathAsync(item.FullPath);
    }

    public void CopyBrowserItems(IEnumerable<BrowserItem> items, bool cut)
    {
        _browserClipboard.Clear();
        _browserClipboard.AddRange(items.DistinctBy(item => item.FullPath, StringComparer.OrdinalIgnoreCase));
        _browserClipboardIsCut = cut;
        PasteBrowserItemsCommand.RaiseCanExecuteChanged();
        OnPropertyChanged(nameof(CanPasteBrowserItems));
        Status = _browserClipboard.Count == 0 ? "Nothing selected" : cut ? "Cut item ready to paste" : "Copied item ready to paste";
    }

    public async Task CreateBrowserFolderAsync(string name)
    {
        if (string.IsNullOrWhiteSpace(BrowserLocation)) return;
        try
        {
            await _browser.CreateFolderAsync(BrowserLocation, name);
            await BrowseAsync(addToHistory: false);
            Status = "Folder created";
        }
        catch (Exception ex)
        {
            AppLogger.Error("browse", ex, "create folder failed");
            Status = ex.Message;
        }
    }

    public async Task RenameBrowserItemAsync(BrowserItem item, string name)
    {
        try
        {
            await _browser.RenameAsync(item, name);
            await BrowseAsync(addToHistory: false);
            Status = "Renamed";
        }
        catch (Exception ex)
        {
            AppLogger.Error("browse", ex, $"rename failed path=\"{item.FullPath}\"");
            Status = ex.Message;
        }
    }

    public async Task DeleteBrowserItemsAsync(IEnumerable<BrowserItem> items)
    {
        var selected = items.DistinctBy(item => item.FullPath, StringComparer.OrdinalIgnoreCase).ToList();
        if (selected.Count == 0) return;
        try
        {
            foreach (var item in selected)
            {
                await _browser.DeleteAsync(item);
            }
            await BrowseAsync(addToHistory: false);
            Status = selected.Count == 1 ? "Deleted" : $"Deleted {selected.Count:n0} items";
        }
        catch (Exception ex)
        {
            AppLogger.Error("browse", ex, "delete failed");
            Status = ex.Message;
        }
    }

    public async Task RestoreRecycleItemsAsync(IEnumerable<BrowserItem> items, bool replaceExistingFiles)
    {
        try
        {
            var outcome = await _browser.RestoreFromRecycleAsync(items, replaceExistingFiles);
            await BrowseAsync(addToHistory: false);
            Status = outcome.RestoredCount == 1 ? "Restored 1 item" : $"Restored {outcome.RestoredCount:n0} items";
            AppLogger.Info("browse", $"recycle restored count={outcome.RestoredCount}");
        }
        catch (Exception ex)
        {
            AppLogger.Error("browse", ex, "recycle restore failed");
            Status = ex.Message;
        }
    }

    public async Task CreateBrowserShortcutAsync(BrowserItem item)
    {
        try
        {
            await _browser.CreateShortcutAsync(item, BrowserLocation);
            await BrowseAsync(addToHistory: false);
            Status = "Shortcut created";
        }
        catch (Exception ex)
        {
            AppLogger.Error("browse", ex, $"shortcut failed path=\"{item.FullPath}\"");
            Status = ex.Message;
        }
    }

    public void ShowBrowserItemProperties(BrowserItem item)
    {
        try
        {
            _browser.ShowProperties(item);
        }
        catch (Exception ex)
        {
            AppLogger.Error("browse", ex, $"properties failed path=\"{item.FullPath}\"");
            Status = ex.Message;
        }
    }

    private async Task PasteBrowserItemsAsync()
    {
        if (_browserClipboard.Count == 0 || string.IsNullOrWhiteSpace(BrowserLocation)) return;
        try
        {
            var count = await _browser.CopyAsync(_browserClipboard, BrowserLocation, _browserClipboardIsCut);
            if (_browserClipboardIsCut)
            {
                _browserClipboard.Clear();
                _browserClipboardIsCut = false;
                PasteBrowserItemsCommand.RaiseCanExecuteChanged();
                OnPropertyChanged(nameof(CanPasteBrowserItems));
            }
            await BrowseAsync(addToHistory: false);
            Status = count == 1 ? "Pasted" : $"Pasted {count:n0} items";
        }
        catch (Exception ex)
        {
            AppLogger.Error("browse", ex, "paste failed");
            Status = ex.Message;
        }
    }

    private static SearchResult CreateSearchResult(BrowserItem item) => new()
    {
        Name = item.Name,
        Extension = item.IsFolder ? "" : Path.GetExtension(item.Name).TrimStart('.'),
        Path = NasFileBrowser.GetParentFolder(item.FullPath) ?? item.FullPath,
        ResolvedPath = item.FullPath,
        WindowsPath = item.FullPath,
        Type = item.Kind,
        Size = item.Size,
        Modified = item.Modified == DateTime.MinValue ? "" : item.Modified.ToString("O"),
        IsFolder = item.IsFolder,
    };

    private Task OpenPathAsync(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            Status = "QSurfer could not resolve a Windows path for the selected result.";
            return Task.CompletedTask;
        }

        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            Status = "Opened";
        }
        catch (Exception ex)
        {
            AppLogger.Error("browse", ex, $"open failed path=\"{path}\"");
            Status = ex.Message;
        }
        return Task.CompletedTask;
    }

    private static bool PathRootIsAvailable(string path)
    {
        var root = Path.GetPathRoot(path);
        if (string.IsNullOrWhiteSpace(root) || !root.EndsWith(":\\", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return DriveInfo.GetDrives().Any(drive => drive.Name.Equals(root, StringComparison.OrdinalIgnoreCase));
    }

    private void PersistPinnedTabs()
    {
        _config.PinnedTabs = SearchTabs.Where(tab => tab.IsPinned)
            .Select(tab => new PinnedTabConfig
            {
                Title = tab.Title,
                Query = tab.Query,
                ViewKey = tab.SelectedViewMode.Key,
                SortValue = tab.SortSpecification,
                TypeIndex = Enumerable.Range(0, _fileTypes.Count)
                    .FirstOrDefault(index => ReferenceEquals(_fileTypes[index], tab.SelectedFileType)),
                TypeNames = tab.TypeFilterOptions.Where(option => option.IsSelected).Select(option => option.Name).ToList(),
                DateFrom = tab.DateFrom,
                DateTo = tab.DateTo,
                ExactMatch = tab.ExactMatch,
                SearchContents = tab.SearchContents,
            })
            .ToList();
        ConfigStore.Save(_config);
    }

    private static IReadOnlyList<FavoriteTreeNode> BuildFavoritesTree(
        IEnumerable<SearchResult> favorites,
        IEnumerable<SavedSearch> savedSearches,
        ISet<string>? expandedGroups = null)
    {
        var roots = new List<FavoriteTreeNode>();
        var folders = new Dictionary<string, FavoriteTreeNode>(StringComparer.OrdinalIgnoreCase);

        var saved = savedSearches.OrderBy(search => search.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        if (saved.Count > 0)
        {
            var savedRoot = new FavoriteTreeNode { Name = "Saved searches", FolderPath = "__saved_searches__", IsExpanded = IsFavoriteGroupExpanded("__saved_searches__", expandedGroups, true) };
            foreach (var search in saved)
            {
                savedRoot.Children.Add(new FavoriteTreeNode { Name = search.Name, SavedSearch = search });
            }
            roots.Add(savedRoot);
        }

        foreach (var result in favorites
                     .DistinctBy(HistoryStore.ResultKey, StringComparer.OrdinalIgnoreCase)
                     .OrderBy(item => item.Groups.FirstOrDefault() ?? "\uffff", StringComparer.CurrentCultureIgnoreCase)
                     .ThenBy(item => item.FileName, StringComparer.CurrentCultureIgnoreCase))
        {
            var groups = result.Groups
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value.Trim().Replace('/', '\\').Trim('\\'))
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (groups.Count == 0)
            {
                var unfiled = roots.FirstOrDefault(node => node.FolderPath == "__unfiled__");
                if (unfiled == null)
                {
                    unfiled = new FavoriteTreeNode { Name = "Unfiled favorites", FolderPath = "__unfiled__", IsExpanded = IsFavoriteGroupExpanded("__unfiled__", expandedGroups, true) };
                    roots.Add(unfiled);
                }
                unfiled.Children.Add(new FavoriteTreeNode { Name = result.FileName, Result = result });
                continue;
            }

            foreach (var group in groups)
            {
                var path = "";
                ICollection<FavoriteTreeNode> siblings = roots;
                FavoriteTreeNode? parent = null;
                foreach (var part in group.Split('\\', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    path = string.IsNullOrWhiteSpace(path) ? part : path + "\\" + part;
                    if (!folders.TryGetValue(path, out var node))
                    {
                        node = new FavoriteTreeNode { Name = part, FolderPath = path, IsExpanded = IsFavoriteGroupExpanded(path, expandedGroups, !path.Contains('\\')) };
                        folders[path] = node;
                        siblings.Add(node);
                    }
                    siblings = node.Children;
                    parent = node;
                }
                parent!.Children.Add(new FavoriteTreeNode { Name = result.FileName, Result = result });
            }
        }

        return roots;
    }

    private static IEnumerable<FavoriteTreeNode> FlattenFavoriteNodes(IEnumerable<FavoriteTreeNode> nodes) =>
        nodes.SelectMany(node => new[] { node }.Concat(FlattenFavoriteNodes(node.Children)));

    private static bool IsFavoriteGroupExpanded(string path, ISet<string>? expandedGroups, bool defaultValue) =>
        expandedGroups?.Contains(path) ?? defaultValue;

    private static string? FavoriteNodeKey(FavoriteTreeNode? node)
    {
        if (node == null)
        {
            return null;
        }
        if (node.Result != null)
        {
            return "result:" + HistoryStore.ResultKey(node.Result);
        }
        return node.SavedSearch != null ? "search:" + node.SavedSearch.Id : "group:" + node.FolderPath;
    }

    private static FavoriteTreeNode? FindFavoriteNode(IEnumerable<FavoriteTreeNode> nodes, string? key) =>
        string.IsNullOrWhiteSpace(key)
            ? null
            : FlattenFavoriteNodes(nodes).FirstOrDefault(node => FavoriteNodeKey(node) == key);

    private void BrowserItemsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(HasBrowserItems));
        OnPropertyChanged(nameof(HasNoBrowserItems));
    }

    private static bool SameResult(SearchResult left, SearchResult right) =>
        string.Equals(left.Path, right.Path, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(left.FileName, right.FileName, StringComparison.OrdinalIgnoreCase);

    private sealed record BrowserTabState(
        string Location,
        ObservableCollection<BrowserItem> Items,
        IReadOnlyList<BrowserBreadcrumb> Breadcrumbs,
        IReadOnlyList<string> History,
        int HistoryIndex,
        BrowserItem? SelectedItem);

    public void Dispose()
    {
        _nasNavigationRetryTimer.Stop();
        _nasNavigationRetryTimer.Tick -= NasNavigationRetryTimer_Tick;
        foreach (var tab in SearchTabs)
        {
            tab.Dispose();
        }
        _browseCancellation?.Cancel();
        _browseCancellation?.Dispose();
        _client.Dispose();
        foreach (var icon in _iconCache.Values.Distinct())
        {
            icon.Dispose();
        }
        _recycleBinIcon?.Dispose();
        _iconCache.Clear();
        _browserItems.CollectionChanged -= BrowserItemsCollectionChanged;
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
