using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using QSurfer.Core.Models;

namespace QSurfer.Core.Services;

public sealed class PathMapper(AppConfig config)
{
    private static readonly object MappedDrivesGate = new();
    private static readonly Regex NetUseDrivePattern = new(
        @"\b(?<local>[A-Za-z]:)\s+(?<remote>\\\\.+?)\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static IReadOnlyList<MappedDrive> _mappedDrives = [];
    private static DateTime _mappedDrivesExpiresUtc = DateTime.MinValue;
    public string Resolve(SearchResult result)
    {
        var qpath = Normalize(result.Path);
        var fileName = result.FileName.Trim();
        if (!result.IsFolder && !result.HasUsableFileName)
        {
            throw new InvalidOperationException("Qsirch did not provide a file name for this result. Run the search again to refresh it from the NAS.");
        }
        if (!string.IsNullOrWhiteSpace(fileName) && !qpath.EndsWith(fileName, StringComparison.OrdinalIgnoreCase))
        {
            qpath = qpath.TrimEnd('\\') + "\\" + fileName;
        }
        foreach (var mapping in OrderedPathMappings())
        {
            var target = Normalize(mapping.MappedRoot).TrimEnd('\\');
            var source = Normalize(mapping.ShareRoot).TrimEnd('\\');
            if (string.IsNullOrWhiteSpace(target) || string.IsNullOrWhiteSpace(source))
            {
                continue;
            }

            if (TryResolveMappedRoot(qpath, source, target, out var resolved) && IsAvailablePathRoot(resolved))
            {
                return resolved;
            }
        }

        var netUsePath = ResolveFromMappedDrives(qpath);
        if (!string.IsNullOrWhiteSpace(netUsePath))
        {
            return netUsePath;
        }

        var existingMappedPath = ResolveExistingMappedPath(qpath);
        if (!string.IsNullOrWhiteSpace(existingMappedPath))
        {
            return existingMappedPath;
        }

        var savedPath = Normalize(result.ResolvedPath);
        if (!string.IsNullOrWhiteSpace(savedPath))
        {
            var savedMappedPath = savedPath.StartsWith(@"\\", StringComparison.OrdinalIgnoreCase)
                ? ResolveFromMappedDrives(savedPath)
                : null;
            if (!string.IsNullOrWhiteSpace(savedMappedPath))
            {
                return savedMappedPath;
            }
            if (IsAvailablePathRoot(savedPath))
            {
                return savedPath;
            }
        }

        if (qpath.StartsWith(@"\\", StringComparison.OrdinalIgnoreCase) || Path.IsPathFullyQualified(qpath))
        {
            return qpath;
        }

        var nasUncPath = ResolveNasUncPath(qpath);
        if (!string.IsNullOrWhiteSpace(nasUncPath))
        {
            return nasUncPath;
        }

        throw new InvalidOperationException("Could not resolve this NAS result to a Windows path. Configure the NAS host or add a path mapping in Settings.");
    }

    public string? TryResolve(SearchResult result)
    {
        try
        {
            return Resolve(result);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    public string? TryResolvePreferredPath(SearchResult result)
    {
        return TryResolve(result);
    }

    public string ResolveBrowserPath(string path)
    {
        var normalized = Normalize(path);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return normalized;
        }

        // A full UNC path identifies an actual server share. Only an exact Windows
        // drive mapping (or an explicitly UNC-based manual mapping) may replace it.
        // Do not let a broad relative mapping turn \\nas\share2 into X:\Share2
        // merely because X: points at another share on the same NAS.
        if (normalized.StartsWith(@"\\", StringComparison.OrdinalIgnoreCase))
        {
            var mappedPath = ResolveFromMappedDrives(normalized);
            if (!string.IsNullOrWhiteSpace(mappedPath))
            {
                return mappedPath;
            }

            normalized = PreferMappedServerName(normalized);

            foreach (var mapping in OrderedPathMappings())
            {
                var target = Normalize(mapping.MappedRoot).TrimEnd('\\');
                var source = Normalize(mapping.ShareRoot).TrimEnd('\\');
                if (!source.StartsWith(@"\\", StringComparison.OrdinalIgnoreCase) ||
                    string.IsNullOrWhiteSpace(target))
                {
                    continue;
                }

                if (TryResolveMappedRoot(normalized, source, target, out var resolved) && IsAvailablePathRoot(resolved))
                {
                    return resolved;
                }
            }

            return normalized;
        }

        foreach (var mapping in OrderedPathMappings())
        {
            var target = Normalize(mapping.MappedRoot).TrimEnd('\\');
            var source = Normalize(mapping.ShareRoot).TrimEnd('\\');
            if (string.IsNullOrWhiteSpace(target) || string.IsNullOrWhiteSpace(source))
            {
                continue;
            }

            if (TryResolveMappedRoot(normalized, source, target, out var resolved) && IsAvailablePathRoot(resolved))
            {
                return resolved;
            }

            var uncParts = normalized.Trim('\\').Split('\\', StringSplitOptions.RemoveEmptyEntries);
            if (normalized.StartsWith(@"\\", StringComparison.OrdinalIgnoreCase) && uncParts.Length >= 2 &&
                TryResolveMappedRoot(string.Join("\\", uncParts.Skip(1)), source, target, out resolved) &&
                IsAvailablePathRoot(resolved))
            {
                return resolved;
            }
        }

        return ResolveFromMappedDrives(normalized) ?? normalized;
    }

    public string? TryResolveUnc(SearchResult result)
    {
        var savedPath = Normalize(result.ResolvedPath);
        if (savedPath.StartsWith(@"\\", StringComparison.OrdinalIgnoreCase))
        {
            return savedPath;
        }

        var qpath = Normalize(result.Path);
        var fileName = result.FileName.Trim();
        if (!string.IsNullOrWhiteSpace(fileName) && !qpath.EndsWith(fileName, StringComparison.OrdinalIgnoreCase))
        {
            qpath = qpath.TrimEnd('\\') + "\\" + fileName;
        }
        if (qpath.StartsWith(@"\\", StringComparison.OrdinalIgnoreCase))
        {
            return qpath;
        }

        foreach (var mapping in OrderedPathMappings())
        {
            var source = Normalize(mapping.ShareRoot).TrimEnd('\\');
            if (source.StartsWith(@"\\", StringComparison.OrdinalIgnoreCase) &&
                TryResolveMappedRoot(qpath, source, source, out var resolved))
            {
                return resolved;
            }
        }

        return ResolveUncFromMappedDrives(qpath) ?? ResolveNasUncPath(qpath);
    }

    public static IReadOnlyList<PathMapping> DiscoverWindowsPathMappings()
    {
        return DiscoverWindowsDriveMappings()
            .Where(mapping => !string.IsNullOrWhiteSpace(mapping.QsirchShareRoot))
            .Select(mapping => new PathMapping
            {
                ShareRoot = mapping.QsirchShareRoot,
                MappedRoot = mapping.DriveRoot,
            })
            .ToList();
    }

    public static IReadOnlyList<WindowsDriveMapping> DiscoverWindowsDriveMappings()
    {
        return ReadMappedDrives()
            .Select(drive =>
            {
                var parts = Normalize(drive.Remote)
                    .Trim('\\')
                    .Split('\\', StringSplitOptions.RemoveEmptyEntries);
                var shareName = parts.Length >= 2 ? parts[^1] : "";
                return new WindowsDriveMapping(
                    drive.LocalRoot,
                    drive.Remote,
                    string.IsNullOrWhiteSpace(shareName) ? "" : "\\" + shareName);
            })
            .OrderBy(mapping => mapping.DriveRoot, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static void RefreshWindowsDriveMappings()
    {
        lock (MappedDrivesGate)
        {
            _mappedDrives = [];
            _mappedDrivesExpiresUtc = DateTime.MinValue;
        }
    }

    public static bool TryMapNetworkDrive(string driveRoot, string networkPath, bool reconnectAtSignIn, out string error)
    {
        error = "";
        var drive = Normalize(driveRoot).TrimEnd('\\').ToUpperInvariant();
        var remote = Normalize(networkPath).TrimEnd('\\');
        if (!Regex.IsMatch(drive, "^[A-Z]:$"))
        {
            error = "Choose a drive letter such as X:.";
            return false;
        }
        if (!Regex.IsMatch(remote, @"^\\\\[^\\]+\\[^\\]+"))
        {
            error = "Enter a network path such as \\server\\share.";
            return false;
        }

        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo("net.exe")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                },
            };
            process.StartInfo.ArgumentList.Add("use");
            process.StartInfo.ArgumentList.Add(drive);
            process.StartInfo.ArgumentList.Add(remote);
            process.StartInfo.ArgumentList.Add($"/persistent:{(reconnectAtSignIn ? "yes" : "no")}");
            process.Start();
            var output = process.StandardOutput.ReadToEnd();
            var standardError = process.StandardError.ReadToEnd();
            if (!process.WaitForExit(8000))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                error = "Windows did not finish mapping the network drive in time.";
                return false;
            }
            if (process.ExitCode != 0)
            {
                var reason = string.IsNullOrWhiteSpace(standardError) ? output : standardError;
                error = string.IsNullOrWhiteSpace(reason)
                    ? "Windows could not map the network drive."
                    : reason.Trim();
                return false;
            }

            RefreshWindowsDriveMappings();
            return true;
        }
        catch (Exception ex)
        {
            AppLogger.Error("path", ex, $"network drive mapping failed drive=\"{drive}\" remote=\"{remote}\"");
            error = "Windows could not map the network drive. Check the path and your access.";
            return false;
        }
    }

