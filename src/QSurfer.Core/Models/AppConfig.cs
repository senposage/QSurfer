using System.Text.Json.Serialization;

namespace QSurfer.Core.Models;

public sealed class AppConfig
{
    private ConnectionDefaults? _rootConnectionDefaults;

    [JsonPropertyName("host")]
    public string Host { get; set; } = "";

    [JsonPropertyName("search_provider")]
    public string SearchProvider { get; set; } = "qsirch";

    [JsonPropertyName("qsirch_enabled")]
    public bool QsirchEnabled { get; set; } = true;

    [JsonPropertyName("search_service")]
    public SearchServiceConnection SearchService { get; set; } = new();

    [JsonPropertyName("port")]
    public int Port { get; set; } = 443;

    [JsonPropertyName("ssl")]
    public bool Ssl { get; set; } = true;

    [JsonPropertyName("ssl_verify")]
    public bool SslVerify { get; set; }

    [JsonPropertyName("user")]
    public string User { get; set; } = "";

    [JsonPropertyName("password")]
    public string Password { get; set; } = "";

    [JsonPropertyName("password_protected")]
    public string ProtectedPassword { get; set; } = "";

    [JsonPropertyName("path_mappings")]
    public List<PathMapping> PathMappings { get; set; } = [];

    [JsonPropertyName("behavior")]
    public BehaviorConfig Behavior { get; set; } = new();

    [JsonPropertyName("history")]
    public HistoryConfig History { get; set; } = new();

    [JsonPropertyName("exclude")]
    public ExcludeConfig Exclude { get; set; } = new();

    [JsonPropertyName("visibility_rules")]
    public List<VisibilityRule> VisibilityRules { get; set; } = [];

    [JsonPropertyName("always_on_top")]
    public bool AlwaysOnTop { get; set; }

    [JsonPropertyName("pinned_tabs")]
    public List<PinnedTabConfig> PinnedTabs { get; set; } = [];

    [JsonPropertyName("hosts")]
    public Dictionary<string, HostConfig> Hosts { get; set; } = [];

    public static string CurrentHostKey => Environment.MachineName.ToUpperInvariant();

    public void ApplyCurrentHost()
    {
        CaptureRootConnectionDefaults();
        MigrateLegacySettings();
        foreach (var rule in VisibilityRules)
        {
            rule.IsGlobal = true;
        }

        if (!Hosts.TryGetValue(CurrentHostKey, out var host))
        {
            return;
        }

        var globalMappings = PathMappings.Where(mapping => mapping.IsGlobal).Select(ClonePathMapping).ToList();
        var globalExclude = CloneExclude(Exclude);
        var globalVisibility = VisibilityRules.Select(CloneVisibilityRule).ToList();

        SearchProvider = Services.SearchProviders.Normalize(host.SearchProvider);
        QsirchEnabled = host.QsirchEnabled;
        Host = host.Host;
        Port = host.Port;
        Ssl = host.Ssl;
        SslVerify = host.SslVerify;
        User = host.User;
        Password = host.Password;
        SearchService = CloneSearchService(host.SearchService);
        // Local mappings deliberately precede shared defaults so a workstation
        // can explicitly override a deployment mapping for the same share.
        PathMappings = host.PathMappings.Where(mapping => !mapping.IsGlobal).Select(ClonePathMapping)
            .Concat(globalMappings)
            .ToList();
        Behavior = CloneBehavior(host.Behavior);
        History = CloneHistory(host.History);
        AlwaysOnTop = host.AlwaysOnTop;
        PinnedTabs = host.PinnedTabs.Select(ClonePinnedTab).ToList();

        NormalizeRules(host.Exclude, global: false);
        Exclude = new ExcludeConfig
        {
            FolderRules = globalExclude.FolderRules.Where(x => x.IsGlobal).Select(CloneTextRule).Concat(host.Exclude.FolderRules.Where(x => !x.IsGlobal).Select(CloneTextRule)).ToList(),
            FileRules = globalExclude.FileRules.Where(x => x.IsGlobal).Select(CloneTextRule).Concat(host.Exclude.FileRules.Where(x => !x.IsGlobal).Select(CloneTextRule)).ToList(),
        };

        VisibilityRules = globalVisibility.Where(x => x.IsGlobal).Select(CloneVisibilityRule)
            .Concat(host.VisibilityRules.Where(x => !x.IsGlobal).Select(CloneVisibilityRule))
            .ToList();
    }

