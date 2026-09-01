using System.IO;
using System.Text.Json;
using QSurfer.Core.Models;

namespace QSurfer.Core.Services;

public static class ConfigStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    public static string ConfigPath
    {
        get
        {
            var packageConfig = Path.Combine(AppContext.BaseDirectory, "config", "config.json");
            if (File.Exists(packageConfig))
            {
                return packageConfig;
            }

            var legacyPackageConfig = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "config", "config.json"));
            if (File.Exists(legacyPackageConfig))
            {
                return legacyPackageConfig;
            }

            var appConfig = Path.Combine(AppContext.BaseDirectory, "config.json");
            if (File.Exists(appConfig))
            {
                return appConfig;
            }

            var repoConfig = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "config.json"));
            return File.Exists(repoConfig) ? repoConfig : appConfig;
        }
    }

    public static string PortableRoot
    {
        get
        {
            var configDirectory = Path.GetDirectoryName(ConfigPath) ?? AppContext.BaseDirectory;
            return Path.GetFileName(configDirectory).Equals("config", StringComparison.OrdinalIgnoreCase)
                ? Path.GetDirectoryName(configDirectory) ?? configDirectory
                : configDirectory;
        }
    }

    public static AppConfig Load()
    {
        var path = ConfigPath;
        try
        {
            var text = File.ReadAllText(path);
            var config = JsonSerializer.Deserialize<AppConfig>(text, JsonOptions) ?? new AppConfig();
            config.ApplyCurrentHost();
            return config;
        }
        catch (Exception ex)
        {
            if (File.Exists(path))
            {
                BackupUnreadableConfig(path);
                AppLogger.Error("config", ex, $"could not read config; a recovery copy was kept path=\"{path}\"");
            }
            var config = new AppConfig();
            config.ApplyCurrentHost();
            return config;
        }
    }

    public static void Save(AppConfig config)
    {
        try
        {
            config.CaptureCurrentHost();
            var path = ConfigPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? AppContext.BaseDirectory);
            using var configLock = AcquireLock(path);
            if (configLock == null)
            {
                AppLogger.Warn("config", $"skipped save because another instance held the lock path=\"{path}\"");
                return;
            }

            var persisted = ReadPersisted(path);
            if (persisted == null)
            {
                BackupUnreadableConfig(path);
                persisted = new AppConfig();
                AppLogger.Warn("config", $"replaced unreadable config after preserving a recovery copy path=\"{path}\"");
            }

            var hostKey = AppConfig.CurrentHostKey;
            if (config.Hosts.TryGetValue(hostKey, out var currentHost))
            {
                persisted.Hosts[hostKey] = currentHost;
            }
            persisted.Host = config.Host;
            persisted.Port = config.Port;
            persisted.Ssl = config.Ssl;
            persisted.SslVerify = config.SslVerify;
            persisted.User = config.User;
            persisted.Password = config.Password;
            persisted.Exclude = config.Exclude;
            persisted.VisibilityRules = config.VisibilityRules;
            persisted.ClearRootMachineSettings();

            var tempPath = path + "." + Environment.ProcessId + ".tmp";
            try
            {
                File.WriteAllText(tempPath, JsonSerializer.Serialize(persisted, JsonOptions));
                ReplaceAtomically(tempPath, path);
            }
            finally
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }

            config.Hosts = persisted.Hosts;
            config.Exclude = persisted.Exclude;
            config.VisibilityRules = persisted.VisibilityRules;
            config.ApplyCurrentHost();
        }
        catch (Exception ex)
        {
            AppLogger.Error("config", ex, "config save failed");
        }
    }

    private static AppConfig? ReadPersisted(string path)
    {
        if (!File.Exists(path))
        {
            return new AppConfig();
        }

        try
        {
            var config = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(path), JsonOptions) ?? new AppConfig();
            config.MigrateLegacySettings();
            return config;
        }
        catch
        {
            return null;
        }
    }

    private static FileStream? AcquireLock(string path)
    {
        var lockPath = path + ".lock";
        for (var attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (attempt < 9)
            {
                Thread.Sleep(25);
            }
        }
        return null;
    }

    private static void ReplaceAtomically(string sourcePath, string destinationPath)
    {
        if (!File.Exists(destinationPath))
        {
            File.Move(sourcePath, destinationPath);
            return;
        }

        var backupPath = destinationPath + ".bak";
        try
        {
            File.Replace(sourcePath, destinationPath, backupPath, ignoreMetadataErrors: true);
        }
        catch (PlatformNotSupportedException)
        {
            File.Copy(sourcePath, destinationPath, overwrite: true);
        }
    }

    private static void BackupUnreadableConfig(string path)
    {
        try
        {
            var directory = Path.GetDirectoryName(path) ?? AppContext.BaseDirectory;
            var name = Path.GetFileNameWithoutExtension(path);
            var extension = Path.GetExtension(path);
            var backupPath = Path.Combine(directory, $"{name}.unreadable-{DateTime.Now:yyyyMMdd-HHmmss}{extension}");
            if (!File.Exists(backupPath))
            {
                File.Copy(path, backupPath);
            }
        }
        catch
        {
            // Logging the original config failure is more useful than masking it with a backup failure.
        }
    }
}