    public static bool IsValidManualMapping(PathMapping mapping, out string error)
    {
        var source = Normalize(mapping.ShareRoot);
        var target = Normalize(mapping.MappedRoot);
        if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(target))
        {
            error = "Each path mapping needs both a Qsirch share path and a Windows path.";
            return false;
        }
        if (!source.StartsWith('\\'))
        {
            error = "A Qsirch share path must begin with a backslash, for example \\Shared.";
            return false;
        }
        if (!Regex.IsMatch(target, @"^(?:[A-Za-z]:\\?|\\\\[^\\]+\\[^\\]+)"))
        {
            error = "A Windows path must be a drive such as X:\\ or a UNC path such as \\server\\share.";
            return false;
        }

        error = "";
        return true;
    }

    private static string? ResolveExistingMappedPath(string qpath)
    {
        var relative = qpath.TrimStart('\\');
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative))
        {
            return null;
        }

        var separator = relative.IndexOf('\\');
        var withoutLeadingShare = separator >= 0 ? relative[(separator + 1)..] : "";
        foreach (var drive in DriveInfo.GetDrives())
        {
            if (drive.DriveType != DriveType.Network || !drive.IsReady)
            {
                continue;
            }

            foreach (var candidateRelativePath in new[] { relative, withoutLeadingShare }
                         .Where(value => !string.IsNullOrWhiteSpace(value))
                         .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var candidate = CombineRoot(drive.RootDirectory.FullName, candidateRelativePath);
                if (File.Exists(candidate) || Directory.Exists(candidate))
                {
                    AppLogger.Info("path", $"resolved saved result from mapped drive path=\"{candidate}\"");
                    return candidate;
                }
            }
        }

        return null;
    }

    private static bool IsAvailablePathRoot(string path)
    {
        var root = Path.GetPathRoot(path);
        if (string.IsNullOrWhiteSpace(root) || !Regex.IsMatch(root, "^[A-Za-z]:\\\\$"))
        {
            return true;
        }

        return DriveInfo.GetDrives().Any(drive => drive.Name.Equals(root, StringComparison.OrdinalIgnoreCase));
    }

    private string? ResolveNasUncPath(string qpath)
    {
        if (string.IsNullOrWhiteSpace(config.Host) ||
            qpath.StartsWith(@"\\", StringComparison.OrdinalIgnoreCase) ||
            Path.IsPathFullyQualified(qpath))
        {
            return null;
        }

        var host = config.Host.Trim().Trim('\\', '/');
        if (host.Contains("://", StringComparison.OrdinalIgnoreCase) &&
            Uri.TryCreate(host, UriKind.Absolute, out var uri))
        {
            host = uri.Host;
        }
        if (string.IsNullOrWhiteSpace(host) || host.IndexOfAny(['\\', '/', '?', '#']) >= 0)
        {
            return null;
        }

        var segments = qpath.TrimStart('\\')
            .Split('\\', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length == 0 || segments.Any(segment => segment is "." or ".."))
        {
            return null;
        }

        return PreferMappedServerName(@"\\" + host + "\\" + string.Join("\\", segments));
    }

    private static string? ResolveFromMappedDrives(string qpath)
    {
        var relative = qpath.TrimStart('\\');
        foreach (var drive in NetUseDrives().OrderByDescending(drive => Normalize(drive.Remote).Length))
        {
            var remote = Normalize(drive.Remote).TrimEnd('\\');
            if (qpath.StartsWith(remote, StringComparison.OrdinalIgnoreCase))
            {
                return CombineRoot(drive.LocalRoot, qpath[remote.Length..].TrimStart('\\'));
            }

            if (TryResolveEquivalentUncShare(qpath, remote, drive.LocalRoot, out var equivalentPath))
            {
                return equivalentPath;
            }

            var remoteParts = remote.Trim('\\').Split('\\', StringSplitOptions.RemoveEmptyEntries);
            var shareName = remoteParts.Length > 1 ? remoteParts[^1] : "";
            if (string.IsNullOrWhiteSpace(shareName))
            {
                continue;
            }

            foreach (var prefix in CandidateSharePrefixes(shareName))
            {
                if (relative.Equals(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    return drive.LocalRoot;
                }
                if (relative.StartsWith(prefix + "\\", StringComparison.OrdinalIgnoreCase))
                {
                    return CombineRoot(drive.LocalRoot, relative[(prefix.Length + 1)..]);
                }
            }
        }
        return null;
    }

    private static bool TryResolveEquivalentUncShare(string path, string remote, string localRoot, out string resolved)
    {
        resolved = "";
        if (!path.StartsWith(@"\\", StringComparison.OrdinalIgnoreCase) ||
            !remote.StartsWith(@"\\", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var pathParts = path.Trim('\\').Split('\\', StringSplitOptions.RemoveEmptyEntries);
        var remoteParts = remote.Trim('\\').Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (pathParts.Length < 2 || remoteParts.Length < 2 ||
            !SameWindowsServer(pathParts[0], remoteParts[0]) ||
            !pathParts[1].Equals(remoteParts[1], StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        resolved = CombineRoot(localRoot, string.Join("\\", pathParts.Skip(2)));
        return true;
    }

    private static bool SameWindowsServer(string first, string second)
    {
        if (first.Equals(second, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var firstShortName = first.Split('.', 2)[0];
        var secondShortName = second.Split('.', 2)[0];
        return firstShortName.Equals(secondShortName, StringComparison.OrdinalIgnoreCase);
    }

    private static string PreferMappedServerName(string path)
    {
        if (!path.StartsWith(@"\\", StringComparison.OrdinalIgnoreCase))
        {
            return path;
        }

        var pathParts = path.Trim('\\').Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (pathParts.Length < 2)
        {
            return path;
        }

        foreach (var drive in NetUseDrives())
        {
            var remoteParts = Normalize(drive.Remote).Trim('\\')
                .Split('\\', StringSplitOptions.RemoveEmptyEntries);
            if (remoteParts.Length < 2 || !SameWindowsServer(pathParts[0], remoteParts[0]))
            {
                continue;
            }

            // Windows authenticates domain SMB sessions against the FQDN. Reuse the
            // name from an existing mapped connection when the app was configured
            // with a short NAS host name.
            return @"\\" + remoteParts[0] + "\\" + string.Join("\\", pathParts.Skip(1));
        }

        return path;
    }

    private static string? ResolveUncFromMappedDrives(string qpath)
    {
        var relative = qpath.TrimStart('\\');
        foreach (var drive in NetUseDrives().OrderByDescending(drive => Normalize(drive.Remote).Length))
        {
            var remote = Normalize(drive.Remote).TrimEnd('\\');
            if (qpath.StartsWith(remote, StringComparison.OrdinalIgnoreCase))
            {
                return remote + qpath[remote.Length..];
            }

            var shareName = remote.Trim('\\').Split('\\', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
            if (string.IsNullOrWhiteSpace(shareName))
            {
                continue;
            }

            foreach (var prefix in CandidateSharePrefixes(shareName))
            {
                if (relative.Equals(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    return remote;
                }
                if (relative.StartsWith(prefix + "\\", StringComparison.OrdinalIgnoreCase))
                {
                    return CombineRoot(remote, relative[(prefix.Length + 1)..]);
                }
            }
        }
        return null;
    }

    private static bool TryResolveMappedRoot(string qpath, string source, string target, out string resolved)
    {
        foreach (var prefix in CandidateMappingPrefixes(source))
        {
            if (!TryMatchPathRoot(qpath, prefix, out var rest))
            {
                continue;
            }

            resolved = CombineRoot(target, rest);
            return true;
        }

        resolved = "";
        return false;
    }

    private static IEnumerable<string> CandidateMappingPrefixes(string source)
    {
        var normalized = Normalize(source).Trim('\\');
        if (!string.IsNullOrWhiteSpace(normalized))
        {
            yield return normalized;
        }

        var sourceParts = normalized.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        var shareName = sourceParts.Length > 0 ? sourceParts[^1] : "";
        if (!string.IsNullOrWhiteSpace(shareName))
        {
            yield return shareName;
            yield return "Shared\\" + shareName;
        }
    }

    // Nested drive mappings must beat their parent share. For example, an S: mapping
    // for Shared\\Scans should win over X: mapped to the broader Shared root.
    private IEnumerable<PathMapping> OrderedPathMappings() => (config.PathMappings ?? [])
        .Where(mapping => mapping != null && IsValidManualMapping(mapping, out _))
        .OrderByDescending(mapping => CandidateMappingPrefixes(mapping.ShareRoot)
            .Select(prefix => prefix.Length)
            .DefaultIfEmpty(0)
            .Max());

    private static bool TryMatchPathRoot(string path, string root, out string rest)
    {
        path = Normalize(path).TrimStart('\\');
        root = Normalize(root).Trim('\\');
        if (path.Equals(root, StringComparison.OrdinalIgnoreCase))
        {
            rest = "";
            return true;
        }
        if (path.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase))
        {
            rest = path[(root.Length + 1)..];
            return true;
        }

        rest = "";
        return false;
    }

    private static IEnumerable<string> CandidateSharePrefixes(string shareName)
    {
        yield return shareName;
        yield return "Shared\\" + shareName;
    }

    private static string CombineRoot(string root, string rest)
    {
        root = root.TrimEnd('\\') + "\\";
        return string.IsNullOrWhiteSpace(rest) ? root : Path.Combine(root, rest);
    }

    private static IReadOnlyList<MappedDrive> NetUseDrives()
    {
        lock (MappedDrivesGate)
        {
            if (DateTime.UtcNow < _mappedDrivesExpiresUtc)
            {
                return _mappedDrives;
            }

            _mappedDrives = ReadMappedDrives();
            _mappedDrivesExpiresUtc = DateTime.UtcNow.AddMinutes(2);
            return _mappedDrives;
        }
    }

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
            var drives = new List<MappedDrive>();
            foreach (var line in output.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries))
            {
                // `net use` renders the remote path through the end of the line, so
                // splitting on whitespace truncates shares such as "PC Law" to "PC".
                var match = NetUseDrivePattern.Match(line);
                if (match.Success)
                {
                    drives.Add(new MappedDrive(
                        match.Groups["local"].Value + "\\",
                        match.Groups["remote"].Value.Trim()));
                }
            }
            return drives;
        }
        catch
        {
            return [];
        }
    }

    private static string Normalize(string value) => (value ?? "").Replace('/', '\\').Trim();

    private sealed record MappedDrive(string LocalRoot, string Remote);
}

public sealed record WindowsDriveMapping(string DriveRoot, string NetworkPath, string QsirchShareRoot);