    public void CaptureCurrentHost()
    {
        CaptureRootConnectionDefaults();
        NormalizeRules(Exclude, global: false);
        var globalMappings = PathMappings.Where(mapping => mapping.IsGlobal).Select(ClonePathMapping).ToList();
        var globalExclude = new ExcludeConfig
        {
            FolderRules = Exclude.FolderRules.Where(x => x.IsGlobal).Select(CloneTextRule).ToList(),
            FileRules = Exclude.FileRules.Where(x => x.IsGlobal).Select(CloneTextRule).ToList(),
        };

        var localExclude = new ExcludeConfig
        {
            FolderRules = Exclude.FolderRules.Where(x => !x.IsGlobal).Select(CloneTextRule).ToList(),
            FileRules = Exclude.FileRules.Where(x => !x.IsGlobal).Select(CloneTextRule).ToList(),
        };

        Hosts[CurrentHostKey] = new HostConfig
        {
            SearchProvider = Services.SearchProviders.Normalize(SearchProvider),
            QsirchEnabled = QsirchEnabled,
            Host = Host,
            Port = Port,
            Ssl = Ssl,
            SslVerify = SslVerify,
            User = User,
            Password = Password,
            SearchService = CloneSearchService(SearchService),
            PathMappings = PathMappings.Where(mapping => !mapping.IsGlobal).Select(ClonePathMapping).ToList(),
            Behavior = CloneBehavior(Behavior),
            History = CloneHistory(History),
            Exclude = localExclude,
            VisibilityRules = VisibilityRules.Where(x => !x.IsGlobal).Select(CloneVisibilityRule).ToList(),
            AlwaysOnTop = AlwaysOnTop,
            PinnedTabs = PinnedTabs.Select(ClonePinnedTab).ToList(),
        };

        RestoreRootConnectionDefaults();
        ClearRootMachineSettings();
        PathMappings = globalMappings;
        Exclude = globalExclude;
        VisibilityRules = VisibilityRules.Where(x => x.IsGlobal).Select(CloneVisibilityRule).ToList();
    }

    public void ClearRootMachineSettings()
    {
        // The root NAS connection is a shared deployment default. Other live settings belong to a host record.
        PathMappings = [];
        Behavior = new BehaviorConfig();
        History = new HistoryConfig();
        AlwaysOnTop = false;
        PinnedTabs = [];
    }

    private void CaptureRootConnectionDefaults()
    {
        _rootConnectionDefaults ??= new ConnectionDefaults(SearchProvider, QsirchEnabled, Host, Port, Ssl, SslVerify, User, Password, CloneSearchService(SearchService));
    }

    private void RestoreRootConnectionDefaults()
    {
        var defaults = _rootConnectionDefaults ?? new ConnectionDefaults(SearchProvider, QsirchEnabled, Host, Port, Ssl, SslVerify, User, Password, CloneSearchService(SearchService));
        SearchProvider = Services.SearchProviders.Normalize(defaults.SearchProvider);
        QsirchEnabled = defaults.QsirchEnabled;
        Host = defaults.Host;
        Port = defaults.Port;
        Ssl = defaults.Ssl;
        SslVerify = defaults.SslVerify;
        User = defaults.User;
        Password = defaults.Password;
        SearchService = CloneSearchService(defaults.SearchService);
    }

    private static void NormalizeRules(ExcludeConfig exclude, bool global)
    {
        exclude.MigrateLegacyRules(global);
    }

    public void MigrateLegacySettings()
    {
        if (MigrateStandaloneSearchProvider())
        {
            _rootConnectionDefaults = null;
        }
        NormalizeNasIdentity();
        // Version 1.1 raised the practical initial-search default. Preserve any
        // intentional custom limit while moving the old default forward.
        if (Behavior.MaxSearchResults == 500)
        {
            Behavior.MaxSearchResults = 1000;
        }
        UpgradeGlobalHotkey(Behavior);
        UpgradeFavoritesNavigationSplit(Behavior);

        NormalizeRules(Exclude, global: true);
        foreach (var host in Hosts.Values)
        {
            NormalizeRules(host.Exclude, global: false);
            UpgradeGlobalHotkey(host.Behavior);
            UpgradeFavoritesNavigationSplit(host.Behavior);
        }
    }

