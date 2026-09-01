using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using QSurfer.Core.Models;
using QSurfer.Core.Services;

namespace QSurfer.Avalonia;

public sealed partial class VersionHistoryWindow : Window
{
    private readonly SnapshotTimelineService _timeline;
    private readonly string _sourcePath;
    private readonly bool _isFolder;
    private readonly bool _canRestoreFiles;
    private readonly bool _canRestoreFolders;
    private readonly bool _oneTimeFolderRestoreArmed;
    private readonly AppConfig _config;
    private CancellationTokenSource? _loadCancellation;
    private SnapshotVersion? _selectedVersion;

    public VersionHistoryWindow(string sourcePath, bool isFolder, AppConfig config, bool openedFromExplorer)
    {
        _sourcePath = sourcePath;
        _isFolder = isFolder;
        _config = config;
        var restorePolicy = config.Behavior.OriginalRestorePolicy?.Trim().ToLowerInvariant();
        _canRestoreFiles = restorePolicy != "disabled";
        _oneTimeFolderRestoreArmed = openedFromExplorer && config.Behavior.OneTimeFolderRestore;
        _canRestoreFolders = _oneTimeFolderRestoreArmed;
        _timeline = new SnapshotTimelineService(config);
        InitializeComponent();
        DataContext = this;
        ItemNameText.Text = Path.GetFileName(sourcePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) is { Length: > 0 } itemName
            ? itemName
            : sourcePath;
        SourcePathText.Text = sourcePath;
        if (_oneTimeFolderRestoreArmed)
        {
            RecoveryNoteText.Text = "Break-glass folder restore is armed for this one restore. It will clear after a successful restore.";
        }
        else if (!_canRestoreFiles)
        {
            RestoreButton.IsVisible = false;
            RecoveryNoteText.Text = "Snapshots are read-only. Restore to the original location is disabled in Settings.";
        }
        else if (_isFolder && !_canRestoreFolders)
        {
            RestoreButton.IsVisible = false;
            RecoveryNoteText.Text = "Snapshots are read-only. Folder restore requires the one-session override in Settings; Recover copy remains available.";
        }
        Opened += async (_, _) => await LoadVersionsAsync();
        Closed += (_, _) => _loadCancellation?.Cancel();
    }

    public ObservableCollection<SnapshotVersion> Versions { get; } = [];

    private async Task LoadVersionsAsync()
    {
        _loadCancellation?.Cancel();
        _loadCancellation?.Dispose();
        _loadCancellation = new CancellationTokenSource();
        var token = _loadCancellation.Token;
        Versions.Clear();
        EmptyState.IsVisible = false;
        StatusText.Text = "Loading accessible snapshots...";

        try
        {
            var timeline = await _timeline.LoadAsync(_sourcePath, _isFolder, token);
            if (token.IsCancellationRequested)
            {
                return;
            }

            foreach (var version in timeline.Versions)
            {
                Versions.Add(version);
            }

            EmptyState.IsVisible = Versions.Count == 0;
            StatusText.Text = Versions.Count == 0
                ? string.IsNullOrWhiteSpace(timeline.SnapshotRoot)
                    ? "No accessible @Recently-Snapshot folder was found for this item."
                    : "No earlier version of this item was found in the accessible snapshots."
                : $"{Versions.Count:n0} version{(Versions.Count == 1 ? "" : "s")} found";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            AppLogger.Error("timeline", ex, $"load failed source=\"{_sourcePath}\"");
            EmptyState.IsVisible = true;
            StatusText.Text = "Version history could not be loaded.";
        }
    }

