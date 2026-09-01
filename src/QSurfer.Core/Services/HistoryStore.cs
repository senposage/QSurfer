using System.Text.Json;
using System.IO;
using Microsoft.Data.Sqlite;
using QSurfer.Core.Models;

namespace QSurfer.Core.Services;

public sealed class HistoryStore
{
    private const int QueryLimit = 500;
    private readonly AppConfig _config;
    private readonly string _connectionString;
    private readonly object _gate = new();
    private bool _initialized;

    public HistoryStore(AppConfig config)
    {
        _config = config;
        var localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var directory = string.IsNullOrWhiteSpace(localData)
            ? Path.Combine(ConfigStore.PortableRoot, "data", "cache")
            : Path.Combine(localData, "QSurfer", "cache");
        var path = Path.Combine(directory, $"{Environment.MachineName.ToUpperInvariant()}.history.sqlite");
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            DefaultTimeout = 5,
        }.ToString();
    }

    public IReadOnlyList<SearchResult> Favorites(string group = "")
    {
        return ReadResults(
            "starred = 1" + (string.IsNullOrWhiteSpace(group) ? "" : " AND groups_json LIKE $group"),
            command =>
            {
                if (!string.IsNullOrWhiteSpace(group))
                {
                    command.Parameters.AddWithValue("$group", $"%\"{NormalizeGroup(group)}%" );
                }
            });
    }

    public IReadOnlyList<string> RecentSearches(int limit = 12)
    {
        lock (_gate)
        {
            try
            {
                using var connection = Open();
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT query FROM recent_searches WHERE machine_id = $machine AND user_id = $user ORDER BY used_at DESC LIMIT $limit;";
                BindScope(command);
                command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 50));
                using var reader = command.ExecuteReader();
                var results = new List<string>();
                while (reader.Read())
                {
                    results.Add(reader.GetString(0));
                }
                return results;
            }
            catch (Exception ex)
            {
                AppLogger.Error("history", ex, "recent search query failed");
                return [];
            }
        }
    }

    public void RecordSearch(string query)
    {
        var text = query.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }
        lock (_gate)
        {
            try
            {
                using var connection = Open();
                using var command = connection.CreateCommand();
                command.CommandText = """
                    INSERT INTO recent_searches (machine_id, user_id, query, used_at)
                    VALUES ($machine, $user, $query, $used)
                    ON CONFLICT(machine_id, user_id, query) DO UPDATE SET used_at = excluded.used_at;
                    """;
                BindScope(command);
                command.Parameters.AddWithValue("$query", text);
                command.Parameters.AddWithValue("$used", DateTime.UtcNow.Ticks);
                command.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                AppLogger.Error("history", ex, "recent search update failed");
            }
        }
    }

    public IReadOnlyList<SavedSearch> SavedSearches()
    {
        lock (_gate)
        {
            try
            {
                using var connection = Open();
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT id, name, query FROM saved_searches WHERE machine_id = $machine AND user_id = $user ORDER BY name COLLATE NOCASE;";
                BindScope(command);
                using var reader = command.ExecuteReader();
                var results = new List<SavedSearch>();
                while (reader.Read())
                {
                    results.Add(new SavedSearch(reader.GetInt64(0), reader.GetString(1), reader.GetString(2)));
                }
                return results;
            }
            catch (Exception ex)
            {
                AppLogger.Error("history", ex, "saved search query failed");
                return [];
            }
        }
    }

    public bool SaveSearch(string name, string query)
    {
        lock (_gate)
        {
            try
            {
                using var connection = Open();
                using var command = connection.CreateCommand();
                command.CommandText = """
                    INSERT INTO saved_searches (machine_id, user_id, name, query, saved_at)
                    VALUES ($machine, $user, $name, $query, $saved)
                    ON CONFLICT(machine_id, user_id, query) DO UPDATE SET name = excluded.name, saved_at = excluded.saved_at;
                    """;
                BindScope(command);
                command.Parameters.AddWithValue("$name", name.Trim());
                command.Parameters.AddWithValue("$query", query.Trim());
                command.Parameters.AddWithValue("$saved", DateTime.UtcNow.Ticks);
                command.ExecuteNonQuery();
                return true;
            }
            catch (Exception ex)
            {
                AppLogger.Error("history", ex, "saved search update failed");
                return false;
            }
        }
    }

    public void DeleteSavedSearch(long id)
    {
        lock (_gate)
        {
            try
            {
                using var connection = Open();
                using var command = connection.CreateCommand();
                command.CommandText = "DELETE FROM saved_searches WHERE id = $id AND machine_id = $machine AND user_id = $user;";
                BindScope(command);
                command.Parameters.AddWithValue("$id", id);
                command.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                AppLogger.Error("history", ex, "saved search delete failed");
            }
        }
    }

    public IReadOnlyList<string> FavoriteGroups()
    {
        lock (_gate)
        {
            try
            {
                using var connection = Open();
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT groups_json FROM history_results WHERE machine_id = $machine AND user_id = $user AND starred = 1;";
                BindScope(command);
                using var reader = command.ExecuteReader();
                return ReadGroups(reader)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(group => group, StringComparer.CurrentCultureIgnoreCase)
                    .ToList();
            }
            catch (Exception ex)
            {
                AppLogger.Error("history", ex, "favorite group query failed");
                return [];
            }
        }
    }

    public IReadOnlyList<string> GroupsFor(SearchResult result)
    {
        lock (_gate)
        {
            try
            {
                using var connection = Open();
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT groups_json FROM history_results WHERE result_key = $key AND machine_id = $machine AND user_id = $user;";
                command.Parameters.AddWithValue("$key", ResultKey(result));
                BindScope(command);
                var groups = command.ExecuteScalar() as string;
                return ParseGroups(groups);
            }
            catch (Exception ex)
            {
                AppLogger.Error("history", ex, "favorite group lookup failed");
                return [];
            }
        }
    }

    public void SetStarred(SearchResult result, bool starred) => SetStarred([result], starred);

    public void SetStarred(IEnumerable<SearchResult> source, bool starred)
    {
        if (!_config.History.Enabled)
        {
            return;
        }
        var results = source.DistinctBy(ResultKey, StringComparer.OrdinalIgnoreCase).ToList();
        lock (_gate)
        {
            try
            {
                using var connection = Open();
                using var transaction = connection.BeginTransaction();
                foreach (var result in results)
                {
                    UpsertResult(connection, transaction, result, preserveFavorite: false, starred, starred ? result.Groups : []);
                }
                transaction.Commit();
            }
            catch (Exception ex)
            {
                AppLogger.Error("history", ex, "sqlite favorite update failed");
            }
        }
    }

    public void SetGroups(SearchResult result, IEnumerable<string> groups) => SetGroups([result], groups);

    public void SetGroups(IEnumerable<SearchResult> source, IEnumerable<string> groups)
    {
        if (!_config.History.Enabled)
        {
            return;
        }
        var results = source.DistinctBy(ResultKey, StringComparer.OrdinalIgnoreCase).ToList();
        var normalized = groups.Where(group => !string.IsNullOrWhiteSpace(group)).Select(NormalizeGroup).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        lock (_gate)
        {
            try
            {
                using var connection = Open();
                using var transaction = connection.BeginTransaction();
                foreach (var result in results)
                {
                    UpsertResult(connection, transaction, result, preserveFavorite: false, starred: true, normalized);
                }
                transaction.Commit();
            }
            catch (Exception ex)
            {
                AppLogger.Error("history", ex, "sqlite group update failed");
            }
        }
    }

    public void SaveFavoriteStates(IEnumerable<SearchResult> source)
    {
        if (!_config.History.Enabled)
        {
            return;
        }

        var results = source.DistinctBy(ResultKey, StringComparer.OrdinalIgnoreCase).ToList();
        lock (_gate)
        {
            try
            {
                using var connection = Open();
                using var transaction = connection.BeginTransaction();
                foreach (var result in results)
                {
                    UpsertResult(
                        connection,
                        transaction,
                        result,
                        preserveFavorite: false,
                        starred: result.IsFavorite,
                        groups: result.IsFavorite ? result.Groups : []);
                }
                transaction.Commit();
            }
            catch (Exception ex)
            {
                AppLogger.Error("history", ex, "sqlite favorite state update failed");
            }
        }
    }

    public bool IsStarred(SearchResult result) => StarredKeys().Contains(ResultKey(result));

    public HashSet<string> StarredKeys()
    {
        lock (_gate)
        {
            try
            {
                using var connection = Open();
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT result_key FROM history_results WHERE machine_id = $machine AND user_id = $user AND starred = 1;";
                BindScope(command);
                using var reader = command.ExecuteReader();
                var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                while (reader.Read())
                {
                    keys.Add(reader.GetString(0));
                }
                return keys;
            }
            catch (Exception ex)
            {
                AppLogger.Error("history", ex, "sqlite favorite key query failed");
                return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }
        }
    }

    public void ClearCurrentMachine(bool clearStarred)
    {
        lock (_gate)
        {
            try
            {
                using var connection = Open();
                using var command = connection.CreateCommand();
                command.CommandText = clearStarred
                    ? "DELETE FROM history_results WHERE machine_id = $machine AND user_id = $user;"
                    : "DELETE FROM history_results WHERE machine_id = $machine AND user_id = $user AND starred = 0;";
                BindScope(command);
                command.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                AppLogger.Error("history", ex, "sqlite history clear failed");
            }
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            try
            {
                using var connection = Open();
                using var command = connection.CreateCommand();
                command.CommandText = """
                    DELETE FROM history_results WHERE machine_id = $machine AND user_id = $user;
                    DELETE FROM recent_searches WHERE machine_id = $machine AND user_id = $user;
                    DELETE FROM saved_searches WHERE machine_id = $machine AND user_id = $user;
                    VACUUM;
                    """;
                BindScope(command);
                command.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                AppLogger.Error("history", ex, "sqlite maintenance reset failed");
            }
        }
    }

    public static string ResultKey(SearchResult result) => ResultKey(result.Path, result.Name);

    private IReadOnlyList<SearchResult> ReadResults(string where, Action<SqliteCommand>? bind = null)
    {
        lock (_gate)
        {
            try
            {
                using var connection = Open();
                using var command = connection.CreateCommand();
                command.CommandText = $"""
                    SELECT name, extension, path, type, size, modified, is_folder, starred, groups_json, resolved_path, raw_json
                    FROM history_results
                    WHERE machine_id = $machine AND user_id = $user AND {where}
                    ORDER BY starred DESC, last_used DESC
                    LIMIT $limit;
                    """;
                BindScope(command);
                command.Parameters.AddWithValue("$limit", QueryLimit);
                bind?.Invoke(command);
                using var reader = command.ExecuteReader();
                var results = new List<SearchResult>();
                while (reader.Read())
                {
                    results.Add(ReadResult(reader));
                }
                return SortFoldersFirst(results);
            }
            catch (Exception ex)
            {
                AppLogger.Error("history", ex, "sqlite result query failed");
                return [];
            }
        }
    }

    private void UpsertResult(SqliteConnection connection, SqliteTransaction transaction, SearchResult result, bool preserveFavorite, bool starred = false, IEnumerable<string>? groups = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            INSERT INTO history_results (result_key, machine_id, user_id, name, extension, path, type, size, modified, is_folder, last_used, uses, starred, groups_json, resolved_path, raw_json)
            VALUES ($key, $machine, $user, $name, $extension, $path, $type, $size, $modified, $folder, $lastUsed, 1, $starred, $groups, $resolvedPath, $raw)
            ON CONFLICT(result_key, machine_id, user_id) DO UPDATE SET
              name = excluded.name,
              extension = excluded.extension,
              path = excluded.path,
              type = excluded.type,
              size = excluded.size,
              modified = excluded.modified,
              is_folder = excluded.is_folder,
              resolved_path = CASE WHEN excluded.resolved_path <> '' THEN excluded.resolved_path ELSE history_results.resolved_path END,
              last_used = excluded.last_used,
              uses = history_results.uses + 1,
              raw_json = excluded.raw_json,
              starred = {(preserveFavorite ? "history_results.starred" : "excluded.starred")},
              groups_json = {(preserveFavorite ? "history_results.groups_json" : "excluded.groups_json")};
            """;
        command.Parameters.AddWithValue("$key", ResultKey(result));
        BindScope(command);
        command.Parameters.AddWithValue("$name", result.Name);
        command.Parameters.AddWithValue("$extension", result.Extension);
        command.Parameters.AddWithValue("$path", result.Path);
        // Only Favorites need a durable Windows/UNC location; ordinary history stays lean.
        command.Parameters.AddWithValue("$resolvedPath", starred ? result.ResolvedPath ?? "" : "");
        command.Parameters.AddWithValue("$type", result.Type);
        command.Parameters.AddWithValue("$size", result.Size);
        command.Parameters.AddWithValue("$modified", result.Modified);
        command.Parameters.AddWithValue("$folder", result.IsFolder ? 1 : 0);
        command.Parameters.AddWithValue("$lastUsed", DateTime.UtcNow.Ticks);
        command.Parameters.AddWithValue("$starred", starred ? 1 : 0);
        command.Parameters.AddWithValue("$groups", JsonSerializer.Serialize((groups ?? []).ToList()));
        command.Parameters.AddWithValue("$raw", RawJson(result));
        command.ExecuteNonQuery();
    }

    private SqliteConnection Open()
    {
        var path = new SqliteConnectionStringBuilder(_connectionString).DataSource;
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ConfigStore.PortableRoot);
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using (var busyTimeout = connection.CreateCommand())
        {
            busyTimeout.CommandText = "PRAGMA busy_timeout = 5000;";
            busyTimeout.ExecuteNonQuery();
        }
        if (_initialized)
        {
            return connection;
        }
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode = WAL;
            PRAGMA synchronous = NORMAL;
            CREATE TABLE IF NOT EXISTS history_results (
              result_key TEXT NOT NULL,
              machine_id TEXT NOT NULL,
              user_id TEXT NOT NULL,
              name TEXT NOT NULL,
              extension TEXT NOT NULL,
              path TEXT NOT NULL,
              type TEXT NOT NULL,
              size INTEGER NOT NULL,
              modified TEXT NOT NULL,
              is_folder INTEGER NOT NULL,
              last_used INTEGER NOT NULL,
              uses INTEGER NOT NULL,
              starred INTEGER NOT NULL,
              groups_json TEXT NOT NULL,
              resolved_path TEXT NOT NULL DEFAULT '',
              raw_json TEXT NOT NULL,
              PRIMARY KEY (result_key, machine_id, user_id)
            );
            CREATE INDEX IF NOT EXISTS idx_history_scope_last_used ON history_results(machine_id, user_id, last_used DESC);
            CREATE INDEX IF NOT EXISTS idx_history_scope_starred ON history_results(machine_id, user_id, starred);
            CREATE TABLE IF NOT EXISTS recent_searches (
              machine_id TEXT NOT NULL,
              user_id TEXT NOT NULL,
              query TEXT NOT NULL,
              used_at INTEGER NOT NULL,
              PRIMARY KEY (machine_id, user_id, query)
            );
            CREATE TABLE IF NOT EXISTS saved_searches (
              id INTEGER PRIMARY KEY AUTOINCREMENT,
              machine_id TEXT NOT NULL,
              user_id TEXT NOT NULL,
              name TEXT NOT NULL,
              query TEXT NOT NULL,
              saved_at INTEGER NOT NULL,
              UNIQUE (machine_id, user_id, query)
            );
            """;
        command.ExecuteNonQuery();
        EnsureColumn(connection, "history_results", "resolved_path", "TEXT NOT NULL DEFAULT ''");
        _initialized = true;
        return connection;
    }

    private static SearchResult ReadResult(SqliteDataReader reader)
    {
        var raw = reader.GetString(10);
        SearchResult result;
        try
        {
            using var document = JsonDocument.Parse(raw);
            result = QsirchClient.ResultFromJson(document.RootElement.Clone());
            result.Raw = document.RootElement.Clone();
        }
        catch
        {
            result = new SearchResult();
        }
        // The indexed columns are portable and normalized when the result is saved.
        // Raw Qsirch payloads are retained for optional thumbnail actions only.
        result.Name = reader.GetString(0);
        result.Extension = reader.GetString(1);
        result.Path = reader.GetString(2);
        result.ResolvedPath = reader.IsDBNull(9) ? "" : reader.GetString(9);
        result.Type = reader.GetString(3);
        result.Size = reader.GetInt64(4);
        result.Modified = reader.GetString(5);
        result.IsFolder = reader.GetInt64(6) != 0;
        result.IsFavorite = reader.GetInt64(7) != 0;
        result.Groups = ParseGroups(reader.GetString(8)).ToList();
        return result;
    }

    public void UpdateResolvedPath(SearchResult result)
    {
        if (!_config.History.Enabled || string.IsNullOrWhiteSpace(result.ResolvedPath))
        {
            return;
        }

        lock (_gate)
        {
            try
            {
                using var connection = Open();
                using var command = connection.CreateCommand();
                command.CommandText = "UPDATE history_results SET resolved_path = $resolvedPath WHERE result_key = $key AND machine_id = $machine AND user_id = $user AND starred = 1;";
                command.Parameters.AddWithValue("$resolvedPath", result.ResolvedPath);
                command.Parameters.AddWithValue("$key", ResultKey(result));
                BindScope(command);
                command.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                AppLogger.Error("history", ex, "sqlite resolved path update failed");
            }
        }
    }

    private static void EnsureColumn(SqliteConnection connection, string table, string column, string definition)
    {
        using var columns = connection.CreateCommand();
        columns.CommandText = $"PRAGMA table_info({table});";
        using var reader = columns.ExecuteReader();
        while (reader.Read())
        {
            if (reader.GetString(1).Equals(column, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }

        using var alter = connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {definition};";
        alter.ExecuteNonQuery();
    }

    private static IEnumerable<string> ReadGroups(SqliteDataReader reader)
    {
        while (reader.Read())
        {
            foreach (var group in ParseGroups(reader.GetString(0)))
            {
                yield return group;
            }
        }
    }

    private static string RawJson(SearchResult result) => result.Raw.ValueKind == JsonValueKind.Object ? result.Raw.GetRawText() : "{}";

    private static IReadOnlyList<string> ParseGroups(string? value)
    {
        try
        {
            return JsonSerializer.Deserialize<List<string>>(value ?? "[]")?
                .Where(group => !string.IsNullOrWhiteSpace(group))
                .Select(NormalizeGroup)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList() ?? [];
        }
        catch
        {
            return [];
        }
    }

    private void BindScope(SqliteCommand command)
    {
        command.Parameters.AddWithValue("$machine", Environment.MachineName);
        command.Parameters.AddWithValue("$user", UserId);
    }

    private static List<SearchResult> SortFoldersFirst(IEnumerable<SearchResult> results) => results
        .OrderBy(result => result.IsFolder ? 0 : 1)
        .ThenBy(result => result.FileName, StringComparer.CurrentCultureIgnoreCase)
        .ToList();

    private static string NormalizeGroup(string group) => group.Trim().Replace('/', '\\').Trim('\\');
    private static string UserId => $"{Environment.UserDomainName}\\{Environment.UserName}";
    private static string ResultKey(string path, string name) => $"{path}\0{name}".ToLowerInvariant();
}