    private void NormalizeNasIdentity()
    {
        SearchProvider = Services.SearchProviders.Normalize(SearchProvider);
        Host = Services.NasIdentity.NormalizeHost(Host);
        NormalizeMappings(PathMappings, Host);
        foreach (var host in Hosts.Values)
        {
            host.SearchProvider = Services.SearchProviders.Normalize(host.SearchProvider);
            host.Host = Services.NasIdentity.NormalizeHost(host.Host);
            NormalizeMappings(host.PathMappings, host.Host);
            host.SearchService.Normalize();
        }
        SearchService.Normalize();
    }

    private bool MigrateStandaloneSearchProvider()
    {
        // Early service wiring made this an either/or provider selection. Keep
        // that data, but move it into the optional companion-service slot so
        // the NAS/Qsirch connection is never replaced again.
        var legacyRootService = IsLegacyStandaloneService(SearchProvider, User);
        var migrated = legacyRootService;
        if (legacyRootService && string.IsNullOrWhiteSpace(SearchService.ProtectedToken))
        {
            SearchService.ProtectedToken = ProtectedPassword;
        }
        MigrateStandaloneConnection(SearchProvider, Host, Port, Ssl, SslVerify, Password, SearchService, resetRoot: true, legacyRootService);
        if (legacyRootService)
        {
            ProtectedPassword = "";
        }
        foreach (var host in Hosts.Values)
        {
            var legacyStandaloneService = IsLegacyStandaloneService(host.SearchProvider, host.User);
            migrated |= legacyStandaloneService;
            MigrateStandaloneConnection(host.SearchProvider, host.Host, host.Port, host.Ssl, host.SslVerify, host.Password, host.SearchService, resetRoot: false, legacyStandaloneService);
            if (legacyStandaloneService)
            {
                host.SearchProvider = Services.SearchProviders.Qsirch;
                host.Host = "";
                host.Port = 443;
                host.Ssl = true;
                host.SslVerify = false;
                host.User = "";
                host.Password = "";
            }
            else if (Services.SearchProviders.IsStandalone(host.SearchProvider))
            {
                // A workstation can have an old provider flag while still
                // holding a real NAS connection. QIndexer is now additive;
                // never erase those credentials just to normalize the flag.
                host.SearchProvider = Services.SearchProviders.Qsirch;
                migrated = true;
            }
        }
        return migrated;
    }

    private static bool IsLegacyStandaloneService(string provider, string user) =>
        Services.SearchProviders.IsStandalone(provider) && string.IsNullOrWhiteSpace(user);

    private void MigrateStandaloneConnection(string provider, string host, int port, bool ssl, bool sslVerify, string token, SearchServiceConnection service, bool resetRoot, bool isLegacyStandaloneService)
    {
        if (!isLegacyStandaloneService)
        {
            return;
        }

        if (!service.IsConfigured)
        {
            service.Enabled = true;
            service.Host = host;
            service.Port = port;
            service.Ssl = ssl;
            service.SslVerify = sslVerify;
            service.Token = token;
        }

        if (resetRoot)
        {
            SearchProvider = Services.SearchProviders.Qsirch;
            Host = "";
            Port = 443;
            Ssl = true;
            SslVerify = false;
            User = "";
            Password = "";
        }
    }

    private static void NormalizeMappings(IEnumerable<PathMapping> mappings, string configuredHost)
    {
        foreach (var mapping in mappings)
        {
            mapping.ShareRoot = Services.NasIdentity.NormalizeShareRoot(mapping.ShareRoot, configuredHost);
        }
    }

    private static void UpgradeFavoritesNavigationSplit(BehaviorConfig behavior)
    {
        // The former 60/40 default left too little room for Explorer-style
        // navigation. Preserve custom splits while moving the old default.
        if (Math.Abs(behavior.FavoritesNavigationSplit - 0.6) < 0.001)
        {
            behavior.FavoritesNavigationSplit = 0.35;
        }
    }

