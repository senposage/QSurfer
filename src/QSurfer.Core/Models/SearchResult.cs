using System.Text.Json;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace QSurfer.Core.Models;

public sealed class SearchResult : INotifyPropertyChanged
{
    private object? _iconSource;
    private bool _isFavorite;
    private List<string> _groups = [];
    private string _resolvedPath = "";
    private string _windowsPath = "";
    private string _modified = "";
    private bool _showInternalPath;

    public string Name { get; set; } = "";
    public string Extension { get; set; } = "";
    public string Path { get; set; } = "";
    public string ResolvedPath
    {
        get => _resolvedPath;
        set
        {
            var normalized = value ?? "";
            if (_resolvedPath.Equals(normalized, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
            _resolvedPath = normalized;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DisplayPath));
        }
    }
    public string WindowsPath
    {
        get => _windowsPath;
        set
        {
            var normalized = value ?? "";
            if (_windowsPath.Equals(normalized, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
            _windowsPath = normalized;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DisplayPath));
        }
    }
    public string Type { get; set; } = "";
    public long Size { get; set; }
    public string Modified
    {
        get => _modified;
        set
        {
            var normalized = value ?? "";
            if (_modified == normalized)
            {
                return;
            }
            _modified = normalized;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ModifiedDate));
            OnPropertyChanged(nameof(ModifiedGroup));
        }
    }
    public bool IsFolder { get; set; }
    public bool FolderMetadataChecked { get; set; }
    public ExplorerResultGroup? ExplorerGroup { get; set; }
    public bool ShowInternalPath
    {
        get => _showInternalPath;
        set
        {
            if (_showInternalPath == value)
            {
                return;
            }
            _showInternalPath = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DisplayPath));
        }
    }
    public bool IsFavorite
    {
        get => _isFavorite;
        set
        {
            if (_isFavorite == value)
            {
                return;
            }
            _isFavorite = value;
            OnPropertyChanged();
        }
    }
    public List<string> Groups
    {
        get => _groups;
        set
        {
            var normalized = value
                .Where(group => !string.IsNullOrWhiteSpace(group))
                .Select(group => group.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (_groups.SequenceEqual(normalized, StringComparer.OrdinalIgnoreCase))
            {
                return;
            }
            _groups = normalized;
            OnPropertyChanged();
        }
    }
    public JsonElement Raw { get; set; }

    public string FileName
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Name))
            {
                var fromPath = System.IO.Path.GetFileName(Path.TrimEnd('\\', '/'));
                return !string.IsNullOrWhiteSpace(Extension) && fromPath.EndsWith("." + Extension, StringComparison.OrdinalIgnoreCase)
                    ? fromPath
                    : "";
            }
            if (string.IsNullOrWhiteSpace(Extension) || Name.EndsWith("." + Extension, StringComparison.OrdinalIgnoreCase))
            {
                return Name;
            }
            return $"{Name}.{Extension}";
        }
    }

    public bool HasUsableFileName => !string.IsNullOrWhiteSpace(FileName) &&
                                     !FileName.Trim().Equals("." + Extension.Trim(), StringComparison.OrdinalIgnoreCase);

    public string DisplayPath
    {
        get
        {
            return ShowInternalPath ? Path.Replace('/', '\\').Trim() : WindowsPath;
        }
    }

    public string Kind => IsFolder ? "Folder" : string.IsNullOrWhiteSpace(Extension) ? "File" : Extension.ToUpperInvariant() + " File";
    public string SizeText => IsFolder || Size <= 0 ? "" : Size >= 1048576 ? $"{Size / 1048576.0:F1} MB" : $"{Math.Max(1, Size / 1024)} KB";
    public DateTime? ModifiedDate => TryParseModified(Modified);
    public string ModifiedGroup => DateGroup(ModifiedDate);
    public string IconText => IsFolder ? "Folder" : "File";
    public string IconGlyph => IsFolder ? "\uE8B7" : "\uE7C3";
    public bool HasThumbnailAction
    {
        get
        {
            return Raw.ValueKind == JsonValueKind.Object &&
                   Raw.TryGetProperty("actions", out var actions) &&
                   actions.TryGetProperty("thumbnail", out var thumbnail) &&
                   thumbnail.ValueKind != JsonValueKind.Null &&
                   !string.IsNullOrWhiteSpace(thumbnail.ToString());
        }
    }

    // UI hosts supply their native image object; the search model stays framework-free.
    public object? IconSource
    {
        get => _iconSource;
        set
        {
            if (Equals(_iconSource, value))
            {
                return;
            }
            _iconSource = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private static DateTime? TryParseModified(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (DateTime.TryParse(value, CultureInfo.CurrentCulture, DateTimeStyles.AssumeLocal, out var local))
        {
            return local;
        }
        if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var invariant))
        {
            return invariant;
        }
        return null;
    }

    private static string DateGroup(DateTime? value)
    {
        if (value == null)
        {
            return "";
        }

        var date = value.Value.Date;
        var today = DateTime.Today;
        var thisWeek = StartOfWeek(today);
        var lastWeek = thisWeek.AddDays(-7);
        var thisMonth = new DateTime(today.Year, today.Month, 1);
        var lastMonth = thisMonth.AddMonths(-1);

        if (date == today)
        {
            return "Today";
        }
        if (date == today.AddDays(-1))
        {
            return "Yesterday";
        }
        if (date >= thisWeek)
        {
            return "Earlier this week";
        }
        if (date >= lastWeek && date < thisWeek)
        {
            return "Last week";
        }
        if (date >= thisMonth)
        {
            return "Earlier this month";
        }
        if (date >= lastMonth && date < thisMonth)
        {
            return "Last month";
        }
        return "Older";
    }

    private static DateTime StartOfWeek(DateTime date)
    {
        var offset = ((int)date.DayOfWeek - (int)CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek + 7) % 7;
        return date.Date.AddDays(-offset);
    }
}

