using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
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
    private readonly List<PathMapping> _initialGlobalMappings;
    private readonly Dictionary<TextBox, DateTimeOffset> _escapeHeldSince = new();
    private static readonly TimeSpan EscapeClearDelay = TimeSpan.FromMilliseconds(650);
    private bool _updatingThemeColors;
    private bool _applyingThemePreset;
    private bool _synchronizingLocalIndexerCatalogMode;
    private bool _localIndexerCatalogUpdateInProgress;
    private bool _localIndexerAdministrationOpened;
    private LocalIndexerManager? _localIndexer;

    public SettingsWindow() : this(new AppConfig(), null)
    {
    }

    public SettingsWindow(AppConfig config, Func<string, HotkeyRegistrationResult>? configureHotkey)
    {
        _config = config;
        _configureHotkey = configureHotkey;
        InitializeComponent();
        if (OperatingSystem.IsLinux())
        {
            LocalIndexerDescriptionText.Text = "QSurfer downloads the latest verified QIndexer release from GitHub and installs it as a systemd service. It keeps running when QSurfer is closed. The service runs as this user; configure mounted network roots through advanced QIndexer administration.";
            InstallLocalIndexerButton.Content = "Install Linux service";
        }
        Opened += async (_, _) =>
        {
            FitToCurrentDisplay();
            AppLogger.Info("settings", "settings opened; refreshing local QIndexer status");
            await RefreshLocalIndexerStatusAsync();
        };
        Activated += async (_, _) =>
        {
            if (_localIndexerAdministrationOpened)
            {
                await RefreshLocalIndexerStatusAsync();
            }
        };
        Closed += (_, _) => ResetLocalIndexerManager();

        CanEditGlobalSettings = GlobalRuleAuthorization.CanManageGlobalSettings();
        var currentUser = GlobalRuleAuthorization.GetCurrentUserStatus();
        WindowsIdentityText.Text = $"Current user: {currentUser.UserName}  |  Access: {currentUser.DisplayStatus}";
        _initialGlobalFolderRules = config.Exclude.FolderRules.Where(rule => rule.IsGlobal).Select(CloneRule).ToList();
        _initialGlobalFileRules = config.Exclude.FileRules.Where(rule => rule.IsGlobal).Select(CloneRule).ToList();
        _initialGlobalVisibilityRules = config.VisibilityRules.Where(rule => rule.IsGlobal).Select(CloneVisibilityRule).ToList();
        _initialGlobalMappings = config.PathMappings.Where(mapping => mapping.IsGlobal).Select(CloneMapping).ToList();

        Mappings = new ObservableCollection<PathMapping>(config.PathMappings.Select(CloneMapping));
        Providers = new ObservableCollection<SearchProviderConnection>(CreateProviderRows(config));
        DetectedMappings = new ObservableCollection<WindowsDriveMapping>(PathMapper.DiscoverWindowsDriveMappings());
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
        AdditionalProvidersGrid.ItemsSource = Providers;
        DetectedMappingsGrid.ItemsSource = DetectedMappings;
        FolderRulesGrid.ItemsSource = FolderRules;
        FileRulesGrid.ItemsSource = FileRules;
        VisibilityRulesGrid.ItemsSource = VisibilityRules;

        ConfigurePathMappingsForPlatform();

        ShowInTaskbarBox.IsChecked = config.Behavior.ShowInTaskbar;
        MinimizeToTrayBox.IsChecked = config.Behavior.MinimizeToTray;
        ExitToTrayBox.IsChecked = config.Behavior.ExitToTray;
        ClearResultsWithQueryBox.IsChecked = config.Behavior.ClearResultsWithQuery;
        ConfirmScopeReplacementBox.IsChecked = config.Behavior.ConfirmScopeReplacement;
        AlwaysOnTopBox.IsChecked = config.AlwaysOnTop;
        DefaultSearchContentsBox.IsChecked = config.Behavior.SearchContents;
        HighlightMatchesBox.IsChecked = config.Behavior.HighlightMatches;
        PreviewPaneBox.IsChecked = config.Behavior.PreviewPane;
        ShowRecoverySystemFoldersBox.IsChecked = config.Behavior.ShowRecoverySystemFolders;
        ShowQSurferSafetyCopiesBox.IsChecked = config.Behavior.ShowQSurferSafetyCopies;
        ShowHiddenTemporaryFilesBox.IsChecked = config.Behavior.ShowHiddenTemporaryFiles;
        FlattenRecycleBinBox.IsChecked = config.Behavior.FlattenRecycleBin;
        ShowLocalNavigationFoldersBox.IsChecked = config.Behavior.ShowLocalNavigationFolders;
        ShowLocalNavigationDrivesBox.IsChecked = config.Behavior.ShowLocalNavigationDrives;
        UseThumbnailsBox.IsChecked = config.Behavior.UseQsirchThumbnails;
        LocalIndexerEnabledBox.IsChecked = config.LocalIndexer.Enabled;
        LocalIndexerUsePostgresBox.IsChecked = config.LocalIndexer.UsePostgres;
        LocalIndexerUsePostgresBox.IsCheckedChanged += LocalIndexerUsePostgresBox_IsCheckedChanged;
        ResultLimitBox.Text = Math.Max(config.Behavior.MaxSearchResults, 50).ToString();
        SearchTimeoutBox.Text = Math.Clamp(config.Behavior.SearchTimeoutSeconds, 10, 300).ToString();
        FirstPageSizeBox.Text = Math.Clamp(config.Behavior.FirstPageSize, 5, 500).ToString();
        NextPageSizeBox.Text = Math.Clamp(config.Behavior.NextPageSize, 10, 500).ToString();
        BulkOpenLimitBox.Text = Math.Clamp(config.Behavior.MaxBulkOpenTabs, 1, 100).ToString();
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
        SelectTaggedItem(NavigationScopeIncludeGestureBox, config.Behavior.NavigationScopeIncludeGesture, "Shift");
        SelectTaggedItem(NavigationScopeExcludeGestureBox, config.Behavior.NavigationScopeExcludeGesture, "Shift+Alt");
        SelectTaggedItem(ResultViewBox, config.Behavior.ResultView, "details");
        SelectTaggedItem(ResultSortBox, config.Behavior.ResultSort, "folder");
        SelectTaggedItem(OriginalRestorePolicyBox, config.Behavior.OriginalRestorePolicy, "files");
        SelectTaggedItem(OneTimeFolderRestoreBox, config.Behavior.OneTimeFolderRestore ? "one_time" : "blocked", "blocked");
        HistoryEnabledBox.IsChecked = config.History.Enabled;
        SelectTheme(string.IsNullOrWhiteSpace(config.Behavior.Theme) ? "system" : config.Behavior.Theme);
        UseWindowsAccentColorBox.IsChecked = config.Behavior.UseWindowsAccentColor;
        SelectThemePreset("custom");
        SetThemeColorBoxes(config.Behavior.ThemeColors);
        UpdateThemeColorPickers();
        UpdateAccentControls();
    }

    public bool Saved { get; private set; }
    public bool ClearHistoryRequested { get; private set; }
    public bool ClearStarredRequested { get; private set; }
    public bool ResetDatabaseRequested { get; private set; }
    public event EventHandler? LocalIndexerAvailabilityChanged;
    public bool CanEditGlobalSettings { get; }
    public bool CanEditGlobalRules => CanEditGlobalSettings;
    public bool GlobalRulesAreLocked => !CanEditGlobalSettings;
    public ObservableCollection<PathMapping> Mappings { get; }
    public ObservableCollection<SearchProviderConnection> Providers { get; }
    public ObservableCollection<WindowsDriveMapping> DetectedMappings { get; }
    public ObservableCollection<ScopedTextRule> FolderRules { get; }
    public ObservableCollection<ScopedTextRule> FileRules { get; }
    public ObservableCollection<VisibilityRule> VisibilityRules { get; }

    private void FitToCurrentDisplay()
    {
        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        if (screen == null)
        {
            return;
        }

        // Avalonia reports a screen's working area in physical pixels while a
        // Window's dimensions are device-independent pixels. Use the current
        // display scale so the dialog fits equally well on high-DPI monitors.
        var scale = RenderScaling > 0 ? RenderScaling : 1;
        var width = screen.WorkingArea.Width / scale;
        var height = screen.WorkingArea.Height / scale;
        MaxWidth = width;
        MaxHeight = height;
        Width = width * 0.92;
        Height = height * 0.88;
    }

    private void Save_Click(object? sender, RoutedEventArgs e)
    {
        var providers = Providers
            .Where(provider => provider.Enabled || !string.IsNullOrWhiteSpace(provider.Host) || !string.IsNullOrWhiteSpace(provider.User) || !string.IsNullOrWhiteSpace(provider.Credential))
            .Select(CloneProvider)
            .ToList();
        foreach (var provider in providers)
        {
            provider.Normalize();
            if (provider.Enabled && !provider.IsConfigured)
            {
                var label = string.IsNullOrWhiteSpace(provider.Name) ? provider.Host : provider.Name;
                ShowValidation($"Complete the {provider.ProviderLabel} connection for {label}, or disable it.");
                return;
            }
        }
        if (!providers.Any(provider => provider.IsConfigured) && LocalIndexerEnabledBox.IsChecked != true)
        {
            ShowValidation("Add and complete a search provider, or enable the managed QIndexer.");
            return;
        }
        var primaryQsirch = providers.FirstOrDefault(provider => provider.IsQsirch && provider.IsConfigured)
            ?? providers.FirstOrDefault(provider => provider.IsQsirch);
        var primaryQIndexer = providers.FirstOrDefault(provider => provider.IsQIndexer && provider.IsConfigured)
            ?? providers.FirstOrDefault(provider => provider.IsQIndexer);
        if (!int.TryParse(ResultLimitBox.Text, out var limit) || limit < 50)
        {
            ShowValidation("Search result limit must be at least 50.");
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
        if (!int.TryParse(BulkOpenLimitBox.Text, out var bulkOpenLimit) || bulkOpenLimit is < 1 or > 100)
        {
            ShowValidation("Folders opened at once must be from 1 to 100.");
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
        if (!TryReadThemeColors(out var themeColors, out var themeColorError))
        {
            ShowValidation(themeColorError);
            return;
        }
        var oneTimeFolderRestore = SelectedTag(OneTimeFolderRestoreBox, "blocked") == "one_time";
        if (oneTimeFolderRestore && !string.Equals(OneTimeFolderRestoreAcknowledgementBox.Text?.Trim(), "I understand", StringComparison.OrdinalIgnoreCase))
        {
            ShowValidation("Type I understand to arm the one-time folder restore.");
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

        _config.SearchProvider = SearchProviders.Qsirch;
        _config.QsirchEnabled = primaryQsirch?.Enabled == true;
        _config.Host = primaryQsirch?.Host ?? "";
        _config.Port = primaryQsirch?.Port ?? 443;
        _config.User = primaryQsirch?.User ?? "";
        _config.Password = primaryQsirch?.Password ?? "";
        _config.Ssl = primaryQsirch?.Ssl ?? true;
        _config.SslVerify = primaryQsirch?.SslVerify ?? true;
        _config.SearchService = new SearchServiceConnection
        {
            Enabled = primaryQIndexer?.Enabled == true,
            Host = primaryQIndexer?.Host ?? "",
            Port = primaryQIndexer?.Port ?? 41973,
            Ssl = primaryQIndexer?.Ssl ?? false,
            SslVerify = primaryQIndexer?.SslVerify ?? true,
            Token = primaryQIndexer?.Token ?? "",
        };
        _config.AdditionalSearchProviders = providers
            .Where(provider => !ReferenceEquals(provider, primaryQsirch) && !ReferenceEquals(provider, primaryQIndexer))
            .Select(CloneProvider)
            .ToList();
        _config.LocalIndexer.Enabled = LocalIndexerEnabledBox.IsChecked == true;
        _config.LocalIndexer.UsePostgres = LocalIndexerUsePostgresBox.IsChecked == true;
        _config.AlwaysOnTop = AlwaysOnTopBox.IsChecked == true;
        _config.Behavior.ShowInTaskbar = ShowInTaskbarBox.IsChecked == true;
        _config.Behavior.MinimizeToTray = MinimizeToTrayBox.IsChecked == true;
        _config.Behavior.ExitToTray = ExitToTrayBox.IsChecked == true;
        _config.Behavior.ClearResultsWithQuery = ClearResultsWithQueryBox.IsChecked == true;
        _config.Behavior.ConfirmScopeReplacement = ConfirmScopeReplacementBox.IsChecked != false;
        _config.Behavior.NavigationScopeIncludeGesture = SelectedTag(NavigationScopeIncludeGestureBox, "Shift");
        _config.Behavior.NavigationScopeExcludeGesture = SelectedTag(NavigationScopeExcludeGestureBox, "Shift+Alt");
        _config.Behavior.SearchContents = DefaultSearchContentsBox.IsChecked == true;
        _config.Behavior.HighlightMatches = HighlightMatchesBox.IsChecked == true;
        _config.Behavior.PreviewPane = PreviewPaneBox.IsChecked == true;
        _config.Behavior.ShowRecoverySystemFolders = ShowRecoverySystemFoldersBox.IsChecked == true;
        _config.Behavior.ShowQSurferSafetyCopies = ShowQSurferSafetyCopiesBox.IsChecked == true;
        _config.Behavior.ShowHiddenTemporaryFiles = ShowHiddenTemporaryFilesBox.IsChecked == true;
        _config.Behavior.FlattenRecycleBin = FlattenRecycleBinBox.IsChecked == true;
        _config.Behavior.ShowLocalNavigationFolders = ShowLocalNavigationFoldersBox.IsChecked == true;
        _config.Behavior.ShowLocalNavigationDrives = ShowLocalNavigationDrivesBox.IsChecked == true;
        _config.Behavior.UseQsirchThumbnails = UseThumbnailsBox.IsChecked == true;
        _config.Behavior.MaxSearchResults = limit;
        _config.Behavior.SearchTimeoutSeconds = timeout;
        _config.Behavior.FirstPageSize = firstPageSize;
        _config.Behavior.NextPageSize = nextPageSize;
        _config.Behavior.MaxBulkOpenTabs = bulkOpenLimit;
        _config.Behavior.Theme = SelectedTheme();
        _config.Behavior.ThemeColors = themeColors;
        _config.Behavior.UseWindowsAccentColor = UseWindowsAccentColorBox.IsChecked == true;
        _config.Behavior.ResultView = SelectedTag(ResultViewBox, "details");
        _config.Behavior.ResultSort = SelectedTag(ResultSortBox, "folder");
        _config.Behavior.OriginalRestorePolicy = SelectedTag(OriginalRestorePolicyBox, "files");
        _config.Behavior.OneTimeFolderRestore = oneTimeFolderRestore;
        _config.Behavior.GlobalHotkey = hotkey;
        _config.Behavior.KeyboardShortcuts = shortcuts;
        _config.History.Enabled = HistoryEnabledBox.IsChecked == true;
        var mappings = Mappings
            .Where(mapping => !string.IsNullOrWhiteSpace(mapping.ShareRoot) || !string.IsNullOrWhiteSpace(mapping.MappedRoot))
            .Select(CloneMapping)
            .ToList();
        foreach (var mapping in mappings)
        {
            if (!PathMapper.IsValidManualMapping(mapping, out var mappingError))
            {
                ShowValidation(mappingError);
                return;
            }
        }
        _config.PathMappings = PreserveGlobalMappings(mappings, _initialGlobalMappings);
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

    private async void InstallLocalIndexerService_Click(object? sender, RoutedEventArgs e)
    {
        if (!LocalIndexerManager.IsSupported)
        {
            LocalIndexerStatusText.Text = "Local QIndexer management is not supported on this operating system.";
            return;
        }

        var usePostgres = LocalIndexerUsePostgresBox.IsChecked == true;
        _config.LocalIndexer.UsePostgres = usePostgres;
        ConfigStore.Save(_config);

        if (OperatingSystem.IsLinux())
        {
            var linuxConfirmation = new ConfirmationWindow(
                "Install local indexing service",
            "QSurfer will download the latest verified QIndexer release from GitHub, then ask the system for permission to install and start its boot-time systemd service. Its configuration and index stay in your QSurfer data folder.",
                "Continue");
            if (await linuxConfirmation.ShowDialog<bool?>(this) != true) return;
            await RunLinuxIndexerCommandAsync("--install-local-indexer-service", "Linux service setup did not finish. The existing QIndexer state was left unchanged.", !usePostgres);
            return;
        }

        if (LocalIndexerManager.IsElevated())
        {
            await InstallLocalIndexerAsync(new LocalIndexerManager(stateDirectory: LocalIndexerManager.MachineStateRoot, usePostgres: usePostgres));
            return;
        }

        var confirmation = new ConfirmationWindow(
            "Install local indexing service",
            "QSurfer will ask Windows for permission to install and start its local QIndexer service. It uses fixed local volumes and loopback listeners; advanced QIndexer administration can configure additional roots and bindings. If you cancel the Windows prompt, no service is installed.",
            "Continue");
        if (await confirmation.ShowDialog<bool?>(this) != true)
        {
            return;
        }

        try
        {
            var progressPath = CreateLocalIndexerProgressPath();
            var executable = Environment.ProcessPath ?? throw new InvalidOperationException("QSurfer could not locate its executable for service setup.");
            var startInfo = new ProcessStartInfo(executable)
            {
                UseShellExecute = true,
                Verb = "runas",
                // These are one-shot WinExe helpers. Keep them out of the
                // user's workspace; their outcome is reported in QSurfer.
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            startInfo.ArgumentList.Add("--install-local-indexer-service");
            startInfo.ArgumentList.Add("--local-indexer-progress");
            startInfo.ArgumentList.Add(progressPath);
            if (!usePostgres)
            {
                startInfo.ArgumentList.Add("--local-indexer-sqlite");
            }
            using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Windows did not start the elevated service installer.");
            if (await WaitForLocalIndexerHelperAsync(process, progressPath) != 0)
            {
                LocalIndexerStatusText.Text = "Windows service setup was cancelled or did not finish.";
                return;
            }

            ResetLocalIndexerManager();
            SetLocalIndexerEnabled(true);
            LocalIndexerStatusText.Text = "Local QIndexer service is installed, running, and healthy.";
            await RefreshLocalIndexerStatusAsync();
        }
        catch (Win32Exception)
        {
            LocalIndexerStatusText.Text = "Windows elevation was cancelled. No local indexing service was installed.";
        }
        catch
        {
            LocalIndexerStatusText.Text = "Windows service setup did not finish. The existing QIndexer state was left unchanged.";
        }
    }

    private async void StartLocalIndexer_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (OperatingSystem.IsLinux())
            {
                await StartLocalIndexerAsync(GetLocalIndexer());
                return;
            }

            if (LocalIndexerManager.IsElevated())
            {
                await StartLocalIndexerAsync(new LocalIndexerManager(stateDirectory: LocalIndexerManager.MachineStateRoot), forceWindowsService: true);
                return;
            }

            var confirmation = new ConfirmationWindow(
                "Start local indexing service",
                "Windows will ask for permission to start QSurfer's installed local QIndexer service.",
                "Start service");
            if (await confirmation.ShowDialog<bool?>(this) != true) return;

            await RunWindowsIndexerCommandAsync("--start-local-indexer-service", "Windows could not start the local indexing service.");
            ResetLocalIndexerManager();
            await StartLocalIndexerAsync(GetLocalIndexer());
        }
        catch (Win32Exception)
        {
            LocalIndexerStatusText.Text = "Windows elevation was cancelled. The local indexing service was not started.";
        }
        catch
        {
            LocalIndexerStatusText.Text = "QIndexer could not be started.";
        }
    }

    private async void UninstallLocalIndexer_Click(object? sender, RoutedEventArgs e)
    {
        var confirmation = new ConfirmationWindow(
            "Uninstall local indexing service",
            "QSurfer will stop and unregister its local QIndexer service, then remove the managed executable, configuration, and tokens. Existing indexed data will be kept.",
            "Uninstall service");
        if (await confirmation.ShowDialog<bool?>(this) != true) return;

        try
        {
            if (OperatingSystem.IsLinux() || LocalIndexerManager.IsElevated())
            {
                await GetLocalIndexer().UninstallAsync(removeIndexData: false);
            }
            else
            {
                await RunWindowsIndexerCommandAsync("--uninstall-local-indexer-service", "Windows could not uninstall the local indexing service.");
            }

            ResetLocalIndexerManager();
            SetLocalIndexerEnabled(false);
            LocalIndexerStatusText.Text = "Local QIndexer service was uninstalled. Indexed data was kept.";
            await RefreshLocalIndexerStatusAsync();
        }
        catch (Win32Exception)
        {
            LocalIndexerStatusText.Text = "Windows elevation was cancelled. The local indexing service was kept.";
        }
        catch
        {
            LocalIndexerStatusText.Text = "QIndexer could not be uninstalled. Its existing state was kept.";
        }
    }

    private async void DisableLocalIndexer_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (OperatingSystem.IsLinux())
            {
                await GetLocalIndexer().DisableAsync();
                SetLocalIndexerEnabled(false);
                await RefreshLocalIndexerStatusAsync();
                return;
            }
            var manager = GetLocalIndexer();
            var status = await manager.GetStatusAsync();
            if (status.UsesWindowsService && !LocalIndexerManager.IsElevated())
            {
                var confirmation = new ConfirmationWindow(
                    "Stop local indexing service",
                    "Windows will ask for permission to stop QSurfer's local indexing service. The existing index, configuration, and tokens will be kept.",
                    "Stop service");
                if (await confirmation.ShowDialog<bool?>(this) != true)
                {
                    return;
                }

                var executable = Environment.ProcessPath ?? throw new InvalidOperationException("QSurfer could not locate its executable to stop the service.");
                var startInfo = new ProcessStartInfo(executable)
                {
                    UseShellExecute = true,
                    Verb = "runas",
                    WindowStyle = ProcessWindowStyle.Hidden,
                };
                startInfo.ArgumentList.Add("--stop-local-indexer-service");
                using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Windows did not start the elevated service stopper.");
                await process.WaitForExitAsync();
                if (process.ExitCode != 0)
                {
                    LocalIndexerStatusText.Text = "Windows service stop was cancelled or did not finish. The index was kept.";
                    return;
                }
            }
            else
            {
                await manager.DisableAsync();
            }
            SetLocalIndexerEnabled(false);
            await RefreshLocalIndexerStatusAsync();
        }
        catch (Win32Exception)
        {
            LocalIndexerStatusText.Text = "Windows elevation was cancelled. The local indexing service is still running.";
        }
        catch
        {
            LocalIndexerStatusText.Text = "QIndexer could not be stopped. Its data and configuration were left unchanged.";
        }
    }

    private async void LocalIndexerUsePostgresBox_IsCheckedChanged(object? sender, RoutedEventArgs e)
    {
        if (_synchronizingLocalIndexerCatalogMode || _localIndexerCatalogUpdateInProgress || !IsVisible)
        {
            return;
        }

        var installedMode = GetLocalIndexer().TryGetConfiguredUsePostgres();
        var requestedMode = LocalIndexerUsePostgresBox.IsChecked == true;
        if (!installedMode.HasValue || installedMode.Value == requestedMode)
        {
            _config.LocalIndexer.UsePostgres = requestedMode;
            ConfigStore.Save(_config);
            return;
        }

        _localIndexerCatalogUpdateInProgress = true;
        var updated = await UpdateLocalIndexerAsync();
        _localIndexerCatalogUpdateInProgress = false;
        if (!updated)
        {
            SetLocalIndexerCatalogMode(installedMode.Value);
        }
    }

    private async void UpdateLocalIndexer_Click(object? sender, RoutedEventArgs e) => await UpdateLocalIndexerAsync();

    private async Task<bool> UpdateLocalIndexerAsync()
    {
        try
        {
            if (!LocalIndexerManager.IsSupported)
            {
                LocalIndexerStatusText.Text = "Local QIndexer management is not supported on this operating system.";
                return false;
            }

            var usePostgres = LocalIndexerUsePostgresBox.IsChecked == true;
            var catalogMessage = usePostgres
                ? " PostgreSQL will be provisioned as a new catalog; the existing SQLite index will be kept but not migrated."
                : " The existing SQLite catalog and configuration will be retained.";

            if (OperatingSystem.IsLinux())
            {
                var confirmation = new ConfirmationWindow(
                    "Update local QIndexer service",
                    "QSurfer will download the latest verified QIndexer release from GitHub, then restart the Linux service." + catalogMessage,
                    "Update service");
                if (await confirmation.ShowDialog<bool?>(this) != true) return false;
                _config.LocalIndexer.UsePostgres = usePostgres;
                ConfigStore.Save(_config);
                ResetLocalIndexerManager();
                await GetLocalIndexer().UpgradeAsync();
                SetLocalIndexerEnabled(true);
                LocalIndexerStatusText.Text = "Local QIndexer update completed.";
                await RefreshLocalIndexerStatusAsync();
                return true;
            }

            if (!LocalIndexerManager.IsElevated())
            {
                var confirmation = new ConfirmationWindow(
                    "Update local QIndexer service",
                    "Windows will ask for permission to stop, update, and restart QSurfer's local QIndexer service." + catalogMessage,
                    "Update service");
                if (await confirmation.ShowDialog<bool?>(this) != true)
                {
                    return false;
                }

                _config.LocalIndexer.UsePostgres = usePostgres;
                ConfigStore.Save(_config);
                var executable = Environment.ProcessPath ?? throw new InvalidOperationException("QSurfer could not locate its executable for service update.");
                var startInfo = new ProcessStartInfo(executable)
                {
                    UseShellExecute = true,
                    Verb = "runas",
                    WindowStyle = ProcessWindowStyle.Hidden,
                };
                startInfo.ArgumentList.Add("--upgrade-local-indexer-service");
                var progressPath = CreateLocalIndexerProgressPath();
                startInfo.ArgumentList.Add("--local-indexer-progress");
                startInfo.ArgumentList.Add(progressPath);
                if (!usePostgres)
                {
                    startInfo.ArgumentList.Add("--local-indexer-sqlite");
                }
                using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Windows did not start the elevated service updater.");
                if (await WaitForLocalIndexerHelperAsync(process, progressPath) != 0)
                {
                    LocalIndexerStatusText.Text = "Windows service update was cancelled or did not finish. The previous executable and index were retained.";
                    return false;
                }
            }
            else
            {
                _config.LocalIndexer.UsePostgres = usePostgres;
                ConfigStore.Save(_config);
                ResetLocalIndexerManager();
                await GetLocalIndexer().UpgradeAsync();
            }
            SetLocalIndexerEnabled(true);
            LocalIndexerStatusText.Text = "Local QIndexer update completed.";
            await RefreshLocalIndexerStatusAsync();
            return true;
        }
        catch (Win32Exception)
        {
            LocalIndexerStatusText.Text = "Windows elevation was cancelled. The existing QIndexer executable and index were retained.";
            return false;
        }
        catch
        {
            LocalIndexerStatusText.Text = "QIndexer could not be updated. Its previous executable and index were retained.";
            return false;
        }
    }

    private async void RefreshLocalIndexer_Click(object? sender, RoutedEventArgs e) => await RefreshLocalIndexerStatusAsync();

    private void SetLocalIndexerCatalogMode(bool usePostgres)
    {
        _synchronizingLocalIndexerCatalogMode = true;
        try
        {
            LocalIndexerUsePostgresBox.IsChecked = usePostgres;
            _config.LocalIndexer.UsePostgres = usePostgres;
        }
        finally
        {
            _synchronizingLocalIndexerCatalogMode = false;
        }
    }

    private async void PauseLocalIndexer_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            await GetLocalIndexer().PauseAsync();
            await RefreshLocalIndexerStatusAsync();
        }
        catch
        {
            LocalIndexerStatusText.Text = "QIndexer could not pause crawling.";
        }
    }

    private async void ResumeLocalIndexer_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            await GetLocalIndexer().ResumeAsync();
            await RefreshLocalIndexerStatusAsync();
        }
        catch
        {
            LocalIndexerStatusText.Text = "QIndexer could not resume crawling.";
        }
    }

    private async void RebuildLocalIndexer_Click(object? sender, RoutedEventArgs e)
    {
        var confirmation = new ConfirmationWindow(
            "Rebuild local index",
            "This clears QIndexer's indexed records for every configured root and then recrawls them. It does not delete or modify any source files. Continue?",
            "Rebuild");
        if (await confirmation.ShowDialog<bool?>(this) != true)
        {
            return;
        }

        try
        {
            await GetLocalIndexer().RebuildAsync();
            await RefreshLocalIndexerStatusAsync();
        }
        catch
        {
            LocalIndexerStatusText.Text = "QIndexer could not rebuild the local index.";
        }
    }

    private void OpenLocalIndexerAdmin_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            _localIndexerAdministrationOpened = true;
            GetLocalIndexer().OpenAdvancedAdministration();
        }
        catch
        {
            LocalIndexerStatusText.Text = "QIndexer administration is not available yet.";
        }
    }

    private async void CopyLocalIndexerAdminToken_Click(object? sender, RoutedEventArgs e)
    {
        var token = GetLocalIndexer().GetAdminTokenForSupport();
        var clipboard = GetTopLevel(this)?.Clipboard;
        if (string.IsNullOrWhiteSpace(token) || clipboard == null)
        {
            LocalIndexerStatusText.Text = "The local QIndexer admin token is not available yet.";
            return;
        }

        await clipboard.SetTextAsync(token);
        LocalIndexerStatusText.Text = "Local QIndexer admin token copied. Paste it into the advanced administration page.";
    }

    private static string CreateLocalIndexerProgressPath()
    {
        var directory = Path.Combine(Path.GetTempPath(), "QSurfer");
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, $"qindexer-setup-{Guid.NewGuid():N}.json");
    }

    private async Task<int> WaitForLocalIndexerHelperAsync(Process process, string progressPath)
    {
        try
        {
            while (!process.HasExited)
            {
                DisplayLocalIndexerSetupProgress(progressPath);
                await Task.Delay(200);
            }
            DisplayLocalIndexerSetupProgress(progressPath);
            return process.ExitCode;
        }
        finally
        {
            TryDeleteProgressFile(progressPath);
        }
    }

    private void DisplayLocalIndexerSetupProgress(string progressPath)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(progressPath));
            var root = document.RootElement;
            var stage = root.TryGetProperty("stage", out var stageValue) ? stageValue.GetString() : null;
            var message = root.TryGetProperty("message", out var messageValue) ? messageValue.GetString() : null;
            var percent = root.TryGetProperty("percent", out var percentValue) && percentValue.TryGetInt32(out var parsedPercent)
                ? $" ({parsedPercent}%)"
                : "";
            if (!string.IsNullOrWhiteSpace(message) || !string.IsNullOrWhiteSpace(stage))
            {
                LocalIndexerStatusText.Text = "QIndexer setup: " + (message ?? stage) + percent;
            }
        }
        catch (IOException)
        {
            // The installer writes atomically; a read can race its rename.
        }
        catch (JsonException)
        {
            // Ignore a partially written progress document and try again.
        }
    }

    private static void TryDeleteProgressFile(string progressPath)
    {
        try
        {
            if (File.Exists(progressPath)) File.Delete(progressPath);
        }
        catch (IOException)
        {
            // Progress files are disposable and may briefly be held by the helper.
        }
    }

    private async Task RunLinuxIndexerCommandAsync(string command, string failureMessage, bool useSqlite = false)
    {
        try
        {
            var executable = Environment.ProcessPath ?? throw new InvalidOperationException("QSurfer could not locate its executable for Linux service setup.");
            var progressPath = CreateLocalIndexerProgressPath();
            var startInfo = new ProcessStartInfo("pkexec")
            {
                UseShellExecute = false,
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            startInfo.ArgumentList.Add(executable);
            startInfo.ArgumentList.Add(command);
            startInfo.ArgumentList.Add("--local-indexer-progress");
            startInfo.ArgumentList.Add(progressPath);
            if (useSqlite)
            {
                startInfo.ArgumentList.Add("--local-indexer-sqlite");
            }
            using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Linux did not start the privileged service helper.");
            if (await WaitForLocalIndexerHelperAsync(process, progressPath) != 0)
            {
                LocalIndexerStatusText.Text = failureMessage;
                return;
            }

            ResetLocalIndexerManager();
            var manager = GetLocalIndexer();
            await manager.StartAsync();
            var health = await manager.WaitForHealthyAsync(CancellationToken.None);
            if (health.State is not (LocalIndexerHealthState.Healthy or LocalIndexerHealthState.SetupRequired))
            {
                throw new InvalidOperationException(health.Message);
            }
            SetLocalIndexerEnabled(true);
            LocalIndexerStatusText.Text = health.State == LocalIndexerHealthState.SetupRequired
                ? "Local QIndexer service is running. Set its first token in advanced administration."
                : "Local QIndexer service is installed and healthy.";
            await RefreshLocalIndexerStatusAsync();
        }
        catch
        {
            LocalIndexerStatusText.Text = failureMessage;
        }
    }

    private async Task InstallLocalIndexerAsync(LocalIndexerManager manager)
    {
        try
        {
            _localIndexer?.Dispose();
            _localIndexer = manager;
            var setup = await manager.EnableAsync(forceWindowsService: OperatingSystem.IsWindows());
            SetLocalIndexerEnabled(true);
            LocalIndexerStatusText.Text = setup.Health.State == LocalIndexerHealthState.SetupRequired
                ? $"Local QIndexer {setup.Version} is running. Set its first token in advanced administration."
                : $"Local QIndexer {setup.Version} is installed, running, and healthy.";
            await RefreshLocalIndexerStatusAsync();
        }
        catch
        {
            manager.Dispose();
            if (ReferenceEquals(_localIndexer, manager)) _localIndexer = null;
            LocalIndexerStatusText.Text = "QIndexer could not be installed. No unverified package was installed.";
        }
    }

    private async Task StartLocalIndexerAsync(LocalIndexerManager manager, bool forceWindowsService = false)
    {
        _localIndexer?.Dispose();
        _localIndexer = manager;
        await manager.StartAsync(forceWindowsService: forceWindowsService);
        var health = await manager.WaitForHealthyAsync(CancellationToken.None);
        if (health.State is not (LocalIndexerHealthState.Healthy or LocalIndexerHealthState.SetupRequired))
        {
            throw new InvalidOperationException(health.Message);
        }
        SetLocalIndexerEnabled(true);
        LocalIndexerStatusText.Text = "Local QIndexer service is running and healthy.";
        await RefreshLocalIndexerStatusAsync();
    }

    private static async Task RunWindowsIndexerCommandAsync(string command, string failureMessage)
    {
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("QSurfer could not locate its executable for local service management.");
        var startInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        startInfo.ArgumentList.Add(command);
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Windows did not start the elevated local service helper.");
        await process.WaitForExitAsync();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(failureMessage);
        }
    }

    private async Task RefreshLocalIndexerStatusAsync()
    {
        try
        {
            var status = await GetLocalIndexer().GetStatusAsync();
            var previousAdminToken = LocalIndexerAdminTokenBox.Text;
            var configuredUsePostgres = GetLocalIndexer().TryGetConfiguredUsePostgres();
            if (configuredUsePostgres.HasValue && !_localIndexerCatalogUpdateInProgress)
            {
                SetLocalIndexerCatalogMode(configuredUsePostgres.Value);
            }
            LocalIndexerEnabledBox.IsChecked = _config.LocalIndexer.Enabled;
            var adminToken = GetLocalIndexer().GetAdminTokenForSupport() ?? "";
            LocalIndexerAdminTokenBox.Text = adminToken;
            if (status.Health.State == LocalIndexerHealthState.Healthy &&
                (!_config.LocalIndexer.Enabled || !string.Equals(previousAdminToken, adminToken, StringComparison.Ordinal)))
            {
                // Completing first-run setup can create/rotate the loopback
                // token while this dialog is open. Rebuild the main client now.
                SetLocalIndexerEnabled(true);
            }
            LocalIndexerStatusText.Text = status.Health.Message;
            var mode = OperatingSystem.IsLinux() ? "Linux user service" : "Windows service";
            var catalogSize = status.IndexSizeBytes.HasValue
                ? $" · catalog {FormatByteSize(status.IndexSizeBytes.Value)}"
                : "";
            LocalIndexerDetailsText.Text = status.Version == null
                ? mode
                : $"Version {status.Version} · {mode} · {status.DocumentCount:n0} documents{catalogSize} · crawler {status.CrawlerState}";
            var roots = status.Roots.Count == 0
                ? "Configured roots: none reported yet."
                : "Configured roots: " + string.Join("; ", status.Roots.Select(root => $"{root.Name} ({root.State}, {root.DocumentCount:n0} documents)"));
            LocalIndexerVolumesText.Text = status.LastCrawl.HasValue
                ? $"{roots}  Last crawl: {status.LastCrawl.Value.LocalDateTime:g}."
                : roots;
        }
        catch (Exception ex)
        {
            AppLogger.Warn("settings", $"local QIndexer status refresh failed error=\"{ex.Message}\"");
            LocalIndexerAdminTokenBox.Text = "";
            LocalIndexerStatusText.Text = "QIndexer status could not be read.";
            LocalIndexerDetailsText.Text = "";
            LocalIndexerVolumesText.Text = "";
        }
    }

    private LocalIndexerManager GetLocalIndexer() => _localIndexer ??= new LocalIndexerManager(usePostgres: _config.LocalIndexer.UsePostgres);

    private static string FormatByteSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0 ? $"{bytes:n0} {units[unit]}" : $"{value:n1} {units[unit]}";
    }

    private void ResetLocalIndexerManager()
    {
        _localIndexer?.Dispose();
        _localIndexer = null;
    }

    private void SetLocalIndexerEnabled(bool enabled)
    {
        _config.LocalIndexer.Enabled = enabled;
        LocalIndexerEnabledBox.IsChecked = enabled;
        ConfigStore.Save(_config);
        LocalIndexerAvailabilityChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e)
    {
        Close();
    }

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

    private void AddQsirchProvider_Click(object? sender, RoutedEventArgs e) => AddAdditionalProvider(SearchProviders.Qsirch);

    private void AddQIndexerProvider_Click(object? sender, RoutedEventArgs e) => AddAdditionalProvider(SearchProviders.QIndexer);

    private void AddAdditionalProvider(string providerType)
    {
        var provider = new SearchProviderConnection
        {
            Provider = providerType,
            Port = string.Equals(providerType, SearchProviders.QIndexer, StringComparison.OrdinalIgnoreCase) ? 41973 : 443,
            Ssl = !string.Equals(providerType, SearchProviders.QIndexer, StringComparison.OrdinalIgnoreCase),
            SslVerify = true,
        };
        Providers.Add(provider);
        AdditionalProvidersGrid.SelectedItem = provider;
    }

    private void RemoveAdditionalProvider_Click(object? sender, RoutedEventArgs e) => RemoveSelected(AdditionalProvidersGrid, Providers);

    private async void MapNetworkDrive_Click(object? sender, RoutedEventArgs e)
    {
        var drive = MapDriveBox.Text?.Trim() ?? "";
        var networkPath = MapNetworkPathBox.Text?.Trim() ?? "";
        if (!OperatingSystem.IsWindows())
        {
            MapNetworkDriveButton.IsEnabled = false;
            var restoreWindowAfterAuthorization = false;
            try
            {
                // Polkit owns its credential prompt. Minimize this modal dialog first so
                // KDE, GNOME, and other desktops never place that prompt behind QSurfer.
                ShowValidation("Waiting for system authorization to mount the share...");
                if (WindowState != WindowState.Minimized)
                {
                    WindowState = WindowState.Minimized;
                    restoreWindowAfterAuthorization = true;
                    await Task.Delay(150);
                }

                var existing = Mappings.FirstOrDefault(mapping =>
                    mapping.ShareRoot.Equals(networkPath, StringComparison.OrdinalIgnoreCase));
                var mountOutcome = await LinuxNativeCifsMountService.MountAsync(
                    networkPath,
                    _config.Host,
                    _config.User,
                    _config.Password,
                    reconnectAfterReboot: MapReconnectBox.IsChecked == true,
                    replacedMountPoint: existing?.MappedRoot);
                if (!mountOutcome.Succeeded)
                {
                    ShowValidation(mountOutcome.Error);
                    return;
                }

                existing ??= Mappings.FirstOrDefault(mapping =>
                    mapping.ShareRoot.Equals(mountOutcome.ShareRoot, StringComparison.OrdinalIgnoreCase));
                if (existing is null)
                {
                    existing = new PathMapping();
                    Mappings.Add(existing);
                }
                existing.ShareRoot = mountOutcome.ShareRoot;
                existing.MappedRoot = mountOutcome.MountPoint;
                MappingsGrid.SelectedItem = existing;
                MapNetworkPathBox.Text = "";
                ShowValidation(MapReconnectBox.IsChecked == true
                    ? "Native SMB share is configured to mount at boot. Save settings to keep this NAS-to-local mapping."
                    : "Native SMB share mounted. Save settings to keep this NAS-to-local mapping.");
            }
            finally
            {
                if (restoreWindowAfterAuthorization)
                {
                    WindowState = WindowState.Normal;
                    Activate();
                }
                MapNetworkDriveButton.IsEnabled = true;
            }
            return;
        }

        var reconnect = MapReconnectBox.IsChecked == true;
        var outcome = await Task.Run(() =>
        {
            var mapped = PathMapper.TryMapNetworkDrive(drive, networkPath, reconnect, out var error);
            return (mapped, error);
        });
        if (!outcome.mapped)
        {
            ShowValidation(outcome.error);
            return;
        }

        DetectedMappings.Clear();
        foreach (var mapping in PathMapper.DiscoverWindowsDriveMappings())
        {
            DetectedMappings.Add(mapping);
        }
        MapDriveBox.Text = "";
        MapNetworkPathBox.Text = "";
        ShowValidation("Network drive mapped. QSurfer will use it automatically.");
    }

    private void ConfigurePathMappingsForPlatform()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        PathMappingsIntro.Text = "Mount a native Linux SMB share here. Enable mount at boot to create a root-owned credential file and a system-wide systemd mount.";
        MapNetworkDriveLabel.Text = "Mount SMB share";
        MapDriveBox.IsVisible = false;
        MapReconnectBox.IsVisible = true;
        MapReconnectBox.Content = "Mount at boot";
        MapReconnectBox.IsChecked = true;
        MapNetworkDriveButton.Content = "Mount share";
        DetectedMappingsGrid.IsVisible = false;
        PathMappingsLayout.RowDefinitions[2].Height = new GridLength(0);
    }

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

    private List<PathMapping> PreserveGlobalMappings(IEnumerable<PathMapping> currentMappings, IEnumerable<PathMapping> originalGlobalMappings)
    {
        if (CanEditGlobalSettings)
        {
            return currentMappings.Select(CloneMapping).ToList();
        }

        return originalGlobalMappings.Select(CloneMapping)
            .Concat(currentMappings.Where(mapping => !mapping.IsGlobal).Select(CloneMapping))
            .ToList();
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

    private void ThemeColorTextChanged(object? sender, TextChangedEventArgs e)
    {
        if (!_applyingThemePreset)
        {
            SelectThemePreset("custom");
        }
        if (!_updatingThemeColors)
        {
            UpdateThemeColorPickers();
        }
    }

    private void ThemePickerColorChanged(object? sender, ColorChangedEventArgs e)
    {
        if (_updatingThemeColors || sender is not ColorPicker picker)
        {
            return;
        }

        _updatingThemeColors = true;
        try
        {
            var target = PickerTarget(picker);
            target.Box.Text = ThemeColorService.ToHex(e.NewColor);
            target.Swatch.Background = new SolidColorBrush(e.NewColor);
            SelectThemePreset("custom");
            UpdateThemePreview();
        }
        finally
        {
            _updatingThemeColors = false;
        }
    }

    private void UseWindowsAccentColorChanged(object? sender, RoutedEventArgs e) => UpdateAccentControls();

    private void ThemePresetChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_applyingThemePreset)
        {
            return;
        }

        var preset = SelectedTag(ThemePresetBox, "custom");
        if (!preset.Equals("custom", StringComparison.OrdinalIgnoreCase))
        {
            ApplyThemePreset(preset);
        }
    }

    private void ResetAllColors_Click(object? sender, RoutedEventArgs e)
    {
        _applyingThemePreset = true;
        try
        {
            SetThemeColorBoxes(new ThemeColorConfig());
            UseWindowsAccentColorBox.IsChecked = false;
            SelectThemePreset("custom");
        }
        finally
        {
            _applyingThemePreset = false;
        }
        UpdateThemeColorPickers();
        UpdateAccentControls();
    }

    private void ResetLightColors_Click(object? sender, RoutedEventArgs e)
    {
        var defaults = new ThemeColorConfig();
        LightSurfaceColorBox.Text = defaults.LightSurface;
        LightAccentColorBox.Text = defaults.LightAccent;
        LightSelectionColorBox.Text = defaults.LightSelection;
        LightHoverColorBox.Text = defaults.LightHover;
        LightMatchColorBox.Text = defaults.LightMatch;
    }

    private void ResetDarkColors_Click(object? sender, RoutedEventArgs e)
    {
        var defaults = new ThemeColorConfig();
        DarkSurfaceColorBox.Text = defaults.DarkSurface;
        DarkAccentColorBox.Text = defaults.DarkAccent;
        DarkSelectionColorBox.Text = defaults.DarkSelection;
        DarkHoverColorBox.Text = defaults.DarkHover;
        DarkMatchColorBox.Text = defaults.DarkMatch;
    }

    private void SetThemeColorBoxes(ThemeColorConfig colors)
    {
        LightSurfaceColorBox.Text = colors.LightSurface;
        LightAccentColorBox.Text = colors.LightAccent;
        LightSelectionColorBox.Text = colors.LightSelection;
        LightHoverColorBox.Text = colors.LightHover;
        LightMatchColorBox.Text = colors.LightMatch;
        DarkSurfaceColorBox.Text = colors.DarkSurface;
        DarkAccentColorBox.Text = colors.DarkAccent;
        DarkSelectionColorBox.Text = colors.DarkSelection;
        DarkHoverColorBox.Text = colors.DarkHover;
        DarkMatchColorBox.Text = colors.DarkMatch;
    }

    private void ApplyThemePreset(string preset)
    {
        var (colors, useWindowsAccent, displayMode) = preset.ToLowerInvariant() switch
        {
            "windows" => (new ThemeColorConfig(), true, "system"),
            "explorer-light" => (new ThemeColorConfig
            {
                LightSurface = "#FFF9FBFD", LightAccent = "#FF0067C0", LightSelection = "#FFCDE8FF", LightHover = "#FFE5F2FF", LightMatch = "#FFFFE38A",
                DarkSurface = "#FF202020", DarkAccent = "#FF60CDFF", DarkSelection = "#FF264F78", DarkHover = "#FF303030", DarkMatch = "#FF765B22",
            }, false, "light"),
            "explorer-dark" => (new ThemeColorConfig
            {
                LightSurface = "#FFF9FBFD", LightAccent = "#FF0067C0", LightSelection = "#FFCDE8FF", LightHover = "#FFE5F2FF", LightMatch = "#FFFFE38A",
                DarkSurface = "#FF202020", DarkAccent = "#FF60CDFF", DarkSelection = "#FF264F78", DarkHover = "#FF303030", DarkMatch = "#FF765B22",
            }, false, "dark"),
            "high-contrast" => (new ThemeColorConfig
            {
                LightSurface = "#FFFFFFFF", LightAccent = "#FF0000B8", LightSelection = "#FF000080", LightHover = "#FFE6E6FF", LightMatch = "#FFFFFF00",
                DarkSurface = "#FF000000", DarkAccent = "#FFFFFF00", DarkSelection = "#FF0000B8", DarkHover = "#FF303030", DarkMatch = "#FF8A6A00",
            }, false, "dark"),
            _ => (new ThemeColorConfig(), false, "system"),
        };

        _applyingThemePreset = true;
        try
        {
            SetThemeColorBoxes(colors);
            UseWindowsAccentColorBox.IsChecked = useWindowsAccent;
            SelectTheme(displayMode);
        }
        finally
        {
            _applyingThemePreset = false;
        }
        UpdateThemeColorPickers();
        UpdateAccentControls();
    }

    private void UpdateThemeColorPickers()
    {
        _updatingThemeColors = true;
        try
        {
            UpdatePicker(LightSurfacePicker, LightSurfaceSwatch, LightSurfaceColorBox.Text, "#FFFFFFFF");
            UpdatePicker(LightAccentPicker, LightAccentSwatch, LightAccentColorBox.Text, "#FF0067B8");
            UpdatePicker(LightSelectionPicker, LightSelectionSwatch, LightSelectionColorBox.Text, "#FFD7EBFF");
            UpdatePicker(LightHoverPicker, LightHoverSwatch, LightHoverColorBox.Text, "#FFE8F3FD");
            UpdatePicker(LightMatchPicker, LightMatchSwatch, LightMatchColorBox.Text, "#FFFFE29A");
            UpdatePicker(DarkSurfacePicker, DarkSurfaceSwatch, DarkSurfaceColorBox.Text, "#FF242830");
            UpdatePicker(DarkAccentPicker, DarkAccentSwatch, DarkAccentColorBox.Text, "#FF36B9AD");
            UpdatePicker(DarkSelectionPicker, DarkSelectionSwatch, DarkSelectionColorBox.Text, "#FF27495F");
            UpdatePicker(DarkHoverPicker, DarkHoverSwatch, DarkHoverColorBox.Text, "#FF2C3847");
            UpdatePicker(DarkMatchPicker, DarkMatchSwatch, DarkMatchColorBox.Text, "#FF725826");
            UpdateThemePreview();
        }
        finally
        {
            _updatingThemeColors = false;
        }
    }

    private static void UpdatePicker(ColorPicker picker, Border swatch, string? value, string fallback)
    {
        var color = ThemeColorService.TryNormalize(value, out var normalized)
            ? Color.Parse(normalized)
            : Color.Parse(fallback);
        picker.Color = color;
        swatch.Background = new SolidColorBrush(color);
    }

    private void UpdateAccentControls()
    {
        var useCustomAccent = UseWindowsAccentColorBox.IsChecked != true;
        LightAccentPicker.IsEnabled = useCustomAccent;
        LightAccentColorBox.IsEnabled = useCustomAccent;
        DarkAccentPicker.IsEnabled = useCustomAccent;
        DarkAccentColorBox.IsEnabled = useCustomAccent;
    }

    private void UpdateThemePreview()
    {
        UpdatePreview(LightPreviewSurface, LightPreviewFolderGlyph, LightPreviewSelection, LightPreviewSelectionText, LightPreviewHover, LightPreviewMatch,
            LightSurfaceColorBox.Text, LightAccentColorBox.Text, LightSelectionColorBox.Text, LightHoverColorBox.Text, LightMatchColorBox.Text,
            "#FFFFFFFF", "#FF0067B8", "#FFD7EBFF", "#FFE8F3FD", "#FFFFE29A");
        UpdatePreview(DarkPreviewSurface, DarkPreviewFolderGlyph, DarkPreviewSelection, DarkPreviewSelectionText, DarkPreviewHover, DarkPreviewMatch,
            DarkSurfaceColorBox.Text, DarkAccentColorBox.Text, DarkSelectionColorBox.Text, DarkHoverColorBox.Text, DarkMatchColorBox.Text,
            "#FF242830", "#FF36B9AD", "#FF27495F", "#FF2C3847", "#FF725826");
    }

    private static void UpdatePreview(Border surface, TextBlock glyph, Border selection, TextBlock selectionText, Border hover, Border match,
        string? surfaceValue, string? accentValue, string? selectionValue, string? hoverValue, string? matchValue,
        string surfaceFallback, string accentFallback, string selectionFallback, string hoverFallback, string matchFallback)
    {
        var surfaceColor = ReadPreviewColor(surfaceValue, surfaceFallback);
        var accentColor = ReadPreviewColor(accentValue, accentFallback);
        var selectionColor = ReadPreviewColor(selectionValue, selectionFallback);
        surface.Background = new SolidColorBrush(surfaceColor);
        surface.BorderBrush = new SolidColorBrush(accentColor);
        glyph.Foreground = new SolidColorBrush(accentColor);
        selection.Background = new SolidColorBrush(selectionColor);
        selectionText.Foreground = new SolidColorBrush(ThemeColorService.ContrastColor(selectionColor));
        hover.Background = new SolidColorBrush(ReadPreviewColor(hoverValue, hoverFallback));
        match.Background = new SolidColorBrush(ReadPreviewColor(matchValue, matchFallback));
    }

    private static Color ReadPreviewColor(string? value, string fallback) =>
        ThemeColorService.TryNormalize(value, out var normalized) ? Color.Parse(normalized) : Color.Parse(fallback);

    private void SelectThemePreset(string preset)
    {
        _applyingThemePreset = true;
        try
        {
            foreach (var item in ThemePresetBox.Items.OfType<ComboBoxItem>())
            {
                if (string.Equals(item.Tag as string, preset, StringComparison.OrdinalIgnoreCase))
                {
                    ThemePresetBox.SelectedItem = item;
                    return;
                }
            }
        }
        finally
        {
            _applyingThemePreset = false;
        }
    }

    private (TextBox Box, Border Swatch) PickerTarget(ColorPicker picker) => picker switch
    {
        _ when ReferenceEquals(picker, LightSurfacePicker) => (LightSurfaceColorBox, LightSurfaceSwatch),
        _ when ReferenceEquals(picker, LightAccentPicker) => (LightAccentColorBox, LightAccentSwatch),
        _ when ReferenceEquals(picker, LightSelectionPicker) => (LightSelectionColorBox, LightSelectionSwatch),
        _ when ReferenceEquals(picker, LightHoverPicker) => (LightHoverColorBox, LightHoverSwatch),
        _ when ReferenceEquals(picker, LightMatchPicker) => (LightMatchColorBox, LightMatchSwatch),
        _ when ReferenceEquals(picker, DarkSurfacePicker) => (DarkSurfaceColorBox, DarkSurfaceSwatch),
        _ when ReferenceEquals(picker, DarkAccentPicker) => (DarkAccentColorBox, DarkAccentSwatch),
        _ when ReferenceEquals(picker, DarkSelectionPicker) => (DarkSelectionColorBox, DarkSelectionSwatch),
        _ when ReferenceEquals(picker, DarkHoverPicker) => (DarkHoverColorBox, DarkHoverSwatch),
        _ when ReferenceEquals(picker, DarkMatchPicker) => (DarkMatchColorBox, DarkMatchSwatch),
        _ => throw new ArgumentOutOfRangeException(nameof(picker)),
    };

    private bool TryReadThemeColors(out ThemeColorConfig colors, out string error)
    {
        colors = new ThemeColorConfig();
        error = "";
        if (!TryReadThemeColor(LightSurfaceColorBox, "Light main surface", out var lightSurface, out error) ||
            !TryReadThemeColor(LightAccentColorBox, "Light accent", out var lightAccent, out error) ||
            !TryReadThemeColor(LightSelectionColorBox, "Light selected result", out var lightSelection, out error) ||
            !TryReadThemeColor(LightHoverColorBox, "Light hover", out var lightHover, out error) ||
            !TryReadThemeColor(LightMatchColorBox, "Light match highlight", out var lightMatch, out error) ||
            !TryReadThemeColor(DarkSurfaceColorBox, "Dark main surface", out var darkSurface, out error) ||
            !TryReadThemeColor(DarkAccentColorBox, "Dark accent", out var darkAccent, out error) ||
            !TryReadThemeColor(DarkSelectionColorBox, "Dark selected result", out var darkSelection, out error) ||
            !TryReadThemeColor(DarkHoverColorBox, "Dark hover", out var darkHover, out error) ||
            !TryReadThemeColor(DarkMatchColorBox, "Dark match highlight", out var darkMatch, out error))
        {
            return false;
        }

        colors = new ThemeColorConfig
        {
            LightSurface = lightSurface,
            LightAccent = lightAccent,
            LightSelection = lightSelection,
            LightHover = lightHover,
            LightMatch = lightMatch,
            DarkSurface = darkSurface,
            DarkAccent = darkAccent,
            DarkSelection = darkSelection,
            DarkHover = darkHover,
            DarkMatch = darkMatch,
        };
        return true;
    }

    private static bool TryReadThemeColor(TextBox box, string name, out string color, out string error)
    {
        if (ThemeColorService.TryNormalize(box.Text, out color))
        {
            error = "";
            return true;
        }

        error = $"{name} must be a hex color such as #0F6CBD.";
        return false;
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
    private static PathMapping CloneMapping(PathMapping mapping) => new() { ShareRoot = mapping.ShareRoot, MappedRoot = mapping.MappedRoot, IsGlobal = mapping.IsGlobal };
    private static IEnumerable<SearchProviderConnection> CreateProviderRows(AppConfig config)
    {
        if (!string.IsNullOrWhiteSpace(config.Host) || !string.IsNullOrWhiteSpace(config.User) || !string.IsNullOrWhiteSpace(config.Password))
        {
            yield return new SearchProviderConnection
            {
                Provider = SearchProviders.Qsirch,
                Enabled = config.QsirchEnabled,
                Host = config.Host,
                Port = config.Port,
                Ssl = config.Ssl,
                SslVerify = config.SslVerify,
                User = config.User,
                Password = config.Password,
            };
        }
        if (!string.IsNullOrWhiteSpace(config.SearchService.Host) || !string.IsNullOrWhiteSpace(config.SearchService.Token))
        {
            yield return new SearchProviderConnection
            {
                Provider = SearchProviders.QIndexer,
                Enabled = config.SearchService.Enabled,
                Host = config.SearchService.Host,
                Port = config.SearchService.Port,
                Ssl = config.SearchService.Ssl,
                SslVerify = config.SearchService.SslVerify,
                Token = config.SearchService.Token,
            };
        }
        foreach (var provider in config.AdditionalSearchProviders)
        {
            yield return CloneProvider(provider);
        }
    }
    private static SearchProviderConnection CloneProvider(SearchProviderConnection provider) => new()
    {
        Provider = provider.Provider,
        Name = provider.Name,
        Enabled = provider.Enabled,
        Host = provider.Host,
        Port = provider.Port,
        Ssl = provider.Ssl,
        SslVerify = provider.SslVerify,
        User = provider.User,
        Password = provider.Password,
        Token = provider.Token,
    };
    private static ScopedTextRule CloneRule(ScopedTextRule rule) => new() { Pattern = rule.Pattern, IsGlobal = rule.IsGlobal };
    private static VisibilityRule CloneVisibilityRule(VisibilityRule rule) => new() { Access = rule.Access, Identity = rule.Identity, Pattern = rule.Pattern, IsGlobal = rule.IsGlobal };

    private void ShowValidation(string message)
    {
        ValidationMessage.Text = message;
        ValidationMessage.IsVisible = true;
    }
}