    private static void UpgradeGlobalHotkey(BehaviorConfig behavior)
    {
        // Ctrl+S was the original default, but it steals Save from Office and
        // other applications when registered globally. Preserve every other
        // custom assignment while moving only former shipped defaults aside.
        if (string.Equals(behavior.GlobalHotkey, "Ctrl+S", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(behavior.GlobalHotkey, "Alt+Z", StringComparison.OrdinalIgnoreCase))
        {
            behavior.GlobalHotkey = "Ctrl+Alt+Q";
        }
    }

    private static PathMapping ClonePathMapping(PathMapping mapping) => new() { ShareRoot = mapping.ShareRoot, MappedRoot = mapping.MappedRoot, IsGlobal = mapping.IsGlobal };
    private static SearchServiceConnection CloneSearchService(SearchServiceConnection service) => new()
    {
        Enabled = service.Enabled,
        Host = service.Host,
        Port = service.Port,
        Ssl = service.Ssl,
        SslVerify = service.SslVerify,
        Token = service.Token,
        ProtectedToken = service.ProtectedToken,
    };
    private static ScopedTextRule CloneTextRule(ScopedTextRule rule) => new() { Pattern = rule.Pattern, IsGlobal = rule.IsGlobal };
    private static VisibilityRule CloneVisibilityRule(VisibilityRule rule) => new() { Access = rule.Access, Identity = rule.Identity, Pattern = rule.Pattern, IsGlobal = rule.IsGlobal };
    private static PinnedTabConfig ClonePinnedTab(PinnedTabConfig tab) => new() { Title = tab.Title, Query = tab.Query, ViewKey = tab.ViewKey, SortValue = tab.SortValue, TypeIndex = tab.TypeIndex, TypeNames = tab.TypeNames.ToList(), DateFrom = tab.DateFrom, DateTo = tab.DateTo, ExactMatch = tab.ExactMatch, SearchContents = tab.SearchContents, ScopePaths = tab.ScopePaths.ToList(), ExcludedScopePaths = tab.ExcludedScopePaths.ToList() };
    private static ExcludeConfig CloneExclude(ExcludeConfig exclude) => new()
    {
        FolderRules = exclude.FolderRules.Select(CloneTextRule).ToList(),
        FileRules = exclude.FileRules.Select(CloneTextRule).ToList(),
    };
    private static BehaviorConfig CloneBehavior(BehaviorConfig behavior) => new()
    {
        ShowInTaskbar = behavior.ShowInTaskbar,
        MinimizeToTray = behavior.MinimizeToTray,
        ExitToTray = behavior.ExitToTray,
        ClearResultsWithQuery = behavior.ClearResultsWithQuery,
        ConfirmScopeReplacement = behavior.ConfirmScopeReplacement,
        NavigationScopeIncludeGesture = behavior.NavigationScopeIncludeGesture,
        NavigationScopeExcludeGesture = behavior.NavigationScopeExcludeGesture,
        GlobalHotkey = behavior.GlobalHotkey,
        KeyboardShortcuts = CloneKeyboardShortcuts(behavior.KeyboardShortcuts),
        ThemeColors = CloneThemeColors(behavior.ThemeColors),
        UseWindowsAccentColor = behavior.UseWindowsAccentColor,
        FavoritesPaneWidth = behavior.FavoritesPaneWidth,
        PreviewPaneWidth = behavior.PreviewPaneWidth,
        FavoritesSectionHeight = behavior.FavoritesSectionHeight,
        RecentSearchesSectionHeight = behavior.RecentSearchesSectionHeight,
        FavoritesNavigationSplit = behavior.FavoritesNavigationSplit,
        ShowNavigationPane = behavior.ShowNavigationPane,
        ShowLocalNavigationFolders = behavior.ShowLocalNavigationFolders,
        ShowLocalNavigationDrives = behavior.ShowLocalNavigationDrives,
        NavigationExpandedPaths = behavior.NavigationExpandedPaths.ToList(),
        Theme = behavior.Theme,
        HighlightMatches = behavior.HighlightMatches,
        ShowRecoverySystemFolders = behavior.ShowRecoverySystemFolders,
        ShowQSurferSafetyCopies = behavior.ShowQSurferSafetyCopies,
        ShowHiddenTemporaryFiles = behavior.ShowHiddenTemporaryFiles,
        FlattenRecycleBin = behavior.FlattenRecycleBin,
        UseQsirchThumbnails = behavior.UseQsirchThumbnails,
        SearchContents = behavior.SearchContents,
        PreviewPane = behavior.PreviewPane,
        OriginalRestorePolicy = behavior.OriginalRestorePolicy,
        OneTimeFolderRestore = behavior.OneTimeFolderRestore,
        ResultView = behavior.ResultView,
        ResultSort = behavior.ResultSort,
        VisibleDetailColumns = behavior.VisibleDetailColumns.ToList(),
        SearchTimeoutSeconds = behavior.SearchTimeoutSeconds,
        FirstPageSize = behavior.FirstPageSize,
        NextPageSize = behavior.NextPageSize,
        MaxSearchResults = behavior.MaxSearchResults,
        MaxBulkOpenTabs = behavior.MaxBulkOpenTabs,
        LastSeenWhatsNewVersion = behavior.LastSeenWhatsNewVersion,
        LastSeenFirstRunGuideVersion = behavior.LastSeenFirstRunGuideVersion,
    };
    private static HistoryConfig CloneHistory(HistoryConfig history) => new()
    {
        Enabled = history.Enabled,
    };
    private static KeyboardShortcutConfig CloneKeyboardShortcuts(KeyboardShortcutConfig shortcuts) => new()
    {
        FocusSearch = shortcuts.FocusSearch,
        Refresh = shortcuts.Refresh,
        Back = shortcuts.Back,
        Forward = shortcuts.Forward,
        Up = shortcuts.Up,
        Open = shortcuts.Open,
        CopyPath = shortcuts.CopyPath,
        Cut = shortcuts.Cut,
        Paste = shortcuts.Paste,
        Rename = shortcuts.Rename,
        Delete = shortcuts.Delete,
        NewFolder = shortcuts.NewFolder,
        Favorite = shortcuts.Favorite,
    };
    private static ThemeColorConfig CloneThemeColors(ThemeColorConfig? colors)
    {
        colors ??= new ThemeColorConfig();
        return new ThemeColorConfig
        {
            LightSurface = colors.LightSurface,
            LightAccent = colors.LightAccent,
            LightSelection = colors.LightSelection,
            LightHover = colors.LightHover,
            LightMatch = colors.LightMatch,
            DarkSurface = colors.DarkSurface,
            DarkAccent = colors.DarkAccent,
            DarkSelection = colors.DarkSelection,
            DarkHover = colors.DarkHover,
            DarkMatch = colors.DarkMatch,
        };
    }


    private sealed record ConnectionDefaults(string SearchProvider, bool QsirchEnabled, string Host, int Port, bool Ssl, bool SslVerify, string User, string Password, SearchServiceConnection SearchService);
}

public sealed class HostConfig
{
    [JsonPropertyName("search_provider")]
    public string SearchProvider { get; set; } = "qsirch";

