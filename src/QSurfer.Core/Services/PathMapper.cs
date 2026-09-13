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
            throw new InvalidOperationException("The NAS search service did not provide a file name for this result. Run the search again to refresh it from the NAS.");
        }
        if (!string.IsNullOrWhiteSpace(fileName) && !qpath.EndsWith(fileName, StringComparison.OrdinalIgnoreCase))
        {
            qpath = qpath.TrimEnd('\\') + "\\" + fileName;
        }
        if (!OperatingSystem.IsWindows())
        {
            var linuxPath = ResolveLinuxPath(qpath);
            if (Path.IsPathFullyQualified(linuxPath) && !linuxPath.StartsWith(@"\\", StringComparison.Ordinal))
            {
                return linuxPath;
            }

            linuxPath = ResolveLinuxPath(result.ResolvedPath);
            if (Path.IsPathFullyQualified(linuxPath) && !linuxPath.StartsWith(@"\\", StringComparison.Ordinal))
            {
                return linuxPath;
            }

            throw new InvalidOperationException("Could not resolve this NAS result to a mounted Linux path. Add a NAS-to-local path mapping in Settings.");
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
        if (!OperatingSystem.IsWindows())
        {
            return ResolveLinuxPath(path);
        }

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

        return ResolveFromMappedDrives(normalized) ?? ResolveNasUncPath(normalized) ?? normalized;
    }

    public string DisplayBrowserPath(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            var uncPath = Normalize(path);
            if (!uncPath.StartsWith(@"\\", StringComparison.OrdinalIgnoreCase))
            {
                return path;
            }

            var segments = uncPath.Trim('\\').Split('\\', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length >= 2 && NasIdentity.HostsMatch(segments[0], config.Host))
            {
                return "\\" + string.Join("\\", segments.Skip(1));
            }

            return path;
        }

        var localPath = NormalizeUnixPath(path);
        if (!Path.IsPathFullyQualified(localPath) || localPath.StartsWith("//", StringComparison.Ordinal))
        {
            return path;
        }

        foreach (var mapping in OrderedPathMappings())
        {
            var localRoot = NormalizeUnixPath(mapping.MappedRoot).TrimEnd('/');
            var shareRoot = Normalize(mapping.ShareRoot).TrimEnd('\\');
            if (string.IsNullOrWhiteSpace(localRoot) || string.IsNullOrWhiteSpace(shareRoot) ||
                !TryMatchLocalRoot(localPath, localRoot, out var remainder))
            {
                continue;
            }

            var shareName = shareRoot.Trim('\\')
                .Split('\\', StringSplitOptions.RemoveEmptyEntries)
                .LastOrDefault();
            if (!string.IsNullOrWhiteSpace(shareName))
            {
                return string.IsNullOrWhiteSpace(remainder)
                    ? "/" + shareName
                    : "/" + shareName + "/" + remainder.Replace('\\', '/');
            }
        }

        return localPath;
    }

    public string? TryResolveNasSearchPath(string path)
    {
        var normalized = Normalize(path).TrimEnd('\\');
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return null;
        }

        var compactNasScope = TryResolveCompactNasSearchScope(normalized);
        if (!string.IsNullOrWhiteSpace(compactNasScope))
        {
            return compactNasScope;
        }

        if (normalized.StartsWith("\\") && !normalized.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return normalized;
        }

        foreach (var mapping in OrderedPathMappings())
        {
            var localRoot = Normalize(OperatingSystem.IsWindows() ? mapping.MappedRoot : NormalizeUnixPath(mapping.MappedRoot)).TrimEnd('\\', '/');
            var shareRoot = Normalize(mapping.ShareRoot).TrimEnd('\\');
            if (string.IsNullOrWhiteSpace(localRoot) || string.IsNullOrWhiteSpace(shareRoot) ||
                !TryMatchLocalRoot(normalized, localRoot, out var remainder))
            {
                continue;
            }

            return string.IsNullOrWhiteSpace(remainder) ? shareRoot : shareRoot + "\\" + remainder;
        }

        if (OperatingSystem.IsWindows())
        {
            foreach (var drive in NetUseDrives().OrderByDescending(drive => drive.LocalRoot.Length))
            {
                var localRoot = Normalize(drive.LocalRoot).TrimEnd('\\');
                if (!TryMatchLocalRoot(normalized, localRoot, out var remainder))
                {
                    continue;
                }

                var remoteParts = Normalize(drive.Remote).Trim('\\').Split('\\', StringSplitOptions.RemoveEmptyEntries);
                if (remoteParts.Length < 2)
                {
                    continue;
                }

                var shareRoot = "\\" + remoteParts[^1];
                return string.IsNullOrWhiteSpace(remainder) ? shareRoot : shareRoot + "\\" + remainder;
            }
        }

        if (normalized.StartsWith(@"\\", StringComparison.Ordinal))
        {
            var segments = normalized.Trim('\\').Split('\\', StringSplitOptions.RemoveEmptyEntries);
            return segments.Length >= 2 ? "\\" + string.Join("\\", segments.Skip(1)) : null;
        }

        return null;
    }

    private string? TryResolveCompactNasSearchScope(string path)
    {
        // Folder scopes are stored without their leading slash so they can be
        // compared against Qsirch result paths. Expand only names that identify
        // a configured or mapped network share; never reinterpret local paths.
        if (path.StartsWith('\\') || path.StartsWith('/') || path.Contains(':'))
        {
            return null;
        }

        var segments = path.Trim('\\').Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            return null;
        }

        var shareName = segments[0];
        var isKnownShare = OrderedPathMappings().Any(mapping =>
            string.Equals(
                Normalize(mapping.ShareRoot).Trim('\\')
                    .Split('\\', StringSplitOptions.RemoveEmptyEntries)
                    .LastOrDefault(),
                shareName,
                StringComparison.OrdinalIgnoreCase));

        if (!isKnownShare && OperatingSystem.IsWindows())
        {
            isKnownShare = NetUseDrives().Any(drive =>
                string.Equals(
                    Normalize(drive.Remote).Trim('\\')
                        .Split('\\', StringSplitOptions.RemoveEmptyEntries)
                        .LastOrDefault(),
                    shareName,
                    StringComparison.OrdinalIgnoreCase));
        }

        return isKnownShare ? "\\" + string.Join("\\", segments) : null;
    }

    public string? TryResolveUnc(SearchResult result)
    {
        if (!OperatingSystem.IsWindows())
        {
            return TryResolve(result);
        }

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
        var target = OperatingSystem.IsWindows() ? Normalize(mapping.MappedRoot) : NormalizeUnixPath(mapping.MappedRoot);
        if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(target))
        {
            error = "Each path mapping needs both a NAS share path and a local path.";
            return false;
        }
        if (!source.StartsWith('\\'))
        {
            error = "A NAS share path must begin with a backslash, for example \\Shared.";
            return false;
        }
        if (!OperatingSystem.IsWindows() && Path.IsPathFullyQualified(target) && !target.StartsWith(@"\\", StringComparison.Ordinal))
        {
            error = "";
            return true;
        }
        if (!Regex.IsMatch(target, @"^(?:[A-Za-z]:\\?|\\\\[^\\]+\\[^\\]+)"))
        {
            error = OperatingSystem.IsWindows()
                ? "A local path must be a drive such as X:\\ or a UNC path such as \\server\\share."
                : "A Linux local path must be an absolute mount path such as /mnt/shared.";
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

        var host = NasIdentity.NormalizeHost(config.Host);
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

        return PreferMappedServerName(NasIdentity.BuildUncPath(host, string.Join("\\", segments)));
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

    private static bool TryMatchLocalRoot(string path, string root, out string rest)
    {
        path = Normalize(path).TrimEnd('\\', '/');
        root = Normalize(root).TrimEnd('\\', '/');
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

    private string ResolveLinuxPath(string path)
    {
        var remotePath = Normalize(path);

        // A concise NAS path such as \Shared\Clients is deliberately accepted on
        // Linux. NormalizeUnixPath turns it into /Shared/Clients, which otherwise
        // looks like a local absolute path and bypasses the configured CIFS mount.
        // Resolve that NAS form before considering genuine Unix paths such as
        // /home/alex/Documents.
        if (remotePath.StartsWith('\\') && !remotePath.StartsWith("//", StringComparison.Ordinal))
        {
            foreach (var mapping in OrderedPathMappings())
            {
                var target = NormalizeUnixPath(mapping.MappedRoot).TrimEnd('/');
                var source = Normalize(mapping.ShareRoot).TrimEnd('\\');
                if (!Path.IsPathFullyQualified(target) || target.StartsWith("//", StringComparison.Ordinal) || string.IsNullOrWhiteSpace(source))
                {
                    continue;
                }

                foreach (var prefix in CandidateMappingPrefixes(source))
                {
                    if (TryMatchPathRoot(remotePath, prefix, out var rest))
                    {
                        return CombineUnixRoot(target, rest);
                    }
                }
            }
        }

        var localPath = NormalizeUnixPath(path);
        if (localPath.StartsWith("/", StringComparison.Ordinal) && !localPath.StartsWith("//", StringComparison.Ordinal))
        {
            foreach (var mapping in OrderedPathMappings())
            {
                var target = NormalizeUnixPath(mapping.MappedRoot).TrimEnd('/');
                var source = Normalize(mapping.ShareRoot).TrimEnd('\\');
                var shareName = source.Trim('\\')
                    .Split('\\', StringSplitOptions.RemoveEmptyEntries)
                    .LastOrDefault();
                if (!Path.IsPathFullyQualified(target) || target.StartsWith("//", StringComparison.Ordinal) ||
                    string.IsNullOrWhiteSpace(shareName) ||
                    !TryMatchLinuxShareAlias(localPath, shareName, out var rest))
                {
                    continue;
                }

                return CombineUnixRoot(target, rest);
            }
        }
        if (Path.IsPathFullyQualified(localPath) && !localPath.StartsWith("//", StringComparison.Ordinal))
        {
            return localPath;
        }

        foreach (var mapping in OrderedPathMappings())
        {
            var target = NormalizeUnixPath(mapping.MappedRoot).TrimEnd('/');
            var source = Normalize(mapping.ShareRoot).TrimEnd('\\');
            if (!Path.IsPathFullyQualified(target) || target.StartsWith("//", StringComparison.Ordinal) || string.IsNullOrWhiteSpace(source))
            {
                continue;
            }

            foreach (var prefix in CandidateMappingPrefixes(source))
            {
                if (TryMatchPathRoot(remotePath, prefix, out var rest))
                {
                    return CombineUnixRoot(target, rest);
                }
            }
        }

        return path;
    }

    private static string CombineUnixRoot(string root, string rest)
    {
        var segments = rest.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
        return segments.Length == 0 ? root : Path.Combine([root, .. segments]);
    }

    private static bool TryMatchLinuxShareAlias(string path, string shareName, out string rest)
    {
        var alias = "/" + shareName.Trim('/');
        if (path.Equals(alias, StringComparison.OrdinalIgnoreCase))
        {
            rest = "";
            return true;
        }
        if (path.StartsWith(alias + "/", StringComparison.OrdinalIgnoreCase))
        {
            rest = path[(alias.Length + 1)..];
            return true;
        }

        rest = "";
        return false;
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

    private static string NormalizeUnixPath(string value) => (value ?? "").Replace('\\', '/').Trim();

    private sealed record MappedDrive(string LocalRoot, string Remote);
}

public sealed record WindowsDriveMapping(string DriveRoot, string NetworkPath, string QsirchShareRoot);
