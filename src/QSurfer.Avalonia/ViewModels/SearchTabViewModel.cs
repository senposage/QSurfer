using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using QSurfer.Core.Models;

namespace QSurfer.Avalonia.ViewModels;

public sealed class SearchTabViewModel : INotifyPropertyChanged, IDisposable
{
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
    private string _searchTitle;
    private string _browseTitle = "";
    private bool _isBrowsing;
    private string _query = "";
    private string _status = "Ready";
    private bool _isSearching;
    private bool _exactMatch;
    private bool _searchContents;
    private bool _isPinned;
    private string _workspaceGlyph = "\uE721";
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
        SearchCommand = new AsyncCommand(() => _search(this), () => !IsSearching && !string.IsNullOrWhiteSpace(Query));
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
    public string ScopePath { get; private set; } = "";
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

    public void SetWorkspaceMode(bool browsing)
    {
        if (_isBrowsing != browsing)
        {
            _isBrowsing = browsing;
            OnPropertyChanged(nameof(Title));
        }

        WorkspaceGlyph = browsing ? "\uE838" : "\uE721";
    }

    public void SetBrowseLocation(string path)
    {
        var trimmed = (path ?? "").Trim().TrimEnd('\\', '/');
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
            if (value?.IsFolder == true)
            {
                ScopePath = value.Path;
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
    }

    public bool ContainsResult(SearchResult candidate) => _allResults.Any(existing =>
        string.Equals(existing.Path, candidate.Path, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(existing.FileName, candidate.FileName, StringComparison.OrdinalIgnoreCase));

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
        SelectedScope = SearchScopes[0];
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

    private void ApplyFilters()
    {
        if (_allResults.Count == 0 && Results.Count == 0)
        {
            OnPropertyChanged(nameof(TypeFilterSummary));
            return;
        }

        var filtered = _allResults.Where(MatchesFilters);
        foreach (var result in Sort(filtered))
        {
            if (!Results.Contains(result))
            {
                Results.Add(result);
            }
        }
        for (var index = Results.Count - 1; index >= 0; index--)
        {
            if (!MatchesFilters(Results[index]))
            {
                Results.RemoveAt(index);
            }
        }
        var ordered = Sort(Results).ToList();
        if (!Results.SequenceEqual(ordered))
        {
            Results.Clear();
            foreach (var result in ordered)
            {
                Results.Add(result);
            }
        }
        OnPropertyChanged(nameof(TypeFilterSummary));
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
        if (SelectedScope.Key == "folder" && !string.IsNullOrWhiteSpace(ScopePath) &&
            !result.Path.StartsWith(ScopePath.TrimEnd('\\', '/') + "\\", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(result.Path.TrimEnd('\\', '/'), ScopePath.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        if (ExactMatch && !SearchContents && !string.IsNullOrWhiteSpace(Query))
        {
            var pattern = $"(?<![A-Za-z0-9]){Regex.Escape(Query.Trim())}(?![A-Za-z0-9])";
            if (!Regex.IsMatch(result.FileName, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            {
                return false;
            }
        }
        return true;
    }

    private DateTime? NormalizeDate(DateTime? value) =>
        value is { } date && date.Date > Today.Date ? Today : value;

    private static bool MatchesType(SearchResult result, FileTypeFilter filter) =>
        result.IsFolder
            ? filter.IncludeFolders
            : filter.IncludeAllFiles || filter.Extensions.Contains(result.Extension, StringComparer.OrdinalIgnoreCase);

    private IEnumerable<SearchResult> Sort(IEnumerable<SearchResult> source)
    {
        IOrderedEnumerable<SearchResult> ordered = source.OrderBy(result => result.IsFolder ? 0 : 1);
        foreach (var rule in _sortRules)
        {
            ordered = ApplySortRule(ordered, rule);
        }
        return ordered.ThenBy(result => result.FileName, StringComparer.CurrentCultureIgnoreCase);
    }

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
