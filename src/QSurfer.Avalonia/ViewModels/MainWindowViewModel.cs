using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Avalonia.Threading;
using Avalonia.Media.Imaging;
using QSurfer.Core.Models;
using QSurfer.Core.Services;
using QSurfer.Avalonia.Services;

namespace QSurfer.Avalonia.ViewModels;

public sealed class MainWindowViewModel : INotifyPropertyChanged, IDisposable
{
    private const string HomeNavigationPath = "qsurfer://home";
    private const int AddressSuggestionLimit = 12;
    private const int AddressPathBranchLimit = 8;
    private const int InitialAddressSuggestionDebounceMilliseconds = 750;
    private const int NestedAddressSuggestionDebounceMilliseconds = 50;
    private static readonly TimeSpan AddressDirectorySearchCacheLifetime = TimeSpan.FromMinutes(2);
    private static readonly Regex TrailingScopeClause = new(
        @"(?:^|\s)in:(?:""(?<quoted>[^""]+)""|(?<path>.+))\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private readonly AppConfig _config;
    private readonly IReadOnlyList<FileTypeFilter> _fileTypes;
    private readonly HistoryStore _history;
    private ISearchProvider _client;
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
    private readonly Dictionary<NavigationTreeNode, CancellationTokenSource> _navigationLoadCancellations = [];
    private readonly List<string> _browserHistory = [];
    private readonly List<BrowserItem> _browserClipboard = [];
    private readonly List<SearchTabViewModel> _pinnedTabsAwaitingReload = [];
    private bool _browserClipboardIsCut;
    private string _status = "Ready";
    private SearchTabViewModel? _selectedSearchTab;
    private BrowserItem? _selectedBrowserItem;
    private FavoriteTreeNode? _selectedFavoriteNode;
    private string _browserLocation = "";
    private bool _isFavoritesVisible = true;
    private bool _isPreviewVisible;
    private bool _isNavigationPaneVisible;
    private bool _isNavigationVisible;
    private bool _isAddressNavigationPending;
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
    private CancellationTokenSource? _addressSuggestionCancellation;
    private readonly Dictionary<string, AddressDirectorySearchCacheEntry> _addressDirectorySearchCache = new(StringComparer.Ordinal);
    private bool _isAddressSuggestionsOpen;
    private int _nextTabNumber = 1;
    private readonly SemaphoreSlim _linuxMountGate = new(1, 1);

    public MainWindowViewModel() : this(RuntimeMode.IsDemo ? DemoCatalog.CreateConfig() : ConfigStore.Load(), startBackgroundWork: true)
    {
    }

    internal MainWindowViewModel(AppConfig config, bool startBackgroundWork)
    {
        _config = config;
        if (RuntimeMode.IsDemo)
        {
            DemoCatalog.Initialize();
        }
        if (startBackgroundWork && !RuntimeMode.IsDemo)
        {
            AutoConfigureLinuxMountMappings();
        }
        _client = CreateClient();
        _mapper = new PathMapper(_config);
        _rules = new ResultRules(_config);
        _history = new HistoryStore(_config);
        _isPreviewVisible = _config.Behavior.PreviewPane;
        _isNavigationPaneVisible = _config.Behavior.ShowNavigationPane;
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
        NavigateUpCommand = new AsyncCommand(NavigateUpAsync, () => NasFileBrowser.GetParentFolder(ResolveBrowserLocation(BrowserLocation)) != null);
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

        if (startBackgroundWork && !RuntimeMode.IsDemo)
        {
            RestorePinnedTabs();
        }
        if (SearchTabs.Count == 0)
        {
            AddSearchTab();
        }
        SelectedSearchTab = SearchTabs[0];
        EnsureNavigationRoots();
        if (startBackgroundWork)
        {
            if (RuntimeMode.IsDemo)
            {
                DemoCatalog.SeedHistory(_history);
                IsNasNavigationOnline = true;
                NasNavigationStatus = "Demo archive ready";
                _ = LoadDemoNavigationAsync();
            }
            else
            {
                _ = LoadNasNavigationRootAsync();
                _nasNavigationRetryTimer.Start();
            }
            _ = RefreshFavoritesAsync();
            _ = RefreshRecentSearchesAsync();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<SearchTabViewModel> SearchTabs { get; } = [];
    public ObservableCollection<BrowserPathSuggestion> AddressSuggestions { get; } = [];
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
    public bool HasFavorites => FavoriteTree.Count > 0;
    public ObservableCollection<string> RecentSearches { get; } = [];
    public bool HasRecentSearches => RecentSearches.Count > 0;
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
    public bool IsNasConnectionConfigured => _config.QsirchEnabled &&
                                            !string.IsNullOrWhiteSpace(_config.Host) &&
                                            !string.IsNullOrWhiteSpace(_config.User) &&
                                            !string.IsNullOrWhiteSpace(_config.Password);
    public bool IsSearchServiceConfigured => _config.SearchService.IsConfigured;
    public bool IsConnectionConfigured => IsNasConnectionConfigured || IsSearchServiceConfigured;
    public bool NeedsConnection => !IsConnectionConfigured;
    public string CurrentWindowsUser
    {
        get
        {
            if (RuntimeMode.IsDemo)
            {
                return "Demo operator | Sample workspace";
            }

            var user = GlobalRuleAuthorization.GetCurrentUserStatus();
            return $"{user.UserName} | {user.DisplayStatus}";
        }
    }

    public string ConnectionSummary => RuntimeMode.IsDemo
        ? "Demo archive | synthetic data"
        : IsConnectionConfigured
        ? _client.ProviderName
        : "Search: not configured";
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
                IsAddressNavigationPending = false;
                SyncNavigationScopeSelection(value);
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
        set
        {
            if (SetField(ref _isFavoritesVisible, value))
            {
            }
        }
    }

    public bool IsPreviewVisible
    {
        get => _isPreviewVisible;
        set => SetField(ref _isPreviewVisible, value);
    }

    public bool IsNavigationPaneVisible
    {
        get => _isNavigationPaneVisible;
        set
        {
            if (SetField(ref _isNavigationPaneVisible, value))
            {
                _config.Behavior.ShowNavigationPane = value;
                ConfigStore.Save(_config);
            }
        }
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
            OnPropertyChanged(nameof(IsLocalRecycleBinView));
            OnPropertyChanged(nameof(IsNasRecycleBinView));
            OnPropertyChanged(nameof(CanOpenVersionHistory));
            OnPropertyChanged(nameof(IsHomeView));
            OnPropertyChanged(nameof(IsBrowserFileListVisible));
            SelectedSearchTab?.SetWorkspaceMode(value);
        }
    }

    public bool IsSearchContentVisible => !IsNavigationVisible;
    public bool IsRecycleBinView => IsNavigationVisible && IsRecycleBinPath(BrowserLocation);
    public bool IsLocalRecycleBinView => IsRecycleBinView && !IsNasBackedPath(BrowserLocation);
    public bool IsNasRecycleBinView => IsRecycleBinView && IsNasBackedPath(BrowserLocation);
    public bool CanOpenVersionHistory => !IsNavigationVisible || IsNasBackedPath(BrowserLocation);
    public bool IsHomeView => IsNavigationVisible && IsHomeLocation(BrowserLocation);
    public bool IsAddressNavigationPending
    {
        get => _isAddressNavigationPending;
        private set
        {
            if (SetField(ref _isAddressNavigationPending, value))
            {
                OnPropertyChanged(nameof(IsBrowserFileListVisible));
            }
        }
    }

    public bool IsBrowserFileListVisible => IsNavigationVisible && !IsHomeView && !IsAddressNavigationPending;

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
        private set
        {
            if (SetField(ref _nativePreviewHost, value))
            {
                OnPropertyChanged(nameof(ShowPreviewDetails));
            }
        }
    }

