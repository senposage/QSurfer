using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using QSurfer.Core.Models;
using QSurfer.Core.Services;
using QSurfer.Avalonia.Services;

namespace QSurfer.Avalonia;

public sealed partial class SettingsWindow : Window
{
    private readonly AppConfig _config;
    private readonly Func<string, HotkeyRegistrationResult>? _configureHotkey;
    private readonly List<ScopedTextRule> _initialGlobalFolderRules;
    private readonly List<ScopedTextRule> _initialGlobalFileRules;
    private readonly List<VisibilityRule> _initialGlobalVisibilityRules;
    private readonly Dictionary<TextBox, DateTimeOffset> _escapeHeldSince = new();
    private static readonly TimeSpan EscapeClearDelay = TimeSpan.FromMilliseconds(650);

    public SettingsWindow() : this(new AppConfig(), null)
    {
    }

    public SettingsWindow(AppConfig config, Func<string, HotkeyRegistrationResult>? configureHotkey)
    {
        _config = config;
        _configureHotkey = configureHotkey;
        InitializeComponent();

        CanEditGlobalRules = GlobalRuleAuthorization.CanManageGlobalRules();
        _initialGlobalFolderRules = config.Exclude.FolderRules.Where(rule => rule.IsGlobal).Select(CloneRule).ToList();
        _initialGlobalFileRules = config.Exclude.FileRules.Where(rule => rule.IsGlobal).Select(CloneRule).ToList();
        _initialGlobalVisibilityRules = config.VisibilityRules.Where(rule => rule.IsGlobal).Select(CloneVisibilityRule).ToList();

        var mappings = config.PathMappings.Select(CloneMapping).ToList();
        foreach (var detected in PathMapper.DiscoverWindowsPathMappings())
        {
            if (mappings.Any(mapping => string.Equals(
                    NormalizeMappedRoot(mapping.MappedRoot),
                    NormalizeMappedRoot(detected.MappedRoot),
                    StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            mappings.Add(detected);
        }

        Mappings = new ObservableCollection<PathMapping>(mappings);
        FolderRules = new ObservableCollection<ScopedTextRule>(config.Exclude.FolderRules.Select(CloneRule));
        FileRules = new ObservableCollection<ScopedTextRule>(config.Exclude.FileRules.Select(CloneRule));
        VisibilityRules = new ObservableCollection<VisibilityRule>(config.VisibilityRules.Select(rule => new VisibilityRule
        {
            Access = rule.Access,
            Identity = rule.IsGlobal ? "" : rule.Identity,
            Pattern = rule.Pattern,
            IsGlobal = rule.IsGlobal,
        }));
        MappingsGrid.ItemsSource = Mappings;
        FolderRulesGrid.ItemsSource = FolderRules;
        FileRulesGrid.ItemsSource = FileRules;
        VisibilityRulesGrid.ItemsSource = VisibilityRules;

        HostBox.Text = config.Host;
        PortBox.Text = config.Port.ToString();
        UsernameBox.Text = config.User;
        PasswordBox.Text = config.Password;
        SslBox.IsChecked = config.Ssl;
        VerifyCertificateBox.IsChecked = config.SslVerify;
        ShowInTaskbarBox.IsChecked = config.Behavior.ShowInTaskbar;
        MinimizeToTrayBox.IsChecked = config.Behavior.MinimizeToTray;
        ExitToTrayBox.IsChecked = config.Behavior.ExitToTray;
        ClearResultsWithQueryBox.IsChecked = config.Behavior.ClearResultsWithQuery;
        AlwaysOnTopBox.IsChecked = config.AlwaysOnTop;
        FoldersFirstBox.IsChecked = config.Behavior.FoldersFirst;
        DefaultSearchContentsBox.IsChecked = config.Behavior.SearchContents;
        HighlightMatchesBox.IsChecked = config.Behavior.HighlightMatches;
        PreviewPaneBox.IsChecked = config.Behavior.PreviewPane;
        ShowInternalPathsBox.IsChecked = config.Behavior.ShowQsirchInternalPaths;
        UseThumbnailsBox.IsChecked = config.Behavior.UseQsirchThumbnails;
        AllowDownloadBox.IsChecked = config.Behavior.AllowDownload;
        ResultLimitBox.Text = Math.Clamp(config.Behavior.MaxSearchResults, 50, 5000).ToString();
        SearchTimeoutBox.Text = Math.Clamp(config.Behavior.SearchTimeoutSeconds, 10, 300).ToString();
        FirstPageSizeBox.Text = Math.Clamp(config.Behavior.FirstPageSize, 5, 500).ToString();
        NextPageSizeBox.Text = Math.Clamp(config.Behavior.NextPageSize, 10, 500).ToString();
        HotkeyBox.Text = config.Behavior.GlobalHotkey;
        FocusSearchShortcutBox.Text = config.Behavior.KeyboardShortcuts.FocusSearch;
        RefreshShortcutBox.Text = config.Behavior.KeyboardShortcuts.Refresh;
        BackShortcutBox.Text = config.Behavior.KeyboardShortcuts.Back;
        ForwardShortcutBox.Text = config.Behavior.KeyboardShortcuts.Forward;
        UpShortcutBox.Text = config.Behavior.KeyboardShortcuts.Up;
        OpenShortcutBox.Text = config.Behavior.KeyboardShortcuts.Open;
        CopyPathShortcutBox.Text = config.Behavior.KeyboardShortcuts.CopyPath;
        CutShortcutBox.Text = config.Behavior.KeyboardShortcuts.Cut;
        PasteShortcutBox.Text = config.Behavior.KeyboardShortcuts.Paste;
        RenameShortcutBox.Text = config.Behavior.KeyboardShortcuts.Rename;
        DeleteShortcutBox.Text = config.Behavior.KeyboardShortcuts.Delete;
        NewFolderShortcutBox.Text = config.Behavior.KeyboardShortcuts.NewFolder;
        FavoriteShortcutBox.Text = config.Behavior.KeyboardShortcuts.Favorite;
        SelectTaggedItem(ResultViewBox, config.Behavior.ResultView, "details");
        SelectTaggedItem(ResultSortBox, config.Behavior.ResultSort, "folder");
        HistoryEnabledBox.IsChecked = config.History.Enabled;
        SelectTheme(string.IsNullOrWhiteSpace(config.Behavior.Theme) ? "system" : config.Behavior.Theme);
    }

    public bool Saved { get; private set; }
    public bool ClearHistoryRequested { get; private set; }
    public bool ClearStarredRequested { get; private set; }
    public bool ResetDatabaseRequested { get; private set; }
    public bool CanEditGlobalRules { get; }
    public bool GlobalRulesAreLocked => !CanEditGlobalRules;
    public ObservableCollection<PathMapping> Mappings { get; }
    public ObservableCollection<ScopedTextRule> FolderRules { get; }
    public ObservableCollection<ScopedTextRule> FileRules { get; }
    public ObservableCollection<VisibilityRule> VisibilityRules { get; }

    private void Save_Click(object? sender, RoutedEventArgs e)
    {
        var host = HostBox.Text?.Trim() ?? "";
        var user = UsernameBox.Text?.Trim() ?? "";
        var password = PasswordBox.Text ?? "";
        if ((!string.IsNullOrWhiteSpace(host) || !string.IsNullOrWhiteSpace(user) || !string.IsNullOrWhiteSpace(password)) &&
            (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(user) || string.IsNullOrWhiteSpace(password)))
        {
            ShowValidation("Enter the NAS host, username, and password together.");
            return;
        }
        if (!int.TryParse(PortBox.Text, out var port) || port is < 1 or > 65535)
        {
            ShowValidation("Enter a port number between 1 and 65535.");
            return;
        }
        if (!int.TryParse(ResultLimitBox.Text, out var limit) || limit is < 50 or > 5000)
        {
            ShowValidation("Search result limit must be from 50 to 5000.");
            return;
        }
        if (!int.TryParse(SearchTimeoutBox.Text, out var timeout) || timeout is < 10 or > 300)
        {
            ShowValidation("Search timeout must be from 10 to 300 seconds.");
            return;
        }
        if (!int.TryParse(FirstPageSizeBox.Text, out var firstPageSize) || firstPageSize is < 5 or > 500)
        {
            ShowValidation("Initial results must be from 5 to 500.");
            return;
        }
        if (!int.TryParse(NextPageSizeBox.Text, out var nextPageSize) || nextPageSize is < 10 or > 500)
        {
            ShowValidation("Additional results must be from 10 to 500.");
            return;
        }
        if (!WindowsHotkeyService.TryNormalize(HotkeyBox.Text, out var hotkey, out var hotkeyError))
        {
            ShowValidation(hotkeyError);
            return;
        }
        if (!TryReadKeyboardShortcuts(out var shortcuts, out var shortcutError))
        {
            ShowValidation(shortcutError);
            return;
        }
        if (_configureHotkey != null)
        {
            var registration = _configureHotkey(hotkey);
            if (!registration.Registered)
            {
                HotkeyStatus.Text = registration.Error;
                ShowValidation(registration.Error);
                return;
            }
            hotkey = registration.NormalizedShortcut;
            HotkeyStatus.Text = $"Registered {hotkey}.";
        }

        _config.Host = host;
        _config.Port = SslBox.IsChecked == true && port == 8080 ? 443 : port;
        _config.User = user;
        _config.Password = password;
        _config.Ssl = SslBox.IsChecked == true;
        _config.SslVerify = VerifyCertificateBox.IsChecked == true;
        _config.AlwaysOnTop = AlwaysOnTopBox.IsChecked == true;
        _config.Behavior.ShowInTaskbar = ShowInTaskbarBox.IsChecked == true;
        _config.Behavior.MinimizeToTray = MinimizeToTrayBox.IsChecked == true;
        _config.Behavior.ExitToTray = ExitToTrayBox.IsChecked == true;
        _config.Behavior.ClearResultsWithQuery = ClearResultsWithQueryBox.IsChecked == true;
        _config.Behavior.FoldersFirst = FoldersFirstBox.IsChecked == true;
        _config.Behavior.SearchContents = DefaultSearchContentsBox.IsChecked == true;
        _config.Behavior.HighlightMatches = HighlightMatchesBox.IsChecked == true;
        _config.Behavior.PreviewPane = PreviewPaneBox.IsChecked == true;
        _config.Behavior.ShowQsirchInternalPaths = ShowInternalPathsBox.IsChecked == true;
        _config.Behavior.UseQsirchThumbnails = UseThumbnailsBox.IsChecked == true;
        _config.Behavior.AllowDownload = AllowDownloadBox.IsChecked == true;
        _config.Behavior.MaxSearchResults = limit;
        _config.Behavior.SearchTimeoutSeconds = timeout;
        _config.Behavior.FirstPageSize = firstPageSize;
        _config.Behavior.NextPageSize = nextPageSize;
        _config.Behavior.Theme = SelectedTheme();
        _config.Behavior.ResultView = SelectedTag(ResultViewBox, "details");
        _config.Behavior.ResultSort = SelectedTag(ResultSortBox, "folder");
        _config.Behavior.GlobalHotkey = hotkey;
        _config.Behavior.KeyboardShortcuts = shortcuts;
        _config.History.Enabled = HistoryEnabledBox.IsChecked == true;
        _config.PathMappings = Mappings
            .Where(mapping => !string.IsNullOrWhiteSpace(mapping.ShareRoot) && !string.IsNullOrWhiteSpace(mapping.MappedRoot))
            .Select(CloneMapping)
            .ToList();
        var folderRules = NormalizeRules(FolderRules);
        var fileRules = NormalizeRules(FileRules);
        var visibilityRules = VisibilityRules
            .Where(rule => !string.IsNullOrWhiteSpace(rule.Pattern))
            .Select(rule => new VisibilityRule
            {
                Access = rule.Access.Equals("allow", StringComparison.OrdinalIgnoreCase) ? "allow" : "deny",
                Identity = rule.IsGlobal || string.IsNullOrWhiteSpace(rule.Identity) ? "*" : rule.Identity.Trim(),
                Pattern = rule.Pattern.Trim(),
                IsGlobal = rule.IsGlobal,
            })
            .ToList();
        _config.Exclude.FolderRules = PreserveGlobalRules(folderRules, _initialGlobalFolderRules);
        _config.Exclude.FileRules = PreserveGlobalRules(fileRules, _initialGlobalFileRules);
        _config.VisibilityRules = PreserveGlobalRules(visibilityRules, _initialGlobalVisibilityRules);
        ClearHistoryRequested = ClearHistoryBox.IsChecked == true;
        ClearStarredRequested = ClearStarredBox.IsChecked == true;
        ConfigStore.Save(_config);
        Saved = true;
        Close();
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close();

    private bool TryReadKeyboardShortcuts(out KeyboardShortcutConfig shortcuts, out string error)
    {
        var values = new KeyboardShortcutConfig();
        error = "";
        var fields = new (string Name, TextBox Box, Action<string> Set)[]
        {
            ("Focus search", FocusSearchShortcutBox, value => values.FocusSearch = value),
            ("Refresh", RefreshShortcutBox, value => values.Refresh = value),
            ("Back", BackShortcutBox, value => values.Back = value),
            ("Forward", ForwardShortcutBox, value => values.Forward = value),
            ("Up one folder", UpShortcutBox, value => values.Up = value),
            ("Open", OpenShortcutBox, value => values.Open = value),
            ("Copy selected item", CopyPathShortcutBox, value => values.CopyPath = value),
            ("Cut selected item", CutShortcutBox, value => values.Cut = value),
            ("Paste", PasteShortcutBox, value => values.Paste = value),
            ("Rename", RenameShortcutBox, value => values.Rename = value),
            ("Delete", DeleteShortcutBox, value => values.Delete = value),
            ("New folder", NewFolderShortcutBox, value => values.NewFolder = value),
            ("Add or remove Favorite", FavoriteShortcutBox, value => values.Favorite = value),
        };

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, box, set) in fields)
        {
            if (string.IsNullOrWhiteSpace(box.Text))
            {
                set("");
                continue;
            }
            if (!KeyboardShortcut.TryParse(box.Text, out var shortcut, out var parseError))
            {
                error = $"{name}: {parseError}";
                shortcuts = values;
                return false;
            }
            if (!seen.Add(shortcut.DisplayText))
            {
                error = $"{name}: that shortcut is already assigned.";
                shortcuts = values;
                return false;
            }
            box.Text = shortcut.DisplayText;
            set(shortcut.DisplayText);
        }
        shortcuts = values;
        return true;
    }

    private void ShortcutBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not TextBox box)
        {
            return;
        }

        if (e.Key == Key.Escape)
        {
            _escapeHeldSince[box] = DateTimeOffset.UtcNow;
            e.Handled = true;
            return;
        }

        if (!KeyboardShortcut.TryCapture(e, out var shortcut))
        {
            e.Handled = true;
            return;
        }

        if (ReferenceEquals(box, HotkeyBox) && shortcut.Modifiers == KeyModifiers.None)
        {
            HotkeyStatus.Text = "The global shortcut needs Ctrl, Alt, or Shift.";
            e.Handled = true;
            return;
        }

        box.Text = shortcut.DisplayText;
        if (ReferenceEquals(box, HotkeyBox))
        {
            HotkeyStatus.Text = "Save to register this global shortcut.";
        }
        e.Handled = true;
    }

