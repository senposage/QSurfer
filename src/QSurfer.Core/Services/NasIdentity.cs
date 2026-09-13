using System.Text.RegularExpressions;

namespace QSurfer.Core.Services;

/// <summary>
/// Keeps SMB identity stable across settings, path mappings, and platform mounts.
/// QSurfer stores shares as \\server\share; a Linux mount point is only a local target.
/// </summary>
public static class NasIdentity
{
    public static string NormalizeHost(string? value)
    {
        var candidate = (value ?? "").Trim().Replace(@"\.", ".", StringComparison.Ordinal);
        if (Uri.TryCreate(candidate, UriKind.Absolute, out var uri))
        {
            candidate = uri.Host;
        }

        candidate = candidate.Trim().Trim('\\', '/');
        return candidate.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
    }

    public static string NormalizeShareRoot(string? value, string? configuredHost = null)
    {
        var candidate = (value ?? "").Trim().Replace('/', '\\').Replace(@"\.", ".", StringComparison.Ordinal);
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return "";
        }

        var parts = candidate.Trim('\\').Split('\\', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length >= 2)
        {
            var server = NormalizeHost(parts[0]);
            var preferredHost = NormalizeHost(configuredHost);
            if (!string.IsNullOrWhiteSpace(preferredHost) && HostsMatch(server, preferredHost))
            {
                server = preferredHost;
            }

            return string.IsNullOrWhiteSpace(server) ? "" : $@"\\{server}\{parts[1]}";
        }

        var host = NormalizeHost(configuredHost);
        return string.IsNullOrWhiteSpace(host) ? "\\" + parts[0] : $@"\\{host}\{parts[0]}";
    }

    public static string BuildUncPath(string host, string relativePath)
    {
        var normalizedHost = NormalizeHost(host);
        var suffix = (relativePath ?? "").Trim().TrimStart('\\', '/').Replace('/', '\\');
        return string.IsNullOrWhiteSpace(normalizedHost) || string.IsNullOrWhiteSpace(suffix)
            ? ""
            : $@"\\{normalizedHost}\{suffix}";
    }

    public static bool HostsMatch(string? left, string? right)
    {
        var leftHost = NormalizeHost(left);
        var rightHost = NormalizeHost(right);
        return !string.IsNullOrWhiteSpace(leftHost) && !string.IsNullOrWhiteSpace(rightHost) &&
               (leftHost.Equals(rightHost, StringComparison.OrdinalIgnoreCase) ||
                leftHost.Split('.')[0].Equals(rightHost.Split('.')[0], StringComparison.OrdinalIgnoreCase));
    }
}
