using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using QSurfer.Core.Services;

namespace QSurfer.Avalonia.Services;

/// <summary>
/// Creates a kernel CIFS mount instead of depending on a file-manager's
/// user-space SMB implementation. The credential file exists only for the
/// duration of the privileged mount request.
/// </summary>
public static class LinuxNativeCifsMountService
{
    private static readonly TimeSpan AuthorizationTimeout = TimeSpan.FromSeconds(90);

    public static string PersistentMountPoint(string sharePath, string configuredHost)
    {
        if (!TryGetShare(sharePath, configuredHost, out var server, out var share, out _))
        {
            return "";
        }

        return MountPoint(server, share);
    }

    public static bool IsLegacyQSurferMountPoint(string mountPoint)
    {
        if (OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(mountPoint))
        {
            return false;
        }

        try
        {
            var legacyRoot = Path.GetFullPath(UserDataPaths.Subdirectory("mounts")).TrimEnd('/');
            var candidate = Path.GetFullPath(mountPoint).TrimEnd('/');
            return candidate.Equals(legacyRoot, StringComparison.Ordinal) ||
                   candidate.StartsWith(legacyRoot + "/", StringComparison.Ordinal);
        }
        catch (IOException)
        {
            return false;
        }
    }

    public static bool IsMounted(string mountPoint)
    {
        if (OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(mountPoint))
        {
            return false;
        }

        var normalized = Path.GetFullPath(mountPoint).TrimEnd('/');
        return DiscoverMountedShares()
            .Any(mount => Path.GetFullPath(mount.MountPoint).TrimEnd('/').Equals(normalized, StringComparison.Ordinal));
    }