    private void ShortcutBoxKeyUp(object? sender, KeyEventArgs e)
    {
        if (sender is not TextBox box || e.Key != Key.Escape || !_escapeHeldSince.Remove(box, out var started))
        {
            return;
        }

        if (DateTimeOffset.UtcNow - started >= EscapeClearDelay)
        {
            box.Text = "";
            if (ReferenceEquals(box, HotkeyBox))
            {
                HotkeyStatus.Text = "Save to disable the global shortcut.";
            }
        }
        e.Handled = true;
    }

    private void ResetDatabase_Click(object? sender, RoutedEventArgs e)
    {
        ResetDatabaseRequested = true;
        ShowValidation("Saved data will be reset when you save these settings.");
    }

    private void AddMapping_Click(object? sender, RoutedEventArgs e)
    {
        var mapping = new PathMapping();
        Mappings.Add(mapping);
        MappingsGrid.SelectedItem = mapping;
    }

    private void RemoveMapping_Click(object? sender, RoutedEventArgs e) => RemoveSelected(MappingsGrid, Mappings);

    private void AddFolderRule_Click(object? sender, RoutedEventArgs e) => AddRule(FolderRulesGrid, FolderRules);
    private void RemoveFolderRule_Click(object? sender, RoutedEventArgs e) => RemoveSelected(FolderRulesGrid, FolderRules);
    private void AddFileRule_Click(object? sender, RoutedEventArgs e) => AddRule(FileRulesGrid, FileRules);
    private void RemoveFileRule_Click(object? sender, RoutedEventArgs e) => RemoveSelected(FileRulesGrid, FileRules);
    private void AddDenyRule_Click(object? sender, RoutedEventArgs e) => AddVisibility("deny");
    private void AddAllowRule_Click(object? sender, RoutedEventArgs e) => AddVisibility("allow");
    private void RemoveVisibilityRule_Click(object? sender, RoutedEventArgs e) => RemoveSelected(VisibilityRulesGrid, VisibilityRules);

