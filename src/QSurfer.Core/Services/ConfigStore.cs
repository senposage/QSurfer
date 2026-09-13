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

    // The portable file is deliberately limited to deployment defaults and rules
    // explicitly marked global. Workstation settings live in local SQLite.
    public static string ConfigPath => Path.Combine(AppContext.BaseDirectory, "config", "config.json");
    private static readonly MachineSettingsStore MachineSettings = new();

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
        var config = ReadPersisted(path);
        if (config == null)
        {
            if (File.Exists(path))
            {
                BackupUnreadableConfig(path);
                AppLogger.Warn("config", $"could not read shared config; a recovery copy was kept path=\"{path}\"");
            }
            config = new AppConfig();
        }
        HydrateSharedPassword(config, path);
        HydrateSharedSearchServiceToken(config, path);

        try
        {
            var localSettings = MachineSettings.Load();
            if (localSettings != null)
            {
                var seededConnection = false;
                if (HasSharedConnectionDefaults(config) && !HasConnectionSettings(localSettings))
                {
                    CopySharedConnectionDefaults(config, localSettings);
                    seededConnection = true;
                }
                if (HasSharedSearchServiceDefaults(config) && !HasSearchServiceSettings(localSettings))
                {
                    CopySharedSearchServiceDefaults(config, localSettings);
                    seededConnection = true;
                }
                if (seededConnection)
                {
                    MachineSettings.Save(localSettings);
                    AppLogger.Info("config", "seeded incomplete local search settings from shared deployment defaults");
                }
                config.Hosts[AppConfig.CurrentHostKey] = localSettings;
            }
            else if (config.Hosts.Remove(AppConfig.CurrentHostKey, out var legacySettings))
            {
                // Migrate the one legacy record for this workstation, then remove
                // it so future changes cannot leak back into the portable config.
                if (MachineSettings.Save(legacySettings))
                {
                    using var configLock = AcquireLock(path);
                    if (configLock != null)
                    {
                        SaveSharedConfigIfChanged(config, path);
                    }
                    else
                    {
                        AppLogger.Warn("config", "migrated local settings but could not remove the legacy shared record because the portable config was locked");
                    }
                    config.Hosts[AppConfig.CurrentHostKey] = legacySettings;
                    AppLogger.Info("config", "migrated workstation overrides from shared config into local SQLite");
                }
                else
                {
                    config.Hosts[AppConfig.CurrentHostKey] = legacySettings;
                }
            }
            else if (HasSharedConnectionDefaults(config) || HasSharedSearchServiceDefaults(config))
            {
                // New workstations begin with the portable deployment defaults,
                // then own their copy in the local SQLite database from that point on.
                config.CaptureCurrentHost();
                if (config.Hosts.TryGetValue(AppConfig.CurrentHostKey, out var importedSettings) && MachineSettings.Save(importedSettings))
                {
                    AppLogger.Info("config", "seeded local SQLite settings from shared deployment defaults");
                }
            }
            config.ApplyCurrentHost();
            AppLogger.Info("config", "configuration ready");
            return config;
        }
        catch (Exception ex)
        {
            AppLogger.Error("config", ex, "could not apply local SQLite settings");
            config.ApplyCurrentHost();
            return config;
        }
    }

    public static void Save(AppConfig config)
    {
        if (RuntimeMode.IsDemo)
        {
            AppLogger.Info("demo", "ignored configuration save from isolated demo mode");
            return;
        }

        try
        {
            config.CaptureCurrentHost();
            var hostKey = AppConfig.CurrentHostKey;
            if (!config.Hosts.TryGetValue(hostKey, out var currentHost))
            {
                return;
            }

            // Most saves are harmless UI preference changes. If one arrives
            // while the live connection is blank, never let it erase a
            // previously configured workstation connection.
            var existingSettings = MachineSettings.Load();
            if (existingSettings != null)
            {
                var preservedConnection = false;
                if (HasConnectionSettings(existingSettings) && !HasConnectionSettings(currentHost))
                {
                    CopyConnectionSettings(existingSettings, currentHost);
                    preservedConnection = true;
                }
                if (HasSearchServiceSettings(existingSettings) && !HasSearchServiceSettings(currentHost))
                {
                    CopySearchServiceSettings(existingSettings, currentHost);
                    preservedConnection = true;
                }
                if (preservedConnection)
                {
                    AppLogger.Warn("config", "preserved existing local search connection settings during an empty automatic save");
                }
            }

            if (!MachineSettings.Save(currentHost))
            {
                return;
            }

            var path = ConfigPath;
            using var configLock = AcquireLock(path);
            if (configLock == null)
            {
                AppLogger.Warn("config", $"local settings were saved, but shared defaults could not be updated because another instance held the lock path=\"{path}\"");
                config.ApplyCurrentHost();
                return;
            }

            var persisted = ReadPersisted(path);
            if (persisted == null)
            {
                BackupUnreadableConfig(path);
                persisted = new AppConfig();
                AppLogger.Warn("config", $"replaced unreadable config after preserving a recovery copy path=\"{path}\"");
            }

            persisted.Hosts.Remove(hostKey);
            // Local connection and UI changes must never overwrite a shared
            // deployment profile. Shared connection updates use the explicit
            // administrator-only method below.
            persisted.Password = "";
            persisted.SearchService.Token = "";
            persisted.PathMappings = config.PathMappings;
            persisted.Exclude = config.Exclude;
            persisted.VisibilityRules = config.VisibilityRules;

            SaveSharedConfigIfChanged(persisted, path);

            config.Hosts = persisted.Hosts;
            config.Hosts[hostKey] = currentHost;
            config.Exclude = persisted.Exclude;
            config.VisibilityRules = persisted.VisibilityRules;
            config.ApplyCurrentHost();
        }
        catch (Exception ex)
        {
            AppLogger.Error("config", ex, "config save failed");
        }
        finally
        {
            // CaptureCurrentHost temporarily restores the portable defaults.
            // Every exit path must put the active host record back into the
            // live config, including a transient database or file-lock error.
            config.ApplyCurrentHost();
        }
    }

    private static void SaveSharedConfigIfChanged(AppConfig config, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? AppContext.BaseDirectory);
        var serialized = JsonSerializer.Serialize(config, JsonOptions);
        if (File.Exists(path) && string.Equals(File.ReadAllText(path), serialized, StringComparison.Ordinal))
        {
            return;
        }

        var tempPath = path + "." + Environment.ProcessId + ".tmp";
        try
        {
            File.WriteAllText(tempPath, serialized);
            ReplaceAtomically(tempPath, path);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    private static bool HasSharedConnectionDefaults(AppConfig config) =>
        !string.IsNullOrWhiteSpace(config.Host) &&
        !string.IsNullOrWhiteSpace(config.User) &&
        !string.IsNullOrWhiteSpace(config.Password);

    private static bool HasConnectionSettings(HostConfig? settings) =>
        settings != null &&
        !string.IsNullOrWhiteSpace(settings.Host) &&
        !string.IsNullOrWhiteSpace(settings.User) &&
        !string.IsNullOrWhiteSpace(settings.Password);

    private static bool HasSharedSearchServiceDefaults(AppConfig config) => config.SearchService.IsConfigured;

    private static bool HasSearchServiceSettings(HostConfig? settings) => settings?.SearchService.HasCredentials == true;

    private static void CopySharedConnectionDefaults(AppConfig config, HostConfig settings)
    {
        settings.SearchProvider = SearchProviders.Qsirch;
        settings.Host = config.Host;
        settings.Port = config.Port;
        settings.Ssl = config.Ssl;
        settings.SslVerify = config.SslVerify;
        settings.User = config.User;
        settings.Password = config.Password;
    }

    private static void CopyConnectionSettings(HostConfig source, HostConfig target)
    {
        target.SearchProvider = SearchProviders.Qsirch;
        target.Host = source.Host;
        target.Port = source.Port;
        target.Ssl = source.Ssl;
        target.SslVerify = source.SslVerify;
        target.User = source.User;
        target.Password = source.Password;
    }

    private static void CopySharedSearchServiceDefaults(AppConfig config, HostConfig settings) =>
        settings.SearchService = CloneSearchService(config.SearchService);

    private static void CopySearchServiceSettings(HostConfig source, HostConfig target) =>
        target.SearchService = CloneSearchService(source.SearchService);

    public static bool SaveSharedDeploymentConnection(string host, int port, bool ssl, bool sslVerify, string user, string password)
    {
        if (RuntimeMode.IsDemo)
        {
            AppLogger.Info("demo", "ignored shared connection save from isolated demo mode");
            return false;
        }

        if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(user) ||
            string.IsNullOrWhiteSpace(password))
        {
            return false;
        }

        var path = ConfigPath;
        using var configLock = AcquireLock(path);
        if (configLock == null)
        {
            AppLogger.Warn("config", "could not update shared deployment connection because the portable config was locked");
            return false;
        }

        var persisted = ReadPersisted(path) ?? new AppConfig();
        persisted.SearchProvider = SearchProviders.Qsirch;
        persisted.Host = NasIdentity.NormalizeHost(host);
        persisted.Port = port;
        persisted.Ssl = ssl;
        persisted.SslVerify = sslVerify;
        persisted.User = user.Trim();
        persisted.Password = "";
        persisted.ProtectedPassword = DeploymentCredentialProtector.Protect(password);
        SaveSharedConfigIfChanged(persisted, path);
        AppLogger.Info("config", "updated encrypted shared deployment connection");
        return true;
    }

    public static bool SaveSharedDeploymentSearchService(SearchServiceConnection service)
    {
        if (RuntimeMode.IsDemo || !service.IsConfigured)
        {
            return false;
        }

        var path = ConfigPath;
        using var configLock = AcquireLock(path);
        if (configLock == null)
        {
            AppLogger.Warn("config", "could not update shared deployment search service because the portable config was locked");
            return false;
        }

        var persisted = ReadPersisted(path) ?? new AppConfig();
        persisted.SearchService = CloneSearchService(service);
        persisted.SearchService.Token = "";
        persisted.SearchService.ProtectedToken = DeploymentCredentialProtector.Protect(service.Token);
        SaveSharedConfigIfChanged(persisted, path);
        AppLogger.Info("config", "updated encrypted shared deployment search-service token");
        return true;
    }

    private static void HydrateSharedPassword(AppConfig config, string path)
    {
        if (!string.IsNullOrWhiteSpace(config.Password))
        {
            config.ProtectedPassword = DeploymentCredentialProtector.Protect(config.Password);
            config.Password = "";
            using var configLock = AcquireLock(path);
            if (configLock == null)
            {
                AppLogger.Warn("config", "could not convert a legacy shared password because the portable config was locked");
                return;
            }

            var persisted = ReadPersisted(path);
            if (persisted == null)
            {
                return;
            }
            persisted.Password = "";
            persisted.ProtectedPassword = config.ProtectedPassword;
            SaveSharedConfigIfChanged(persisted, path);
            AppLogger.Info("config", "converted a legacy shared password into an encrypted deployment credential");
        }

        if (string.IsNullOrWhiteSpace(config.ProtectedPassword))
        {
            return;
        }

        try
        {
            config.Password = DeploymentCredentialProtector.Unprotect(config.ProtectedPassword);
        }
        catch (Exception ex)
        {
            config.Password = "";
            AppLogger.Error("config", ex, "could not decrypt the shared deployment credential");
        }
    }

    private static void HydrateSharedSearchServiceToken(AppConfig config, string path)
    {
        var service = config.SearchService;
        if (!string.IsNullOrWhiteSpace(service.Token))
        {
            service.ProtectedToken = DeploymentCredentialProtector.Protect(service.Token);
            service.Token = "";
            using var configLock = AcquireLock(path);
            if (configLock == null)
            {
                AppLogger.Warn("config", "could not convert a legacy shared search-service token because the portable config was locked");
                return;
            }

            var persisted = ReadPersisted(path);
            if (persisted == null)
            {
                return;
            }
            persisted.SearchService.Token = "";
            persisted.SearchService.ProtectedToken = service.ProtectedToken;
            SaveSharedConfigIfChanged(persisted, path);
        }

        if (string.IsNullOrWhiteSpace(service.ProtectedToken))
        {
            return;
        }

        try
        {
            service.Token = DeploymentCredentialProtector.Unprotect(service.ProtectedToken);
        }
        catch (Exception ex)
        {
            service.Token = "";
            AppLogger.Error("config", ex, "could not decrypt the shared deployment search-service token");
        }
    }

    private static SearchServiceConnection CloneSearchService(SearchServiceConnection service) => new()
    {
        Enabled = service.Enabled,
        Host = service.Host,
        Port = service.Port,
        Ssl = service.Ssl,
        SslVerify = service.SslVerify,
        Token = service.Token,
        ProtectedToken = service.ProtectedToken,
    };

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
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? AppContext.BaseDirectory);
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