    [JsonPropertyName("qsirch_enabled")]
    public bool QsirchEnabled { get; set; } = true;

    [JsonPropertyName("host")]
    public string Host { get; set; } = "";

    [JsonPropertyName("port")]
    public int Port { get; set; } = 443;

    [JsonPropertyName("ssl")]
    public bool Ssl { get; set; } = true;

    [JsonPropertyName("ssl_verify")]
    public bool SslVerify { get; set; }

    [JsonPropertyName("user")]
    public string User { get; set; } = "";

    [JsonPropertyName("password")]
    public string Password { get; set; } = "";

    [JsonPropertyName("search_service")]
    public SearchServiceConnection SearchService { get; set; } = new();

    [JsonPropertyName("path_mappings")]
    public List<PathMapping> PathMappings { get; set; } = [];

    [JsonPropertyName("behavior")]
    public BehaviorConfig Behavior { get; set; } = new();

    [JsonPropertyName("history")]
    public HistoryConfig History { get; set; } = new();

    [JsonPropertyName("exclude")]
    public ExcludeConfig Exclude { get; set; } = new();

    [JsonPropertyName("visibility_rules")]
    public List<VisibilityRule> VisibilityRules { get; set; } = [];

    [JsonPropertyName("always_on_top")]
    public bool AlwaysOnTop { get; set; }