    private void AddRule(DataGrid grid, ObservableCollection<ScopedTextRule> rules)
    {
        var rule = new ScopedTextRule { Pattern = "*", IsGlobal = false };
        rules.Add(rule);
        grid.SelectedItem = rule;
    }

    private void AddVisibility(string access)
    {
        var identities = new List<string>();
        if (VisibilityThisMachineBox.IsChecked == true)
        {
            identities.Add(Environment.MachineName);
        }
        if (VisibilityThisUserBox.IsChecked == true)
        {
            identities.Add($@"{Environment.UserDomainName}\{Environment.UserName}");
        }
        if (identities.Count == 0)
        {
            identities.Add($@"{Environment.UserDomainName}\{Environment.UserName}");
        }

        VisibilityRule? selected = null;
        foreach (var identity in identities.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var rule = new VisibilityRule
            {
                Access = access,
                Identity = identity,
                Pattern = "*",
                IsGlobal = false,
            };
            VisibilityRules.Add(rule);
            selected ??= rule;
        }
        VisibilityRulesGrid.SelectedItem = selected;
    }

    private void VisibilityGlobalRule_Checked(object? sender, RoutedEventArgs e)
    {
        if (sender is CheckBox { DataContext: VisibilityRule rule })
        {
            rule.Identity = "";
        }
    }

