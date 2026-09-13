using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using QSurfer.Core.Models;

namespace QSurfer.Core.Services;

/// <summary>
/// Keeps workstation-specific preferences out of the portable deployment configuration.
/// The database lives under the current user's application-data directory.
/// </summary>
public sealed class MachineSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly string _connectionString;
    private readonly object _gate = new();
    private bool _initialized;

    public MachineSettingsStore()
    {
        var directory = UserDataPaths.Subdirectory("settings");
        var path = Path.Combine(directory, "settings.sqlite");
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            DefaultTimeout = 5,
        }.ToString();
        AppLogger.Info("config", "local SQLite settings initialized");
    }

    public HostConfig? Load()
    {
        lock (_gate)
        {
            try
            {
                using var connection = Open();
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT settings_json, password_protected, search_service_token_protected FROM machine_settings WHERE machine_id = $machine;";
                command.Parameters.AddWithValue("$machine", AppConfig.CurrentHostKey);
                using var reader = command.ExecuteReader();
                if (!reader.Read())
                {
                    reader.Close();
                    if (TryImportLegacyLinuxSettings(connection))
                    {
                        return Load();
                    }
                    AppLogger.Info("config", "no local SQLite settings found");
                    return null;
                }

                var payload = reader.GetString(0);
                var settings = JsonSerializer.Deserialize<HostConfig>(payload, JsonOptions);
                if (settings == null)
                {
                    return null;
                }

                var hasProtectedPassword = !reader.IsDBNull(1);
                var keyringAvailable = LinuxSecretStore.IsAvailable;
                var passwordLoadedFromKeyring = false;
                var requiresCredentialUpgrade = !string.IsNullOrWhiteSpace(settings.SearchService.Token);
                var requiresLayoutUpgrade = Math.Abs(settings.Behavior.FavoritesNavigationSplit - 0.6) < 0.001;
                if (requiresLayoutUpgrade)
                {
                    settings.Behavior.FavoritesNavigationSplit = 0.35;
                }
                if (!OperatingSystem.IsWindows())
                {
                    var keyringPassword = LinuxSecretStore.TryLoadConnectionPassword(AppConfig.CurrentHostKey);
                    if (keyringPassword != null)
                    {
                        settings.Password = keyringPassword;
                        passwordLoadedFromKeyring = true;
                    }
                }
                if (!passwordLoadedFromKeyring && hasProtectedPassword)
                {
                    var storedPassword = reader.GetFieldValue<byte[]>(1);
                    settings.Password = UnprotectStoredSecret(storedPassword, "NAS credential", out var passwordNeedsUpgrade);
                    requiresCredentialUpgrade |= passwordNeedsUpgrade;
                }
                requiresCredentialUpgrade |= !OperatingSystem.IsWindows() &&
                    keyringAvailable &&
                    !passwordLoadedFromKeyring &&
                    !string.IsNullOrWhiteSpace(settings.Password);
                if (!reader.IsDBNull(2))
                {
                    var tokenPayload = reader.GetFieldValue<byte[]>(2);
                    settings.SearchService.Token = UnprotectStoredSecret(tokenPayload, "QIndexer token", out var tokenNeedsUpgrade);
                    requiresCredentialUpgrade |= tokenNeedsUpgrade;
                }
                AppLogger.Info("config", "loaded local SQLite settings");
                if (requiresLayoutUpgrade || requiresCredentialUpgrade ||
                    (!hasProtectedPassword && !string.IsNullOrWhiteSpace(settings.Password)))
                {
                    // Upgrade stored layout defaults, plaintext, and older DPAPI-only credentials.
                    var legacyPassword = settings.Password;
                    reader.Close();
                    if (Save(settings))
                    {
                        settings.Password = legacyPassword;
                        AppLogger.Info("config", "migrated local settings to the current layout and credential format");
                    }
                }
                return settings;
            }
            catch (Exception ex)
            {
                AppLogger.Error("config", ex, "could not read local SQLite settings");
                return null;
            }
        }
    }

    public bool Save(HostConfig settings)
    {
        lock (_gate)
        {
            try
            {
                using var connection = Open();
                using var command = connection.CreateCommand();
                command.CommandText = """
                    INSERT INTO machine_settings (machine_id, settings_json, password_protected, search_service_token_protected, updated_at)
                    VALUES ($machine, $settings, $password, $serviceToken, $updated)
                    ON CONFLICT(machine_id) DO UPDATE SET
                        settings_json = excluded.settings_json,
                        password_protected = excluded.password_protected,
                        search_service_token_protected = excluded.search_service_token_protected,
                        updated_at = excluded.updated_at;
                    """;
                command.Parameters.AddWithValue("$machine", AppConfig.CurrentHostKey);
                command.Parameters.AddWithValue("$settings", SerializeWithoutPassword(settings));
                command.Parameters.Add("$password", SqliteType.Blob).Value = ProtectPassword(settings.Password) ?? (object)DBNull.Value;
                command.Parameters.Add("$serviceToken", SqliteType.Blob).Value = ProtectLocalSecret(settings.SearchService.Token) ?? (object)DBNull.Value;
                command.Parameters.AddWithValue("$updated", DateTime.UtcNow.Ticks);
                command.ExecuteNonQuery();
                AppLogger.Info("config", $"saved local SQLite settings hostPresent={!string.IsNullOrWhiteSpace(settings.Host)} userPresent={!string.IsNullOrWhiteSpace(settings.User)} credentialPresent={!string.IsNullOrWhiteSpace(settings.Password)}");
                return true;
            }
            catch (Exception ex)
            {
                AppLogger.Error("config", ex, "could not save local SQLite settings");
                return false;
            }
        }
    }

    private SqliteConnection Open()
    {
        var path = new SqliteConnectionStringBuilder(_connectionString).DataSource;
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? AppContext.BaseDirectory);
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        if (!_initialized)
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS machine_settings (
                    machine_id TEXT NOT NULL PRIMARY KEY,
                    settings_json TEXT NOT NULL,
                    password_protected BLOB NULL,
                    updated_at INTEGER NOT NULL
                );
                """;
            command.ExecuteNonQuery();
            EnsureColumn(connection, "machine_settings", "password_protected", "BLOB NULL");
            EnsureColumn(connection, "machine_settings", "search_service_token_protected", "BLOB NULL");
            _initialized = true;
        }
        return connection;
    }

    private static bool TryImportLegacyLinuxSettings(SqliteConnection destination)
    {
        if (OperatingSystem.IsWindows())
        {
            return false;
        }

        foreach (var legacyRoot in UserDataPaths.LegacyLinuxRoots())
        {
            var sourcePath = Path.Combine(legacyRoot, "settings", "settings.sqlite");
            if (!File.Exists(sourcePath))
            {
                continue;
            }

            try
            {
                using var source = new SqliteConnection(new SqliteConnectionStringBuilder
                {
                    DataSource = sourcePath,
                    Mode = SqliteOpenMode.ReadOnly,
                }.ToString());
                source.Open();
                var hasPassword = HasColumn(source, "machine_settings", "password_protected");
                var hasToken = HasColumn(source, "machine_settings", "search_service_token_protected");
                using var read = source.CreateCommand();
                read.CommandText = $"""
                    SELECT settings_json,
                           {(hasPassword ? "password_protected" : "NULL")} AS password_protected,
                           {(hasToken ? "search_service_token_protected" : "NULL")} AS search_service_token_protected,
                           updated_at
                    FROM machine_settings
                    WHERE machine_id = $machine COLLATE NOCASE
                    LIMIT 1;
                    """;
                read.Parameters.AddWithValue("$machine", AppConfig.CurrentHostKey);
                using var legacy = read.ExecuteReader();
                if (!legacy.Read())
                {
                    continue;
                }

                using var write = destination.CreateCommand();
                write.CommandText = """
                    INSERT INTO machine_settings (machine_id, settings_json, password_protected, search_service_token_protected, updated_at)
                    VALUES ($machine, $settings, $password, $serviceToken, $updated)
                    ON CONFLICT(machine_id) DO NOTHING;
                    """;
                write.Parameters.AddWithValue("$machine", AppConfig.CurrentHostKey);
                write.Parameters.AddWithValue("$settings", legacy.GetString(0));
                write.Parameters.Add("$password", SqliteType.Blob).Value = legacy.IsDBNull(1) ? DBNull.Value : legacy.GetFieldValue<byte[]>(1);
                write.Parameters.Add("$serviceToken", SqliteType.Blob).Value = legacy.IsDBNull(2) ? DBNull.Value : legacy.GetFieldValue<byte[]>(2);
                write.Parameters.AddWithValue("$updated", legacy.GetInt64(3));
                if (write.ExecuteNonQuery() > 0)
                {
                    AppLogger.Info("config", "imported legacy Linux local SQLite settings");
                    return true;
                }
            }
            catch (SqliteException)
            {
                // A partial or locked legacy database should never block startup.
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return false;
    }

    private static string SerializeWithoutPassword(HostConfig settings)
    {
        var node = JsonSerializer.SerializeToNode(settings, JsonOptions)?.AsObject()
            ?? throw new InvalidOperationException("Could not serialize local settings.");
        node["password"] = "";
        if (node["search_service"] is JsonObject service)
        {
            service["token"] = "";
            service["token_protected"] = "";
        }
        return node.ToJsonString(JsonOptions);
    }

    private static byte[]? ProtectLocalSecret(string secret)
    {
        if (string.IsNullOrEmpty(secret))
        {
            return null;
        }
        var payload = OperatingSystem.IsWindows()
            ? ProtectedData.Protect(System.Text.Encoding.UTF8.GetBytes(secret), optionalEntropy: null, DataProtectionScope.CurrentUser)
            : System.Text.Encoding.UTF8.GetBytes(secret);
        return DeploymentCredentialProtector.ProtectLocalPayload(payload);
    }

    private static byte[]? ProtectPassword(string password)
    {
        if (string.IsNullOrEmpty(password))
        {
            if (!OperatingSystem.IsWindows())
            {
                LinuxSecretStore.TryRemoveConnectionPassword(AppConfig.CurrentHostKey);
            }
            return null;
        }
        if (!OperatingSystem.IsWindows())
        {
            if (LinuxSecretStore.TrySaveConnectionPassword(AppConfig.CurrentHostKey, password))
            {
                return null;
            }

            // Preserve a usable connection on stripped-down desktops without a
            // Secret Service implementation. The payload remains encrypted and
            // is migrated into the keyring on the next successful save.
            return DeploymentCredentialProtector.ProtectLocalPayload(System.Text.Encoding.UTF8.GetBytes(password));
        }
        var dpapiPayload = ProtectedData.Protect(System.Text.Encoding.UTF8.GetBytes(password), optionalEntropy: null, DataProtectionScope.CurrentUser);
        return DeploymentCredentialProtector.ProtectLocalPayload(dpapiPayload);
    }

    private static string UnprotectPassword(byte[] protectedPassword)
    {
        if (protectedPassword.Length == 0)
        {
            return "";
        }
        if (!OperatingSystem.IsWindows())
        {
            return System.Text.Encoding.UTF8.GetString(DeploymentCredentialProtector.UnprotectLocalPayload(protectedPassword));
        }
        return System.Text.Encoding.UTF8.GetString(ProtectedData.Unprotect(protectedPassword, optionalEntropy: null, DataProtectionScope.CurrentUser));
    }

    private static string UnprotectStoredSecret(byte[] payload, string name, out bool needsUpgrade)
    {
        needsUpgrade = false;
        if (payload.Length == 0)
        {
            return "";
        }

        if (DeploymentCredentialProtector.IsLocalPayload(payload))
        {
            var protectedValue = DeploymentCredentialProtector.UnprotectLocalPayload(payload);
            return OperatingSystem.IsWindows()
                ? UnprotectPassword(protectedValue)
                : System.Text.Encoding.UTF8.GetString(protectedValue);
        }

        if (OperatingSystem.IsWindows())
        {
            needsUpgrade = true;
            return UnprotectPassword(payload);
        }

        // Early Linux builds stored the fallback as raw UTF-8. Secret Service may
        // be absent or decline a request on otherwise valid KDE/Fedora desktops,
        // so retain that credential and upgrade it on the next save.
        try
        {
            var value = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                .GetString(payload);
            needsUpgrade = !string.IsNullOrEmpty(value);
            return value;
        }
        catch (System.Text.DecoderFallbackException)
        {
            AppLogger.Warn("config", $"could not read legacy Linux {name}; enter it again to replace the unusable credential");
            return "";
        }
    }

    private static void EnsureColumn(SqliteConnection connection, string table, string column, string definition)
    {
        using var columns = connection.CreateCommand();
        columns.CommandText = $"PRAGMA table_info({table});";
        using var reader = columns.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }

        using var alter = connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {definition};";
        alter.ExecuteNonQuery();
    }

    private static bool HasColumn(SqliteConnection connection, string table, string column)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({table});";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (reader.GetString(1).Equals(column, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
