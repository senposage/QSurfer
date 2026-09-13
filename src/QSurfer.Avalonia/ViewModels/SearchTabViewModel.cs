using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using QSurfer.Core.Models;

namespace QSurfer.Avalonia.ViewModels;

public sealed class SearchTabViewModel : INotifyPropertyChanged, IDisposable
{
    private static readonly DateTime EarliestSearchDate = new(1970, 1, 1);
    private static readonly Regex TrailingScopeClause = new(
        @"(?:^|\s)in:(?:\""[^\""\r\n]+\""|[^\r\n]+)\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private readonly Func<SearchTabViewModel, Task> _search;
    private readonly Func<SearchTabViewModel, Task> _loadMore;
    private readonly Func<SearchTabViewModel, Task> _stop;
    private readonly Func<SearchTabViewModel, Task> _clear;
    private readonly Func<SearchTabViewModel, Task> _open;
    private readonly Func<SearchTabViewModel, Task> _browse;
    private readonly Func<SearchTabViewModel, Task> _toggleFavorite;
    private readonly Func<SearchTabViewModel, Task> _saveSearch;
    private readonly List<SearchResult> _allResults = [];
    private readonly List<SortRule> _sortRules = [];
    private readonly List<string> _scopePaths = [];
    private readonly List<string> _excludedScopePaths = [];
    private bool _isScopeAppendPending;
    private string _searchTitle;
    private string _browseTitle = "";
    private string _browseLocation = "";
    private bool _isBrowsing;
    private string _query = "";
    private string _status = "Ready";
    private bool _isSearching;
    private bool _exactMatch;
    private bool _searchContents;
    private bool _isPinned;
    private string _workspaceGlyph = "\U0001F50D";
    private bool _isTypeFilterOpen;
    private FileTypeFilter _selectedFileType;
    private ResultViewMode _selectedViewMode;
    private ResultSortMode _selectedSortMode;
    private SearchScope _selectedScope;
    private DateTime? _dateFrom;
    private DateTime? _dateTo;
    private DateRangePreset _selectedDatePreset;
    private bool _applyingDatePreset;
    private SearchResult? _selectedResult;
    private long? _savedSearchId;
    private string _savedSearchName = "";