    private void VersionsGrid_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        _selectedVersion = VersionsGrid.SelectedItem as SnapshotVersion;
        var enabled = _selectedVersion != null;
        OpenButton.IsEnabled = enabled;
        ShowButton.IsEnabled = enabled;
        RecoverButton.IsEnabled = enabled;
        RestoreButton.IsEnabled = enabled && (_selectedVersion!.IsFolder ? _canRestoreFolders : _canRestoreFiles);
    }

    private async void VersionsGrid_DoubleTapped(object? sender, TappedEventArgs e) => await OpenSelectedVersionAsync();

    private async void Open_Click(object? sender, RoutedEventArgs e) => await OpenSelectedVersionAsync();

    private Task OpenSelectedVersionAsync()
    {
        if (_selectedVersion == null)
        {
            return Task.CompletedTask;
        }

        try
        {
            Process.Start(new ProcessStartInfo(_selectedVersion.FullPath) { UseShellExecute = true });
            StatusText.Text = "Opened snapshot version";
        }
        catch (Exception ex)
        {
            AppLogger.Error("timeline", ex, $"open failed path=\"{_selectedVersion.FullPath}\"");
            StatusText.Text = ex.Message;
        }
        return Task.CompletedTask;
    }

    private void Show_Click(object? sender, RoutedEventArgs e)
    {
        if (_selectedVersion == null)
        {
            return;
        }

        try
        {
            var arguments = _selectedVersion.IsFolder
                ? $"\"{_selectedVersion.FullPath}\""
                : $"/select,\"{_selectedVersion.FullPath}\"";
            Process.Start(new ProcessStartInfo("explorer.exe", arguments) { UseShellExecute = true });
            StatusText.Text = "Showing snapshot location";
        }
        catch (Exception ex)
        {
            AppLogger.Error("timeline", ex, $"show failed path=\"{_selectedVersion.FullPath}\"");
            StatusText.Text = ex.Message;
        }
    }

    private async void Recover_Click(object? sender, RoutedEventArgs e)
    {
        var storageProvider = GetTopLevel(this)?.StorageProvider;
        if (_selectedVersion == null || storageProvider == null)
        {
            return;
        }

        var folders = await storageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose a folder for the recovered copy",
            AllowMultiple = false,
        });
        var destination = folders.FirstOrDefault() is { } folder
            ? StorageProviderExtensions.TryGetLocalPath(folder)
            : null;
        if (string.IsNullOrWhiteSpace(destination))
        {
            return;
        }

        RecoverButton.IsEnabled = false;
        StatusText.Text = "Recovering a separate copy...";
        try
        {
            var recoveredPath = await _timeline.RecoverCopyAsync(_selectedVersion, destination);
            StatusText.Text = "Recovered copy: " + recoveredPath;
        }
        catch (Exception ex)
        {
            AppLogger.Error("timeline", ex, $"recovery failed source=\"{_selectedVersion.FullPath}\"");
            StatusText.Text = "Recovery failed: " + ex.Message;
        }
        finally
        {
            RecoverButton.IsEnabled = _selectedVersion != null;
        }
    }

    private async void RestoreOriginal_Click(object? sender, RoutedEventArgs e)
    {
        if (_selectedVersion == null ||
            (_selectedVersion.IsFolder && !_canRestoreFolders) ||
            (!_selectedVersion.IsFolder && !_canRestoreFiles))
        {
            return;
        }

        var kind = _selectedVersion.IsFolder ? "folder and its contents" : "file";
        var message = $"Replace the current {kind} at:\n{_sourcePath}\n\nwith the snapshot from {_selectedVersion.SnapshotText}?\n\nQSurfer will keep the current item as a separate backup beside it before restoring the snapshot.";
        var confirmation = new ConfirmationWindow("Restore original location", message, "Replace current version");
        if (await confirmation.ShowDialog<bool?>(this) != true)
        {
            return;
        }

        SetRecoveryActionsEnabled(false);
        StatusText.Text = "Restoring the selected snapshot...";
        try
        {
            var outcome = await _timeline.RestoreOriginalAsync(_selectedVersion, _sourcePath);
            if (_selectedVersion.IsFolder && _oneTimeFolderRestoreArmed)
            {
                _config.Behavior.OneTimeFolderRestore = false;
                ConfigStore.Save(_config);
            }
            StatusText.Text = string.IsNullOrWhiteSpace(outcome.BackupPath)
                ? "Restored to the original location."
                : "Restored to the original location. Previous item saved as " + outcome.BackupPath;
        }
        catch (Exception ex)
        {
            AppLogger.Error("timeline", ex, $"restore failed source=\"{_selectedVersion.FullPath}\" destination=\"{_sourcePath}\"");
            StatusText.Text = "Restore failed: " + ex.Message;
        }
        finally
        {
            SetRecoveryActionsEnabled(_selectedVersion != null);
        }
    }

    private void SetRecoveryActionsEnabled(bool enabled)
    {
        OpenButton.IsEnabled = enabled;
        ShowButton.IsEnabled = enabled;
        RecoverButton.IsEnabled = enabled;
        RestoreButton.IsEnabled = enabled && (_selectedVersion is { IsFolder: false } ? _canRestoreFiles : _canRestoreFolders);
    }

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();
}
