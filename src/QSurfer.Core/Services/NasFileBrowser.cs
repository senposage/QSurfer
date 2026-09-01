using System.Globalization;
using System.IO.Enumeration;
using System.Runtime.InteropServices;
using System.ComponentModel;

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
                // NAS shares own their recycle policy. A normal shell delete lets QNAP
                // route the request through @Recycle when that feature is enabled.
                Flags = FileOperationNoConfirmation | FileOperationNoErrorUi,
            };
            var result = SHFileOperationW(ref operation);
            if (result != 0)
            {
                throw new Win32Exception(result, "Windows could not delete the selected item.");
            }
            if (operation.Aborted)
            {
                throw new OperationCanceledException("The delete operation was canceled.");
            }
        }, cancellationToken);
    }

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
                throw new InvalidOperationException("Select an item inside a QNAP Recycle Bin to restore it.");
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
            return BrowseServerShares(NormalizeServerRoot(folderPath), cancellationToken);
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

    private static DirectoryReadResult BrowseRecycleBin(
        string folderPath,
        Action<IReadOnlyList<BrowserItem>>? batchReceived,
        CancellationToken cancellationToken)
    {
        var normalized = NormalizeFolder(folderPath);
        if (!Directory.Exists(normalized))
        {
            throw new DirectoryNotFoundException($"The network folder could not be found: {normalized}");
        }

        var items = new List<BrowserItem>();
        var batch = new List<BrowserItem>(25);
        var pendingFolders = new Stack<string>();
        var skipped = 0;
        pendingFolders.Push(normalized);
        while (pendingFolders.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = pendingFolders.Pop();
            try
            {
                foreach (var entry in EnumerateRecycleEntries(current))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        if (entry.IsFolder)
                        {
                            pendingFolders.Push(entry.FullPath);
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
                            items.Add(item);
                            batch.Add(item);
                            if (batch.Count == 25)
                            {
                                batchReceived?.Invoke(batch.ToArray());
                                batch.Clear();
                            }
                        }
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
                skipped++;
            }
            catch (IOException)
            {
                skipped++;
            }
        }

        if (batch.Count > 0)
        {
            batchReceived?.Invoke(batch.ToArray());
        }

        var ordered = items
            .OrderByDescending(item => item.DisplayDate)
            .ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
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
    public DateTime DisplayDate => Deleted ?? Modified;

    public string Glyph => IsFolder ? "\uE8B7" : "\uE8A5";
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
    public string Kind => IsFolder ? "Folder" : string.IsNullOrWhiteSpace(Path.GetExtension(Name)) ? "File" : Path.GetExtension(Name).TrimStart('.').ToUpperInvariant() + " File";
    public string SizeText => IsFolder ? "" : Size >= 1_048_576
        ? string.Format(CultureInfo.CurrentCulture, "{0:N1} MB", Size / 1_048_576d)
        : string.Format(CultureInfo.CurrentCulture, "{0:N0} KB", Math.Max(1, Size / 1024d));

    public event PropertyChangedEventHandler? PropertyChanged;
}
