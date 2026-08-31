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
        try
        {
            var text = File.ReadAllText(ConfigPath);
            var config = JsonSerializer.Deserialize<AppConfig>(text, JsonOptions) ?? new AppConfig();
            config.ApplyCurrentHost();
            return config;
        }
        catch
        {
            var config = new AppConfig();
            config.ApplyCurrentHost();
            return config;
        }
    }

    public static void Save(AppConfig config)
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
            AppLogger.Warn("config", $"skipped save because existing config could not be read path=\"{path}\"");
            return;
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
            File.Copy(tempPath, path, overwrite: true);
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
}