    [JsonPropertyName("pinned_tabs")]
    public List<PinnedTabConfig> PinnedTabs { get; set; } = [];
}

public sealed class SearchServiceConnection
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    [JsonPropertyName("host")]
    public string Host { get; set; } = "";

    [JsonPropertyName("port")]
    public int Port { get; set; } = 41973;

    [JsonPropertyName("ssl")]
    public bool Ssl { get; set; }

    [JsonPropertyName("ssl_verify")]
    public bool SslVerify { get; set; } = true;

    [JsonPropertyName("token")]
    public string Token { get; set; } = "";

    [JsonPropertyName("token_protected")]
    public string ProtectedToken { get; set; } = "";

    [JsonIgnore]
    public bool HasCredentials => !string.IsNullOrWhiteSpace(Host) && !string.IsNullOrWhiteSpace(Token);

    [JsonIgnore]
    public bool IsConfigured => Enabled && HasCredentials;

    public void Normalize()
    {
        Host = Services.NasIdentity.NormalizeHost(Host);
        Port = Port is >= 1 and <= 65535 ? Port : 41973;
    }
}

public sealed class PinnedTabConfig
{
    [JsonPropertyName("title")]
    public string Title { get; set; } = "Search";

    [JsonPropertyName("query")]
    public string Query { get; set; } = "";

    [JsonPropertyName("view")]
    public string ViewKey { get; set; } = "details";

    [JsonPropertyName("sort")]
    public string SortValue { get; set; } = "recent:desc";

    [JsonPropertyName("type_index")]
    public int TypeIndex { get; set; }

    [JsonPropertyName("type_names")]
    public List<string> TypeNames { get; set; } = [];

    [JsonPropertyName("date_from")]
    public DateTime? DateFrom { get; set; }

    [JsonPropertyName("date_to")]
    public DateTime? DateTo { get; set; }

    [JsonPropertyName("exact_match")]
    public bool ExactMatch { get; set; }

    [JsonPropertyName("search_contents")]
    public bool SearchContents { get; set; }

    [JsonPropertyName("scope_paths")]
    public List<string> ScopePaths { get; set; } = [];

    [JsonPropertyName("excluded_scope_paths")]
    public List<string> ExcludedScopePaths { get; set; } = [];
}

public sealed class PathMapping
{
    [JsonPropertyName("share_root")]
    public string ShareRoot { get; set; } = "";

    [JsonPropertyName("mapped_root")]
    public string MappedRoot { get; set; } = "";

    [JsonPropertyName("global")]
    public bool IsGlobal { get; set; }
}

public sealed class BehaviorConfig
{
    [JsonPropertyName("show_in_taskbar")]
    public bool ShowInTaskbar { get; set; } = true;

    [JsonPropertyName("minimize_to_tray")]
    public bool MinimizeToTray { get; set; }

    [JsonPropertyName("exit_to_tray")]
    public bool ExitToTray { get; set; }

    [JsonPropertyName("clear_results_with_query")]
    public bool ClearResultsWithQuery { get; set; }

    [JsonPropertyName("confirm_scope_replacement")]
    public bool ConfirmScopeReplacement { get; set; } = true;

    [JsonPropertyName("navigation_scope_include_gesture")]
    public string NavigationScopeIncludeGesture { get; set; } = "Shift";

    [JsonPropertyName("navigation_scope_exclude_gesture")]
    public string NavigationScopeExcludeGesture { get; set; } = "Shift+Alt";

    [JsonPropertyName("global_hotkey")]
    public string GlobalHotkey { get; set; } = "Ctrl+Alt+Q";

    [JsonPropertyName("keyboard_shortcuts")]
    public KeyboardShortcutConfig KeyboardShortcuts { get; set; } = new();

    [JsonPropertyName("theme_colors")]
    public ThemeColorConfig ThemeColors { get; set; } = new();

    [JsonPropertyName("use_windows_accent_color")]
    public bool UseWindowsAccentColor { get; set; }

    [JsonPropertyName("favorites_pane_width")]
    public int FavoritesPaneWidth { get; set; } = 238;

    [JsonPropertyName("preview_pane_width")]
    public int PreviewPaneWidth { get; set; } = 280;

    [JsonPropertyName("favorites_section_height")]
    public int FavoritesSectionHeight { get; set; } = 220;

