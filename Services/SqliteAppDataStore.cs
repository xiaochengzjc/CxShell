using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;

namespace CxShell.Services;

/// <summary>
/// Shared versioned SQLite storage for application-owned persistent data.
/// Payloads are owned by their domain stores; sensitive payloads remain
/// encrypted before they are written here.
/// </summary>
public sealed class SqliteAppDataStore
{
    public const int CurrentSchemaVersion = 1;

    private const string DataTableName = "app_data";
    private static readonly ConcurrentDictionary<string, object> DatabaseLocks = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private readonly string _databasePath;
    private readonly object _databaseLock;

    public SqliteAppDataStore(string? storageDirectory = null)
    {
        var directory = string.IsNullOrWhiteSpace(storageDirectory)
            ? SessionStorageService.GetStorageDirectory()
            : Path.GetFullPath(storageDirectory);
        Directory.CreateDirectory(directory);
        _databasePath = Path.Combine(directory, "cxshell.db");
        _databaseLock = DatabaseLocks.GetOrAdd(_databasePath, static _ => new object());
        SQLitePCL.Batteries_V2.Init();
        EnsureSchema();
    }

    public string DatabasePath => _databasePath;

    public string? Read(string collection, string key = "default")
    {
        ValidateKey(collection, nameof(collection));
        ValidateKey(key, nameof(key));
        lock (_databaseLock)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT payload FROM {DataTableName} WHERE collection = $collection AND item_key = $key;";
            command.Parameters.AddWithValue("$collection", collection);
            command.Parameters.AddWithValue("$key", key);
            return command.ExecuteScalar() as string;
        }
    }

    public bool WriteIfMissing(string collection, string key, string payload)
    {
        ValidateKey(collection, nameof(collection));
        ValidateKey(key, nameof(key));
        ArgumentNullException.ThrowIfNull(payload);
        lock (_databaseLock)
        {
            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"""
                INSERT OR IGNORE INTO {DataTableName}(collection, item_key, payload, updated_at_utc)
                VALUES ($collection, $key, $payload, $updated);
                """;
            command.Parameters.AddWithValue("$collection", collection);
            command.Parameters.AddWithValue("$key", key);
            command.Parameters.AddWithValue("$payload", payload);
            command.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToString("O"));
            var inserted = command.ExecuteNonQuery() > 0;
            transaction.Commit();
            return inserted;
        }
    }

    public void Write(string collection, string key, string payload)
    {
        ValidateKey(collection, nameof(collection));
        ValidateKey(key, nameof(key));
        ArgumentNullException.ThrowIfNull(payload);
        lock (_databaseLock)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = $"""
                INSERT INTO {DataTableName}(collection, item_key, payload, updated_at_utc)
                VALUES ($collection, $key, $payload, $updated)
                ON CONFLICT(collection, item_key) DO UPDATE SET
                    payload = excluded.payload,
                    updated_at_utc = excluded.updated_at_utc;
                """;
            command.Parameters.AddWithValue("$collection", collection);
            command.Parameters.AddWithValue("$key", key);
            command.Parameters.AddWithValue("$payload", payload);
            command.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToString("O"));
            command.ExecuteNonQuery();
        }
    }

    public IReadOnlyList<(string Key, string Payload)> ReadCollection(string collection)
    {
        ValidateKey(collection, nameof(collection));
        lock (_databaseLock)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT item_key, payload FROM {DataTableName} WHERE collection = $collection ORDER BY item_key;";
            command.Parameters.AddWithValue("$collection", collection);
            using var reader = command.ExecuteReader();
            var values = new List<(string Key, string Payload)>();
            while (reader.Read())
                values.Add((reader.GetString(0), reader.GetString(1)));
            return values;
        }
    }

    public void Delete(string collection, string key)
    {
        ValidateKey(collection, nameof(collection));
        ValidateKey(key, nameof(key));
        lock (_databaseLock)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = $"DELETE FROM {DataTableName} WHERE collection = $collection AND item_key = $key;";
            command.Parameters.AddWithValue("$collection", collection);
            command.Parameters.AddWithValue("$key", key);
            command.ExecuteNonQuery();
        }
    }

    public void DeleteCollection(string collection)
    {
        ValidateKey(collection, nameof(collection));
        lock (_databaseLock)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = $"DELETE FROM {DataTableName} WHERE collection = $collection;";
            command.Parameters.AddWithValue("$collection", collection);
            command.ExecuteNonQuery();
        }
    }

    public bool ImportLegacyFile(
        string collection,
        string key,
        string legacyPath,
        Func<string, bool>? validator = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(legacyPath);
        if (!File.Exists(legacyPath))
            return false;

        var payload = File.ReadAllText(legacyPath);
        if (validator != null && !validator(payload))
            return false;

        var inserted = WriteIfMissing(collection, key, payload);
        ArchiveLegacyFile(legacyPath);
        return inserted;
    }

    public static void ArchiveLegacyFile(string path)
    {
        if (!File.Exists(path))
            return;

        var backupPath = path + ".migrated.bak";
        if (File.Exists(backupPath))
            backupPath = path + $".migrated-{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}.bak";

        try
        {
            File.Move(path, backupPath);
        }
        catch (IOException)
        {
            // Keep the original if it cannot be archived; the SQLite copy remains authoritative.
        }
        catch (UnauthorizedAccessException)
        {
            // Keep the original if it cannot be archived; the SQLite copy remains authoritative.
        }
    }

    private SqliteConnection OpenConnection()
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = false,
            DefaultTimeout = 15
        };
        var connection = new SqliteConnection(builder.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA busy_timeout = 15000;";
        command.ExecuteNonQuery();
        return connection;
    }

    private void EnsureSchema()
    {
        lock (_databaseLock)
        {
            using var connection = OpenConnection();
            ExecuteSchemaCommand(connection, "PRAGMA journal_mode = WAL;");
            ExecuteSchemaCommand(connection, $"""
                CREATE TABLE IF NOT EXISTS {DataTableName} (
                    collection TEXT NOT NULL,
                    item_key TEXT NOT NULL,
                    payload TEXT NOT NULL,
                    updated_at_utc TEXT NOT NULL,
                    PRIMARY KEY(collection, item_key)
                );
                """);
            ExecuteSchemaCommand(connection, """
                CREATE TABLE IF NOT EXISTS schema_metadata (
                    metadata_key TEXT NOT NULL PRIMARY KEY,
                    metadata_value TEXT NOT NULL
                );
                """);
            ExecuteSchemaCommand(connection, $"""
                INSERT INTO schema_metadata(metadata_key, metadata_value)
                VALUES ('schema_version', '{CurrentSchemaVersion}')
                ON CONFLICT(metadata_key) DO UPDATE SET metadata_value = '{CurrentSchemaVersion}'
                WHERE CAST(schema_metadata.metadata_value AS INTEGER) < {CurrentSchemaVersion};
                """);
        }
    }

    private static void ExecuteSchemaCommand(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static void ValidateKey(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value.Length > 200)
            throw new ArgumentOutOfRangeException(parameterName, "SQLite collection and item keys cannot exceed 200 characters.");
    }
}