    public SearchTabViewModel(
        int number,
        IReadOnlyList<FileTypeFilter> fileTypes,
        Func<SearchTabViewModel, Task> search,
        Func<SearchTabViewModel, Task> loadMore,
        Func<SearchTabViewModel, Task> stop,
        Func<SearchTabViewModel, Task> clear,
        Func<SearchTabViewModel, Task> open,
        Func<SearchTabViewModel, Task> browse,
        Func<SearchTabViewModel, Task> toggleFavorite,
        Func<SearchTabViewModel, Task> saveSearch)
    {
        Number = number;
        _searchTitle = $"Search {number}";
        FileTypes = fileTypes;
        _selectedFileType = fileTypes[0];
        _search = search;
        _loadMore = loadMore;
        _stop = stop;
        _clear = clear;
        _open = open;
        _browse = browse;
        _toggleFavorite = toggleFavorite;
        _saveSearch = saveSearch;
        ViewModes =
        [
            new ResultViewMode { Name = "Details", Key = "details" },
            new ResultViewMode { Name = "List", Key = "list" },
            new ResultViewMode { Name = "Small icons", Key = "small_icons" },
            new ResultViewMode { Name = "Large icons", Key = "large_icons" },
        ];
        SortModes =
        [
            new ResultSortMode { Name = "Folder groups", Key = "folder" },
            new ResultSortMode { Name = "Recentness", Key = "recent" },
            new ResultSortMode { Name = "Name", Key = "name" },
            new ResultSortMode { Name = "Location", Key = "location" },
            new ResultSortMode { Name = "Date modified", Key = "modified" },
            new ResultSortMode { Name = "Type", Key = "type" },
            new ResultSortMode { Name = "Size", Key = "size" },
        ];
        SearchScopes =
        [
            new SearchScope { Name = "All folders", Key = "all" },
            new SearchScope { Name = "This folder", Key = "folder" },
        ];
        var today = DateTime.Today;
        var weekStart = today.AddDays(-((int)today.DayOfWeek + 6) % 7);
        var monthStart = new DateTime(today.Year, today.Month, 1);
        DatePresets =
        [
            new DateRangePreset { Name = "Any date", To = today },
            new DateRangePreset { Name = "Today", From = today, To = today },
            new DateRangePreset { Name = "This week", From = weekStart, To = today },
            new DateRangePreset { Name = "Last week", From = weekStart.AddDays(-7), To = weekStart.AddDays(-1) },
            new DateRangePreset { Name = "This month", From = monthStart, To = today },
            new DateRangePreset { Name = "Last month", From = monthStart.AddMonths(-1), To = monthStart.AddDays(-1) },
            new DateRangePreset { Name = "Last 3 months", From = monthStart.AddMonths(-2), To = today },
            new DateRangePreset { Name = "Last 6 months", From = monthStart.AddMonths(-5), To = today },
            new DateRangePreset { Name = "Last year", From = today.AddYears(-1), To = today },
            new DateRangePreset { Name = "Custom range" },
        ];
        TypeFilterOptions = new ObservableCollection<FileTypeFilterOption>(fileTypes
            .Where(filter => filter.Name != "All types")
            .Select(filter => new FileTypeFilterOption { Filter = filter }));
        foreach (var option in TypeFilterOptions)
        {
            option.PropertyChanged += TypeFilterOptionPropertyChanged;
        }

        _selectedViewMode = ViewModes[0];
        _selectedSortMode = SortModes[0];
        _sortRules.Add(new SortRule(_selectedSortMode.Key, DefaultSortDescending(_selectedSortMode.Key)));
        _selectedScope = SearchScopes[0];
        _selectedDatePreset = DatePresets[0];
        _dateTo = Today;
        Results.CollectionChanged += ResultsChanged;
        // A replacement search is safe: the owning window cancels the current token
        // and advances SearchVersion before starting the next request.
        SearchCommand = new AsyncCommand(
            () => _search(this),
            () => !string.IsNullOrWhiteSpace(Query),
            allowsReplacementWhileExecuting: true);
        LoadMoreCommand = new AsyncCommand(() => _loadMore(this), () => !IsSearching && CanLoadMore);
        StopCommand = new AsyncCommand(() => _stop(this), () => IsSearching);
        ClearCommand = new AsyncCommand(() => _clear(this), () => !IsSearching && (_allResults.Count > 0 || !string.IsNullOrWhiteSpace(Query)));
        ClearFiltersCommand = new AsyncCommand(ClearFiltersAsync);
        OpenCommand = new AsyncCommand(() => _open(this), () => SelectedResult != null);
        BrowseCommand = new AsyncCommand(() => _browse(this), () => SelectedResult != null);
        ToggleFavoriteCommand = new AsyncCommand(() => _toggleFavorite(this), () => SelectedResult != null);
        SaveSearchCommand = new AsyncCommand(() => _saveSearch(this), () => !string.IsNullOrWhiteSpace(Query));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler? PinChanged;

    public int Number { get; }
    public ObservableCollection<SearchResult> Results { get; } = [];
    public IReadOnlyList<FileTypeFilter> FileTypes { get; }
    public ObservableCollection<FileTypeFilterOption> TypeFilterOptions { get; }
    public IReadOnlyList<ResultViewMode> ViewModes { get; }
    public IReadOnlyList<ResultSortMode> SortModes { get; }
    public IReadOnlyList<SearchScope> SearchScopes { get; }
    public IReadOnlyList<DateRangePreset> DatePresets { get; }
    public AsyncCommand SearchCommand { get; }
    public AsyncCommand LoadMoreCommand { get; }
    public AsyncCommand StopCommand { get; }
    public AsyncCommand ClearCommand { get; }
    public AsyncCommand ClearFiltersCommand { get; }
    public AsyncCommand OpenCommand { get; }
    public AsyncCommand BrowseCommand { get; }
    public AsyncCommand ToggleFavoriteCommand { get; }
    public AsyncCommand SaveSearchCommand { get; }
    public CancellationTokenSource? SearchCancellation { get; set; }
    public int SearchVersion { get; set; }
    public int NextOffset { get; set; }
    public bool CanLoadMore { get; set; }
    public string ScopePath => _scopePaths.FirstOrDefault() ?? "";
    public IReadOnlyList<string> ScopePaths => _scopePaths;
    public IReadOnlyList<string> ExcludedScopePaths => _excludedScopePaths;
    public bool IsScopeAppendPending
    {
        get => _isScopeAppendPending;
        private set
        {
            if (SetField(ref _isScopeAppendPending, value))
            {
                RefreshScopeDisplayEntries();
            }
        }
    }
    public ObservableCollection<ScopeDisplayEntry> ScopeDisplayEntries { get; } = [];
    public string ScopeSummary
    {
        get
        {
            var included = string.Join(Environment.NewLine, _scopePaths.Select(CompactScopeDisplayPath));
            return _excludedScopePaths.Count == 0
                ? included
                : $"{included}{Environment.NewLine}Excluding:{Environment.NewLine}{string.Join(Environment.NewLine, _excludedScopePaths.Select(CompactScopeDisplayPath))}";
        }
    }
    public bool HasFolderScope => SelectedScope.Key == "folder" && (_scopePaths.Count > 0 || _excludedScopePaths.Count > 0);
    public long? SavedSearchId => _savedSearchId;
    public string SavedSearchName => _savedSearchName;
    public bool HasSavedSearchSource => _savedSearchId.HasValue;

    public void SetSavedSearchSource(long id, string name)
    {
        _savedSearchId = id;
        _savedSearchName = name?.Trim() ?? "";
        OnPropertyChanged(nameof(SavedSearchId));
        OnPropertyChanged(nameof(SavedSearchName));
        OnPropertyChanged(nameof(HasSavedSearchSource));
    }

    public void SetScopeFolder(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        // A normal folder selection replaces the entire scope definition.
        // Explicit exclusions are retained only for multi-folder scope editing.
        _excludedScopePaths.Clear();
        SetScopeFolders([path]);
    }

    public void BeginScopeAppend()
    {
        if (_scopePaths.Count > 0)
        {
            IsScopeAppendPending = true;
        }
    }

    public bool ApplyAddressScope(string path)
    {
        if (ContainsIncludedScope(path))
        {
            IsScopeAppendPending = false;
            return false;
        }

        if (!IsScopeAppendPending)
        {
            SetScopeFolder(path);
            return true;
        }

        IsScopeAppendPending = false;
        return AddScopeFolder(path);
    }

    public bool ContainsIncludedScope(string path)
    {
        var normalized = NormalizeScopePath(path);
        return !string.IsNullOrWhiteSpace(normalized) &&
               _scopePaths.Any(scope => scope.Equals(normalized, StringComparison.OrdinalIgnoreCase));
    }

    public bool AddScopeFolder(string path)
    {
        var normalized = NormalizeScopePath(path);
        if (string.IsNullOrWhiteSpace(normalized) ||
            _scopePaths.Any(scope => scope.Equals(normalized, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        _scopePaths.Add(normalized);
        SelectedScope = SearchScopes[1];
        OnPropertyChanged(nameof(ScopePath));
        OnPropertyChanged(nameof(ScopePaths));
        OnPropertyChanged(nameof(ExcludedScopePaths));
        RefreshScopeDisplayEntries();
        OnPropertyChanged(nameof(ScopeSummary));
        OnPropertyChanged(nameof(HasFolderScope));
        ApplyFilters(forceRepaint: true);
        return true;
    }

    public void SetScopeFolders(IEnumerable<string> paths)
    {
        var normalized = paths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(NormalizeScopePath)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        _scopePaths.Clear();
        _scopePaths.AddRange(normalized);
        IsScopeAppendPending = false;
        SelectedScope = _scopePaths.Count > 0 || _excludedScopePaths.Count > 0 ? SearchScopes[1] : SearchScopes[0];
        OnPropertyChanged(nameof(ScopePath));
        OnPropertyChanged(nameof(ScopePaths));
        OnPropertyChanged(nameof(ExcludedScopePaths));
        RefreshScopeDisplayEntries();
        OnPropertyChanged(nameof(ScopeSummary));
        OnPropertyChanged(nameof(HasFolderScope));
        ApplyFilters(forceRepaint: true);
    }

    public void RestoreScope(
        string scopeKey,
        string? scopePath,
        IReadOnlyList<string>? scopePaths = null,
        IReadOnlyList<string>? excludedScopePaths = null)
    {
        _scopePaths.Clear();
        _scopePaths.AddRange((scopePaths ?? [scopePath ?? ""])
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(NormalizeScopePath)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase));
        _excludedScopePaths.Clear();
        _excludedScopePaths.AddRange((excludedScopePaths ?? [])
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(NormalizeScopePath)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase));
        IsScopeAppendPending = false;
        SelectedScope = (_scopePaths.Count > 0 || _excludedScopePaths.Count > 0) && scopeKey.Equals("folder", StringComparison.OrdinalIgnoreCase)
            ? SearchScopes[1]
            : SearchScopes.First(scope => scope.Key.Equals(scopeKey, StringComparison.OrdinalIgnoreCase));
        OnPropertyChanged(nameof(ScopePath));
        OnPropertyChanged(nameof(ScopePaths));
        OnPropertyChanged(nameof(ExcludedScopePaths));
        RefreshScopeDisplayEntries();
        OnPropertyChanged(nameof(ScopeSummary));
        OnPropertyChanged(nameof(HasFolderScope));
        ApplyFilters(forceRepaint: true);
    }

    public void ToggleScopeFolder(string path)
    {
        var normalized = NormalizeScopePath(path);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return;
        }

        var directScope = _scopePaths.FindIndex(scope => scope.Equals(normalized, StringComparison.OrdinalIgnoreCase));
        if (directScope >= 0)
        {
            _scopePaths.RemoveAt(directScope);
            _excludedScopePaths.RemoveAll(excluded => IsPathWithinScope(excluded, normalized));
        }
        else if (IsWithinAnyScope(normalized, _scopePaths))
        {
            var excludedScope = _excludedScopePaths.FindIndex(scope => scope.Equals(normalized, StringComparison.OrdinalIgnoreCase));
            if (excludedScope >= 0)
            {
                _excludedScopePaths.RemoveAt(excludedScope);
            }
            else
            {
                _excludedScopePaths.RemoveAll(excluded => IsPathWithinScope(excluded, normalized));
                _excludedScopePaths.Add(normalized);
            }
        }
        else
        {
            _scopePaths.Add(normalized);
        }

        SelectedScope = _scopePaths.Count > 0 || _excludedScopePaths.Count > 0 ? SearchScopes[1] : SearchScopes[0];
        OnPropertyChanged(nameof(ScopePath));
        OnPropertyChanged(nameof(ScopePaths));
        OnPropertyChanged(nameof(ExcludedScopePaths));
        RefreshScopeDisplayEntries();
        OnPropertyChanged(nameof(ScopeSummary));
        OnPropertyChanged(nameof(HasFolderScope));
        ApplyFilters();
    }

    public void ToggleExcludedScopeFolder(string path)
    {
        var normalized = NormalizeScopePath(path);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return;
        }

        var directExclusion = _excludedScopePaths.FindIndex(scope => scope.Equals(normalized, StringComparison.OrdinalIgnoreCase));
        if (directExclusion >= 0)
        {
            _excludedScopePaths.RemoveAt(directExclusion);
        }
        else
        {
            _excludedScopePaths.RemoveAll(excluded => IsPathWithinScope(excluded, normalized));
            _excludedScopePaths.Add(normalized);
        }

        SelectedScope = _scopePaths.Count > 0 || _excludedScopePaths.Count > 0 ? SearchScopes[1] : SearchScopes[0];
        OnPropertyChanged(nameof(ScopePath));
        OnPropertyChanged(nameof(ScopePaths));
        OnPropertyChanged(nameof(ExcludedScopePaths));
        RefreshScopeDisplayEntries();
        OnPropertyChanged(nameof(ScopeSummary));
        OnPropertyChanged(nameof(HasFolderScope));
        ApplyFilters();
    }

