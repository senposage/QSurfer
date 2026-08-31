using System.Text.Json.Serialization;

namespace QSurfer.Core.Models;

public sealed class AppConfig
{
    private ConnectionDefaults? _rootConnectionDefaults;

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
    public bool AlwaysOnTop { get; set; } = true;

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

        var globalExclude = CloneExclude(Exclude);
        var globalVisibility = VisibilityRules.Select(CloneVisibilityRule).ToList();

        Host = host.Host;
        Port = host.Port;
        Ssl = host.Ssl;
        SslVerify = host.SslVerify;
        User = host.User;
        Password = host.Password;
        PathMappings = host.PathMappings.Select(ClonePathMapping).ToList();
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
        PromoteMissingSharedConnectionValues();
        NormalizeRules(Exclude, global: false);
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
            Host = Host,
            Port = Port,
            Ssl = Ssl,
            SslVerify = SslVerify,
            User = User,
            Password = Password,
            PathMappings = PathMappings.Select(ClonePathMapping).ToList(),
            Behavior = CloneBehavior(Behavior),
            History = CloneHistory(History),
            Exclude = localExclude,
            VisibilityRules = VisibilityRules.Where(x => !x.IsGlobal).Select(CloneVisibilityRule).ToList(),
            AlwaysOnTop = AlwaysOnTop,
            PinnedTabs = PinnedTabs.Select(ClonePinnedTab).ToList(),
        };

        RestoreRootConnectionDefaults();
        ClearRootMachineSettings();
        Exclude = globalExclude;
        VisibilityRules = VisibilityRules.Where(x => x.IsGlobal).Select(CloneVisibilityRule).ToList();
    }

    public void ClearRootMachineSettings()
    {
        // The root NAS connection is a shared deployment default. Other live settings belong to a host record.
        PathMappings = [];
        Behavior = new BehaviorConfig();
        History = new HistoryConfig();
        AlwaysOnTop = true;
        PinnedTabs = [];
    }

    private void CaptureRootConnectionDefaults()
    {
        _rootConnectionDefaults ??= new ConnectionDefaults(Host, Port, Ssl, SslVerify, User, Password);
    }

    private void PromoteMissingSharedConnectionValues()
    {
        var defaults = _rootConnectionDefaults ?? new ConnectionDefaults(Host, Port, Ssl, SslVerify, User, Password);
        if (string.IsNullOrWhiteSpace(defaults.Host) && !string.IsNullOrWhiteSpace(Host))
        {
            defaults = defaults with { Host = Host, Port = Port, Ssl = Ssl, SslVerify = SslVerify };
        }
        if (string.IsNullOrWhiteSpace(defaults.User) && !string.IsNullOrWhiteSpace(User))
        {
            defaults = defaults with { User = User };
        }
        if (string.IsNullOrWhiteSpace(defaults.Password) && !string.IsNullOrWhiteSpace(Password))
        {
            defaults = defaults with { Password = Password };
        }
        _rootConnectionDefaults = defaults;
    }

    private void RestoreRootConnectionDefaults()
    {
        var defaults = _rootConnectionDefaults ?? new ConnectionDefaults(Host, Port, Ssl, SslVerify, User, Password);
        Host = defaults.Host;
        Port = defaults.Port;
        Ssl = defaults.Ssl;
        SslVerify = defaults.SslVerify;
        User = defaults.User;
        Password = defaults.Password;
    }

    private static void NormalizeRules(ExcludeConfig exclude, bool global)
    {
        exclude.MigrateLegacyRules(global);
    }

    public void MigrateLegacySettings()
    {
        NormalizeRules(Exclude, global: true);
        foreach (var host in Hosts.Values)
        {
            NormalizeRules(host.Exclude, global: false);
        }
    }

    private static PathMapping ClonePathMapping(PathMapping mapping) => new() { ShareRoot = mapping.ShareRoot, MappedRoot = mapping.MappedRoot };
    private static ScopedTextRule CloneTextRule(ScopedTextRule rule) => new() { Pattern = rule.Pattern, IsGlobal = rule.IsGlobal };
    private static VisibilityRule CloneVisibilityRule(VisibilityRule rule) => new() { Access = rule.Access, Identity = rule.Identity, Pattern = rule.Pattern, IsGlobal = rule.IsGlobal };
    private static PinnedTabConfig ClonePinnedTab(PinnedTabConfig tab) => new() { Title = tab.Title, Query = tab.Query, ViewKey = tab.ViewKey, SortValue = tab.SortValue, TypeIndex = tab.TypeIndex, TypeNames = tab.TypeNames.ToList(), DateFrom = tab.DateFrom, DateTo = tab.DateTo, ExactMatch = tab.ExactMatch, SearchContents = tab.SearchContents };
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
        GlobalHotkey = behavior.GlobalHotkey,
        KeyboardShortcuts = CloneKeyboardShortcuts(behavior.KeyboardShortcuts),
        Theme = behavior.Theme,
        HighlightMatches = behavior.HighlightMatches,
        ShowQsirchInternalPaths = behavior.ShowQsirchInternalPaths,
        ExactMatch = behavior.ExactMatch,
        UseQsirchThumbnails = behavior.UseQsirchThumbnails,
        SearchContents = behavior.SearchContents,
        PreviewPane = behavior.PreviewPane,
        AllowDownload = behavior.AllowDownload,
        FoldersFirst = behavior.FoldersFirst,
        ResultView = behavior.ResultView,
        ResultSort = behavior.ResultSort,
        VisibleDetailColumns = behavior.VisibleDetailColumns.ToList(),
        SearchTimeoutSeconds = behavior.SearchTimeoutSeconds,
        FirstPageSize = behavior.FirstPageSize,
        NextPageSize = behavior.NextPageSize,
        MaxSearchResults = behavior.MaxSearchResults,
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

    private sealed record ConnectionDefaults(string Host, int Port, bool Ssl, bool SslVerify, string User, string Password);
}

public sealed class HostConfig
{
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
    public bool AlwaysOnTop { get; set; } = true;

    [JsonPropertyName("pinned_tabs")]
    public List<PinnedTabConfig> PinnedTabs { get; set; } = [];
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
}

public sealed class PathMapping
{
    [JsonPropertyName("share_root")]
    public string ShareRoot { get; set; } = "";

    [JsonPropertyName("mapped_root")]
    public string MappedRoot { get; set; } = "";
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

    [JsonPropertyName("global_hotkey")]
    public string GlobalHotkey { get; set; } = "Ctrl+S";

    [JsonPropertyName("keyboard_shortcuts")]
    public KeyboardShortcutConfig KeyboardShortcuts { get; set; } = new();

    [JsonPropertyName("theme")]
    public string Theme { get; set; } = "system";

    [JsonPropertyName("highlight_matches")]
    public bool HighlightMatches { get; set; } = true;

    [JsonPropertyName("show_qsirch_internal_paths")]
    public bool ShowQsirchInternalPaths { get; set; }

    [JsonPropertyName("exact_match")]
    public bool ExactMatch { get; set; }

    [JsonPropertyName("use_qsirch_thumbnails")]
    public bool UseQsirchThumbnails { get; set; }

    [JsonPropertyName("search_contents")]
    public bool SearchContents { get; set; }

    [JsonPropertyName("preview_pane")]
    public bool PreviewPane { get; set; }

    [JsonPropertyName("allow_download")]
    public bool AllowDownload { get; set; }

    [JsonPropertyName("folders_first")]
    public bool FoldersFirst { get; set; } = true;

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
    public int MaxSearchResults { get; set; } = 500;

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
