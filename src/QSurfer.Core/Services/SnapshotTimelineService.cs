using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using QSurfer.Core.Models;

namespace QSurfer.Core.Services;

/// <summary>
/// Reads QNAP's exposed @Recently-Snapshot folders through the user's normal SMB access.
/// Snapshot folders are treated as read-only sources; recovery makes a separate copy or explicitly restores a selected version.
/// </summary>
public sealed class SnapshotTimelineService(AppConfig config)
{
    private const string SnapshotFolderName = "@Recently-Snapshot";
    private static readonly Regex SnapshotNamePattern = new(
        @"^GMT(?<offset>[+-]\d{2})_(?<date>\d{4}-\d{2}-\d{2})_(?<time>\d{4})$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public Task<SnapshotTimeline> LoadAsync(string path, bool isFolder, CancellationToken cancellationToken = default) =>
        Task.Run(() => Load(path, isFolder, cancellationToken), cancellationToken);

    public Task<string> RecoverCopyAsync(SnapshotVersion version, string destinationFolder, CancellationToken cancellationToken = default) =>
        Task.Run(() => RecoverCopy(version, destinationFolder, cancellationToken), cancellationToken);

    public Task<SnapshotRestoreOutcome> RestoreOriginalAsync(SnapshotVersion version, string destinationPath, CancellationToken cancellationToken = default) =>
        Task.Run(() => RestoreOriginal(version, destinationPath, cancellationToken), cancellationToken);

    private SnapshotTimeline Load(string path, bool isFolder, CancellationToken cancellationToken)
    {
        var candidates = SnapshotCandidates(path).ToList();
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Directory.Exists(candidate.SnapshotRoot))
            {
                continue;
            }

            var versions = new List<SnapshotVersion>();
            foreach (var snapshotDirectory in Directory.EnumerateDirectories(candidate.SnapshotRoot))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var snapshotName = Path.GetFileName(snapshotDirectory);
                if (!TryParseSnapshotTimestamp(snapshotName, out var snapshotTime))
                {
                    continue;
                }

                var snapshotPath = Combine(snapshotDirectory, candidate.RelativePath);
                if (isFolder ? !Directory.Exists(snapshotPath) : !File.Exists(snapshotPath))
                {
                    continue;
                }

                var metadata = ReadMetadata(snapshotPath, isFolder);
                versions.Add(new SnapshotVersion(
                    snapshotName,
                    snapshotTime,
                    snapshotPath,
                    isFolder,
                    metadata.Modified,
                    metadata.Size));
            }

            var ordered = versions
                .OrderByDescending(version => version.SnapshotTime)
                .ThenByDescending(version => version.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            AppLogger.Info("timeline", $"source=\"{path}\" root=\"{candidate.SnapshotRoot}\" versions={ordered.Count}");
            return new SnapshotTimeline(path, candidate.SnapshotRoot, ordered);
        }