    [JsonPropertyName("recent_searches_section_height")]
    public int RecentSearchesSectionHeight { get; set; } = 112;

    [JsonPropertyName("favorites_navigation_split")]
    public double FavoritesNavigationSplit { get; set; } = 0.35;

    [JsonPropertyName("show_navigation_pane")]
    public bool ShowNavigationPane { get; set; } = true;

    [JsonPropertyName("show_local_navigation_folders")]
    public bool ShowLocalNavigationFolders { get; set; } = true;

    [JsonPropertyName("show_local_navigation_drives")]
    public bool ShowLocalNavigationDrives { get; set; }

    [JsonPropertyName("navigation_expanded_paths")]
    public List<string> NavigationExpandedPaths { get; set; } = [];

    [JsonPropertyName("theme")]
    public string Theme { get; set; } = "system";

    [JsonPropertyName("highlight_matches")]
    public bool HighlightMatches { get; set; } = true;

    [JsonPropertyName("show_recovery_system_folders")]
    public bool ShowRecoverySystemFolders { get; set; }

    [JsonPropertyName("show_qsurfer_safety_copies")]
    public bool ShowQSurferSafetyCopies { get; set; }

    [JsonPropertyName("show_hidden_temporary_files")]
    public bool ShowHiddenTemporaryFiles { get; set; }

    [JsonPropertyName("flatten_recycle_bin")]
    public bool FlattenRecycleBin { get; set; } = true;

    [JsonPropertyName("use_qsirch_thumbnails")]
    public bool UseQsirchThumbnails { get; set; }

    [JsonPropertyName("search_contents")]
    public bool SearchContents { get; set; }

    [JsonPropertyName("preview_pane")]
    public bool PreviewPane { get; set; }

    [JsonPropertyName("original_restore_policy")]
    public string OriginalRestorePolicy { get; set; } = "files";

    [JsonPropertyName("one_time_folder_restore")]
    public bool OneTimeFolderRestore { get; set; }

    [JsonPropertyName("result_view")]
    public string ResultView { get; set; } = "details";

    [JsonPropertyName("result_sort")]
    public string ResultSort { get; set; } = "recent";

    [JsonPropertyName("visible_detail_columns")]
    public List<string> VisibleDetailColumns { get; set; } = ["name", "location", "modified", "type", "size"];

    [JsonPropertyName("search_timeout_seconds")]
    public int SearchTimeoutSeconds { get; set; } = 90;

    [JsonPropertyName("first_page_size")]
    public int FirstPageSize { get; set; } = 15;

    [JsonPropertyName("next_page_size")]
    public int NextPageSize { get; set; } = 100;

    [JsonPropertyName("max_search_results")]
    public int MaxSearchResults { get; set; } = 1000;

    [JsonPropertyName("max_bulk_open_tabs")]
    public int MaxBulkOpenTabs { get; set; } = 15;

    [JsonPropertyName("last_seen_whats_new_version")]
    public string LastSeenWhatsNewVersion { get; set; } = "";

    [JsonPropertyName("last_seen_first_run_guide_version")]
    public string LastSeenFirstRunGuideVersion { get; set; } = "";

}

public sealed class ThemeColorConfig
{
    [JsonPropertyName("light_surface")]
    public string LightSurface { get; set; } = "#FFFFFFFF";

    [JsonPropertyName("light_accent")]
    public string LightAccent { get; set; } = "#FF0067B8";

    [JsonPropertyName("light_selection")]
    public string LightSelection { get; set; } = "#FFD7EBFF";

    [JsonPropertyName("light_hover")]
    public string LightHover { get; set; } = "#FFE8F3FD";

    [JsonPropertyName("light_match")]
    public string LightMatch { get; set; } = "#FFFFE29A";

    [JsonPropertyName("dark_surface")]
    public string DarkSurface { get; set; } = "#FF242830";

    [JsonPropertyName("dark_accent")]
    public string DarkAccent { get; set; } = "#FF36B9AD";

    [JsonPropertyName("dark_selection")]
    public string DarkSelection { get; set; } = "#FF27495F";

    [JsonPropertyName("dark_hover")]
    public string DarkHover { get; set; } = "#FF2C3847";

    [JsonPropertyName("dark_match")]
    public string DarkMatch { get; set; } = "#FF725826";
}