    public static IReadOnlyList<LinuxMountedShare> DiscoverMountedShares(string configuredHost = "")
    {
        if (OperatingSystem.IsWindows())
        {
            return [];
        }

        try
        {
            const string mountsPath = "/proc/self/mounts";
            if (!File.Exists(mountsPath))
            {
                return [];
            }

            return File.ReadLines(mountsPath)
                .Select(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                .Where(parts => parts.Length >= 3 && parts[2].Equals("cifs", StringComparison.OrdinalIgnoreCase))
                .Select(parts => new { Source = UnescapeMountField(parts[0]), Target = UnescapeMountField(parts[1]) })
                .Where(mount => mount.Source.StartsWith("//", StringComparison.Ordinal) && Directory.Exists(mount.Target))
                .Select(mount => TryParseMountedShare(mount.Source, mount.Target, configuredHost))
                .Where(mount => mount != null)
                .Select(mount => mount!)
                .DistinctBy(mount => mount.MountPoint, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
    }

    public static async Task<LinuxSmbMountResult> MountAsync(
        string sharePath,
        string configuredHost,
        string configuredUser,
        string configuredPassword,
        bool reconnectAfterReboot = false,
        string? replacedMountPoint = null,
        CancellationToken cancellationToken = default)
    {
        if (OperatingSystem.IsWindows())
        {
            return LinuxSmbMountResult.Failure("Native CIFS mounting is only available on Linux.");
        }

        if (!TryGetShare(sharePath, configuredHost, out var server, out var share, out var error))
        {
            return LinuxSmbMountResult.Failure(error);
        }
        if (string.IsNullOrWhiteSpace(configuredUser) || string.IsNullOrWhiteSpace(configuredPassword))
        {
            return LinuxSmbMountResult.Failure("Save the NAS username and password before mounting an SMB share.");
        }

        var mountCifs = FindCommand("/usr/sbin/mount.cifs", "/usr/bin/mount.cifs");
        if (mountCifs == null)
        {
            return LinuxSmbMountResult.Failure("Native SMB support needs cifs-utils. Install it with: sudo dnf install cifs-utils");
        }
        var pkexec = FindCommand("/usr/bin/pkexec", "/bin/pkexec");
        if (pkexec == null)
        {
            return LinuxSmbMountResult.Failure("Native SMB mounting needs Polkit (pkexec) to authorize the one-time mount.");
        }

        var mountPoint = MountPoint(server, share);
        var isMounted = await IsMountPointAsync(mountPoint, cancellationToken);
        var legacyMountPoint = IsLegacyQSurferMountPoint(replacedMountPoint ?? "") &&
                               !Path.GetFullPath(replacedMountPoint!).Equals(Path.GetFullPath(mountPoint), StringComparison.Ordinal)
            ? Path.GetFullPath(replacedMountPoint!)
            : "";

        var credentialsPath = Path.Combine(RuntimeDirectory(), ".qsurfer-cifs-" + Guid.NewGuid().ToString("N") + ".credentials");
        try
        {
            await File.WriteAllTextAsync(
                credentialsPath,
                $"username={configuredUser.Trim()}\npassword={configuredPassword}\n",
                cancellationToken);
            File.SetUnixFileMode(credentialsPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);

            if (reconnectAfterReboot)
            {
                if (!IsSystemdAvailable())
                {
                    return LinuxSmbMountResult.Failure("Mount at boot needs a systemd host. QSurfer will not create an eager fstab CIFS mount; use a one-time mount or configure the host's mount service.");
                }

                return await InstallPersistentMountAsync(
                    server,
                    share,
                    mountPoint,
                    credentialsPath,
                    isMounted,
                    legacyMountPoint,
                    cancellationToken);
            }

            if (!string.IsNullOrWhiteSpace(legacyMountPoint))
            {
                var removalError = await RemoveLegacyMountAsync(legacyMountPoint, cancellationToken);
                if (!string.IsNullOrWhiteSpace(removalError))
                {
                    return LinuxSmbMountResult.Failure(removalError);
                }
            }

            if (isMounted)
            {
                return LinuxSmbMountResult.Successful($@"\\{server}\{share}", mountPoint);
            }

            const string mountScript = """
                set -eu
                mount_point=$1
                user_id=$2
                group_id=$3
                mount_cifs=$4
                source=$5
                options=$6
                /usr/bin/mkdir -p "$mount_point"
                /usr/bin/chown "$user_id:$group_id" "$mount_point"
                exec "$mount_cifs" "$source" "$mount_point" -o "$options"
                """;
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = pkexec,
                    UseShellExecute = false,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true,
                },
            };
            // A GUI session should use its registered Polkit agent. Disabling
            // pkexec's text fallback prevents an invisible terminal password
            // prompt from leaving the Settings window stuck on "Waiting".
            process.StartInfo.ArgumentList.Add("--disable-internal-agent");
            process.StartInfo.ArgumentList.Add("/bin/sh");
            process.StartInfo.ArgumentList.Add("-c");
            process.StartInfo.ArgumentList.Add(mountScript);
            process.StartInfo.ArgumentList.Add("qsurfer-cifs-mount");
            process.StartInfo.ArgumentList.Add(mountPoint);
            process.StartInfo.ArgumentList.Add(GetUserId().ToString());
            process.StartInfo.ArgumentList.Add(GetGroupId().ToString());
            process.StartInfo.ArgumentList.Add(mountCifs);
            process.StartInfo.ArgumentList.Add($"//{server}/{share}");
            process.StartInfo.ArgumentList.Add($"credentials={credentialsPath},uid={GetUserId()},gid={GetGroupId()},file_mode=0660,dir_mode=0770,echo_interval=10,resilienthandles");
            process.Start();

            var standardErrorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            using var authorizationCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            authorizationCancellation.CancelAfter(AuthorizationTimeout);
            await process.WaitForExitAsync(authorizationCancellation.Token);
            var standardError = (await standardErrorTask).Trim();
            if (process.ExitCode != 0 || !await IsMountPointAsync(mountPoint, cancellationToken))
            {
                var detail = string.IsNullOrWhiteSpace(standardError)
                    ? "No graphical system-authorization prompt was available, or the request was cancelled."
                    : standardError;
                return LinuxSmbMountResult.Failure($"Could not create a native SMB mount for \\\\{server}\\{share}. {detail}");
            }

            return LinuxSmbMountResult.Successful($@"\\{server}\{share}", mountPoint);
        }
        catch (OperationCanceledException)
        {
            return LinuxSmbMountResult.Failure("Mounting the SMB share timed out waiting for system authorization.");
        }
        catch (Exception ex)
        {
            return LinuxSmbMountResult.Failure($"Could not create a native SMB mount: {ex.Message}");
        }
        finally
        {
            try
            {
                File.Delete(credentialsPath);
            }
            catch (IOException)
            {
                // The privileged helper may still be finishing its read. The runtime
                // directory is private to this desktop session and is cleared on logout.
            }
        }
    }

    private static bool TryGetShare(string rawPath, string configuredHost, out string server, out string share, out string error)
    {
        server = "";
        share = "";
        error = "";
        var parts = (rawPath ?? "").Trim().Replace('/', '\\').Trim('\\')
            .Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 2)
        {
            server = parts[0];
            share = parts[1];
        }
        else if (parts.Length == 1 && !string.IsNullOrWhiteSpace(configuredHost))
        {
            server = NasIdentity.NormalizeHost(configuredHost);
            share = parts[0];
        }
        else
        {
            error = "Enter an SMB share such as \\server\\share, or configure the NAS host and enter its share name.";
            return false;
        }

        server = NasIdentity.NormalizeHost(server);
        if (string.IsNullOrWhiteSpace(server) || string.IsNullOrWhiteSpace(share))
        {
            error = "Enter one SMB share, for example \\server\\share.";
            return false;
        }
        return true;
    }