    public void RemoveScopeEntry(ScopeDisplayEntry entry)
    {
        var normalized = NormalizeScopePath(entry.Path);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return;
        }

        if (entry.IsExcluded)
        {
            _excludedScopePaths.RemoveAll(path => path.Equals(normalized, StringComparison.OrdinalIgnoreCase));
        }
        else
        {
            _scopePaths.RemoveAll(path => path.Equals(normalized, StringComparison.OrdinalIgnoreCase));
            _excludedScopePaths.RemoveAll(path => IsPathWithinScope(path, normalized));
        }

        SelectedScope = _scopePaths.Count > 0 || _excludedScopePaths.Count > 0 ? SearchScopes[1] : SearchScopes[0];
        OnPropertyChanged(nameof(ScopePath));
        OnPropertyChanged(nameof(ScopePaths));
        OnPropertyChanged(nameof(ExcludedScopePaths));
        RefreshScopeDisplayEntries();
        OnPropertyChanged(nameof(ScopeSummary));
        OnPropertyChanged(nameof(HasFolderScope));
        ApplyFilters();
    }

    public void SetScopeEntryIncluded(ScopeDisplayEntry entry, bool included)
    {
        if (entry.IsPending)
        {
            return;
        }

        var normalized = NormalizeScopePath(entry.Path);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return;
        }

        var isIncluded = _scopePaths.Any(path => path.Equals(normalized, StringComparison.OrdinalIgnoreCase));
        if (isIncluded == included)
        {
            return;
        }

        if (included)
        {
            _excludedScopePaths.RemoveAll(path => path.Equals(normalized, StringComparison.OrdinalIgnoreCase));
            _scopePaths.Add(normalized);
        }
        else
        {
            _scopePaths.RemoveAll(path => path.Equals(normalized, StringComparison.OrdinalIgnoreCase));
            _excludedScopePaths.RemoveAll(path => path.Equals(normalized, StringComparison.OrdinalIgnoreCase));
            _excludedScopePaths.Add(normalized);
        }

        SelectedScope = SearchScopes[1];
        OnPropertyChanged(nameof(ScopePath));
        OnPropertyChanged(nameof(ScopePaths));
        OnPropertyChanged(nameof(ExcludedScopePaths));
        RefreshScopeDisplayEntries();
        OnPropertyChanged(nameof(ScopeSummary));
        OnPropertyChanged(nameof(HasFolderScope));
        ApplyFilters(forceRepaint: true);
    }

    private void RefreshScopeDisplayEntries()
    {
        var entries = _scopePaths
            .Select(path => new ScopeDisplayEntry(path, CompactScopeDisplayPath(path), false, CountResultsWithinScope(path)))
            .Concat(_excludedScopePaths.Select(path => new ScopeDisplayEntry(path, $"Excluded: {CompactScopeDisplayPath(path)}", true, CountResultsWithinScope(path))))
            // A shared path's lexical order also puts its parent before its children.
            .OrderBy(entry => entry.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (IsScopeAppendPending)
        {
            entries.Add(ScopeDisplayEntry.Pending);
        }

        ScopeDisplayEntries.Clear();
        foreach (var entry in entries)
        {
            ScopeDisplayEntries.Add(entry);
        }
    }

    public void ClearScopeFolder()
    {
        IsScopeAppendPending = false;
        _excludedScopePaths.Clear();
        SetScopeFolders([]);
    }
    public string SortSpecification => string.Join(',', _sortRules.Select(rule => $"{rule.Key}:{(rule.Descending ? "desc" : "asc")}"));
    public string PrimarySortKey => _sortRules.FirstOrDefault()?.Key ?? _selectedSortMode.Key;

    public string Title
    {
        get => _isBrowsing && !string.IsNullOrWhiteSpace(_browseTitle) ? _browseTitle : _searchTitle;
    }

    public string WorkspaceGlyph
    {
        get => _workspaceGlyph;
        private set => SetField(ref _workspaceGlyph, value);
    }

    public bool IsBrowsing => _isBrowsing;
    public string BrowseLocation => _browseLocation;

    public void SetWorkspaceMode(bool browsing)
    {
        if (_isBrowsing != browsing)
        {
            _isBrowsing = browsing;
            OnPropertyChanged(nameof(IsBrowsing));
            OnPropertyChanged(nameof(Title));
        }

        WorkspaceGlyph = browsing ? "\U0001F4C2" : "\U0001F50D";
    }

    public void SetBrowseLocation(string path)
    {
        var trimmed = (path ?? "").Trim().TrimEnd('\\', '/');
        _browseLocation = path ?? "";
        _browseTitle = string.IsNullOrWhiteSpace(trimmed) ? "Browse" : CompactBrowseTitle(trimmed);
        if (_isBrowsing)
        {
            OnPropertyChanged(nameof(Title));
        }
    }

    public string Query
    {
        get => _query;
        set
        {
            if (!SetField(ref _query, value))
            {
                return;
            }
            _searchTitle = string.IsNullOrWhiteSpace(value) ? $"Search {Number}" : value.Trim();
            if (!_isBrowsing)
            {
                OnPropertyChanged(nameof(Title));
            }
            ApplyFilters();
            SearchCommand.RaiseCanExecuteChanged();
            SaveSearchCommand.RaiseCanExecuteChanged();
            ClearCommand.RaiseCanExecuteChanged();
        }
    }

    public string Status
    {
        get => _status;
        set => SetField(ref _status, value);
    }

    public bool IsSearching
    {
        get => _isSearching;
        set
        {
            if (!SetField(ref _isSearching, value))
            {
                return;
            }
            SearchCommand.RaiseCanExecuteChanged();
            LoadMoreCommand.RaiseCanExecuteChanged();
            StopCommand.RaiseCanExecuteChanged();
            ClearCommand.RaiseCanExecuteChanged();
        }
    }

    public bool ExactMatch
    {
        get => _exactMatch;
        set
        {
            if (SetField(ref _exactMatch, value))
            {
                ApplyFilters();
            }
        }
    }

    public bool SearchContents
    {
        get => _searchContents;
        set
        {
            if (SetField(ref _searchContents, value))
            {
                ApplyFilters();
            }
        }
    }

    public bool IsPinned
    {
        get => _isPinned;
        set
        {
            if (!SetField(ref _isPinned, value))
            {
                return;
            }
            PinChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public bool IsTypeFilterOpen
    {
        get => _isTypeFilterOpen;
        set => SetField(ref _isTypeFilterOpen, value);
    }

    public FileTypeFilter SelectedFileType
    {
        get => _selectedFileType;
        set => SetField(ref _selectedFileType, value);
    }

    public ResultViewMode SelectedViewMode
    {
        get => _selectedViewMode;
        set
        {
            if (!SetField(ref _selectedViewMode, value))
            {
                return;
            }
            OnPropertyChanged(nameof(IsDetailsView));
            OnPropertyChanged(nameof(IsListView));
            OnPropertyChanged(nameof(IsIconView));
            OnPropertyChanged(nameof(IsLargeIconView));
            OnPropertyChanged(nameof(IsSmallIconView));
        }
    }

    public ResultSortMode SelectedSortMode
    {
        get => _selectedSortMode;
        set
        {
            if (SetField(ref _selectedSortMode, value))
            {
                _sortRules.Clear();
                _sortRules.Add(new SortRule(value.Key, DefaultSortDescending(value.Key)));
                OnPropertyChanged(nameof(SortSpecification));
                ApplyFilters();
            }
        }
    }

    public SearchScope SelectedScope
    {
        get => _selectedScope;
        set
        {
            if (SetField(ref _selectedScope, value))
            {
                ApplyFilters();
                OnPropertyChanged(nameof(HasFolderScope));
            }
        }
    }

    public DateTime? DateFrom
    {
        get => _dateFrom;
        set
        {
            var normalized = NormalizeDate(value);
            if (SetField(ref _dateFrom, normalized))
            {
                SelectCustomDatePreset();
                ApplyFilters();
            }
            else if (value != normalized)
            {
                OnPropertyChanged();
            }
        }
    }

    public DateTime? DateTo
    {
        get => _dateTo;
        set
        {
            var normalized = NormalizeDate(value);
            if (SetField(ref _dateTo, normalized))
            {
                SelectCustomDatePreset();
                ApplyFilters();
            }
            else if (value != normalized)
            {
                OnPropertyChanged();
            }
        }
    }

    public SearchResult? SelectedResult
    {
        get => _selectedResult;
        set
        {
            if (!SetField(ref _selectedResult, value))
            {
                return;
            }
            OpenCommand.RaiseCanExecuteChanged();
            BrowseCommand.RaiseCanExecuteChanged();
            ToggleFavoriteCommand.RaiseCanExecuteChanged();
        }
    }

    public DateRangePreset SelectedDatePreset
    {
        get => _selectedDatePreset;
        set
        {
            if (!SetField(ref _selectedDatePreset, value) || _applyingDatePreset || value.Name == "Custom range")
            {
                return;
            }
            _applyingDatePreset = true;
            _dateFrom = value.From;
            _dateTo = value.To;
            OnPropertyChanged(nameof(DateFrom));
            OnPropertyChanged(nameof(DateTo));
            _applyingDatePreset = false;
            ApplyFilters();
        }
    }

    public bool IsDetailsView => SelectedViewMode.Key == "details";
    public bool IsListView => SelectedViewMode.Key == "list";
    public bool IsIconView => SelectedViewMode.Key is "small_icons" or "large_icons";
    public bool IsLargeIconView => SelectedViewMode.Key == "large_icons";
    public bool IsSmallIconView => SelectedViewMode.Key == "small_icons";
    public bool HasResults => Results.Count > 0;
    public bool HasNoResults => !HasResults;
    // A blank start with today's end date is the default "everything" state.
    public bool HasDateRange => DateFrom.HasValue || (DateTo.HasValue && DateTo.Value.Date < Today.Date);
    public DateTime Today => DateTime.Today;
    public DateTime MinimumSearchDate => EarliestSearchDate;
    public string TypeFilterSummary
    {
        get
        {
            var selected = TypeFilterOptions.Where(option => option.IsSelected).Select(option => option.Name).ToList();
            return selected.Count switch
            {
                0 => "All types",
                1 => selected[0],
                _ => $"{selected.Count} types",
            };
        }
    }

    public void CancelSearch()
    {
        try
        {
            SearchCancellation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    public void ResetResults()
    {
        _allResults.Clear();
        Results.Clear();
        SelectedResult = null;
        NextOffset = 0;
        CanLoadMore = false;
        LoadMoreCommand.RaiseCanExecuteChanged();
        RefreshScopeDisplayEntries();
    }


    public bool ContainsResult(SearchResult candidate) => _allResults.Any(existing =>
        string.Equals(existing.FileName, candidate.FileName, StringComparison.OrdinalIgnoreCase) &&
        ResultPathCandidates(existing).Any(existingPath =>
            ResultPathCandidates(candidate).Any(candidatePath =>
                NormalizeNasPath(existingPath).Equals(NormalizeNasPath(candidatePath), StringComparison.OrdinalIgnoreCase))));

    public void AddResults(IEnumerable<SearchResult> source)
    {
        var changed = false;
        foreach (var result in source)
        {
            if (ContainsResult(result))
            {
                continue;
            }
            _allResults.Add(result);
            changed = true;
        }
        if (changed)
        {
            ApplyFilters();
            RefreshScopeDisplayEntries();
        }
    }

    public void SetCanLoadMore(bool value)
    {
        CanLoadMore = value;
        LoadMoreCommand.RaiseCanExecuteChanged();
    }

    public void ApplyTypeSelection(IEnumerable<string> typeNames)
    {
        var names = new HashSet<string>(typeNames ?? [], StringComparer.OrdinalIgnoreCase);
        foreach (var option in TypeFilterOptions)
        {
            option.IsSelected = names.Contains(option.Name);
        }
        ApplyFilters();
    }

    public void ApplyColumnSort(string key, bool append)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return;
        }

        var existingIndex = _sortRules.FindIndex(rule => rule.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
        if (!append)
        {
            var descending = existingIndex == 0 ? !_sortRules[0].Descending : DefaultSortDescending(key);
            _sortRules.Clear();
            _sortRules.Add(new SortRule(key, descending));
        }
        else if (existingIndex >= 0)
        {
            var rule = _sortRules[existingIndex];
            _sortRules[existingIndex] = rule with { Descending = !rule.Descending };
        }
        else
        {
            _sortRules.Add(new SortRule(key, DefaultSortDescending(key)));
        }

        var primary = SortModes.FirstOrDefault(mode => mode.Key.Equals(_sortRules[0].Key, StringComparison.OrdinalIgnoreCase));
        if (primary != null && !ReferenceEquals(primary, _selectedSortMode))
        {
            _selectedSortMode = primary;
            OnPropertyChanged(nameof(SelectedSortMode));
        }
        OnPropertyChanged(nameof(SortSpecification));
        ApplyFilters();
    }

    public void ApplySortSpecification(string? specification)
    {
        var parsed = (specification ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(value => value.Split(':', 2, StringSplitOptions.TrimEntries))
            .Where(parts => parts.Length > 0 && SortModes.Any(mode => mode.Key.Equals(parts[0], StringComparison.OrdinalIgnoreCase)))
            .Select(parts => new SortRule(parts[0], parts.Length > 1 ? parts[1].Equals("desc", StringComparison.OrdinalIgnoreCase) : DefaultSortDescending(parts[0])))
            .ToList();
        if (parsed.Count == 0)
        {
            return;
        }

        _sortRules.Clear();
        _sortRules.AddRange(parsed);
        var primary = SortModes.First(mode => mode.Key.Equals(_sortRules[0].Key, StringComparison.OrdinalIgnoreCase));
        _selectedSortMode = primary;
        OnPropertyChanged(nameof(SelectedSortMode));
        OnPropertyChanged(nameof(SortSpecification));
        ApplyFilters();
    }

    public void RefreshVisibleResults(bool forceRepaint = false) => ApplyFilters(forceRepaint);

    public void Dispose()
    {
        CancelSearch();
        SearchCancellation?.Dispose();
        SearchCancellation = null;
        Results.CollectionChanged -= ResultsChanged;
        foreach (var option in TypeFilterOptions)
        {
            option.PropertyChanged -= TypeFilterOptionPropertyChanged;
        }
    }

    private Task ClearFiltersAsync()
    {
        ExactMatch = false;
        SearchContents = false;
        SelectedDatePreset = DatePresets[0];
        RestoreScope("all", "");
        foreach (var option in TypeFilterOptions)
        {
            option.IsSelected = false;
        }
        ApplyFilters();
        return Task.CompletedTask;
    }

    private void SelectCustomDatePreset()
    {
        if (_applyingDatePreset || _selectedDatePreset.Name == "Custom range")
        {
            return;
        }
        _selectedDatePreset = DatePresets[^1];
        OnPropertyChanged(nameof(SelectedDatePreset));
    }

    private void ApplyFilters(bool forceRepaint = false)
    {
        if (_allResults.Count == 0 && Results.Count == 0)
        {
            OnPropertyChanged(nameof(TypeFilterSummary));
            if (HasFolderScope)
            {
                RefreshScopeDisplayEntries();
            }
            return;
        }

        var ordered = Sort(_allResults.Where(MatchesFilters)).ToList();
        if (forceRepaint)
        {
            // A user-selected arrangement is infrequent. Resetting the collection here
            // makes Avalonia redraw immediately, unlike incremental moves during a
            // streaming search where preserving the scroll position is more important.
            Results.Clear();
            foreach (var result in ordered)
            {
                Results.Add(result);
            }
        }
        else
        {
            SynchronizeVisibleResults(ordered);
        }
        OnPropertyChanged(nameof(TypeFilterSummary));
        if (HasFolderScope)
        {
            RefreshScopeDisplayEntries();
        }
    }

    private void SynchronizeVisibleResults(IReadOnlyList<SearchResult> ordered)
    {
        // Never clear and repopulate during a paint batch: the view treats that as a new list and resets its scrollbar.
        var expected = ordered.ToHashSet();
        for (var index = Results.Count - 1; index >= 0; index--)
        {
            if (!expected.Contains(Results[index]))
            {
                Results.RemoveAt(index);
            }
        }

        for (var targetIndex = 0; targetIndex < ordered.Count; targetIndex++)
        {
            var result = ordered[targetIndex];
            var currentIndex = Results.IndexOf(result);
            if (currentIndex < 0)
            {
                Results.Insert(targetIndex, result);
            }
            else if (currentIndex != targetIndex)
            {
                Results.Move(currentIndex, targetIndex);
            }
        }
    }

    private bool MatchesFilters(SearchResult result)
    {
        var selectedTypes = TypeFilterOptions.Where(option => option.IsSelected).Select(option => option.Filter).ToList();
        if (selectedTypes.Count > 0 && !selectedTypes.Any(filter => MatchesType(result, filter)))
        {
            return false;
        }
        if (!result.IsFolder && DateFrom is { } from && result.ModifiedDate is { } modifiedFrom && modifiedFrom.Date < from.Date)
        {
            return false;
        }
        if (!result.IsFolder && DateTo is { } to && result.ModifiedDate is { } modifiedTo && modifiedTo.Date > to.Date)
        {
            return false;
        }
        if (!result.IsFolder && HasDateRange && result.ModifiedDate == null)
        {
            return false;
        }
        if (HasFolderScope && !MatchesFolderScope(result))
        {
            return false;
        }
        var queryTerm = TrailingScopeClause.Replace(Query, "").Trim();
        if (ExactMatch && !SearchContents && !string.IsNullOrWhiteSpace(queryTerm))
        {
            var pattern = $"(?<![A-Za-z0-9]){Regex.Escape(queryTerm)}(?![A-Za-z0-9])";
            if (!Regex.IsMatch(result.FileName, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            {
                return false;
            }
        }
        return true;
    }

    private static bool IsWithinAnyScope(string path, IEnumerable<string> scopes) =>
        scopes.Any(scope => IsPathWithinScope(path, scope));

    private static bool IsResultWithinAnyScope(SearchResult result, IEnumerable<string> scopes)
    {
        // NAS search, browser results, and saved results can carry the same location
        // in different forms. Scope matching must accept their canonical NAS path as
        // well as the mapped/locally resolved form without making UI state depend on
        // which source supplied the result.
        return ResultPathCandidates(result)
            .Any(path => IsWithinAnyScope(path, scopes));
    }

    // A more specific explicit selection wins. This lets an included child remain
    // searchable after its parent is turned off in the search-folder list.
    private bool MatchesFolderScope(SearchResult result) =>
        ResultPathCandidates(result).Any(MatchesFolderScopePath);

    private bool MatchesFolderScopePath(string path)
    {
        var mostSpecificLength = -1;
        bool? decision = null;

        foreach (var (scope, included) in _scopePaths.Select(scope => (scope, true))
                     .Concat(_excludedScopePaths.Select(scope => (scope, false))))
        {
            if (!IsPathWithinScope(path, scope))
            {
                continue;
            }

            var length = NormalizeScopePath(scope).Length;
            // An exclusion breaks a tie, keeping a deliberately disabled exact
            // folder disabled unless a deeper child was explicitly included.
            if (length > mostSpecificLength || (length == mostSpecificLength && !included))
            {
                mostSpecificLength = length;
                decision = included;
            }
        }

        return decision ?? _scopePaths.Count == 0;
    }

    private int CountResultsWithinScope(string scopePath) =>
        _allResults.Count(result =>
            IsResultWithinAnyScope(result, [scopePath]) &&
            MatchesFilters(result));

    private static IEnumerable<string> ResultPathCandidates(SearchResult result)
    {
        foreach (var path in new[] { result.Path, result.ResolvedPath, result.WindowsPath })
        {
            if (!string.IsNullOrWhiteSpace(path))
            {
                yield return path;
            }
        }
    }

    private static bool IsPathWithinScope(string path, string scopePath)
    {
        var result = NormalizeNasPath(path);
        var scope = NormalizeNasPath(scopePath);
        return string.Equals(result, scope, StringComparison.OrdinalIgnoreCase) ||
               result.StartsWith(scope + "\\", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeNasPath(string path) =>
        (path ?? "")
            .Replace('/', '\\')
            .Trim()
            .TrimEnd('*')
            .Trim('\\');

    private static string NormalizeScopePath(string path) =>
        NormalizeNasPath(path).TrimEnd('\\');

    private static string CompactScopeDisplayPath(string path)
    {
        var normalized = NormalizeScopePath(path);
        if (!OperatingSystem.IsWindows())
        {
            var unixPath = normalized.Replace('\\', '/');
            return unixPath.StartsWith('/') ? unixPath : "/" + unixPath;
        }

        if (normalized.Length == 2 && char.IsLetter(normalized[0]) && normalized[1] == ':')
        {
            return normalized + "\\";
        }
        return normalized.StartsWith("Shared\\", StringComparison.OrdinalIgnoreCase)
            ? normalized["Shared\\".Length..]
            : normalized;
    }

    private DateTime? NormalizeDate(DateTime? value)
    {
        if (value is not { } date)
        {
            return null;
        }

        if (date.Date < EarliestSearchDate)
        {
            return null;
        }

        return date.Date > Today.Date ? Today : date.Date;
    }


    private static bool MatchesType(SearchResult result, FileTypeFilter filter) =>
        result.IsFolder
            ? filter.IncludeFolders
            : filter.IncludeAllFiles || filter.Extensions.Contains(result.Extension, StringComparer.OrdinalIgnoreCase);

    private IEnumerable<SearchResult> Sort(IEnumerable<SearchResult> source)
    {
        var rules = _sortRules.Count == 0
            ? [new SortRule("folder", false)]
            : _sortRules;

        // Folder groups is deliberately Explorer-like: folders are surfaced first.
        // Every other arrangement sorts folders and files together by the selected
        // property, so the default grouping cannot mask an explicit sort choice.
        IOrderedEnumerable<SearchResult> ordered;
        var firstRuleIndex = 0;
        if (rules[0].Key.Equals("folder", StringComparison.OrdinalIgnoreCase))
        {
            ordered = source
                .OrderBy(result => result.IsFolder ? 0 : 1)
                // Folder names remain alphabetical. Files use newest first, then
                // fall back to their filename where dates are equal or unavailable.
                .ThenBy(result => result.IsFolder ? result.FileName : "", StringComparer.CurrentCultureIgnoreCase)
                .ThenByDescending(result => result.IsFolder ? DateTime.MinValue : result.ModifiedDate ?? DateTime.MinValue);
            firstRuleIndex = 1;
        }
        else
        {
            ordered = OrderBySortRule(source, rules[0]);
            firstRuleIndex = 1;
        }

        for (var index = firstRuleIndex; index < rules.Count; index++)
        {
            ordered = ApplySortRule(ordered, rules[index]);
        }
        // Providers do not necessarily order equal names or timestamps alike. Keep
        // the final order local and stable so batches cannot reshuffle the view.
        return ordered
            .ThenBy(result => result.FileName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(StableResultPath, StringComparer.OrdinalIgnoreCase);
    }

    private static IOrderedEnumerable<SearchResult> OrderBySortRule(IEnumerable<SearchResult> source, SortRule rule) =>
        rule.Key.ToLowerInvariant() switch
        {
            "name" => rule.Descending
                ? source.OrderByDescending(result => result.FileName, StringComparer.CurrentCultureIgnoreCase)
                : source.OrderBy(result => result.FileName, StringComparer.CurrentCultureIgnoreCase),
            "modified" or "recent" => rule.Descending
                ? source.OrderByDescending(result => result.ModifiedDate ?? DateTime.MinValue)
                : source.OrderBy(result => result.ModifiedDate ?? DateTime.MinValue),
            "type" => rule.Descending
                ? source.OrderByDescending(result => result.Kind, StringComparer.CurrentCultureIgnoreCase)
                : source.OrderBy(result => result.Kind, StringComparer.CurrentCultureIgnoreCase),
            "size" => rule.Descending
                ? source.OrderByDescending(result => result.Size)
                : source.OrderBy(result => result.Size),
            _ => rule.Descending
                ? source.OrderByDescending(result => result.DisplayPath, StringComparer.CurrentCultureIgnoreCase)
                : source.OrderBy(result => result.DisplayPath, StringComparer.CurrentCultureIgnoreCase),
        };

    private static IOrderedEnumerable<SearchResult> ApplySortRule(IOrderedEnumerable<SearchResult> source, SortRule rule) =>
        rule.Key.ToLowerInvariant() switch
        {
            "name" => rule.Descending
                ? source.ThenByDescending(result => result.FileName, StringComparer.CurrentCultureIgnoreCase)
                : source.ThenBy(result => result.FileName, StringComparer.CurrentCultureIgnoreCase),
            "modified" or "recent" => rule.Descending
                ? source.ThenByDescending(result => result.ModifiedDate ?? DateTime.MinValue)
                : source.ThenBy(result => result.ModifiedDate ?? DateTime.MinValue),
            "type" => rule.Descending
                ? source.ThenByDescending(result => result.Kind, StringComparer.CurrentCultureIgnoreCase)
                : source.ThenBy(result => result.Kind, StringComparer.CurrentCultureIgnoreCase),
            "size" => rule.Descending
                ? source.ThenByDescending(result => result.Size)
                : source.ThenBy(result => result.Size),
            _ => rule.Descending
                ? source.ThenByDescending(result => result.DisplayPath, StringComparer.CurrentCultureIgnoreCase)
                : source.ThenBy(result => result.DisplayPath, StringComparer.CurrentCultureIgnoreCase),
        };

    private static bool DefaultSortDescending(string key) => key is "modified" or "recent" or "size";

    private static string StableResultPath(SearchResult result) =>
        NormalizeNasPath(string.IsNullOrWhiteSpace(result.Path)
            ? result.ResolvedPath ?? result.WindowsPath ?? ""
            : result.Path);

    private static string CompactBrowseTitle(string path)
    {
        var parts = path.Trim('\\', '/').Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
        return parts.Length <= 2 ? path : $"...\\{parts[^2]}\\{parts[^1]}";
    }

    private sealed record SortRule(string Key, bool Descending);

    private void TypeFilterOptionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FileTypeFilterOption.IsSelected))
        {
            ApplyFilters();
            OnPropertyChanged(nameof(TypeFilterOptions));
        }
    }

    private void ResultsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(HasResults));
        OnPropertyChanged(nameof(HasNoResults));
        ClearCommand.RaiseCanExecuteChanged();
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

public sealed record ScopeDisplayEntry(string Path, string Text, bool IsExcluded, int ResultCount = 0, bool IsPending = false)
{
    public static ScopeDisplayEntry Pending { get; } = new("", "Type a new path to add to this query", false, 0, true);
    public bool IsIncluded => !IsExcluded && !IsPending;
    public bool CanRemove => !IsPending;
    public string ResultCountText => ResultCount == 1 ? "1 result" : $"{ResultCount:N0} results";
}