public sealed class KeyboardShortcutConfig
{
    [JsonPropertyName("focus_search")]
    public string FocusSearch { get; set; } = "Ctrl+F";

    [JsonPropertyName("refresh")]
    public string Refresh { get; set; } = "F5";

    [JsonPropertyName("back")]
    public string Back { get; set; } = "Alt+Left";

    [JsonPropertyName("forward")]
    public string Forward { get; set; } = "Alt+Right";

    [JsonPropertyName("up")]
    public string Up { get; set; } = "Alt+Up";

    [JsonPropertyName("open")]
    public string Open { get; set; } = "Enter";

    [JsonPropertyName("copy_path")]
    public string CopyPath { get; set; } = "Ctrl+C";

    [JsonPropertyName("cut")]
    public string Cut { get; set; } = "Ctrl+X";

    [JsonPropertyName("paste")]
    public string Paste { get; set; } = "Ctrl+V";

    [JsonPropertyName("rename")]
    public string Rename { get; set; } = "F2";

    [JsonPropertyName("delete")]
    public string Delete { get; set; } = "Delete";

    [JsonPropertyName("new_folder")]
    public string NewFolder { get; set; } = "Ctrl+Shift+N";

    [JsonPropertyName("favorite")]
    public string Favorite { get; set; } = "Ctrl+D";
}

public sealed class HistoryConfig
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

}

public sealed class ExcludeConfig
{
    private static readonly string[] DefaultFolderPatterns =
    [
        "@Recently-Snapshot\\*",
        "@Recycle\\*",
        "#recycle\\*",
        ".sync\\*",
        ".qsync\\*",
        ".qsync_sn\\*",
    ];

    private static readonly string[] DefaultFilePatterns =
    [
        "Thumbs.db",
        "desktop.ini",
        "*.tmp",
        "~$*",
        ".DS_Store",
        "*.qsync",
        "*.qsync_tmp",
        "*.syncing",
        "*_conflict_*",
        "*conflicted copy*",
    ];

    private List<string>? _legacyFolders;
    private List<string>? _legacyFiles;
    private List<ScopedTextRule>? _folderRules;
    private List<ScopedTextRule>? _fileRules;

    [JsonPropertyName("folders")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? LegacyFolders
    {
        get => _legacyFolders;
        set => _legacyFolders = value;
    }

    [JsonPropertyName("files")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? LegacyFiles
    {
        get => _legacyFiles;
        set => _legacyFiles = value;
    }

    [JsonPropertyName("folder_rules")]
    public List<ScopedTextRule> FolderRules
    {
        get => _folderRules ??= [];
        set => _folderRules = value ?? [];
    }

    [JsonPropertyName("file_rules")]
    public List<ScopedTextRule> FileRules
    {
        get => _fileRules ??= [];
        set => _fileRules = value ?? [];
    }

    public void MigrateLegacyRules(bool global)
    {
        if (_folderRules == null)
        {
            IEnumerable<string> folderPatterns = _legacyFolders is { Count: > 0 } ? _legacyFolders : DefaultFolderPatterns;
            FolderRules = folderPatterns
                .Select(pattern => new ScopedTextRule { Pattern = pattern, IsGlobal = global })
                .ToList();
        }
        if (_fileRules == null)
        {
            IEnumerable<string> filePatterns = _legacyFiles is { Count: > 0 } ? _legacyFiles : DefaultFilePatterns;
            FileRules = filePatterns
                .Select(pattern => new ScopedTextRule { Pattern = pattern, IsGlobal = global })
                .ToList();
        }

        _legacyFolders = null;
        _legacyFiles = null;
    }
}

public sealed class ScopedTextRule
{
    [JsonPropertyName("pattern")]
    public string Pattern { get; set; } = "";

    [JsonPropertyName("global")]
    public bool IsGlobal { get; set; }
}

public sealed class VisibilityRule
{
    [JsonPropertyName("access")]
    public string Access { get; set; } = "deny";

    [JsonPropertyName("identity")]
    public string Identity { get; set; } = "*";

    [JsonPropertyName("pattern")]
    public string Pattern { get; set; } = "";

    [JsonPropertyName("global")]
    public bool IsGlobal { get; set; }
}