    private static string MountPoint(string server, string share)
    {
        // A system CIFS mount belongs under /mnt, not inside QSurfer's private
        // state directory. Keep the server and share readable for people using
        // a shell or another file manager, while avoiding name collisions.
        return Path.Combine("/mnt", "qsurfer", SafeMountPathSegment(server), SafeMountPathSegment(share));
    }

    private static string SafeMountPathSegment(string value)
    {
        var segment = string.Concat((value ?? "").Trim().Select(character =>
            char.IsLetterOrDigit(character) || character is '.' or '-' or '_' ? character : '-'));
        return string.IsNullOrWhiteSpace(segment) ? "share" : segment;
    }

    public static bool HasPersistentSystemMount(string mountPoint)
    {
        if (OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(mountPoint))
        {
            return false;
        }

        try
        {
            var normalized = Path.GetFullPath(mountPoint).TrimEnd('/');
            var unitValue = EscapeSystemdValue(normalized);
            return Directory.EnumerateFiles("/etc/systemd/system", "*.mount")
                .Any(path => File.ReadLines(path).Any(line =>
                    line.Trim().Equals($"Where={unitValue}", StringComparison.Ordinal)));
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool IsSystemdAvailable() =>
        Directory.Exists("/run/systemd/system") &&
        FindCommand("/usr/bin/systemctl", "/bin/systemctl") != null;

    private static async Task<LinuxSmbMountResult> InstallPersistentMountAsync(
        string server,
        string share,
        string mountPoint,
        string credentialsPath,
        bool isMounted,
        string legacyMountPoint,
        CancellationToken cancellationToken)
    {
        var pkexec = FindCommand("/usr/bin/pkexec", "/bin/pkexec");
        if (pkexec == null)
        {
            return LinuxSmbMountResult.Failure("Persistent SMB mount needs Polkit (pkexec) to configure systemd.");
        }
        if (!IsSystemdAvailable())
        {
            return LinuxSmbMountResult.Failure("Persistent SMB mount needs a systemd host.");
        }

        var identifier = ShareIdentifier(server, share);
        var credentialTarget = $"/etc/qsurfer/cifs/{identifier}.credentials";
        var marker = $"# QSurfer-CIFS:{identifier}";
        var mountUnitName = $"{SystemdEscapePath(mountPoint)}.mount";
        var automountUnitName = $"{SystemdEscapePath(mountPoint)}.automount";
        var mountUnitPath = $"/etc/systemd/system/{mountUnitName}";
        var automountUnitPath = $"/etc/systemd/system/{automountUnitName}";
        var legacyMountUnitName = string.IsNullOrWhiteSpace(legacyMountPoint) ? "" : $"{SystemdEscapePath(legacyMountPoint)}.mount";
        var legacyMountUnitPath = string.IsNullOrWhiteSpace(legacyMountUnitName) ? "" : $"/etc/systemd/system/{legacyMountUnitName}";
        var mountUnit = $"""
            [Unit]
            Description=QSurfer SMB mount
            Wants=network-online.target
            After=network-online.target
            Before=remote-fs.target

            [Mount]
            What=//{EscapeSystemdValue(server)}/{EscapeSystemdValue(share)}
            Where={EscapeSystemdValue(mountPoint)}
            Type=cifs
            Options=credentials={credentialTarget},uid={GetUserId()},gid={GetGroupId()},file_mode=0660,dir_mode=0770,_netdev,echo_interval=10,resilienthandles
            TimeoutSec=20

            [Install]
            WantedBy=multi-user.target
            """;
        const string script = """
            set -eu
            source_credentials=$1
            target_credentials=$2
            marker=$3
            mount_unit_path=$4
            automount_unit_path=$5
            mount_unit=$6
            mount_unit_name=$7
            automount_unit_name=$8
            mount_point=$9
            user_id=${10}
            group_id=${11}
            legacy_mount_point=${12}
            legacy_mount_unit_path=${13}
            legacy_mount_unit_name=${14}

            /usr/bin/install -d -m 700 /etc/qsurfer/cifs
            /usr/bin/install -m 600 "$source_credentials" "$target_credentials"
            /usr/bin/mkdir -p "$mount_point"
            /usr/bin/chown "$user_id:$group_id" "$mount_point"

            if [ -n "$legacy_mount_point" ]; then
                /usr/bin/systemctl disable --now "$legacy_mount_unit_name" >/dev/null 2>&1 || true
                /usr/bin/rm -f "$legacy_mount_unit_path"
                if /usr/bin/mountpoint -q "$legacy_mount_point"; then
                    /usr/bin/umount "$legacy_mount_point" || true
                fi
                /usr/bin/rmdir "$legacy_mount_point" 2>/dev/null || true
            fi

            # Replace the former QSurfer fstab pair for this share, if present.
            temp_fstab=$(/usr/bin/mktemp)
            /usr/bin/awk -v marker="$marker" '$0 == marker { skip = 1; next } skip { skip = 0; next } { print }' /etc/fstab > "$temp_fstab"
            /usr/bin/install -m 644 "$temp_fstab" /etc/fstab
            /usr/bin/rm -f "$temp_fstab"

            # Replace the previous on-demand unit for this exact mountpoint.
            /usr/bin/systemctl disable --now "$automount_unit_name" >/dev/null 2>&1 || true
            /usr/bin/rm -f "$automount_unit_path"
            temp_mount=$(/usr/bin/mktemp)
            printf '%s\n' "$mount_unit" > "$temp_mount"
            /usr/bin/install -m 644 "$temp_mount" "$mount_unit_path"
            /usr/bin/rm -f "$temp_mount"
            /usr/bin/systemctl daemon-reload
            /usr/bin/systemctl enable "$mount_unit_name"
            if ! /usr/bin/mountpoint -q "$mount_point"; then
                /usr/bin/systemctl start "$mount_unit_name"
            fi
            """;

        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = pkexec,
                    UseShellExecute = false,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                },
            };
            process.StartInfo.ArgumentList.Add("--disable-internal-agent");
            process.StartInfo.ArgumentList.Add("/bin/sh");
            process.StartInfo.ArgumentList.Add("-c");
            process.StartInfo.ArgumentList.Add(script);
            process.StartInfo.ArgumentList.Add("qsurfer-systemd-mount");
            process.StartInfo.ArgumentList.Add(credentialsPath);
            process.StartInfo.ArgumentList.Add(credentialTarget);
            process.StartInfo.ArgumentList.Add(marker);
            process.StartInfo.ArgumentList.Add(mountUnitPath);
            process.StartInfo.ArgumentList.Add(automountUnitPath);
            process.StartInfo.ArgumentList.Add(mountUnit);
            process.StartInfo.ArgumentList.Add(mountUnitName);
            process.StartInfo.ArgumentList.Add(automountUnitName);
            process.StartInfo.ArgumentList.Add(mountPoint);
            process.StartInfo.ArgumentList.Add(GetUserId().ToString());
            process.StartInfo.ArgumentList.Add(GetGroupId().ToString());
            process.StartInfo.ArgumentList.Add(legacyMountPoint);
            process.StartInfo.ArgumentList.Add(legacyMountUnitPath);
            process.StartInfo.ArgumentList.Add(legacyMountUnitName);
            process.Start();

            var standardErrorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            using var authorizationCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            authorizationCancellation.CancelAfter(AuthorizationTimeout);
            await process.WaitForExitAsync(authorizationCancellation.Token);
            var standardError = (await standardErrorTask).Trim();
            if (process.ExitCode != 0 || (!isMounted && !HasPersistentSystemMount(mountPoint)))
            {
                var detail = string.IsNullOrWhiteSpace(standardError)
                    ? "The persistent mount could not be configured."
                    : standardError;
                return LinuxSmbMountResult.Failure($@"Could not configure persistent SMB mount for \\{server}\{share}. {detail}");
            }

            return LinuxSmbMountResult.Successful($@"\\{server}\{share}", mountPoint);
        }
        catch (OperationCanceledException)
        {
            return LinuxSmbMountResult.Failure("Configuring the persistent SMB mount timed out waiting for system authorization.");
        }
        catch (Exception ex)
        {
            return LinuxSmbMountResult.Failure($"Could not configure persistent SMB mount: {ex.Message}");
        }
    }

    private static async Task<string?> RemoveLegacyMountAsync(string legacyMountPoint, CancellationToken cancellationToken)
    {
        var pkexec = FindCommand("/usr/bin/pkexec", "/bin/pkexec");
        if (pkexec == null)
        {
            return "Migrating the old SMB mount needs Polkit (pkexec).";
        }

        var legacyMountUnitName = $"{SystemdEscapePath(legacyMountPoint)}.mount";
        var legacyMountUnitPath = $"/etc/systemd/system/{legacyMountUnitName}";
        const string script = """
            set -eu
            mount_point=$1
            mount_unit_path=$2
            mount_unit_name=$3
            if command -v systemctl >/dev/null 2>&1; then
                systemctl disable --now "$mount_unit_name" >/dev/null 2>&1 || true
                rm -f "$mount_unit_path"
                systemctl daemon-reload
            fi
            if mountpoint -q "$mount_point"; then
                umount "$mount_point" || true
            fi
            rmdir "$mount_point" 2>/dev/null || true
            """;

        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = pkexec,
                    UseShellExecute = false,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                },
            };
            process.StartInfo.ArgumentList.Add("--disable-internal-agent");
            process.StartInfo.ArgumentList.Add("/bin/sh");
            process.StartInfo.ArgumentList.Add("-c");
            process.StartInfo.ArgumentList.Add(script);
            process.StartInfo.ArgumentList.Add("qsurfer-remove-legacy-mount");
            process.StartInfo.ArgumentList.Add(legacyMountPoint);
            process.StartInfo.ArgumentList.Add(legacyMountUnitPath);
            process.StartInfo.ArgumentList.Add(legacyMountUnitName);
            process.Start();

            var standardErrorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            using var authorizationCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            authorizationCancellation.CancelAfter(AuthorizationTimeout);
            await process.WaitForExitAsync(authorizationCancellation.Token);
            if (process.ExitCode == 0)
            {
                return null;
            }

            var standardError = (await standardErrorTask).Trim();
            return string.IsNullOrWhiteSpace(standardError)
                ? "Could not remove the old QSurfer SMB mount."
                : $"Could not remove the old QSurfer SMB mount. {standardError}";
        }
        catch (OperationCanceledException)
        {
            return "Migrating the old SMB mount timed out waiting for system authorization.";
        }
        catch (Exception ex)
        {
            return $"Could not remove the old QSurfer SMB mount: {ex.Message}";
        }
    }

    private static string ShareIdentifier(string server, string share)
    {
        var identity = $"{server}\n{share}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..12].ToLowerInvariant();
    }

    private static string SystemdEscapePath(string path) =>
        EscapeSystemdText(Path.GetFullPath(path).Trim('/'), escapeSlashAsDash: true);

    private static string EscapeSystemdValue(string value) =>
        EscapeSystemdText(value ?? "", escapeSlashAsDash: false);

    private static string EscapeSystemdText(string value, bool escapeSlashAsDash)
    {
        var escaped = new StringBuilder();
        foreach (var valueByte in Encoding.UTF8.GetBytes(value))
        {
            if (escapeSlashAsDash && valueByte == (byte)'/')
            {
                escaped.Append('-');
            }
            else if (valueByte is >= (byte)'A' and <= (byte)'Z' or >= (byte)'a' and <= (byte)'z' or >= (byte)'0' and <= (byte)'9' ||
                     valueByte is (byte)':' or (byte)'_' or (byte)'.' || (!escapeSlashAsDash && valueByte is (byte)'/' or (byte)'-'))
            {
                escaped.Append((char)valueByte);
            }
            else
            {
                escaped.Append($"\\x{valueByte:x2}");
            }
        }
        return escaped.ToString();
    }

    private static string RuntimeDirectory()
    {
        var runtime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        return string.IsNullOrWhiteSpace(runtime) ? Path.GetTempPath() : runtime;
    }

    private static string? FindCommand(params string[] candidates) => candidates.FirstOrDefault(File.Exists);

    private static LinuxMountedShare? TryParseMountedShare(string source, string mountPoint, string configuredHost)
    {
        var parts = source.Trim().Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length < 2
            ? null
            : new LinuxMountedShare(NasIdentity.NormalizeShareRoot($@"\\{parts[0]}\{parts[1]}", configuredHost), mountPoint);
    }

    private static string UnescapeMountField(string value) => Regex.Replace(value, @"\\([0-7]{3})", match =>
        ((char)Convert.ToInt32(match.Groups[1].Value, 8)).ToString());

    private static async Task<bool> IsMountPointAsync(string path, CancellationToken cancellationToken)
    {
        var mountPoint = FindCommand("/usr/bin/mountpoint", "/bin/mountpoint");
        if (mountPoint == null)
        {
            return false;
        }
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = mountPoint,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };
        process.StartInfo.ArgumentList.Add("-q");
        process.StartInfo.ArgumentList.Add(path);
        process.Start();
        await process.WaitForExitAsync(cancellationToken);
        return process.ExitCode == 0;
    }

    [DllImport("libc")]
    private static extern uint getuid();

    [DllImport("libc")]
    private static extern uint getgid();

    private static uint GetUserId() => getuid();
    private static uint GetGroupId() => getgid();
}

public sealed record LinuxSmbMountResult(bool Succeeded, string ShareRoot, string MountPoint, string Error)
{
    public static LinuxSmbMountResult Successful(string shareRoot, string mountPoint) => new(true, shareRoot, mountPoint, "");
    public static LinuxSmbMountResult Failure(string error) => new(false, "", "", error);
}

public sealed record LinuxMountedShare(string ShareRoot, string MountPoint);