    private static List<ScopedTextRule> NormalizeRules(IEnumerable<ScopedTextRule> rules) => rules
        .Where(rule => !string.IsNullOrWhiteSpace(rule.Pattern))
        .Select(CloneRule)
        .GroupBy(rule => rule.Pattern, StringComparer.OrdinalIgnoreCase)
        .Select(group => group.First())
        .ToList();

    private List<T> PreserveGlobalRules<T>(IEnumerable<T> currentRules, IEnumerable<T> originalGlobalRules) where T : class
    {
        if (CanEditGlobalRules)
        {
            return currentRules.ToList();
        }

        return originalGlobalRules.Concat(currentRules.Where(rule => !IsGlobal(rule))).ToList();
    }

    private static bool IsGlobal<T>(T rule) where T : class => rule switch
    {
        ScopedTextRule scoped => scoped.IsGlobal,
        VisibilityRule visibility => visibility.IsGlobal,
        _ => false,
    };

    private static void RemoveSelected<T>(DataGrid grid, ObservableCollection<T> items)
    {
        if (grid.SelectedItem is T selected)
        {
            items.Remove(selected);
        }
    }

    private void SelectTheme(string theme)
    {
        foreach (var item in ThemeBox.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Tag as string, theme, StringComparison.OrdinalIgnoreCase))
            {
                ThemeBox.SelectedItem = item;
                return;
            }
        }
    }

    private static void SelectTaggedItem(ComboBox box, string value, string fallback)
    {
        box.SelectedItem = box.Items.OfType<ComboBoxItem>().FirstOrDefault(item =>
            string.Equals(item.Tag as string, value, StringComparison.OrdinalIgnoreCase))
            ?? box.Items.OfType<ComboBoxItem>().FirstOrDefault(item => string.Equals(item.Tag as string, fallback, StringComparison.OrdinalIgnoreCase));
    }

    private string SelectedTheme() => (ThemeBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "system";
    private static string SelectedTag(ComboBox box, string fallback) => (box.SelectedItem as ComboBoxItem)?.Tag as string ?? fallback;
    private static string NormalizeMappedRoot(string path) => (path ?? "").Trim().TrimEnd('\\', '/');
    private static PathMapping CloneMapping(PathMapping mapping) => new() { ShareRoot = mapping.ShareRoot, MappedRoot = mapping.MappedRoot };
    private static ScopedTextRule CloneRule(ScopedTextRule rule) => new() { Pattern = rule.Pattern, IsGlobal = rule.IsGlobal };
    private static VisibilityRule CloneVisibilityRule(VisibilityRule rule) => new() { Access = rule.Access, Identity = rule.Identity, Pattern = rule.Pattern, IsGlobal = rule.IsGlobal };

    private void ShowValidation(string message)
    {
        ValidationMessage.Text = message;
        ValidationMessage.IsVisible = true;
    }
}