        AppLogger.Info("timeline", $"source=\"{path}\" snapshot root unavailable candidates={candidates.Count}");
        return new SnapshotTimeline(path, "", []);
    }

    private string RecoverCopy(SnapshotVersion version, string destinationFolder, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(destinationFolder) || !Directory.Exists(destinationFolder))
        {
            throw new DirectoryNotFoundException("Choose an available recovery folder.");
        }

        var sourceName = Path.GetFileName(version.FullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var recoveredName = RecoveryName(sourceName, version.IsFolder, version.SnapshotTime);
        var destination = FindAvailableDestination(destinationFolder, recoveredName, version.IsFolder);
        if (version.IsFolder)
        {
            CopyDirectory(version.FullPath, destination, cancellationToken);
        }
        else
        {
            File.Copy(version.FullPath, destination);
        }

        AppLogger.Info("timeline", $"recovered snapshot=\"{version.Name}\" source=\"{version.FullPath}\" destination=\"{destination}\"");
        return destination;
    }

    private SnapshotRestoreOutcome RestoreOriginal(SnapshotVersion version, string destinationPath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var destination = Normalize(destinationPath);
        var parent = Path.GetDirectoryName(destination);
        if (string.IsNullOrWhiteSpace(parent) || !Directory.Exists(parent))
        {
            throw new DirectoryNotFoundException("The original item's folder is no longer available.");
        }

        if (version.IsFolder && File.Exists(destination))
        {
            throw new IOException("The original location is now a file, so the folder cannot be restored there.");
        }
        if (!version.IsFolder && Directory.Exists(destination))
        {
            throw new IOException("The original location is now a folder, so the file cannot be restored there.");
        }

        var itemName = Path.GetFileName(destination.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var backup = BackupName(itemName, version.IsFolder);
        var backupPath = FindAvailableDestination(parent, backup, version.IsFolder);

        if (!version.IsFolder)
        {
            var hadExistingFile = File.Exists(destination);
            if (hadExistingFile)
            {
                File.Copy(destination, backupPath);
                MarkSafetyCopy(backupPath, isFolder: false);
            }

            File.Copy(version.FullPath, destination, overwrite: true);
            AppLogger.Info("timeline", $"restored snapshot=\"{version.Name}\" source=\"{version.FullPath}\" destination=\"{destination}\" backup=\"{(hadExistingFile ? backupPath : "")}\"");
            return new SnapshotRestoreOutcome(destination, hadExistingFile ? backupPath : null);
        }

        var staging = Path.Combine(parent, "." + itemName + ".QSurfer-restore-" + Guid.NewGuid().ToString("N"));
        var movedExistingFolder = false;
        try
        {
            CopyDirectory(version.FullPath, staging, cancellationToken);
            if (Directory.Exists(destination))
            {
                Directory.Move(destination, backupPath);
                MarkSafetyCopy(backupPath, isFolder: true);
                movedExistingFolder = true;
            }

            Directory.Move(staging, destination);
            AppLogger.Info("timeline", $"restored folder snapshot=\"{version.Name}\" source=\"{version.FullPath}\" destination=\"{destination}\" backup=\"{(movedExistingFolder ? backupPath : "")}\"");
            return new SnapshotRestoreOutcome(destination, movedExistingFolder ? backupPath : null);
        }
        catch
        {
            if (movedExistingFolder && !Directory.Exists(destination) && Directory.Exists(backupPath))
            {
                Directory.Move(backupPath, destination);
            }
            throw;
        }
        finally
        {
            if (Directory.Exists(staging))
            {
                Directory.Delete(staging, recursive: true);
            }
        }
    }

    private IEnumerable<SnapshotCandidate> SnapshotCandidates(string inputPath)
    {
        var path = Normalize(inputPath);
        if (string.IsNullOrWhiteSpace(path))
        {
            yield break;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in SnapshotCandidatesFor(path))
        {
            if (seen.Add(candidate.SnapshotRoot))
            {
                yield return candidate;
            }
        }
    }

    private IEnumerable<SnapshotCandidate> SnapshotCandidatesFor(string path)
    {
        if (TryGetUncShare(path, out var directShareRoot, out var directRelative))
        {
            foreach (var candidate in CandidatesForShare(directShareRoot, directRelative))
            {
                yield return candidate;
            }
            yield break;
        }

        var driveRoot = Path.GetPathRoot(path);
        if (!string.IsNullOrWhiteSpace(driveRoot))
        {
            var relativeOnDrive = path[driveRoot.Length..].TrimStart('\\');
            yield return new SnapshotCandidate(Combine(driveRoot, SnapshotFolderName), relativeOnDrive);
        }

        foreach (var drive in ReadMappedDrives())
        {
            if (!PathStartsWith(path, drive.LocalRoot))
            {
                continue;
            }

            var remainder = path[drive.LocalRoot.TrimEnd('\\').Length..].TrimStart('\\');
            var uncPath = Combine(drive.Remote, remainder);
            if (!TryGetUncShare(uncPath, out var shareRoot, out var relative))
            {
                continue;
            }

            foreach (var candidate in CandidatesForShare(shareRoot, relative))
            {
                yield return candidate;
            }
        }
    }

    private IEnumerable<SnapshotCandidate> CandidatesForShare(string shareRoot, string relativePath)
    {
        // A drive mapped directly to the share is the friendliest route for Open and recovery.
        foreach (var drive in ReadMappedDrives()
                     .Where(drive => Normalize(drive.Remote).TrimEnd('\\').Equals(shareRoot, StringComparison.OrdinalIgnoreCase)))
        {
            yield return new SnapshotCandidate(Combine(drive.LocalRoot, SnapshotFolderName), relativePath);
        }

        foreach (var mapping in config.PathMappings)
        {
            var mappedRoot = Normalize(mapping.MappedRoot).TrimEnd('\\');
            var source = Normalize(mapping.ShareRoot).Trim('\\');
            if (string.IsNullOrWhiteSpace(mappedRoot) || string.IsNullOrWhiteSpace(source))
            {
                continue;
            }

            var shareName = shareRoot.Trim('\\').Split('\\', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
            if (!string.IsNullOrWhiteSpace(shareName) &&
                (source.Equals(shareName, StringComparison.OrdinalIgnoreCase) ||
                 source.Equals("Shared\\" + shareName, StringComparison.OrdinalIgnoreCase)))
            {
                yield return new SnapshotCandidate(Combine(mappedRoot, SnapshotFolderName), relativePath);
            }
        }

        yield return new SnapshotCandidate(Combine(shareRoot, SnapshotFolderName), relativePath);
    }

    private static bool TryGetUncShare(string path, out string shareRoot, out string relativePath)
    {
        var parts = Normalize(path).Trim('\\').Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (!Normalize(path).StartsWith(@"\\", StringComparison.Ordinal) || parts.Length < 2)
        {
            shareRoot = "";
            relativePath = "";
            return false;
        }

        shareRoot = @"\\" + parts[0] + "\\" + parts[1];
        relativePath = string.Join("\\", parts.Skip(2));
        return true;
    }

    private static bool TryParseSnapshotTimestamp(string snapshotName, out DateTime timestamp)
    {
        var match = SnapshotNamePattern.Match(snapshotName ?? "");
        if (!match.Success)
        {
            timestamp = DateTime.MinValue;
            return false;
        }

        return DateTime.TryParseExact(
            match.Groups["date"].Value + match.Groups["time"].Value,
            "yyyy-MM-ddHHmm",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out timestamp);
    }

    private static (DateTime Modified, long Size) ReadMetadata(string path, bool isFolder)
    {
        try
        {
            if (isFolder)
            {
                return (new DirectoryInfo(path).LastWriteTime, 0);
            }

            var file = new FileInfo(path);
            return (file.LastWriteTime, file.Length);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentOutOfRangeException)
        {
            return (DateTime.MinValue, 0);
        }
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

    private static string RecoveryName(string name, bool isFolder, DateTime snapshotTime)
    {
        var suffix = " (Recovered " + snapshotTime.ToString("yyyy-MM-dd HHmm", CultureInfo.InvariantCulture) + ")";
        return isFolder
            ? name + suffix
            : Path.GetFileNameWithoutExtension(name) + suffix + Path.GetExtension(name);
    }

    private static string BackupName(string name, bool isFolder)
    {
        return isFolder
            ? name + "@qsurfer"
            : Path.GetFileNameWithoutExtension(name) + ".qsurfer" + Path.GetExtension(name);
    }

    private static void MarkSafetyCopy(string path, bool isFolder)
    {
        try
        {
            if (isFolder)
            {
                foreach (var child in Directory.EnumerateFileSystemEntries(path, "*", SearchOption.AllDirectories))
                {
                    File.SetAttributes(child, File.GetAttributes(child) | FileAttributes.Hidden | FileAttributes.ReadOnly);
                }
            }

            File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.Hidden | FileAttributes.ReadOnly);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLogger.Warn("timeline", $"could not mark safety copy hidden and read-only path=\"{path}\" reason=\"{ex.Message}\"");
        }
    }

    private static string FindAvailableDestination(string parent, string name, bool isFolder)
    {
        var candidate = Path.Combine(parent, name);
        if (!File.Exists(candidate) && !Directory.Exists(candidate))
        {
            return candidate;
        }

        var baseName = isFolder ? name : Path.GetFileNameWithoutExtension(name);
        var extension = isFolder ? "" : Path.GetExtension(name);
        for (var number = 2; ; number++)
        {
            candidate = Path.Combine(parent, baseName + " (" + number + ")" + extension);
            if (!File.Exists(candidate) && !Directory.Exists(candidate))
            {
                return candidate;
            }
        }
    }

    private static bool PathStartsWith(string path, string root)
    {
        var normalizedPath = Normalize(path).TrimEnd('\\');
        var normalizedRoot = Normalize(root).TrimEnd('\\');
        return normalizedPath.Equals(normalizedRoot, StringComparison.OrdinalIgnoreCase) ||
               normalizedPath.StartsWith(normalizedRoot + "\\", StringComparison.OrdinalIgnoreCase);
    }

    private static string Combine(string root, string rest) =>
        string.IsNullOrWhiteSpace(rest)
            ? root.TrimEnd('\\')
            : Path.Combine(root.TrimEnd('\\') + "\\", rest);

    private static string Normalize(string value) => (value ?? "").Trim().Replace('/', '\\');

    private static IReadOnlyList<MappedDrive> ReadMappedDrives()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("net.exe", "use")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (process == null)
            {
                return [];
            }

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(2000);
            return output.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                .Select(parts => new
                {
                    Local = parts.FirstOrDefault(part => Regex.IsMatch(part, "^[A-Z]:$", RegexOptions.IgnoreCase)),
                    Remote = parts.FirstOrDefault(part => part.StartsWith(@"\\", StringComparison.OrdinalIgnoreCase)),
                })
                .Where(value => !string.IsNullOrWhiteSpace(value.Local) && !string.IsNullOrWhiteSpace(value.Remote))
                .Select(value => new MappedDrive(value.Local + "\\", value.Remote!))
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    private sealed record SnapshotCandidate(string SnapshotRoot, string RelativePath);
    private sealed record MappedDrive(string LocalRoot, string Remote);
}

public sealed record SnapshotTimeline(string SourcePath, string SnapshotRoot, IReadOnlyList<SnapshotVersion> Versions);

public sealed record SnapshotRestoreOutcome(string DestinationPath, string? BackupPath);

public sealed record SnapshotVersion(
    string Name,
    DateTime SnapshotTime,
    string FullPath,
    bool IsFolder,
    DateTime Modified,
    long Size)
{
    public string SnapshotText => SnapshotTime.ToString("MMM d, yyyy h:mm tt", CultureInfo.CurrentCulture);
    public string ModifiedText => Modified == DateTime.MinValue ? "" : Modified.ToString("g", CultureInfo.CurrentCulture);
    public string TypeText => IsFolder ? "Folder" : "File";
    public string SizeText => IsFolder || Size <= 0
        ? ""
        : Size >= 1_048_576
            ? string.Format(CultureInfo.CurrentCulture, "{0:N1} MB", Size / 1_048_576d)
            : string.Format(CultureInfo.CurrentCulture, "{0:N0} KB", Math.Max(1, Size / 1024d));
}