public sealed class FileTypeFilter
{
    public string Name { get; init; } = "";
    public string Category { get; init; } = "All";
    public string[] Extensions { get; init; } = [];
    public bool IncludeFolders { get; init; }
    public bool IncludeAllFiles { get; init; }

    public override string ToString() => Name;
}

public sealed class FileTypeFilterOption : INotifyPropertyChanged
{
    public required FileTypeFilter Filter { get; init; }
    private bool _isSelected;

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value)
            {
                return;
            }
            _isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }

    public string Name => Filter.Name;
    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed class ResultViewMode
{
    public string Name { get; init; } = "";
    public string Key { get; init; } = "details";

    public override string ToString() => Name;
}

public sealed class ResultSortMode
{
    public string Name { get; init; } = "";
    public string Key { get; init; } = "recent";

    public override string ToString() => Name;
}

public sealed class SearchScope
{
    public string Name { get; init; } = "";
    public string Key { get; init; } = "all";

    public override string ToString() => Name;
}

public sealed class DateRangePreset
{
    public string Name { get; init; } = "Any date";
    public DateTime? From { get; init; }
    public DateTime? To { get; init; }

    public override string ToString() => Name;
}

public sealed class FavoriteGroupNode
{
    public string Name { get; init; } = "";
    public string Path { get; init; } = "";
    public bool IsAllFavorites { get; init; }
    public bool IsSelected { get; set; }
    public ObservableCollection<FavoriteGroupNode> Children { get; } = [];
    public string Glyph => IsAllFavorites ? "\uE73A" : "\uE8B7";
}

public sealed class FavoriteTreeNode
{
    public string Name { get; init; } = "";
    public string FolderPath { get; init; } = "";
    public SearchResult? Result { get; init; }
    public SavedSearch? SavedSearch { get; init; }
    public bool IsSelected { get; set; }
    public bool IsExpanded { get; set; }
    public ObservableCollection<FavoriteTreeNode> Children { get; } = [];
    public bool IsFolder => Result == null && SavedSearch == null;
    public string Glyph => IsFolder ? "\uE8B7" : SavedSearch != null ? "\uE8A2" : Result!.IconGlyph;
    public string ToolTip => IsFolder ? Name : SavedSearch != null ? SavedSearch.Query : $"{Result!.FileName}\n{Result.DisplayPath}";
}

public sealed record SavedSearch(long Id, string Name, string Query);

public sealed record ExplorerResultGroup(string Key, string Name, string Location)
{
    public override string ToString() => Name;
}