    public bool ShowPreviewDetails => NativePreviewHost == null;

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
            OnPropertyChanged(nameof(IsLocalRecycleBinView));
            OnPropertyChanged(nameof(IsNasRecycleBinView));
            OnPropertyChanged(nameof(CanOpenVersionHistory));
            OnPropertyChanged(nameof(IsHomeView));
            OnPropertyChanged(nameof(IsBrowserFileListVisible));
        }
    }

    public async Task NavigateToFolderAsync(NavigationTreeNode node)
    {
        if (node.IsHome)
        {
            ShowHomeDriveOverview();
            return;
        }

        if (node.IsPlaceholder || string.IsNullOrWhiteSpace(node.FullPath))
        {
            return;
        }

        try
        {
            var navigationPath = await EnsureLinuxShareMountedAsync(node.FullPath);
            var resolvedPath = ResolveBrowserLocation(navigationPath);
            if (!node.FullPath.Equals(navigationPath, StringComparison.OrdinalIgnoreCase))
            {
                node = FlattenNavigationNodes(NavigationRoots)
                    .FirstOrDefault(candidate => candidate.FullPath.Equals(navigationPath, StringComparison.OrdinalIgnoreCase))
                    ?? node;
            }
            AppLogger.Info("browse", $"navigation requested folder=\"{node.FullPath}\" resolved=\"{resolvedPath}\"");

            // A tree node can be opened by clicking its label without expanding its
            // disclosure arrow. Populate it here as well, so share-root affordances
            // such as the NAS Recycle Bin are never skipped.
            if (!node.ChildrenLoaded)
            {
                await LoadNavigationChildrenAsync(node);
            }

            IsNavigationVisible = true;
            BrowserLocation = resolvedPath;
            await BrowseAsync();
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
        // Linux SMB mounts are local filesystem paths, so the node cannot be
        // recognized from its UNC form. A configured mount mapping is still a
        // NAS share root and must expose its friendly Recycle Bin entry.
        var isShareRoot = node.IsShareRoot || IsMountedShareRoot(node.FullPath);
        CancelNavigationLoad(node);
        var cancellation = new CancellationTokenSource();
        _navigationLoadCancellations[node] = cancellation;
        var token = cancellation.Token;
        try
        {
            var listing = await _browser.BrowseAsync(node.FullPath, token);
            token.ThrowIfCancellationRequested();
            isShareRoot |= OperatingSystem.IsWindows() && node.IsDrive && listing.Items.Any(item =>
                item.IsFolder && (IsRecycleFolder(item.Name) || IsSnapshotFolder(item.Name)));
            AppLogger.Info("browse", $"navigation listing path=\"{node.FullPath}\" shareRoot={isShareRoot} folders={listing.Items.Count(item => item.IsFolder)} recycle={listing.Items.Any(item => IsRecycleFolder(item.Name))}");
            node.Children.Clear();
            var readyDrives = ReadyWindowsDrives();
            var addedChildren = 0;
            foreach (var item in listing.Items.Where(item => item.IsFolder))
            {
                if (!_config.Behavior.ShowRecoverySystemFolders &&
                    (IsSnapshotFolder(item.Name) || (IsRecycleFolder(item.Name) && !isShareRoot)))
                {
                    continue;
                }
                if (!_config.Behavior.ShowQSurferSafetyCopies && IsQSurferSafetyCopy(item))
                {
                    continue;
                }

                var isRecoveryFolder = IsRecycleFolder(item.Name);
                var resolvedPath = NormalizeNavigationRoot(_mapper.ResolveBrowserPath(item.FullPath));
                var drive = readyDrives.FirstOrDefault(candidate =>
                    candidate.Path.Equals(resolvedPath, StringComparison.OrdinalIgnoreCase));
                var child = CreateNavigationNode(
                    isRecoveryFolder ? "Recycle Bin" : NavigationTreeDisplayName(item.FullPath),
                    item.FullPath,
                    isRecoveryFolder,
                    isNasRoot,
                    isDrive: drive != null,
                    driveUsedFraction: drive?.UsedFraction ?? 0,
                    driveSpaceText: drive?.SpaceText ?? "",
                    driveType: drive?.Type);
                node.Children.Add(child);
                addedChildren++;
                if (addedChildren % 100 == 0)
                {
                    // Let Avalonia paint and process input while an explicitly expanded
                    // folder has a large number of immediate child directories.
                    token.ThrowIfCancellationRequested();
                    await Task.Yield();
                }
                if (ShouldRestoreExpanded(child.FullPath))
                {
                    child.IsExpanded = true;
                }
            }

            // A NAS can omit its protected recycle folder from a generic listing
            // even when the folder itself is reachable. Probe known names at each
            // mounted share root so recovery is discoverable.
            await DiscoverRecycleBinAsync(node, token);
            token.ThrowIfCancellationRequested();
            node.ChildrenLoaded = true;
            if (SelectedSearchTab is { } tab)
            {
                SyncNavigationScopeSelection(tab);
            }
            if (isNasRoot)
            {
                SetNasNavigationState(true, "Online");
            }
            AppLogger.Info("browse", $"navigation folder=\"{node.FullPath}\" folders={node.Children.Count}");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
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
        finally
        {
            if (_navigationLoadCancellations.TryGetValue(node, out var active) && ReferenceEquals(active, cancellation))
            {
                _navigationLoadCancellations.Remove(node);
                active.Dispose();
            }
        }
    }

    public void ReloadConnection()
    {
        if (RuntimeMode.IsDemo)
        {
            RefreshNavigationRoots();
            _ = LoadDemoNavigationAsync();
            Status = "Demo archive reloaded";
            return;
        }

        CancelNavigationLoads();
        _client.Dispose();
        _client = CreateClient();
        _mapper = new PathMapper(_config);
        _rules = new ResultRules(_config);
        RefreshNavigationRoots();
        _ = LoadNasNavigationRootAsync();
        IsPreviewVisible = _config.Behavior.PreviewPane;
        OnPropertyChanged(nameof(IsConnectionConfigured));
        OnPropertyChanged(nameof(NeedsConnection));
        OnPropertyChanged(nameof(ConnectionSummary));
        OnPropertyChanged(nameof(NasConnectionStatus));
        Status = IsConnectionConfigured ? "Connection settings saved" : "Add NAS or QIndexer connection details to search";
    }

    public void StatusMessage(string message) => Status = message;

    private ISearchProvider CreateClient()
    {
        var providers = new List<ISearchProvider>();
        if (IsNasConnectionConfigured)
        {
            var qsirch = new QsirchClient(_config);
            qsirch.SessionRecoveryStatus += message => Dispatcher.UIThread.Post(() => Status = message);
            providers.Add(qsirch);
        }
        if (IsSearchServiceConfigured)
        {
            providers.Add(new QSurferSearchServiceClient(_config));
        }
        return providers.Count switch
        {
            1 => providers[0],
            > 1 => new CompositeSearchProvider(providers),
            _ => new QsirchClient(_config),
        };
    }

    public bool IsAddressSuggestionsOpen
    {
        get => _isAddressSuggestionsOpen;
        set => SetField(ref _isAddressSuggestionsOpen, value);
    }

    public async Task RefreshAddressSuggestionsAsync(string text, bool skipDebounce = false)
    {
        // The previous request owns disposal of its token in its finally block.
        // Disposing it here races with a new keystroke that is trying to cancel it.
        _addressSuggestionCancellation?.Cancel();

        var input = text.Trim();
        if (input.Length < 2)
        {
            SetAddressSuggestions([]);
            return;
        }

        var cancellation = new CancellationTokenSource();
        _addressSuggestionCancellation = cancellation;
        try
        {
            if (!skipDebounce)
            {
                await Task.Delay(GetAddressSuggestionDebounce(input), cancellation.Token);
            }
            var suggestions = await FindAddressSuggestionsAsync(input, cancellation.Token);
            if (!cancellation.IsCancellationRequested)
            {
                SetAddressSuggestions(suggestions);
                AppLogger.Info("browse", $"path suggestions value=\"{input}\" count={suggestions.Count}");
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            AppLogger.Warn("browse", $"path suggestions failed value=\"{input}\" error=\"{ex.Message}\"");
            if (!cancellation.IsCancellationRequested)
            {
                SetAddressSuggestions([]);
            }
        }
        finally
        {
            if (ReferenceEquals(_addressSuggestionCancellation, cancellation))
            {
                _addressSuggestionCancellation = null;
            }
            cancellation.Dispose();
        }
    }

    public void BeginAddressNavigation()
    {
        IsNavigationVisible = true;
        IsAddressNavigationPending = true;
        SelectedBrowserItem = null;
        BrowserBreadcrumbs.Clear();
        Status = "Finding folder...";
    }

    public void RestoreSettledBrowserLocation()
    {
        _addressSuggestionCancellation?.Cancel();
        SetAddressSuggestions([]);
        SelectedSearchTab?.ClearScopeFolder();

        IsAddressNavigationPending = false;
        Status = SelectedSearchTab?.Status ?? (BrowserItems.Count == 0 ? "Ready" : $"Ready {BrowserItems.Count:n0} items");
    }

    public async Task ToggleNavigationSearchScopeAsync(NavigationTreeNode node)
    {
        if (node.IsHome || node.IsPlaceholder || string.IsNullOrWhiteSpace(node.FullPath))
        {
            return;
        }

        var resolvedPath = ResolveBrowserLocation(node.FullPath);
        var scopePath = NormalizeNasScope(ResolveScopePath(resolvedPath));
        if (string.IsNullOrWhiteSpace(scopePath))
        {
            Status = "Only NAS folders can scope a NAS search.";
            return;
        }

        var tab = SelectedSearchTab ?? AddSearchTab();
        tab.ToggleScopeFolder(scopePath);
        SyncNavigationScopeSelection(tab);

        if (string.IsNullOrWhiteSpace(tab.Query))
        {
            Status = tab.HasFolderScope
                ? $"{tab.ScopePaths.Count} search folder{(tab.ScopePaths.Count == 1 ? "" : "s")} selected"
                : "Search folder scope cleared";
            tab.Status = Status;
            return;
        }

        await tab.SearchCommand.ExecuteAsync();
    }

    public async Task ToggleNavigationSearchExclusionAsync(NavigationTreeNode node)
    {
        if (node.IsHome || node.IsPlaceholder || string.IsNullOrWhiteSpace(node.FullPath))
        {
            return;
        }

        var resolvedPath = ResolveBrowserLocation(node.FullPath);
        var scopePath = NormalizeNasScope(ResolveScopePath(resolvedPath));
        if (string.IsNullOrWhiteSpace(scopePath))
        {
            Status = "Only NAS folders can be excluded from a NAS search.";
            return;
        }

        var tab = SelectedSearchTab ?? AddSearchTab();
        tab.ToggleExcludedScopeFolder(scopePath);
        SyncNavigationScopeSelection(tab);

        if (string.IsNullOrWhiteSpace(tab.Query))
        {
            Status = tab.HasFolderScope
                ? $"{tab.ExcludedScopePaths.Count} search folder{(tab.ExcludedScopePaths.Count == 1 ? "" : "s")} excluded"
                : "Search folder scope cleared";
            tab.Status = Status;
            return;
        }

        await tab.SearchCommand.ExecuteAsync();
    }

    public IReadOnlyList<BrowserItem> SelectedNavigationScopeItems()
    {
        var scopes = (SelectedSearchTab?.ScopePaths ?? [])
            .Select(NormalizeNasScope)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (scopes.Count == 0)
        {
            return [];
        }

        return FlattenNavigationNodes(NavigationRoots)
            .Where(node => !node.IsHome && !node.IsPlaceholder && !string.IsNullOrWhiteSpace(node.FullPath))
            .Where(node => scopes.Contains(NormalizeNasScope(ResolveScopePath(ResolveBrowserLocation(node.FullPath)))))
            .Select(node => new BrowserItem(node.Name, node.FullPath, true, 0, DateTime.MinValue))
            .DistinctBy(item => item.FullPath, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public bool RequiresScopeReplacementConfirmation =>
        _config.Behavior.ConfirmScopeReplacement &&
        SelectedSearchTab is { IsScopeAppendPending: false, ScopePaths.Count: > 1 };

    public bool RequiresScopeReplacementConfirmationForAddress(string address)
    {
        // Accepted addresses navigate the browser. They initialize an empty
        // search scope or append only after the user explicitly presses +, so
        // an address can no longer replace a folder selection to confirm.
        return false;
    }

    public async Task SelectAddressSuggestionAsync(BrowserPathSuggestion suggestion)
    {
        BrowserLocation = suggestion.Path;
        DismissAddressSuggestions();
        await BrowseAddressAsync(setSearchScope: true);
    }

    public async Task BrowseAddressAsync(bool setSearchScope)
    {
        var address = (BrowserLocation ?? "").Trim();
        if (string.IsNullOrWhiteSpace(address))
        {
            return;
        }

        if (setSearchScope && SelectedSearchTab is { } tab &&
            (!tab.HasFolderScope || tab.IsScopeAppendPending))
        {
            var scopePath = ResolveScopePath(address);
            AppLogger.Info("browse", $"address scope address=\"{address}\" resolved=\"{scopePath ?? ""}\"");
            if (!string.IsNullOrWhiteSpace(scopePath))
            {
                tab.ApplyAddressScope(scopePath);
            }
        }

        DismissAddressSuggestions();
        IsNavigationVisible = true;
        await BrowseAsync();
    }

    public void BeginFolderScopeAppend()
    {
        SelectedSearchTab?.BeginScopeAppend();
    }

    private string ResolveBrowserLocation(string path) => RuntimeMode.IsDemo
        ? DemoCatalog.ResolveActualPath(path) ?? path
        : _mapper.ResolveBrowserPath(path);

    private string DisplayBrowserLocation(string path) => RuntimeMode.IsDemo
        ? DemoCatalog.ToDisplayPath(path)
        : _mapper.DisplayBrowserPath(path);

    private string? ResolveScopePath(string path)
    {
        if (RuntimeMode.IsDemo)
        {
            return DemoCatalog.ResolveScope(path);
        }

        // Keep explicit mapped-drive and UNC paths in the tab. Qsirch results gain
        // their mapped path before client-side scope filtering, and QIndexer can
        // translate the same form through its advertised root aliases. Collapsing
        // X:\ to Qsirch's internal \Share form here makes the two providers use
        // incompatible namespaces.
        var localPath = ResolveBrowserLocation(path);
        return Path.IsPathFullyQualified(localPath) || IsUncPath(localPath)
            ? localPath
            : _mapper.TryResolveNasSearchPath(path);
    }

    private void SetAddressSuggestions(IEnumerable<BrowserPathSuggestion> suggestions)
    {
        AddressSuggestions.Clear();
        foreach (var suggestion in suggestions)
        {
            AddressSuggestions.Add(suggestion);
        }
        IsAddressSuggestionsOpen = AddressSuggestions.Count > 0;
    }

    private async Task<IReadOnlyList<BrowserPathSuggestion>> FindAddressSuggestionsAsync(string input, CancellationToken cancellationToken)
    {
        var directSuggestions = await Task.Run(() => FindAddressSuggestions(input, cancellationToken), cancellationToken);
        if (directSuggestions.Count > 0 || IsRootedAddressInput(input) || !IsConnectionConfigured)
        {
            return directSuggestions;
        }

        return await FindIndexedAddressSuggestionsAsync(input, cancellationToken);
    }

    internal IReadOnlyList<BrowserPathSuggestion> FindAddressSuggestions(string input, CancellationToken cancellationToken)
    {
        var linuxShareAliases = FindLinuxShareAliasSuggestions(input);
        if (linuxShareAliases.Count > 0)
        {
            return linuxShareAliases;
        }

        var resolved = ResolveBrowserLocation(input);
        if (RuntimeMode.IsDemo &&
            !resolved.StartsWith(RuntimeMode.DemoRoot, StringComparison.OrdinalIgnoreCase))
        {
            return DemoCatalog.ArchiveDisplayName.StartsWith(input.Trim(), StringComparison.OrdinalIgnoreCase)
                ? [new BrowserPathSuggestion(DemoCatalog.ArchiveDisplayName, RuntimeMode.DemoArchiveRoot)]
                : [];
        }
        if (OperatingSystem.IsWindows() && resolved.Length == 2 && char.IsAsciiLetter(resolved[0]) && resolved[1] == ':')
        {
            resolved += Path.DirectorySeparatorChar;
        }

        var root = Path.GetPathRoot(resolved);
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            return [];
        }

        var trailingSeparator = resolved.EndsWith(Path.DirectorySeparatorChar) || resolved.EndsWith(Path.AltDirectorySeparatorChar);
        var relative = resolved[root.Length..].Trim(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var segments = relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var completedSegments = trailingSeparator ? segments : segments.Take(Math.Max(segments.Length - 1, 0)).ToArray();
        var finalPrefix = trailingSeparator || segments.Length == 0 ? "" : segments[^1];
        var parents = new List<string> { root };

        foreach (var segment in completedSegments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            parents = FindChildDirectories(
                parents,
                segment,
                requireExactMatch: true,
                cancellationToken,
                includeHidden: segment.StartsWith(".", StringComparison.Ordinal));
            if (parents.Count == 0)
            {
                return [];
            }
        }

        var candidates = FindChildDirectories(parents, finalPrefix, requireExactMatch: false, cancellationToken);
        return CreateAddressSuggestions(candidates, finalPrefix);
    }

    private IReadOnlyList<BrowserPathSuggestion> FindLinuxShareAliasSuggestions(string input)
    {
        if (OperatingSystem.IsWindows())
        {
            return [];
        }

        var normalized = (input ?? "").Trim().Replace('\\', '/');
        if (!normalized.StartsWith("/", StringComparison.Ordinal))
        {
            return [];
        }

        var relative = normalized.TrimStart('/');
        var segments = relative.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var hasTrailingSeparator = normalized.EndsWith("/", StringComparison.Ordinal);
        if (segments.Length > 1 || (segments.Length == 1 && hasTrailingSeparator))
        {
            return [];
        }

        var typedPrefix = segments.Length == 0 ? "" : segments[0];
        var aliases = _config.PathMappings
            .Select(mapping => mapping.ShareRoot.Trim('\\', '/').Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries).LastOrDefault())
            .Where(share => !string.IsNullOrWhiteSpace(share) && share.StartsWith(typedPrefix, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(share => share, StringComparer.OrdinalIgnoreCase)
            .Take(AddressSuggestionLimit)
            .Select(share => new BrowserPathSuggestion(share!, "/" + share))
            .ToList();

        return aliases;
    }

    private async Task<IReadOnlyList<BrowserPathSuggestion>> FindIndexedAddressSuggestionsAsync(string input, CancellationToken cancellationToken)
    {
        var segments = input.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length == 0 || string.IsNullOrWhiteSpace(segments[0]))
        {
            return [];
        }

        var firstSegment = segments[0];
        var folders = await FindIndexedFoldersAsync(firstSegment, cancellationToken);
        var candidatePaths = folders
            .Where(folder => segments.Length == 1 || folder.FileName.Equals(firstSegment, StringComparison.OrdinalIgnoreCase))
            .Select(ResolveWindowsPath)
            .Where(path => !string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(AddressPathBranchLimit)
            .ToList();

        if (segments.Length == 1)
        {
            return CreateAddressSuggestions(candidatePaths, firstSegment);
        }

        var completedSegments = segments.Skip(1).Take(segments.Length - 2);
        foreach (var segment in completedSegments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            candidatePaths = FindChildDirectories(candidatePaths, segment, requireExactMatch: true, cancellationToken);
            if (candidatePaths.Count == 0)
            {
                return [];
            }
        }

        var finalPrefix = segments[^1];
        return CreateAddressSuggestions(
            FindChildDirectories(candidatePaths, finalPrefix, requireExactMatch: false, cancellationToken),
            finalPrefix);
    }

    private async Task<IReadOnlyList<SearchResult>> FindIndexedFoldersAsync(string firstSegment, CancellationToken cancellationToken)
    {
        if (_addressDirectorySearchCache.TryGetValue(firstSegment, out var cached) &&
            DateTime.UtcNow - cached.CreatedUtc < AddressDirectorySearchCacheLifetime)
        {
            return cached.Folders;
        }

        var folders = await _client.SearchDirectoriesAsync(firstSegment, 50, cancellationToken);
        var uppercaseSegment = firstSegment.ToUpperInvariant();
        if (folders.Count == 0 && !string.Equals(firstSegment, uppercaseSegment, StringComparison.Ordinal))
        {
            folders = await _client.SearchDirectoriesAsync(uppercaseSegment, 50, cancellationToken);
        }

        _addressDirectorySearchCache[firstSegment] = new AddressDirectorySearchCacheEntry(DateTime.UtcNow, folders);
        return folders;
    }

    private IReadOnlyList<BrowserPathSuggestion> CreateAddressSuggestions(IEnumerable<string> paths, string typedPrefix) =>
        paths
            .Select(path => new BrowserPathSuggestion(
                Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
                DisplayBrowserLocation(path)))
            // Prefer an exact-case candidate when both AA and aa exist, but treat case as
            // irrelevant for every normal folder match.
            .OrderBy(suggestion => suggestion.Name.StartsWith(typedPrefix, StringComparison.Ordinal) ? 0 : 1)
            .ThenBy(suggestion => suggestion.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(suggestion => suggestion.Path, StringComparer.OrdinalIgnoreCase)
            .Take(AddressSuggestionLimit)
            .ToList();

    private static int GetAddressSuggestionDebounce(string input) =>
        IsRootedAddressInput(input) || input.IndexOfAny(['\\', '/']) >= 0
            ? NestedAddressSuggestionDebounceMilliseconds
            : InitialAddressSuggestionDebounceMilliseconds;

    private static List<string> FindChildDirectories(
        IEnumerable<string> parents,
        string prefix,
        bool requireExactMatch,
        CancellationToken cancellationToken,
        bool includeHidden = false)
    {
        var options = new EnumerationOptions
        {
            AttributesToSkip = includeHidden ? 0 : FileAttributes.Hidden | FileAttributes.System,
            IgnoreInaccessible = true,
            RecurseSubdirectories = false,
            ReturnSpecialDirectories = false,
        };
        var matches = new List<string>();
        foreach (var parent in parents.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(parent))
            {
                continue;
            }

            foreach (var path in Directory.EnumerateDirectories(parent, "*", options))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                var isMatch = requireExactMatch
                    ? name.Equals(prefix, StringComparison.OrdinalIgnoreCase)
                    : name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
                if (!isMatch)
                {
                    continue;
                }

                matches.Add(path);
                if (matches.Count >= AddressSuggestionLimit * AddressPathBranchLimit)
                {
                    return matches;
                }
            }
        }

        return matches;
    }

    private static bool IsRootedAddressInput(string input)
    {
        var trimmed = input.Trim();
        return Path.IsPathRooted(trimmed) ||
               trimmed.StartsWith(@"\\", StringComparison.Ordinal) ||
               (OperatingSystem.IsWindows() && trimmed.Length >= 2 && char.IsAsciiLetter(trimmed[0]) && trimmed[1] == ':');
    }

    public void AcceptAddressSuggestion(BrowserPathSuggestion suggestion)
    {
        BrowserLocation = suggestion.Path;
        // A pending directory probe can otherwise finish after acceptance and reopen
        // the popup over the newly scoped folder. Cancel the probe instead of merely
        // hiding the popup; retaining the source list preserves keyboard selection
        // state until the next address edit replaces it.
        _addressSuggestionCancellation?.Cancel();
        IsAddressSuggestionsOpen = false;
    }

    public void DismissAddressSuggestions()
    {
        _addressSuggestionCancellation?.Cancel();
        SetAddressSuggestions([]);
    }

    public void ReturnToSearchResults() => IsNavigationVisible = false;

    public void EnterBrowseMode() => IsNavigationVisible = true;

    public async Task ToggleWorkspaceModeAsync()
    {
        if (IsNavigationVisible)
        {
            ReturnToSearchResults();
            return;
        }

        var scopedPath = SelectedSearchTab is { HasFolderScope: true } tab
            ? ResolveWindowsPath(new SearchResult { Path = tab.ScopePath, IsFolder = true })
            : "";
        if (!string.IsNullOrWhiteSpace(scopedPath))
        {
            BrowserLocation = scopedPath;
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
        if (RuntimeMode.IsDemo && !string.IsNullOrWhiteSpace(result.WindowsPath))
        {
            return result.WindowsPath;
        }

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

        RemoveSearchTab(tab);
    }

    private void RemoveSearchTab(SearchTabViewModel tab)
    {
        var index = SearchTabs.IndexOf(tab);
        if (index < 0)
        {
            return;
        }

        tab.PropertyChanged -= SearchTabPropertyChanged;
        tab.Dispose();
        SearchTabs.RemoveAt(index);
        _browserTabs.Remove(tab);
        if (SearchTabs.Count == 0)
        {
            AddSearchTab();
        }
        SelectedSearchTab = SearchTabs[Math.Min(index, SearchTabs.Count - 1)];
        PersistPinnedTabs();
    }

    public void ToggleTabPin(SearchTabViewModel tab)
    {
        tab.IsPinned = !tab.IsPinned;
    }

    /// <summary>
    /// Creates an independent workspace window containing a copy of a tab's
    /// current state. Search commands must be rebound to the new view model,
    /// so a live tab object cannot safely be shared between windows.
    /// </summary>
    public MainWindowViewModel DetachSearchTab(SearchTabViewModel tab)
    {
        var index = SearchTabs.IndexOf(tab);
        if (index < 0)
        {
            throw new InvalidOperationException("The selected tab is no longer available.");
        }

        if (ReferenceEquals(tab, SelectedSearchTab))
        {
            _browserTabs[tab] = CaptureBrowserTab();
        }

        var snapshot = CaptureSearchTab(tab);
        var browserState = _browserTabs.TryGetValue(tab, out var state)
            ? state
            : new BrowserTabState("", [], [], [], -1, null);

        // A background request belongs to its original command pipeline. The
        // moved window retains whatever it has already painted and can resume
        // the search normally if the user asks it to.
        tab.CancelSearch();

        var detached = new MainWindowViewModel();
        detached.ReplaceInitialTab(snapshot, browserState);
        RemoveSearchTab(tab);
        return detached;
    }

    public bool MoveSearchTabTo(SearchTabViewModel tab, MainWindowViewModel destination)
    {
        if (ReferenceEquals(this, destination) || SearchTabs.IndexOf(tab) < 0)
        {
            return false;
        }

        if (ReferenceEquals(tab, SelectedSearchTab))
        {
            _browserTabs[tab] = CaptureBrowserTab();
        }

        var snapshot = CaptureSearchTab(tab);
        var browserState = _browserTabs.TryGetValue(tab, out var state)
            ? state
            : new BrowserTabState("", [], [], [], -1, null);
        tab.CancelSearch();
        destination.ImportTransferredTab(snapshot, browserState);
        RemoveSearchTab(tab);
        return true;
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

    private void ReplaceInitialTab(SearchTabSnapshot snapshot, BrowserTabState browserState)
    {
        foreach (var existing in SearchTabs.ToList())
        {
            existing.PropertyChanged -= SearchTabPropertyChanged;
            existing.Dispose();
        }
        SearchTabs.Clear();
        _browserTabs.Clear();

        var tab = AddSearchTab();
        ApplySearchTabSnapshot(tab, snapshot);
        _browserTabs[tab] = browserState;
        SelectedSearchTab = tab;
    }

    private void ImportTransferredTab(SearchTabSnapshot snapshot, BrowserTabState browserState)
    {
        var tab = AddSearchTab();
        ApplySearchTabSnapshot(tab, snapshot);
        _browserTabs[tab] = browserState;
        SelectedSearchTab = tab;
    }

    private static SearchTabSnapshot CaptureSearchTab(SearchTabViewModel tab) => new(
        tab.Query,
        tab.ExactMatch,
        tab.SearchContents,
        tab.IsPinned,
        tab.TypeFilterOptions.Where(option => option.IsSelected).Select(option => option.Name).ToList(),
        tab.SelectedViewMode.Key,
        tab.SortSpecification,
        tab.SelectedScope.Key,
        tab.ScopePath,
        tab.ScopePaths.ToList(),
        tab.ExcludedScopePaths.ToList(),
        tab.DateFrom,
        tab.DateTo,
        tab.Results.ToList(),
        tab.SelectedResult,
        tab.NextOffset,
        tab.CanLoadMore,
        tab.Status,
        tab.IsBrowsing,
        tab.SavedSearchId,
        tab.SavedSearchName);

    private static void ApplySearchTabSnapshot(SearchTabViewModel tab, SearchTabSnapshot snapshot)
    {
        tab.Query = snapshot.Query;
        tab.ExactMatch = snapshot.ExactMatch;
        tab.SearchContents = snapshot.SearchContents;
        tab.ApplyTypeSelection(snapshot.SelectedTypeNames);
        tab.SelectedViewMode = tab.ViewModes.First(mode => mode.Key.Equals(snapshot.ViewKey, StringComparison.OrdinalIgnoreCase));
        tab.ApplySortSpecification(snapshot.SortSpecification);
        tab.RestoreScope(snapshot.ScopeKey, snapshot.ScopePath, snapshot.ScopePaths, snapshot.ExcludedScopePaths);
        tab.DateFrom = snapshot.DateFrom;
        tab.DateTo = snapshot.DateTo;
        tab.AddResults(snapshot.Results);
        tab.SelectedResult = snapshot.SelectedResult is { } selected
            ? tab.Results.FirstOrDefault(result => SameResult(result, selected))
            : null;
        tab.NextOffset = snapshot.NextOffset;
        tab.SetCanLoadMore(snapshot.CanLoadMore);
        tab.Status = snapshot.Status;
        tab.IsPinned = snapshot.IsPinned;
        tab.SetWorkspaceMode(snapshot.IsBrowsing);
        if (snapshot.SavedSearchId is long savedSearchId)
        {
            tab.SetSavedSearchSource(savedSearchId, snapshot.SavedSearchName);
        }
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
            tab.RestoreScope("folder", null, saved.ScopePaths, saved.ExcludedScopePaths);
            tab.IsPinned = true;
            _pinnedTabsAwaitingReload.Add(tab);
        }
    }

    public async Task ReloadPinnedSearchesAsync()
    {
        if (_pinnedTabsAwaitingReload.Count == 0)
        {
            return;
        }

        var tabs = _pinnedTabsAwaitingReload.ToList();
        _pinnedTabsAwaitingReload.Clear();
        await Task.Delay(TimeSpan.FromMilliseconds(1500));
        foreach (var tab in tabs)
        {
            if (SearchTabs.Contains(tab) && tab.SearchCommand.CanExecute(null))
            {
                _ = tab.SearchCommand.ExecuteAsync();
                await Task.Delay(TimeSpan.FromMilliseconds(125));
            }
        }
    }

    private async Task SearchTabAsync(SearchTabViewModel tab)
    {
        var (query, scopeInput) = ParseSearchInput(tab.Query);
        if (string.IsNullOrWhiteSpace(query))
        {
            if (!string.IsNullOrWhiteSpace(scopeInput))
            {
                SetTabStatus(tab, "Add a file or folder name before the in: folder scope.");
            }
            return;
        }

        if (RuntimeMode.IsDemo)
        {
            await SearchDemoTabAsync(tab, query, scopeInput);
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
            if (!string.IsNullOrWhiteSpace(scopeInput))
            {
                var scopePath = await ResolveSearchScopeAsync(scopeInput, token);
                if (string.IsNullOrWhiteSpace(scopePath))
                {
                    SetTabStatus(tab, $"Could not resolve search folder: {scopeInput}");
                    return;
                }

                tab.SetScopeFolder(scopePath);
                AppLogger.Info("search", $"scope resolved tab=\"{tab.Title}\" input=\"{scopeInput}\" path=\"{scopePath}\"");
            }
            var providerScope = CreateSearchProviderScope(tab);
            await _client.EnsureAvailableAsync(token, providerScope);
            var starredKeys = await Task.Run(_history.StarredKeys, token);
            var typeFilter = _fileTypes[0];
            var resultLimit = Math.Max(_config.Behavior.MaxSearchResults, 50);
            var firstPageSize = Math.Clamp(_config.Behavior.FirstPageSize, 5, 500);
            var nextPageSize = Math.Clamp(_config.Behavior.NextPageSize, 10, 500);
            var serverQuery = BuildServerQuery(query, tab.SearchContents, tab.ExactMatch);
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
                token,
                CreateSearchProviderScope(tab));

            var folders = typeFilter.IncludeFolders
                ? _client.SearchDirectoriesAsync(query, 100, token, providerScope)
                : Task.FromResult<IReadOnlyList<SearchResult>>([]);

            _ = PaintDirectoryResultsAsync(tab, folders, token, version, starredKeys);
            await recentFiles;

            // The fast recent pass is the first result page whenever the requested
            // ordering is also newest-first. Reusing it avoids asking both providers
            // for the identical offset-zero window a second time.
            var recentPageIsFirstPage = tab.HasDateRange ||
                                        (string.Equals(serverSortBy, "modified", StringComparison.OrdinalIgnoreCase) &&
                                         string.Equals(serverSortDirection, "desc", StringComparison.OrdinalIgnoreCase));
            IReadOnlyList<SearchResult> firstPage;
            if (recentPageIsFirstPage)
            {
                firstPage = await recentFiles;
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
                    token,
                    CreateSearchProviderScope(tab));
            }

            var offset = firstPage.Count;
            var pagingComplete = firstPage.Count == 0;

            // Paint the first small page immediately, then keep paging until the
            // configured initial-result limit. Load more deliberately has no cap.
            // Folder scopes are client-side because the NAS protocol cannot express
            // their include/exclude precedence. Bound the initial source scan so a
            // narrow selection cannot walk the entire NAS before first paint.
            while (!pagingComplete &&
                   tab.Results.Count < resultLimit &&
                   offset < resultLimit)
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
                    token,
                    CreateSearchProviderScope(tab));
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

    private async Task SearchDemoTabAsync(SearchTabViewModel tab, string query, string? scopeInput)
    {
        tab.CancelSearch();
        tab.SearchCancellation?.Dispose();
        tab.SearchCancellation = new CancellationTokenSource();
        var token = tab.SearchCancellation.Token;
        var version = ++tab.SearchVersion;

        try
        {
            if (!string.IsNullOrWhiteSpace(scopeInput))
            {
                var scope = DemoCatalog.ResolveScope(scopeInput);
                if (string.IsNullOrWhiteSpace(scope))
                {
                    SetTabStatus(tab, $"Could not find demo folder: {scopeInput}");
                    return;
                }

                tab.SetScopeFolder(scope);
            }

            await RecordRecentSearchAsync(query);
            tab.ResetResults();
            tab.IsSearching = true;
            SetTabStatus(tab, "Searching demo archive...");
            await Task.Delay(120, token);
            var results = DemoCatalog.FilterToScopes(
                DemoCatalog.Search(query, tab.SearchContents),
                tab.ScopePaths,
                tab.ExcludedScopePaths);
            var starredKeys = await Task.Run(_history.StarredKeys, token);
            var pageSize = Math.Clamp(_config.Behavior.FirstPageSize, 10, 80);
            AddVisibleResults(tab, results.Take(pageSize), token, version, starredKeys);
            tab.NextOffset = Math.Min(pageSize, results.Count);
            tab.SetCanLoadMore(tab.NextOffset < results.Count);
            SetTabStatus(tab, $"Ready {tab.Results.Count:n0} demo results");
            AppLogger.Info("demo", $"search query=\"{query}\" returned={results.Count} visible={tab.Results.Count}");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            SetTabStatus(tab, "Search stopped");
        }
        finally
        {
            if (tab.SearchVersion == version)
            {
                tab.IsSearching = false;
            }
        }
    }

    private async Task LoadMoreDemoSearchTabAsync(SearchTabViewModel tab)
    {
        var (query, _) = ParseSearchInput(tab.Query);
        if (string.IsNullOrWhiteSpace(query))
        {
            return;
        }

        tab.CancelSearch();
        tab.SearchCancellation?.Dispose();
        tab.SearchCancellation = new CancellationTokenSource();
        var token = tab.SearchCancellation.Token;
        var version = ++tab.SearchVersion;
        try
        {
            tab.IsSearching = true;
            SetTabStatus(tab, "Loading more demo results...");
            await Task.Delay(90, token);
            var results = DemoCatalog.FilterToScopes(
                DemoCatalog.Search(query, tab.SearchContents),
                tab.ScopePaths,
                tab.ExcludedScopePaths);
            var starredKeys = await Task.Run(_history.StarredKeys, token);
            var offset = tab.NextOffset;
            var pageSize = Math.Clamp(_config.Behavior.NextPageSize, 10, 120);
            AddVisibleResults(tab, results.Skip(offset).Take(pageSize), token, version, starredKeys);
            tab.NextOffset = Math.Min(offset + pageSize, results.Count);
            tab.SetCanLoadMore(tab.NextOffset < results.Count);
            SetTabStatus(tab, $"Ready {tab.Results.Count:n0} demo results");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            SetTabStatus(tab, "Load more stopped");
        }
        finally
        {
            if (tab.SearchVersion == version)
            {
                tab.IsSearching = false;
            }
        }
    }

    private async Task LoadMoreSearchTabAsync(SearchTabViewModel tab)
    {
        if (string.IsNullOrWhiteSpace(tab.Query) || !tab.CanLoadMore)
        {
            return;
        }

        if (RuntimeMode.IsDemo)
        {
            await LoadMoreDemoSearchTabAsync(tab);
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
            var (query, _) = ParseSearchInput(tab.Query);
            if (string.IsNullOrWhiteSpace(query))
            {
                return;
            }
            var serverQuery = BuildServerQuery(query, tab.SearchContents, tab.ExactMatch);
            if (tab.HasDateRange)
            {
                var from = tab.DateFrom?.ToString("yyyy-MM-dd") ?? "1900-01-01";
                var to = tab.DateTo?.ToString("yyyy-MM-dd") ?? DateTime.Today.ToString("yyyy-MM-dd");
                serverQuery += $" modified:{from}..{to}";
            }

            var starredKeys = await Task.Run(_history.StarredKeys, token);
            var pageSize = Math.Clamp(_config.Behavior.NextPageSize, 10, 500);
            var (serverSortBy, serverSortDirection) = ServerSortFor(tab);
            var offset = tab.NextOffset;
            var pagingComplete = false;

            // Load more is an explicit request to exhaust the current query. The
            // terminal empty page is the only reliable completion signal Qsirch gives us.
            while (!pagingComplete)
            {
                token.ThrowIfCancellationRequested();
                if (tab.SearchVersion != version)
                {
                    return;
                }

                SetTabStatus(tab, $"Loading {tab.Results.Count:n0} results...");
                var page = await _client.SearchAsync(
                    serverQuery,
                    _fileTypes[0],
                    pageSize,
                    offset,
                    serverSortBy,
                    serverSortDirection,
                    batch => Dispatcher.UIThread.InvokeAsync(() => AddVisibleResults(tab, batch, token, version, starredKeys)).GetTask(),
                    token,
                    CreateSearchProviderScope(tab));
                offset += page.Count;
                pagingComplete = page.Count == 0;
                AppLogger.Info("search", $"QSurfer tab=\"{tab.Title}\" load-more offset={offset - page.Count} count={page.Count} visible={tab.Results.Count}");
            }

            tab.NextOffset = offset;
            tab.SetCanLoadMore(false);
            SetTabStatus(tab, $"Ready {tab.Results.Count:n0} results");
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
        tab.ClearScopeFolder();
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
            OnPropertyChanged(nameof(HasFavorites));
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
            OnPropertyChanged(nameof(HasRecentSearches));
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
            var tab = AddSearchTab();
            SelectedSearchTab = tab;
            tab.SetSavedSearchSource(savedSearch.Id, savedSearch.Name);
            tab.Query = savedSearch.Query;
            tab.ApplyTypeSelection(savedSearch.TypeNames);
            tab.SelectedViewMode = tab.ViewModes.FirstOrDefault(view => view.Key.Equals(savedSearch.ViewKey, StringComparison.OrdinalIgnoreCase)) ?? tab.SelectedViewMode;
            tab.ApplySortSpecification(savedSearch.SortValue);
            tab.DateFrom = savedSearch.DateFrom;
            tab.DateTo = savedSearch.DateTo;
            tab.ExactMatch = savedSearch.ExactMatch;
            tab.SearchContents = savedSearch.SearchContents;
            tab.RestoreScope("folder", null, savedSearch.ScopePaths, savedSearch.ExcludedScopePaths);
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

        if (!OperatingSystem.IsWindows())
        {
            var mountedPath = _mapper.TryResolve(result);
            if (string.IsNullOrWhiteSpace(mountedPath) || IsUncPath(mountedPath))
            {
                SetTabStatus(tab, "This result is not mapped to a local CIFS mount yet.");
                return;
            }

            await OpenPathAsync(mountedPath);
            SetTabStatus(tab, "Showing location");
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

    public async Task<bool> SaveSearchAsync(SearchTabViewModel tab, string name)
    {
        var query = tab.Query.Trim();
        var displayName = name.Trim();
        if (string.IsNullOrWhiteSpace(query) || string.IsNullOrWhiteSpace(displayName))
        {
            return false;
        }

        if (await FindSavedSearchByNameAsync(displayName) is not null)
        {
            SetTabStatus(tab, $"A saved search named '{displayName}' already exists.");
            return false;
        }

        var savedSearch = new SavedSearch(
            0,
            displayName,
            query,
            tab.ScopePaths.ToList(),
            tab.ExcludedScopePaths.ToList(),
            tab.TypeFilterOptions.Where(option => option.IsSelected).Select(option => option.Name).ToList(),
            tab.SelectedViewMode.Key,
            tab.SortSpecification,
            tab.DateFrom,
            tab.DateTo,
            tab.ExactMatch,
            tab.SearchContents);
        var savedSearchId = await Task.Run(() => _history.SaveSearch(savedSearch));
        if (savedSearchId is not long persistedId)
        {
            SetTabStatus(tab, "Could not save search.");
            return false;
        }
        await RefreshFavoritesAsync();
        var persistedSavedSearch = (await Task.Run(_history.SavedSearches))
            .FirstOrDefault(search => search.Id == persistedId);
        if (persistedSavedSearch is not null)
        {
            tab.SetSavedSearchSource(persistedSavedSearch.Id, persistedSavedSearch.Name);
        }
        SetTabStatus(tab, $"Saved search: {displayName}");
        return true;
    }

    public Task<SavedSearch?> FindSavedSearchByNameAsync(string name)
    {
        var displayName = name.Trim();
        if (string.IsNullOrWhiteSpace(displayName))
        {
            return Task.FromResult<SavedSearch?>(null);
        }

        return Task.Run(() => _history.SavedSearches()
            .FirstOrDefault(search => search.Name.Equals(displayName, StringComparison.OrdinalIgnoreCase)));
    }

    public Task OverwriteSavedSearchAsync(SearchTabViewModel tab) =>
        tab.SavedSearchId is long savedSearchId
            ? OverwriteSavedSearchAsync(tab, new SavedSearch(
                savedSearchId,
                tab.SavedSearchName,
                tab.Query,
                [], [], [], "details", "recent:desc", null, null, false, false))
            : Task.CompletedTask;

    public async Task OverwriteSavedSearchAsync(SearchTabViewModel tab, SavedSearch target)
    {
        var query = tab.Query.Trim();
        if (string.IsNullOrWhiteSpace(query))
        {
            SetTabStatus(tab, "Add a search term before saving changes.");
            return;
        }

        var savedSearch = new SavedSearch(
            target.Id,
            target.Name,
            query,
            tab.ScopePaths.ToList(),
            tab.ExcludedScopePaths.ToList(),
            tab.TypeFilterOptions.Where(option => option.IsSelected).Select(option => option.Name).ToList(),
            tab.SelectedViewMode.Key,
            tab.SortSpecification,
            tab.DateFrom,
            tab.DateTo,
            tab.ExactMatch,
            tab.SearchContents);
        var saved = await Task.Run(() => _history.UpdateSavedSearch(target.Id, savedSearch));
        if (!saved)
        {
            SetTabStatus(tab, "Could not overwrite saved search.");
            return;
        }

        await RefreshFavoritesAsync();
        tab.SetSavedSearchSource(target.Id, target.Name);
        SetTabStatus(tab, $"Updated saved search: {target.Name}");
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

            try
            {
                // Demo records deliberately display a fictional archive path while
                // retaining a private synthetic file path for preview and open actions.
                if (!RuntimeMode.IsDemo || string.IsNullOrWhiteSpace(result.WindowsPath))
                {
                    result.WindowsPath = _mapper.TryResolve(result) ?? result.ResolvedPath;
                }
                result.IsFavorite = starredKeys?.Contains(HistoryStore.ResultKey(result)) == true;
            }
            catch (Exception ex)
            {
                AppLogger.Warn("path", $"result path unavailable name=\"{result.FileName}\" path=\"{result.Path}\" error=\"{ex.Message}\"");
                result.WindowsPath = "";
            }
            if (tab.ContainsResult(result))
            {
                duplicates++;
                continue;
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
            var icon = await Task.Run(() => item.IsRecycleBinFolder
                ? WindowsShellIconService.RecycleBinIcon()
                : WindowsShellIconService.FileTypeIcon(Path.GetExtension(item.Name), item.IsFolder), token);
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
        item.IsRecycleBinFolder
            ? "__recycle_bin__"
            : item.IsFolder
            ? "__folder__"
            : string.IsNullOrWhiteSpace(Path.GetExtension(item.Name)) ? "__file__" : Path.GetExtension(item.Name).ToLowerInvariant();

    private static (string? SortBy, string SortDirection) ServerSortFor(SearchTabViewModel tab) =>
        tab.PrimarySortKey switch
        {
            // Folder groups paints directories from the dedicated alphabetical
            // directory query. Fetch its file half newest-first so the result set
            // and its order are identical regardless of which index supplied it.
            "folder" or "recent" => ("modified", "desc"),
            "modified" => ("modified", "desc"),
            "name" => ("name", "asc"),
            "size" => ("size", "desc"),
            _ => (null, "desc"),
        };

    // Qsirch's bare query searches indexed document content as well as filenames.
    // Quoting a content search preserves the user's phrase; the explicit name field
    // keeps the default search fast and filename-only.
    private static string BuildServerQuery(string query, bool searchContents, bool exactMatch)
    {
        var escapedQuery = query.Replace("\"", "\\\"");
        return searchContents
            ? exactMatch ? $"\"{escapedQuery}\"" : query
            : $"name:\"{escapedQuery}\"";
    }

    private static SearchProviderScope CreateSearchProviderScope(SearchTabViewModel tab) => new(
        tab.ScopePaths.ToList(),
        tab.ExcludedScopePaths.ToList());

    public async Task RemoveRecentSearchAsync(string query)
    {
        await Task.Run(() => _history.DeleteRecentSearch(query));
        await RefreshRecentSearchesAsync();
        IsRecentSearchesOpen = false;
    }

    private async Task<string?> ResolveSearchScopeAsync(string scopeInput, CancellationToken cancellationToken)
    {
        var mappedPath = _mapper.TryResolveNasSearchPath(scopeInput);
        if (!string.IsNullOrWhiteSpace(mappedPath))
        {
            return mappedPath;
        }

        var directPath = ResolveScopePath(scopeInput);
        if (!string.IsNullOrWhiteSpace(directPath))
        {
            return directPath;
        }

        var folders = await _client.SearchDirectoriesAsync(scopeInput, 20, cancellationToken);
        var exact = folders.FirstOrDefault(folder => folder.FileName.Equals(scopeInput, StringComparison.OrdinalIgnoreCase));
        return exact?.Path ?? (folders.Count == 1 ? folders[0].Path : null);
    }

    private static (string Query, string? ScopeInput) ParseSearchInput(string? input)
    {
        var text = (input ?? "").Trim();
        var match = TrailingScopeClause.Match(text);
        if (!match.Success)
        {
            return (text, null);
        }

        var scope = match.Groups["quoted"].Success ? match.Groups["quoted"].Value : match.Groups["path"].Value;
        return (text[..match.Index].Trim(), scope.Trim());
    }

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
        if (ReferenceEquals(tab, SelectedSearchTab) &&
            e.PropertyName is nameof(SearchTabViewModel.ScopePath) or
                nameof(SearchTabViewModel.ScopePaths) or
                nameof(SearchTabViewModel.ExcludedScopePaths))
        {
            SyncNavigationScopeSelection(tab);
        }
        if (e.PropertyName == nameof(SearchTabViewModel.Query) &&
            string.IsNullOrWhiteSpace(tab.Query) &&
            _config.Behavior.ClearResultsWithQuery &&
            !tab.IsPinned)
        {
            _ = ClearSearchTabAsync(tab);
        }
        if ((e.PropertyName is nameof(SearchTabViewModel.SearchContents) or nameof(SearchTabViewModel.ExactMatch)) &&
            !string.IsNullOrWhiteSpace(tab.Query) &&
            !tab.IsSearching)
        {
            _ = tab.SearchCommand.ExecuteAsync();
        }
        if (tab.IsPinned && e.PropertyName is nameof(SearchTabViewModel.Query) or
            nameof(SearchTabViewModel.SelectedViewMode) or
            nameof(SearchTabViewModel.SelectedSortMode) or
            nameof(SearchTabViewModel.SortSpecification) or
            nameof(SearchTabViewModel.TypeFilterOptions) or
            nameof(SearchTabViewModel.DateFrom) or
            nameof(SearchTabViewModel.DateTo) or
            nameof(SearchTabViewModel.ExactMatch) or
            nameof(SearchTabViewModel.SearchContents) or
            nameof(SearchTabViewModel.ScopePaths) or
            nameof(SearchTabViewModel.ExcludedScopePaths))
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
        var parent = NasFileBrowser.GetParentFolder(ResolveBrowserLocation(BrowserLocation));
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

        if (IsHomeLocation(BrowserLocation))
        {
            ShowHomeDriveOverview(addToHistory);
            return;
        }

        var location = ResolveBrowserLocation((BrowserLocation ?? "").Trim());
        if (string.IsNullOrWhiteSpace(location))
        {
            return;
        }

        if (!RuntimeMode.IsDemo && !location.Equals(BrowserLocation, StringComparison.OrdinalIgnoreCase))
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

            var displayLocation = DisplayBrowserLocation(listing.FolderPath);
            BrowserLocation = displayLocation;
            browserTab.SetBrowseLocation(displayLocation);
            IsAddressNavigationPending = false;
            UpdateBrowserBreadcrumbs(displayLocation);
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
                RecordBrowserLocation(displayLocation);
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

    private void ShowHomeDriveOverview(bool addToHistory = true)
    {
        if (RuntimeMode.IsDemo)
        {
            ShowDemoHomeOverview(addToHistory);
            return;
        }

        var browserTab = SelectedSearchTab;
        if (browserTab == null)
        {
            return;
        }

        IsNavigationVisible = true;
        BrowserLocation = HomeNavigationPath;
        browserTab.SetBrowseLocation(BrowserLocation);
        IsAddressNavigationPending = false;
        var homeItems = new List<BrowserItem>();
        if (!OperatingSystem.IsWindows())
        {
            var userHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrWhiteSpace(userHome) && Directory.Exists(userHome))
            {
                homeItems.Add(new BrowserItem("Home folder", userHome, true, 0, DateTime.MinValue));
            }

            if (_config.Behavior.ShowLocalNavigationFolders)
            {
                homeItems.AddRange(LocalNavigationFolders()
                    .Where(folder => !folder.Path.Equals(userHome, StringComparison.OrdinalIgnoreCase))
                    .Select(folder => new BrowserItem(folder.Name, folder.Path, true, 0, DateTime.MinValue)));
            }
        }

        homeItems.AddRange(ReadyWindowsDrives()
            .Where(ShouldShowNavigationDrive)
            .OrderBy(drive => drive.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(drive => new BrowserItem(drive.Name, drive.Path, true, 0, DateTime.MinValue)
            {
                IsDrive = true,
                DriveUsedFraction = drive.UsedFraction,
                DriveSpaceText = drive.SpaceText,
                IconSource = WindowsShellIconService.DriveIcon(drive.Type),
            }));
        BrowserItems = new ObservableCollection<BrowserItem>(homeItems);
        SelectedBrowserItem = null;
        BrowserBreadcrumbs.Clear();
        BrowserBreadcrumbs.Add(new BrowserBreadcrumb("Home", HomeNavigationPath, true));
        if (addToHistory)
        {
            RecordBrowserLocation(HomeNavigationPath);
        }
        Status = OperatingSystem.IsWindows()
            ? $"Home - {BrowserItems.Count:n0} drives"
            : $"Home - {BrowserItems.Count:n0} locations";
        browserTab.Status = Status;
        AppLogger.Info("browse", $"home drive overview drives={BrowserItems.Count}");
    }

    private static bool IsHomeLocation(string? location) =>
        HomeNavigationPath.Equals(location?.Trim(), StringComparison.OrdinalIgnoreCase);

    private void EnsureNavigationRoots()
    {
        if (RuntimeMode.IsDemo)
        {
            EnsureDemoNavigationRoots();
            return;
        }

        if (!NavigationRoots.Any(node => node.IsHome))
        {
            NavigationRoots.Add(new NavigationTreeNode
            {
                Name = "Home",
                FullPath = HomeNavigationPath,
                IsHome = true,
            });
        }

        var nasRoot = OperatingSystem.IsWindows() ? NasNavigationRoot(_config.Host) : null;
        if (!string.IsNullOrWhiteSpace(nasRoot) &&
            !NavigationRoots.Any(node => node.FullPath.Equals(nasRoot, StringComparison.OrdinalIgnoreCase)))
        {
            // The NAS hostname is a container of shares. Its children must retain
            // that share-root identity so each share can expose NAS recovery folders.
            NavigationRoots.Add(CreateNavigationNode(NavigationDisplayName(nasRoot), nasRoot, isShareRoot: true));
        }

        if (_config.Behavior.ShowLocalNavigationFolders)
        {
            foreach (var (name, path) in LocalNavigationFolders())
            {
                if (NavigationRoots.Any(node => node.FullPath.Equals(path, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                NavigationRoots.Add(CreateNavigationNode(name, path));
            }
        }

        // Match Explorer's useful baseline: personal folders first, followed by
        // every ready local, removable, and mapped network drive. Qsirch only
        // knows NAS shares, but browse mode must not hide the rest of Windows.
        foreach (var drive in ReadyWindowsDrives().Where(ShouldShowNavigationDrive))
        {
            if (!NavigationRoots.Any(node => node.FullPath.Equals(drive.Path, StringComparison.OrdinalIgnoreCase)))
            {
                NavigationRoots.Add(CreateNavigationNode(
                    drive.Name,
                    drive.Path,
                    isShareRoot: IsWindowsNasMappedDrive(drive.Path),
                    isDrive: true,
                    driveUsedFraction: drive.UsedFraction,
                    driveSpaceText: drive.SpaceText,
                    driveType: drive.Type));
            }
        }

        var mappedRoots = _config.PathMappings
            .Where(mapping => !OperatingSystem.IsWindows() || !MappingBelongsToConfiguredNas(mapping))
            .Select(mapping => new
            {
                Name = MappingNavigationDisplayName(mapping),
                Path = NormalizeNavigationRoot(mapping.MappedRoot),
                IsNasShare = !OperatingSystem.IsWindows(),
            })
            .Where(mapping => !string.IsNullOrWhiteSpace(mapping.Path) && Path.IsPathFullyQualified(mapping.Path))
            .GroupBy(mapping => mapping.Path, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
        var roots = mappedRoots.Select(mapping => mapping.Path).ToList();
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
            var mappedRoot = mappedRoots.FirstOrDefault(mapping => mapping.Path.Equals(root, StringComparison.OrdinalIgnoreCase));
            NavigationRoots.Add(CreateNavigationNode(
                mappedRoot?.Name ?? NavigationDisplayName(root),
                root,
                isShareRoot: mappedRoot?.IsNasShare == true));
        }

        SortNavigationRoots();
    }

    private void EnsureDemoNavigationRoots()
    {
        if (NavigationRoots.Count > 0)
        {
            return;
        }

        NavigationRoots.Add(new NavigationTreeNode
        {
            Name = "Home",
            FullPath = HomeNavigationPath,
            IsHome = true,
        });
        NavigationRoots.Add(CreateNavigationNode(
            DemoCatalog.NavigationDisplayName,
            RuntimeMode.DemoArchiveRoot,
            isShareRoot: true,
            isDrive: true,
            driveUsedFraction: 0.42,
            driveSpaceText: "2.3 GB free of 4 GB",
            driveType: DriveType.Fixed));
    }

    private async Task LoadDemoNavigationAsync()
    {
        var archive = NavigationRoots.FirstOrDefault(node => node.FullPath.Equals(RuntimeMode.DemoArchiveRoot, StringComparison.OrdinalIgnoreCase));
        if (archive == null)
        {
            return;
        }

        await LoadNavigationChildrenAsync(archive);
        await DiscoverRecycleBinAsync(archive);
        archive.IsExpanded = true;
    }

    private void ShowDemoHomeOverview(bool addToHistory)
    {
        var browserTab = SelectedSearchTab;
        if (browserTab == null)
        {
            return;
        }

        var items = Directory.EnumerateDirectories(RuntimeMode.DemoArchiveRoot)
            .Where(path => !Path.GetFileName(path).StartsWith('@'))
            .OrderBy(path => path, StringComparer.CurrentCultureIgnoreCase)
            .Select(path => new BrowserItem(Path.GetFileName(path), path, true, 0, Directory.GetLastWriteTime(path)))
            .ToList();
        BrowserItems = new ObservableCollection<BrowserItem>(items);
        SelectedBrowserItem = null;
        BrowserLocation = HomeNavigationPath;
        browserTab.SetBrowseLocation(HomeNavigationPath);
        BrowserBreadcrumbs.Clear();
        BrowserBreadcrumbs.Add(new BrowserBreadcrumb("Home", HomeNavigationPath, true));
        if (addToHistory)
        {
            RecordBrowserLocation(HomeNavigationPath);
        }
        Status = $"Demo archive - {items.Count:n0} folders";
        browserTab.Status = Status;
    }

    private void RefreshNavigationRoots()
    {
        CancelNavigationLoads();
        NavigationRoots.Clear();
        EnsureNavigationRoots();
        AppLogger.Info("browse", $"navigation roots refreshed count={NavigationRoots.Count} host=\"{_config.Host}\"");
    }

    private async Task LoadNasNavigationRootAsync()
    {
        if (!OperatingSystem.IsWindows())
        {
            if (IsNasConnectionConfigured)
            {
                // Linux can search Qsirch directly, but folder browsing uses the
                // user's existing SMB mounts rather than Windows UNC enumeration.
                SetNasNavigationState(true, "Search online; browse mounted SMB shares.");
            }
            else
            {
                SetNasNavigationState(false, "NAS search is not configured.");
            }
            return;
        }

        var nasRoot = OperatingSystem.IsWindows() ? NasNavigationRoot(_config.Host) : null;
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
                await FlattenNasNavigationRootAsync(root);
                foreach (var mappedShare in NavigationRoots
                    .Where(node => node.IsDrive && node.IsShareRoot && !node.ChildrenLoaded)
                    .ToList())
                {
                    await LoadNavigationChildrenAsync(mappedShare);
                    mappedShare.IsExpanded = true;
                }
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
        var resolved = NormalizeNavigationRoot(_mapper.ResolveBrowserPath(path));
        if (IsDriveRoot(resolved) && !path.Equals(resolved, StringComparison.OrdinalIgnoreCase))
        {
            return NavigationDisplayName(resolved);
        }

        return NavigationDisplayName(path);
    }

    private async Task FlattenNasNavigationRootAsync(NavigationTreeNode root)
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

        SortNavigationRoots();
        await Task.WhenAll(shares.Select(share => DiscoverRecycleBinAsync(share)));

        // On Windows the NAS host is flattened into its mapped drive roots.
        // Load those roots once so the Recycle Bin is visible beneath X: without
        // ever exposing the NAS share name in the navigation tree.
        if (OperatingSystem.IsWindows())
        {
            foreach (var share in shares)
            {
                await LoadNavigationChildrenAsync(share);
                share.IsExpanded = true;
            }
        }
    }

    private async Task DiscoverRecycleBinAsync(NavigationTreeNode node, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!node.IsShareRoot || node.Children.Any(child => child.IsRecoveryFolder))
        {
            return;
        }

        var recyclePath = await Task.Run(() =>
            new[] { "@Recycle", "@RecycleBin", "#recycle" }
                .Select(name => Path.Combine(node.FullPath, name))
                .FirstOrDefault(Directory.Exists), cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(recyclePath) || node.Children.Any(child => child.IsRecoveryFolder))
        {
            return;
        }

        node.Children.Add(CreateNavigationNode("Recycle Bin", recyclePath, isRecoveryFolder: true));
        AppLogger.Info("browse", $"navigation recycle bin discovered path=\"{recyclePath}\"");
    }

    private void SortNavigationRoots()
    {
        var ordered = NavigationRoots
            .OrderBy(NavigationRootGroup)
            .ThenBy(node => node.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        if (NavigationRoots.SequenceEqual(ordered))
        {
            return;
        }

        NavigationRoots.Clear();
        foreach (var node in ordered)
        {
            NavigationRoots.Add(node);
        }
    }

    private int NavigationRootGroup(NavigationTreeNode node)
    {
        if (node.IsHome)
        {
            return -1;
        }

        if (LocalNavigationFolders().Any(folder =>
                folder.Path.Equals(node.FullPath, StringComparison.OrdinalIgnoreCase)))
        {
            return 0;
        }

        return node.IsDrive ? 1 : 2;
    }

    private NavigationTreeNode CreateNavigationNode(
        string name,
        string path,
        bool isRecoveryFolder = false,
        bool isShareRoot = false,
        bool isDrive = false,
        double driveUsedFraction = 0,
        string driveSpaceText = "",
        DriveType? driveType = null)
    {
        var node = new NavigationTreeNode
        {
            Name = name,
            FullPath = _mapper.ResolveBrowserPath(path),
            IsRecoveryFolder = isRecoveryFolder,
            IsShareRoot = isShareRoot,
            IsDrive = isDrive,
            DriveUsedFraction = driveUsedFraction,
            DriveSpaceText = driveSpaceText,
            IconSource = isRecoveryFolder
                ? RecycleBinIcon()
                : isDrive ? WindowsShellIconService.DriveIcon(driveType ?? DriveType.Fixed) : null,
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

    private bool IsNasBackedPath(string path)
    {
        if (RuntimeMode.IsDemo && DemoCatalog.IsDemoPath(ResolveBrowserLocation(path)))
        {
            return true;
        }

        var normalized = _mapper.ResolveBrowserPath(path ?? "").TrimEnd('\\', '/');
        if (string.IsNullOrWhiteSpace(normalized)) return false;

        if (OperatingSystem.IsWindows())
        {
            var driveRoot = Path.GetPathRoot(normalized);
            if (!string.IsNullOrWhiteSpace(driveRoot) && IsWindowsNasMappedDrive(driveRoot)) return true;
            if (!normalized.StartsWith(@"\\", StringComparison.OrdinalIgnoreCase)) return false;

            var parts = normalized.TrimStart('\\').Split('\\', StringSplitOptions.RemoveEmptyEntries);
            return parts.Length >= 2 && NasIdentity.HostsMatch(parts[0], _config.Host);
        }

        return _config.PathMappings.Any(mapping =>
        {
            var mountPoint = (mapping.MappedRoot ?? "").TrimEnd('\\', '/');
            return !string.IsNullOrWhiteSpace(mountPoint) &&
                   (normalized.Equals(mountPoint, StringComparison.OrdinalIgnoreCase) ||
                    normalized.StartsWith(mountPoint + "/", StringComparison.OrdinalIgnoreCase));
        });
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
        name.Equals("#recycle", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("$Recycle.Bin", StringComparison.OrdinalIgnoreCase);

    private static bool IsSnapshotFolder(string name) =>
        name.Equals("@Recently-Snapshot", StringComparison.OrdinalIgnoreCase);

    private static bool IsRecoverySystemFolder(string name) =>
        IsRecycleFolder(name) || IsSnapshotFolder(name);

    private static bool IsRecoverySystemResult(SearchResult result) =>
        IsRecoverySystemFolder(result.FileName) ||
        result.Path.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries).Any(IsRecoverySystemFolder);

    private static bool IsRecycleFolderPath(string path) =>
        path.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries).Any(IsRecycleFolder);

    private static bool IsLocalTrashPath(string path)
    {
        var normalizedPath = (path ?? "").Replace('\\', '/').TrimEnd('/');
        return !OperatingSystem.IsWindows() &&
               normalizedPath.EndsWith("/.local/share/Trash", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsRecycleBinPath(string path) => IsRecycleFolderPath(path) || IsLocalTrashPath(path);

    private static bool IsRecycleBinRootPath(string path)
    {
        var trimmed = (path ?? "").Trim().TrimEnd('\\', '/');
        var name = Path.GetFileName(trimmed);
        return IsRecycleFolder(name) && !name.Equals("$Recycle.Bin", StringComparison.OrdinalIgnoreCase);
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
        tab.SetBrowseLocation(BrowserLocation);
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

    private bool IsMountedShareRoot(string path) =>
        !OperatingSystem.IsWindows() && _config.PathMappings.Any(mapping =>
            NormalizeNavigationRoot(mapping.MappedRoot).Equals(
                NormalizeNavigationRoot(path),
                StringComparison.OrdinalIgnoreCase));

    private void AutoConfigureLinuxMountMappings()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        _config.PathMappings ??= [];
        var changedMappings = 0;
        foreach (var mapping in _config.PathMappings)
        {
            if (LinuxNativeCifsMountService.IsMounted(mapping.MappedRoot) ||
                LinuxNativeCifsMountService.HasPersistentSystemMount(mapping.MappedRoot))
            {
                continue;
            }

            var persistentPath = LinuxNativeCifsMountService.PersistentMountPoint(mapping.ShareRoot, _config.Host);
            if (!string.IsNullOrWhiteSpace(persistentPath) &&
                !persistentPath.Equals(mapping.MappedRoot, StringComparison.OrdinalIgnoreCase))
            {
                mapping.MappedRoot = persistentPath;
                changedMappings++;
            }
        }

        var addedMappings = 0;
        foreach (var mountedShare in LinuxNativeCifsMountService.DiscoverMountedShares(_config.Host))
        {
            var shareRoot = mountedShare.ShareRoot.TrimEnd('\\');
            if (_config.PathMappings.Any(mapping =>
                (mapping.ShareRoot ?? "").TrimEnd('\\').Equals(shareRoot, StringComparison.OrdinalIgnoreCase)))
            {
                // A stored manual mapping is intentional and always wins.
                continue;
            }

            _config.PathMappings.Add(new PathMapping
            {
                ShareRoot = shareRoot,
                MappedRoot = mountedShare.MountPoint,
            });
            addedMappings++;
        }

        if (addedMappings == 0 && changedMappings == 0)
        {
            return;
        }

        ConfigStore.Save(_config);
        AppLogger.Info("path", $"reconciled {addedMappings + changedMappings} Linux CIFS path mapping(s)");
    }

    private async Task<string> EnsureLinuxShareMountedAsync(string requestedPath)
    {
        if (OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(requestedPath))
        {
            return requestedPath;
        }

        var mapping = _config.PathMappings
            .OrderByDescending(item => (item.MappedRoot ?? "").Length)
            .FirstOrDefault(item => IsPathWithinRoot(requestedPath, item.MappedRoot));
        if (mapping is null)
        {
            return requestedPath;
        }

        var migrateLegacyMount = LinuxNativeCifsMountService.IsLegacyQSurferMountPoint(mapping.MappedRoot);
        if (!migrateLegacyMount && (LinuxNativeCifsMountService.IsMounted(mapping.MappedRoot) ||
            LinuxNativeCifsMountService.HasPersistentSystemMount(mapping.MappedRoot)))
        {
            return requestedPath;
        }

        await _linuxMountGate.WaitAsync();
        try
        {
            if (!migrateLegacyMount && (LinuxNativeCifsMountService.IsMounted(mapping.MappedRoot) ||
                LinuxNativeCifsMountService.HasPersistentSystemMount(mapping.MappedRoot)))
            {
                return requestedPath;
            }

            Status = $"Reconnecting {MappingNavigationDisplayName(mapping)}...";
            var originalRoot = mapping.MappedRoot;
            var preserveBootMount = migrateLegacyMount && LinuxNativeCifsMountService.HasPersistentSystemMount(originalRoot);
            var mount = await LinuxNativeCifsMountService.MountAsync(
                mapping.ShareRoot,
                _config.Host,
                _config.User,
                _config.Password,
                reconnectAfterReboot: preserveBootMount,
                replacedMountPoint: migrateLegacyMount ? originalRoot : null);
            if (!mount.Succeeded)
            {
                throw new IOException(mount.Error);
            }

            mapping.MappedRoot = mount.MountPoint;
            ConfigStore.Save(_config);
            RefreshNavigationRoots();
            return ReplacePathRoot(requestedPath, originalRoot, mount.MountPoint);
        }
        finally
        {
            _linuxMountGate.Release();
        }
    }

    private static bool IsPathWithinRoot(string path, string root)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(root))
        {
            return false;
        }

        var normalizedPath = Path.GetFullPath(path).TrimEnd('/');
        var normalizedRoot = Path.GetFullPath(root).TrimEnd('/');
        return normalizedPath.Equals(normalizedRoot, StringComparison.Ordinal) ||
               normalizedPath.StartsWith(normalizedRoot + "/", StringComparison.Ordinal);
    }

    private static string ReplacePathRoot(string path, string root, string replacement)
    {
        if (!IsPathWithinRoot(path, root))
        {
            return path;
        }

        var normalizedPath = Path.GetFullPath(path);
        var normalizedRoot = Path.GetFullPath(root).TrimEnd('/');
        var relativePath = normalizedPath[normalizedRoot.Length..].TrimStart('/');
        return string.IsNullOrWhiteSpace(relativePath) ? replacement : Path.Combine(replacement, relativePath);
    }

    private bool IsWindowsNasMappedDrive(string path)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(_config.Host))
        {
            return false;
        }

        var configuredHost = NasIdentity.NormalizeHost(_config.Host);
        return !string.IsNullOrWhiteSpace(configuredHost) && PathMapper.DiscoverWindowsDriveMappings()
            .Any(mapping =>
                NormalizeNavigationRoot(mapping.DriveRoot).Equals(NormalizeNavigationRoot(path), StringComparison.OrdinalIgnoreCase) &&
                NasIdentity.HostsMatch(configuredHost, NasIdentity.NormalizeHost(mapping.NetworkPath)));
    }


    private static string MappingNavigationDisplayName(PathMapping mapping)
    {
        var parts = (mapping.ShareRoot ?? "").Trim().Trim('\\', '/')
            .Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 0 ? NavigationDisplayName(mapping.MappedRoot) : parts[^1];
    }

    private static IReadOnlyList<ReadyWindowsDrive> ReadyWindowsDrives()
    {
        var drives = new List<ReadyWindowsDrive>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (!drive.IsReady || drive.DriveType is DriveType.NoRootDirectory or DriveType.Unknown)
                {
                    continue;
                }

                var root = drive.RootDirectory.FullName;
                if (!OperatingSystem.IsWindows() && !IsLinuxBrowsableMount(root))
                {
                    continue;
                }
                var driveLetter = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var label = string.IsNullOrWhiteSpace(drive.VolumeLabel)
                    ? DriveTypeDisplayName(drive.DriveType)
                    : drive.VolumeLabel.Trim();
                var totalBytes = drive.TotalSize;
                var freeBytes = drive.AvailableFreeSpace;
                var usedFraction = totalBytes > 0
                    ? Math.Clamp(1d - (double)freeBytes / totalBytes, 0d, 1d)
                    : 0d;
                drives.Add(new ReadyWindowsDrive(
                    $"{label} ({driveLetter})",
                    root,
                    usedFraction,
                    $"{FormatDriveBytes(freeBytes)} free of {FormatDriveBytes(totalBytes)}",
                    drive.DriveType));
            }
            catch (IOException)
            {
                // A removable or network drive can disappear while the tree is built.
            }
            catch (UnauthorizedAccessException)
            {
                // Keep inaccessible devices out of the navigation tree.
            }
        }
        return drives;
    }

    private bool ShouldShowNavigationDrive(ReadyWindowsDrive drive) =>
        _config.Behavior.ShowLocalNavigationDrives || drive.Type == DriveType.Network;

    private static bool IsLinuxBrowsableMount(string root)
    {
        if (OperatingSystem.IsWindows())
        {
            return true;
        }

        var normalized = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.IsNullOrEmpty(normalized) || normalized == "/")
        {
            return false;
        }

        var systemRoots = new[]
        {
            "/boot", "/dev", "/etc", "/home", "/lib", "/lib64", "/opt", "/proc", "/root",
            "/run", "/sbin", "/snap", "/sys", "/tmp", "/usr", "/var"
        };
        return !systemRoots.Any(systemRoot =>
            normalized.Equals(systemRoot, StringComparison.Ordinal) ||
            normalized.StartsWith(systemRoot + "/", StringComparison.Ordinal));
    }

    private static string DriveTypeDisplayName(DriveType driveType) => driveType switch
    {
        DriveType.Fixed => "Local Disk",
        DriveType.Network => "Network drive",
        DriveType.Removable => "Removable Disk",
        DriveType.CDRom => "DVD Drive",
        _ => "Drive",
    };

    private static string FormatDriveBytes(long bytes)
    {
        var units = new[] { "B", "KB", "MB", "GB", "TB", "PB" };
        double value = Math.Max(0, bytes);
        var index = 0;
        while (value >= 1024 && index < units.Length - 1)
        {
            value /= 1024;
            index++;
        }
        return index == 0 ? $"{value:0} {units[index]}" : $"{value:0.#} {units[index]}";
    }

    private sealed record ReadyWindowsDrive(string Name, string Path, double UsedFraction, string SpaceText, DriveType Type);

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

    public async Task OpenBrowserItemsInNewTabsAsync(IEnumerable<BrowserItem> source)
    {
        var items = source
            .Where(item => !string.IsNullOrWhiteSpace(item.FullPath))
            .DistinctBy(item => item.FullPath, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (items.Count == 0)
        {
            return;
        }

        var requestedCount = items.Count;
        var limit = Math.Clamp(_config.Behavior.MaxBulkOpenTabs, 1, 100);
        items = items.Take(limit).ToList();

        foreach (var item in items)
        {
            var location = item.IsFolder ? item.FullPath : Path.GetDirectoryName(item.FullPath);
            if (string.IsNullOrWhiteSpace(location))
            {
                continue;
            }

            var tab = AddSearchTab();
            SelectedSearchTab = tab;
            IsNavigationVisible = true;
            BrowserLocation = location;
            await BrowseAsync();

            if (!item.IsFolder)
            {
                SelectedBrowserItem = BrowserItems.FirstOrDefault(candidate =>
                    candidate.FullPath.Equals(item.FullPath, StringComparison.OrdinalIgnoreCase));
            }
        }

        Status = requestedCount > items.Count
            ? $"Opened {items.Count:n0} of {requestedCount:n0} items. Increase Folders opened at once in Settings to open more."
            : items.Count == 1 ? "Opened in a new QSurfer tab" : $"Opened {items.Count:n0} items in new QSurfer tabs";
    }

    public async Task ShowBrowserItemsInFileManagerAsync(IEnumerable<BrowserItem> source)
    {
        var items = source
            .Where(item => !string.IsNullOrWhiteSpace(item.FullPath))
            .DistinctBy(item => item.FullPath, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (items.Count == 0)
        {
            return;
        }

        foreach (var item in items)
        {
            var path = item.FullPath;
            if (!OperatingSystem.IsWindows())
            {
                path = _mapper.ResolveBrowserPath(path);
                if (IsUncPath(path))
                {
                    continue;
                }

                await OpenPathAsync(item.IsFolder ? path : Path.GetDirectoryName(path) ?? path);
                continue;
            }

            try
            {
                var arguments = item.IsFolder ? $"\"{path}\"" : $"/select,\"{path}\"";
                Process.Start(new ProcessStartInfo("explorer.exe", arguments) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                AppLogger.Error("browse", ex, $"show location failed path=\"{path}\"");
                Status = ex.Message;
                return;
            }
        }

        Status = items.Count == 1 ? "Showing location" : $"Showing {items.Count:n0} items in File Explorer";
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
            await _browser.CreateFolderAsync(ResolveBrowserLocation(BrowserLocation), name);
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
            var count = await _browser.CopyAsync(_browserClipboard, ResolveBrowserLocation(BrowserLocation), _browserClipboardIsCut);
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

        if (!OperatingSystem.IsWindows())
        {
            path = _mapper.ResolveBrowserPath(path);
            if (IsUncPath(path))
            {
                Status = "This path is not mapped to a local CIFS mount yet.";
                return Task.CompletedTask;
            }
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

    public async Task EmptyLocalRecycleBinAsync()
    {
        if (!IsLocalRecycleBinView)
        {
            return;
        }

        try
        {
            await _browser.EmptyLocalRecycleBinAsync(ResolveBrowserLocation(BrowserLocation));
            await BrowseAsync(addToHistory: false);
            Status = "Recycle Bin emptied";
            AppLogger.Info("browse", $"emptied local recycle bin path=\"{BrowserLocation}\"");
        }
        catch (Exception ex)
        {
            AppLogger.Error("browse", ex, $"empty recycle bin failed path=\"{BrowserLocation}\"");
            Status = ex.Message;
        }
    }

    public bool SupportsVersionHistory(SearchResult? result)
    {
        if (result == null) return false;
        var path = ResolveWindowsPath(result);
        return !string.IsNullOrWhiteSpace(path) && IsNasBackedPath(path);
    }

    private static bool IsUncPath(string path) =>
        path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal);

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
                ScopePaths = tab.ScopePaths.ToList(),
                ExcludedScopePaths = tab.ExcludedScopePaths.ToList(),
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

    private void SyncNavigationScopeSelection(SearchTabViewModel tab)
    {
        var scopes = tab.ScopePaths.Select(NormalizeNasScope).ToList();
        var excludedScopes = tab.ExcludedScopePaths.Select(NormalizeNasScope).ToList();
        foreach (var node in FlattenNavigationNodes(NavigationRoots))
        {
            var scope = node.IsHome || node.IsPlaceholder || string.IsNullOrWhiteSpace(node.FullPath)
                ? null
                : NormalizeNasScope(ResolveScopePath(ResolveBrowserLocation(node.FullPath)));
            node.IsScopeSelected = !string.IsNullOrWhiteSpace(scope) &&
                                   IsIncludedByMostSpecificScope(scope, scopes, excludedScopes);
        }
    }

    private static bool IsIncludedByMostSpecificScope(
        string path,
        IEnumerable<string> includedScopes,
        IEnumerable<string> excludedScopes)
    {
        var mostSpecificLength = -1;
        bool? decision = null;
        foreach (var (scope, included) in includedScopes.Select(scope => (scope, true))
                     .Concat(excludedScopes.Select(scope => (scope, false))))
        {
            if (!IsWithinAnyScope(path, [scope]))
            {
                continue;
            }

            if (scope.Length > mostSpecificLength || (scope.Length == mostSpecificLength && !included))
            {
                mostSpecificLength = scope.Length;
                decision = included;
            }
        }

        return decision == true;
    }

    private static string NormalizeNasScope(string? path) =>
        (path ?? "")
            .Replace('/', '\\')
            .Trim()
            .TrimEnd('*')
            .Trim('\\');

    private static bool IsWithinAnyScope(string path, IEnumerable<string> scopes) =>
        scopes.Any(scope => path.Equals(scope, StringComparison.OrdinalIgnoreCase) ||
                            path.StartsWith(scope + "\\", StringComparison.OrdinalIgnoreCase));

    private static IEnumerable<NavigationTreeNode> FlattenNavigationNodes(IEnumerable<NavigationTreeNode> nodes)
    {
        foreach (var node in nodes)
        {
            yield return node;
            foreach (var child in FlattenNavigationNodes(node.Children))
            {
                yield return child;
            }
        }
    }

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

    private sealed record SearchTabSnapshot(
        string Query,
        bool ExactMatch,
        bool SearchContents,
        bool IsPinned,
        IReadOnlyList<string> SelectedTypeNames,
        string ViewKey,
        string SortSpecification,
        string ScopeKey,
        string ScopePath,
        IReadOnlyList<string> ScopePaths,
        IReadOnlyList<string> ExcludedScopePaths,
        DateTime? DateFrom,
        DateTime? DateTo,
        IReadOnlyList<SearchResult> Results,
        SearchResult? SelectedResult,
        int NextOffset,
        bool CanLoadMore,
        string Status,
        bool IsBrowsing,
        long? SavedSearchId,
        string SavedSearchName);

    public void Dispose()
    {
        _addressSuggestionCancellation?.Cancel();
        _addressSuggestionCancellation?.Dispose();
        _nasNavigationRetryTimer.Stop();
        _nasNavigationRetryTimer.Tick -= NasNavigationRetryTimer_Tick;
        foreach (var tab in SearchTabs)
        {
            tab.Dispose();
        }
        _browseCancellation?.Cancel();
        _browseCancellation?.Dispose();
        CancelNavigationLoads();
        _client.Dispose();
        foreach (var icon in _iconCache.Values.Distinct())
        {
            icon.Dispose();
        }
        _recycleBinIcon?.Dispose();
        _iconCache.Clear();
        _browserItems.CollectionChanged -= BrowserItemsCollectionChanged;
    }

    private void CancelNavigationLoad(NavigationTreeNode node)
    {
        if (_navigationLoadCancellations.Remove(node, out var cancellation))
        {
            cancellation.Cancel();
            cancellation.Dispose();
        }
    }

    private void CancelNavigationLoads()
    {
        foreach (var cancellation in _navigationLoadCancellations.Values)
        {
            cancellation.Cancel();
            cancellation.Dispose();
        }
        _navigationLoadCancellations.Clear();
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

public sealed class BrowserPathSuggestion(string name, string path) : INotifyPropertyChanged
{
    private bool _isKeyboardSelected;

    public string Name { get; } = name;
    public string Path { get; } = path;

    public bool IsKeyboardSelected
    {
        get => _isKeyboardSelected;
        set
        {
            if (_isKeyboardSelected == value)
            {
                return;
            }

            _isKeyboardSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsKeyboardSelected)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

internal sealed record AddressDirectorySearchCacheEntry(DateTime CreatedUtc, IReadOnlyList<SearchResult> Folders);
