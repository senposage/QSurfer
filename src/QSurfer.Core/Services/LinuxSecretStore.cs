using System.Diagnostics;

namespace QSurfer.Core.Services;

/// <summary>
/// Uses the freedesktop Secret Service API through secret-tool. GNOME Keyring
/// and KWallet expose this API, so the secret stays with the signed-in Linux user.
/// </summary>
public static class LinuxSecretStore
{
    private const string Schema = "qsurfer";
    private const string MachineAttribute = "machine";

    public static bool IsAvailable => !OperatingSystem.IsWindows() && FindSecretTool() != null;

    public static string? TryLoadConnectionPassword(string machineId)
    {
        var secretTool = FindSecretTool();
        if (secretTool == null || string.IsNullOrWhiteSpace(machineId))
        {
            return null;
        }

        try
        {
            using var process = CreateProcess(secretTool, "lookup", Schema, MachineAttribute, machineId);
            process.Start();
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            return process.ExitCode == 0 ? output.TrimEnd('\r', '\n') : null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            AppLogger.Warn("config", "could not read the Linux desktop keyring; using the encrypted local fallback when present");
            return null;
        }
    }

    public static bool TrySaveConnectionPassword(string machineId, string password)
    {
        var secretTool = FindSecretTool();
        if (secretTool == null || string.IsNullOrWhiteSpace(machineId) || string.IsNullOrEmpty(password))
        {
            return false;
        }

        try
        {
            using var process = CreateProcess(secretTool, "store", "--label=QSurfer NAS credentials", Schema, MachineAttribute, machineId);
            process.Start();
            process.StandardInput.Write(password);
            process.StandardInput.Close();
            process.WaitForExit();
            if (process.ExitCode == 0)
            {
                return true;
            }

            AppLogger.Warn("config", "Linux desktop keyring declined the NAS credential; using the encrypted local fallback");
            return false;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            AppLogger.Warn("config", "could not write the Linux desktop keyring; using the encrypted local fallback");
            return false;
        }
    }

    public static void TryRemoveConnectionPassword(string machineId)
    {
        var secretTool = FindSecretTool();
        if (secretTool == null || string.IsNullOrWhiteSpace(machineId))
        {
            return;
        }

        try
        {
            using var process = CreateProcess(secretTool, "clear", Schema, MachineAttribute, machineId);
            process.Start();
            process.WaitForExit();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            AppLogger.Warn("config", "could not remove the saved Linux desktop keyring credential");
        }
    }

    private static Process CreateProcess(string secretTool, params string[] arguments)
    {
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = secretTool,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            },
        };
        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }
        return process;
    }

    private static string? FindSecretTool()
    {
        foreach (var path in new[] { "/usr/bin/secret-tool", "/bin/secret-tool" })
        {
            if (File.Exists(path))
            {
                return path;
            }
        }
        return null;
    }
}
