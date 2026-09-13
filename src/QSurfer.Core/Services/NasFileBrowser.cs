using System.Globalization;
using System.IO.Enumeration;
using System.Runtime.InteropServices;
using System.ComponentModel;
using System.Collections.Concurrent;
using System.Diagnostics;
using QSurfer.Core.Models;

namespace QSurfer.Core.Services;

public sealed class NasFileBrowser
{
    public Task<DirectoryReadResult> BrowseAsync(string folderPath, CancellationToken cancellationToken = default)
    {
        return Task.Run(() => Browse(folderPath, cancellationToken), cancellationToken);
    }

    public Task<DirectoryReadResult> BrowseRecycleBinAsync(
        string folderPath,
        Action<IReadOnlyList<BrowserItem>>? batchReceived = null,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(() => BrowseRecycleBin(folderPath, batchReceived, cancellationToken), cancellationToken);
    }

    public Task CreateFolderAsync(string parentPath, string folderName, CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var destination = Path.Combine(NormalizeFolder(parentPath), ValidateChildName(folderName));
            Directory.CreateDirectory(destination);
        }, cancellationToken);
    }

    public Task RenameAsync(BrowserItem item, string newName, CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var parent = Directory.GetParent(item.FullPath)?.FullName
                ?? throw new InvalidOperationException("The item does not have a parent folder.");
            var destination = Path.Combine(parent, ValidateChildName(newName));
            if (item.IsFolder)
            {
                Directory.Move(item.FullPath, destination);
            }
            else
            {
                File.Move(item.FullPath, destination);
            }
        }, cancellationToken);
    }

    public Task DeleteAsync(BrowserItem item, CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!OperatingSystem.IsWindows())
            {
                throw new PlatformNotSupportedException("Deleting browser items is currently supported on Windows only.");
            }

            var operation = new ShellFileOperation
            {
                Function = FileOperationDelete,
                From = item.FullPath + '\0' + '\0',
                // NAS shares own their recycle policy. A normal shell delete lets the NAS
                // route the request through @Recycle when that feature is enabled.
                Flags = FileOperationNoConfirmation | FileOperationNoErrorUi,
            };
            var result = SHFileOperationW(ref operation);
            if (result != 0)
            {
                throw new Win32Exception(result, DescribeDeleteFailure(result));
            }
            if (operation.Aborted)
            {
                throw new OperationCanceledException("The delete operation was canceled.");
            }
        }, cancellationToken);
    }

    public Task EmptyLocalRecycleBinAsync(string folderPath, CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (OperatingSystem.IsWindows())
            {
                var driveRoot = Path.GetPathRoot(NormalizeFolder(folderPath));
                if (string.IsNullOrWhiteSpace(driveRoot))
                {
                    throw new InvalidOperationException("Could not determine the drive for this Recycle Bin.");
                }

                var result = SHEmptyRecycleBinW(IntPtr.Zero, driveRoot, EmptyRecycleNoConfirmation | EmptyRecycleNoProgressUi | EmptyRecycleNoSound);
                if (result != 0)
                {
                    throw new Win32Exception(result, "Windows could not empty this Recycle Bin.");
                }
                return;
            }

            var trashRoot = NormalizeFolder(folderPath);
            if (!trashRoot.TrimEnd('/', '\\').EndsWith("Trash", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Only the local desktop Trash folder can be emptied.");
            }

            foreach (var child in new[] { "files", "info" })
            {
                var directory = Path.Combine(trashRoot, child);
                if (!Directory.Exists(directory)) continue;
                foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
                {
                    if (Directory.Exists(entry)) Directory.Delete(entry, recursive: true);
                    else File.Delete(entry);
                }
            }
        }, cancellationToken);
    }

    private static string DescribeDeleteFailure(int errorCode) => errorCode switch
    {
        32 => "The item is open or being used by another program. Close it and try again.",
        5 => "You do not have permission to delete the selected item.",
        2 or 3 => "The item is no longer available at this location.",
        _ => "Windows could not delete the selected item.",
    };

    public Task<int> CopyAsync(IEnumerable<BrowserItem> items, string destinationFolder, bool move, CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            var destinationRoot = NormalizeFolder(destinationFolder);
            if (!Directory.Exists(destinationRoot))
            {
                throw new DirectoryNotFoundException("The destination folder is no longer available.");
            }

            var copied = 0;
            foreach (var item in items.DistinctBy(candidate => candidate.FullPath, StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var source = Path.GetFullPath(item.FullPath);
                var destination = FindAvailableDestination(destinationRoot, item.Name, item.IsFolder);
                if (item.IsFolder && destination.StartsWith(source.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("A folder cannot be copied into itself.");
                }

                if (item.IsFolder)
                {
                    CopyDirectory(source, destination, cancellationToken);
                    if (move)
                    {
                        Directory.Delete(source, recursive: true);
                    }
                }
                else
                {
                    File.Copy(source, destination);
                    if (move)
                    {
                        File.Delete(source);
                    }
                }
                copied++;
            }
            return copied;
        }, cancellationToken);
    }

    public Task<RecycleRestoreOutcome> RestoreFromRecycleAsync(
        IEnumerable<BrowserItem> items,
        bool replaceExistingFiles,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            var restoreItems = GetRecycleRestoreTargets(items);
            if (restoreItems.Count == 0)
            {
                throw new InvalidOperationException("Select an item inside a NAS Recycle Bin to restore it.");
            }

            foreach (var restore in restoreItems)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var destinationParent = Path.GetDirectoryName(restore.DestinationPath);
                if (string.IsNullOrWhiteSpace(destinationParent))
                {
                    throw new IOException($"QSurfer could not determine the original location for {restore.Item.Name}.");
                }

                Directory.CreateDirectory(destinationParent);
                if (restore.Item.IsFolder)
                {
                    if (File.Exists(restore.DestinationPath) || Directory.Exists(restore.DestinationPath))
                    {
                        throw new IOException($"A live item already exists at {restore.DestinationPath}. Folder restores do not replace existing items.");
                    }

                    Directory.Move(restore.Item.FullPath, restore.DestinationPath);
                    continue;
                }

                if (Directory.Exists(restore.DestinationPath))
                {
                    throw new IOException($"The original location is now a folder: {restore.DestinationPath}");
                }

                if (File.Exists(restore.DestinationPath))
                {
                    if (!replaceExistingFiles)
                    {
                        throw new IOException($"A live file already exists at {restore.DestinationPath}.");
                    }

                    var backupPath = FindAvailableDestination(destinationParent, SafetyCopyFileName(restore.Item.Name), isFolder: false);
                    File.Move(restore.DestinationPath, backupPath);
                    MarkReadOnlyAndHidden(backupPath);
                }

                File.Move(restore.Item.FullPath, restore.DestinationPath);
            }

            return new RecycleRestoreOutcome(restoreItems.Count);
        }, cancellationToken);
    }

    public static IReadOnlyList<RecycleRestoreTarget> GetRecycleRestoreTargets(IEnumerable<BrowserItem> items) => items
        .DistinctBy(item => item.FullPath, StringComparer.OrdinalIgnoreCase)
        .Select(item => TryGetRecycleRestoreTarget(item, out var target) ? new RecycleRestoreTarget(item, target) : null)
        .Where(target => target != null)
        .Cast<RecycleRestoreTarget>()
        .ToList();

    public Task<string> CreateShortcutAsync(BrowserItem item, string destinationFolder, CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!OperatingSystem.IsWindows())
            {
                throw new PlatformNotSupportedException("Creating Windows shortcut files is only available on Windows.");
            }
            var destinationRoot = NormalizeFolder(destinationFolder);
            var name = Path.GetFileNameWithoutExtension(item.Name);
            var shortcutPath = FindAvailableDestination(destinationRoot, name + " - Shortcut.lnk", isFolder: false);
            dynamic? shell = null;
            dynamic? shortcut = null;
            try
            {
                var shellType = Type.GetTypeFromProgID("WScript.Shell")
                    ?? throw new InvalidOperationException("Windows Script Host is not available to create shortcuts.");
                shell = Activator.CreateInstance(shellType);
                shortcut = shell!.CreateShortcut(shortcutPath);
                shortcut.TargetPath = item.FullPath;
                shortcut.WorkingDirectory = item.IsFolder ? item.FullPath : Path.GetDirectoryName(item.FullPath) ?? destinationRoot;
                shortcut.Save();
                return shortcutPath;
            }
            finally
            {
                if (shortcut != null && Marshal.IsComObject(shortcut)) Marshal.FinalReleaseComObject(shortcut);
                if (shell != null && Marshal.IsComObject(shell)) Marshal.FinalReleaseComObject(shell);
            }
        }, cancellationToken);
    }

    public void ShowProperties(BrowserItem item)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The Windows Properties sheet is only available on Windows.");
        }

        if (!SHObjectProperties(IntPtr.Zero, ShopFilePath, item.FullPath, null))
        {
            var error = Marshal.GetLastWin32Error();
            throw new Win32Exception(error, error == 0
                ? "Windows could not open Properties for the selected item."
                : "Windows could not open Properties for the selected item.");
        }
    }

    public static string? GetParentFolder(string folderPath)
    {
        var candidate = (folderPath ?? "").Trim();
        if (!Path.IsPathFullyQualified(candidate))
        {
            return null;
        }

        try
        {
            var normalized = NormalizeFolder(candidate);
            var parent = Directory.GetParent(normalized)?.FullName;
            return IsBrowsableFolder(parent) ? parent : null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static DirectoryReadResult Browse(string folderPath, CancellationToken cancellationToken)
    {
        if (IsServerRoot(folderPath))
        {
            if (!OperatingSystem.IsWindows())
            {
                throw new PlatformNotSupportedException("Browse the NAS through a mounted SMB folder on Linux; Windows UNC server roots are not available.");
            }
            return BrowseServerShares(NormalizeServerRoot(folderPath), cancellationToken);
        }

        if (OperatingSystem.IsWindows() && IsWindowsRecycleBinRoot(folderPath))
        {
            return BrowseWindowsRecycleBin(NormalizeFolder(folderPath), cancellationToken);
        }

        var normalized = NormalizeFolder(folderPath);
        if (!Directory.Exists(normalized))
        {
            throw new DirectoryNotFoundException($"The network folder could not be found: {normalized}");
        }

        var items = new List<BrowserItem>();
        var skipped = 0;
        try
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(normalized))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    items.Add(CreateItem(entry));
                }
                catch (UnauthorizedAccessException)
                {
                    skipped++;
                }
                catch (IOException)
                {
                    skipped++;
                }
            }
        }
        catch (UnauthorizedAccessException)
        {
            throw new UnauthorizedAccessException("You do not have permission to view this network folder.");
        }

        var ordered = items
            .OrderByDescending(item => item.IsFolder)
            .ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        return new DirectoryReadResult(normalized, ordered, skipped);
    }

    private static bool IsWindowsRecycleBinRoot(string folderPath) =>
        Path.GetFileName((folderPath ?? "").TrimEnd('\\', '/'))
            .Equals("$Recycle.Bin", StringComparison.OrdinalIgnoreCase);

    private static DirectoryReadResult BrowseWindowsRecycleBin(string folderPath, CancellationToken cancellationToken)
    {
        dynamic? shell = null;
        dynamic? recycleBin = null;
        try
        {
            var shellType = Type.GetTypeFromProgID("Shell.Application")
                ?? throw new InvalidOperationException("The Windows Shell is not available.");
            shell = Activator.CreateInstance(shellType);
            recycleBin = shell!.NameSpace(10); // CSIDL_BITBUCKET
            if (recycleBin == null)
            {
                throw new IOException("Windows could not open the Recycle Bin.");
            }

            var items = new List<BrowserItem>();
            foreach (dynamic shellItem in recycleBin.Items())
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var path = Convert.ToString(shellItem.Path) ?? "";
                    var name = Convert.ToString(shellItem.Name);
                    if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(name))
                    {
                        continue;
                    }

                    var modified = DateTime.TryParse(Convert.ToString(shellItem.ModifyDate), CultureInfo.CurrentCulture, DateTimeStyles.None, out DateTime value)
                        ? value
                        : DateTime.MinValue;
                    items.Add(new BrowserItem(name, path, Convert.ToBoolean(shellItem.IsFolder), 0, modified)
                    {
                        Deleted = modified == DateTime.MinValue ? null : modified,
                    });
                }
                catch (Exception ex) when (ex is COMException or InvalidCastException or FormatException)
                {
                    AppLogger.Warn("browse", $"could not read a Windows Recycle Bin item: {ex.Message}");
                }
            }

            return new DirectoryReadResult(
                folderPath,
                items.OrderByDescending(item => item.DisplayDate).ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase).ToList(),
                0);
        }
        finally
        {
            if (recycleBin != null && Marshal.IsComObject(recycleBin)) Marshal.FinalReleaseComObject(recycleBin);
            if (shell != null && Marshal.IsComObject(shell)) Marshal.FinalReleaseComObject(shell);
        }
    }

    private static DirectoryReadResult BrowseRecycleBin(
        string folderPath,
        Action<IReadOnlyList<BrowserItem>>? batchReceived,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var normalized = NormalizeFolder(folderPath);
        if (!Directory.Exists(normalized))
        {
            throw new DirectoryNotFoundException($"The network folder could not be found: {normalized}");
        }

        var items = new List<BrowserItem>();
        var itemsGate = new object();
        var batch = new List<BrowserItem>(25);
        var sentInitialBatch = false;
        var skipped = 0;

        void Emit(BrowserItem item)
        {
            IReadOnlyList<BrowserItem>? completedBatch = null;
            lock (itemsGate)
            {
                items.Add(item);
                batch.Add(item);
                var batchThreshold = sentInitialBatch ? 25 : 5;
                if (batch.Count == batchThreshold)
                {
                    completedBatch = batch.ToArray();
                    batch.Clear();
                    sentInitialBatch = true;
                }
            }
            if (completedBatch != null)
            {
                batchReceived?.Invoke(completedBatch);
            }
        }

        void ScanBranch(string current)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                foreach (var entry in EnumerateRecycleEntries(current))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        if (entry.IsFolder)
                        {
                            ScanBranch(entry.FullPath);
                        }
                        else
                        {
                            var item = new BrowserItem(entry.Name, entry.FullPath, false, entry.Size, entry.Modified)
                            {
                                Deleted = entry.Created,
                            };
                            item = item with
                            {
                                DisplayPath = TryGetRecycleRestoreTarget(item, out var destinationPath)
                                    ? Path.GetDirectoryName(destinationPath) ?? destinationPath
                                    : item.DisplayPath,
                            };
                            Emit(item);
                        }
                    }
                    catch (UnauthorizedAccessException)
                    {
                        Interlocked.Increment(ref skipped);
                    }
                    catch (IOException)
                    {
                        Interlocked.Increment(ref skipped);
                    }
                }
            }
            catch (UnauthorizedAccessException)
            {
                Interlocked.Increment(ref skipped);
            }
            catch (IOException)
            {
                Interlocked.Increment(ref skipped);
            }
        }

        // The NAS recycle hierarchy mirrors the original shares. The root's
        // immediate children are independent branches, so scan them concurrently
        // rather than serializing every SMB directory metadata round trip.
        var rootFolders = new List<RecycleScanEntry>();
        foreach (var entry in EnumerateRecycleEntries(normalized))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.IsFolder)
            {
                rootFolders.Add(entry);
                continue;
            }

            var item = new BrowserItem(entry.Name, entry.FullPath, false, entry.Size, entry.Modified)
            {
                Deleted = entry.Created,
            };
            Emit(item with
            {
                DisplayPath = TryGetRecycleRestoreTarget(item, out var destinationPath)
                    ? Path.GetDirectoryName(destinationPath) ?? destinationPath
                    : item.DisplayPath,
            });
        }

        var branches = new ConcurrentQueue<RecycleScanEntry>(
            rootFolders.OrderByDescending(folder => folder.Modified));
        var workerCount = Math.Min(8, branches.Count);
        var workers = new Task[workerCount];
        for (var index = 0; index < workers.Length; index++)
        {
            // LongRunning asks the scheduler to provision this bounded worker
            // immediately instead of ramping up the shared thread pool.
            workers[index] = Task.Factory.StartNew(
                () =>
                {
                    while (branches.TryDequeue(out var branch))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        ScanBranch(branch.FullPath);
                    }
                },
                cancellationToken,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
        }
        Task.WaitAll(workers, cancellationToken);

        IReadOnlyList<BrowserItem>? finalBatch = null;
        lock (itemsGate)
        {
            if (batch.Count > 0)
            {
                finalBatch = batch.ToArray();
            }
        }
        if (finalBatch != null)
        {
            batchReceived?.Invoke(finalBatch);
        }

        List<BrowserItem> ordered;
        lock (itemsGate)
        {
            ordered = items
                .OrderByDescending(item => item.DisplayDate)
                .ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }
        AppLogger.Info("browse", $"recycle scan folder=\"{normalized}\" files={ordered.Count} skipped={skipped} elapsed={stopwatch.ElapsedMilliseconds}ms");
        return new DirectoryReadResult(normalized, ordered, skipped);
    }

    private static IEnumerable<RecycleScanEntry> EnumerateRecycleEntries(string folderPath)
    {
        var options = new EnumerationOptions
        {
            AttributesToSkip = 0,
            IgnoreInaccessible = false,
            RecurseSubdirectories = false,
            ReturnSpecialDirectories = false,
        };
        return new FileSystemEnumerable<RecycleScanEntry>(
            folderPath,
            static (ref FileSystemEntry entry) => new RecycleScanEntry(
                entry.FileName.ToString(),
                entry.ToFullPath(),
                entry.Attributes.HasFlag(FileAttributes.Directory),
                entry.Length,
                entry.CreationTimeUtc.LocalDateTime,
                entry.LastWriteTimeUtc.LocalDateTime),
            options);
    }

    private static void CopyDirectory(string source, string destination, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(destination);
        foreach (var directory in Directory.EnumerateDirectories(source))
        {
            cancellationToken.ThrowIfCancellationRequested();
            CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)), cancellationToken);
        }
        foreach (var file in Directory.EnumerateFiles(source))
        {
            cancellationToken.ThrowIfCancellationRequested();
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
        }
    }

    private static string FindAvailableDestination(string parent, string name, bool isFolder)
    {
        var baseName = isFolder ? name : Path.GetFileNameWithoutExtension(name);
        var extension = isFolder ? "" : Path.GetExtension(name);
        var candidate = Path.Combine(parent, name);
        if (!File.Exists(candidate) && !Directory.Exists(candidate))
        {
            return candidate;
        }
        for (var number = 1; ; number++)
        {
            var suffix = number == 1 ? " - Copy" : $" - Copy ({number})";
            candidate = Path.Combine(parent, baseName + suffix + extension);
            if (!File.Exists(candidate) && !Directory.Exists(candidate))
            {
                return candidate;
            }
        }
    }

    private static bool TryGetRecycleRestoreTarget(BrowserItem item, out string destinationPath)
    {
        var path = item.FullPath.Replace('/', '\\').TrimEnd('\\');
        var parts = path.Trim('\\').Split('\\', StringSplitOptions.RemoveEmptyEntries);
        var recycleIndex = Array.FindIndex(parts, part =>
            part.Equals("@Recycle", StringComparison.OrdinalIgnoreCase) ||
            part.Equals("@RecycleBin", StringComparison.OrdinalIgnoreCase) ||
            part.Equals("#recycle", StringComparison.OrdinalIgnoreCase));
        if (recycleIndex < 1 || recycleIndex >= parts.Length - 1)
        {
            destinationPath = "";
            return false;
        }

        var originalParts = parts[(recycleIndex + 1)..];
        if (originalParts.Any(part => part is "." or ".."))
        {
            destinationPath = "";
            return false;
        }

        var originalRoot = path.StartsWith(@"\\", StringComparison.Ordinal)
            ? @"\\" + string.Join("\\", parts[..recycleIndex])
            : Path.GetPathRoot(path) ?? "";
        if (string.IsNullOrWhiteSpace(originalRoot))
        {
            destinationPath = "";
            return false;
        }

        destinationPath = Path.Combine(originalRoot, Path.Combine(originalParts));
        return true;
    }

    private static string SafetyCopyFileName(string fileName) =>
        Path.GetFileNameWithoutExtension(fileName) + ".qsurfer" + Path.GetExtension(fileName);

    private static void MarkReadOnlyAndHidden(string path)
    {
        try
        {
            File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.Hidden | FileAttributes.ReadOnly);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLogger.Warn("browse", $"could not mark recycle safety copy hidden and read-only path=\"{path}\" reason=\"{ex.Message}\"");
        }
    }

    private static BrowserItem CreateItem(string fullPath)
    {
        var attributes = File.GetAttributes(fullPath);
        var isFolder = attributes.HasFlag(FileAttributes.Directory);
        var name = Path.GetFileName(fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (isFolder)
        {
            var directory = new DirectoryInfo(fullPath);
            return new BrowserItem(name, fullPath, true, 0, ReadLastWriteTime(directory));
        }

        var file = new FileInfo(fullPath);
        return new BrowserItem(name, fullPath, false, file.Length, ReadLastWriteTime(file));
    }

    private static DateTime ReadLastWriteTime(FileSystemInfo item)
    {
        try
        {
            var timestamp = item.LastWriteTime;
            return timestamp is { Year: >= 1601 and <= 9999 } ? timestamp : DateTime.MinValue;
        }
        catch (ArgumentOutOfRangeException)
        {
            return DateTime.MinValue;
        }
        catch (IOException)
        {
            return DateTime.MinValue;
        }
    }

    private static string NormalizeFolder(string folderPath)
    {
        var raw = (folderPath ?? "").Trim();
        var normalized = IsVolumeRoot(raw)
            ? raw.Replace('/', Path.DirectorySeparatorChar)
            : raw.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw new ArgumentException("Enter a network folder path.", nameof(folderPath));
        }
        if (!Path.IsPathFullyQualified(normalized) && !IsServerRoot(normalized))
        {
            throw new ArgumentException(@"Enter a full path such as \\server\share.", nameof(folderPath));
        }

        return normalized;
    }

    private static bool IsVolumeRoot(string path) =>
        path.Length == 3 && char.IsLetter(path[0]) && path[1] == ':' &&
        (path[2] == Path.DirectorySeparatorChar || path[2] == Path.AltDirectorySeparatorChar);

    private static string ValidateChildName(string name)
    {
        var normalized = (name ?? "").Trim();
        if (string.IsNullOrWhiteSpace(normalized) || normalized.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            normalized is "." or "..")
        {
            throw new ArgumentException("Enter a valid file or folder name.", nameof(name));
        }
        return normalized;
    }

    private static bool IsBrowsableFolder(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        return Path.IsPathFullyQualified(path) || IsServerRoot(path);
    }

    private static bool IsServerRoot(string? path)
    {
        var value = (path ?? "").Trim().TrimEnd('\\', '/');
        if (!value.StartsWith(@"\\", StringComparison.Ordinal) || value.Length <= 2)
        {
            return false;
        }

        var server = value[2..];
        return server.IndexOfAny(['\\', '/', '?', '#']) < 0 &&
               server.IndexOfAny(Path.GetInvalidPathChars()) < 0;
    }

    private static string NormalizeServerRoot(string path) => path.Trim().TrimEnd('\\', '/');

    private static DirectoryReadResult BrowseServerShares(string serverRoot, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Windows UNC share discovery is not available on this platform.");
        }
        cancellationToken.ThrowIfCancellationRequested();
        var status = NetShareEnum(serverRoot, 1, out var buffer, -1, out var read, out _, IntPtr.Zero);
        if (status is not 0 and not 234)
        {
            throw new IOException($"Windows could not list shared folders on {serverRoot} (error {status}).");
        }

        try
        {
            var itemSize = Marshal.SizeOf<ShareInfo1>();
            var shares = new List<BrowserItem>();
            for (var index = 0; index < read; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var entry = Marshal.PtrToStructure<ShareInfo1>(IntPtr.Add(buffer, index * itemSize));
                if (string.IsNullOrWhiteSpace(entry.Name) || entry.Name.EndsWith('$') || (entry.Type & 0xff) != 0)
                {
                    continue;
                }

                shares.Add(new BrowserItem(entry.Name, serverRoot.TrimEnd('\\') + "\\" + entry.Name, true, 0, DateTime.MinValue));
            }

            return new DirectoryReadResult(
                serverRoot,
                shares.OrderBy(share => share.Name, StringComparer.CurrentCultureIgnoreCase).ToList(),
                0);
        }
        finally
        {
            if (buffer != IntPtr.Zero)
            {
                NetApiBufferFree(buffer);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShareInfo1
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string Name;
        public int Type;
        [MarshalAs(UnmanagedType.LPWStr)] public string Remark;
    }

    [DllImport("Netapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int NetShareEnum(
        string serverName,
        int level,
        out IntPtr buffer,
        int preferredMaximumLength,
        out int entriesRead,
        out int totalEntries,
        IntPtr resumeHandle);

    [DllImport("Netapi32.dll")]
    private static extern int NetApiBufferFree(IntPtr buffer);

    private const uint ShopFilePath = 0x00000002;
    private const int FileOperationDelete = 3;
    private const ushort FileOperationNoConfirmation = 0x0010;
    private const ushort FileOperationNoErrorUi = 0x0400;
    private const uint EmptyRecycleNoConfirmation = 0x00000001;
    private const uint EmptyRecycleNoProgressUi = 0x00000002;
    private const uint EmptyRecycleNoSound = 0x00000004;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShellFileOperation
    {
        public IntPtr Window;
        public int Function;
        [MarshalAs(UnmanagedType.LPWStr)] public string From;
        [MarshalAs(UnmanagedType.LPWStr)] public string? To;
        public ushort Flags;
        [MarshalAs(UnmanagedType.Bool)] public bool Aborted;
        public IntPtr NameMappings;
        public string? ProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperationW(ref ShellFileOperation operation);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHEmptyRecycleBinW(IntPtr hwnd, string? rootPath, uint flags);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SHObjectProperties(IntPtr hwnd, uint objectType, string objectName, string? propertyName);
}

public sealed record DirectoryReadResult(string FolderPath, IReadOnlyList<BrowserItem> Items, int SkippedCount);

internal readonly record struct RecycleScanEntry(
    string Name,
    string FullPath,
    bool IsFolder,
    long Size,
    DateTime Created,
    DateTime Modified);

public sealed record RecycleRestoreTarget(BrowserItem Item, string DestinationPath);
public sealed record RecycleRestoreOutcome(int RestoredCount);

public sealed record BrowserItem(string Name, string FullPath, bool IsFolder, long Size, DateTime Modified, string DisplayPath = "") : INotifyPropertyChanged
{
    private object? _iconSource;

    public DateTime? Deleted { get; init; }
    public bool IsDrive { get; init; }
    public double DriveUsedFraction { get; init; }
    public string DriveSpaceText { get; init; } = "";
    public DateTime? DisplayDate => Modified == DateTime.MinValue ? null : Deleted ?? Modified;
    public bool IsRecycleBinFolder => IsFolder &&
        (Name.Equals("@Recycle", StringComparison.OrdinalIgnoreCase) ||
         Name.Equals("@RecycleBin", StringComparison.OrdinalIgnoreCase) ||
         Name.Equals("#recycle", StringComparison.OrdinalIgnoreCase) ||
         Name.Equals("$Recycle.Bin", StringComparison.OrdinalIgnoreCase));

    public string Glyph => IsDrive ? "\U0001F4BD" : IsRecycleBinFolder ? "\u267B" : IsFolder ? "\U0001F4C1" : SearchResult.FileGlyph(Path.GetExtension(Name));
    public object? IconSource
    {
        get => _iconSource;
        set
        {
            if (ReferenceEquals(_iconSource, value))
            {
                return;
            }

            _iconSource = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IconSource)));
        }
    }
    public string Kind => IsDrive ? "Drive" : IsFolder ? "Folder" : string.IsNullOrWhiteSpace(Path.GetExtension(Name)) ? "File" : Path.GetExtension(Name).TrimStart('.').ToUpperInvariant() + " File";
    public string SizeText => IsFolder ? "" : Size >= 1_048_576
        ? string.Format(CultureInfo.CurrentCulture, "{0:N1} MB", Size / 1_048_576d)
        : string.Format(CultureInfo.CurrentCulture, "{0:N0} KB", Math.Max(1, Size / 1024d));

    public event PropertyChangedEventHandler? PropertyChanged;
}
