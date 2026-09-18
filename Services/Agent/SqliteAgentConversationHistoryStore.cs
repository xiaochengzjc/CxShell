using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;

namespace CxShell.Services.Agent;

/// <summary>
/// SQLite-backed conversation history. Metadata remains queryable while the
/// message payload stays encrypted, so a database viewer cannot expose the
/// conversation transcript or attached document text directly.
/// </summary>
public sealed class SqliteAgentConversationHistoryStore : IAgentConversationHistoryStore
{
    private sealed record ConversationPayload(
        IReadOnlyList<AgentChatMessage>? ContextMessages,
        IReadOnlyList<AgentConversationMessageRecord>? Messages);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly object _gate = new();
    private readonly string _filePath;
    private readonly string _legacyFilePath;

    static SqliteAgentConversationHistoryStore()
    {
        SQLitePCL.Batteries_V2.Init();
    }

    public SqliteAgentConversationHistoryStore(string? filePath = null, string? legacyFilePath = null)
    {
        _filePath = string.IsNullOrWhiteSpace(filePath)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "CxShell",
                "agent-history.db")
            : Path.GetFullPath(filePath);
        _legacyFilePath = string.IsNullOrWhiteSpace(legacyFilePath)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "CxShell",
                "agent-history.json")
            : Path.GetFullPath(legacyFilePath);

        lock (_gate)
        {
            EnsureSchema();
            MigrateLegacyHistoryIfNeeded();
        }
    }

    public IReadOnlyList<AgentConversationHistoryRecord> Load()
    {
        lock (_gate)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT conversation_id, title, created_at_utc, updated_at_utc,
                       session_id, session_label, mode, provider, model,
                       is_pinned, is_archived, payload
                FROM conversations
                ORDER BY is_pinned DESC, updated_at_utc DESC;
                """;

            var records = new List<AgentConversationHistoryRecord>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var record = ReadRecord(reader);
                if (record != null)
                    records.Add(record);
            }

            return records;
        }
    }

    public void Save(AgentConversationHistoryRecord conversation)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        if (!IsValid(conversation))
            return;

        lock (_gate)
        {
            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO conversations (
                    conversation_id, title, created_at_utc, updated_at_utc,
                    session_id, session_label, mode, provider, model,
                    is_pinned, is_archived, payload)
                VALUES (
                    $conversation_id, $title, $created_at_utc, $updated_at_utc,
                    $session_id, $session_label, $mode, $provider, $model,
                    $is_pinned, $is_archived, $payload)
                ON CONFLICT(conversation_id) DO UPDATE SET
                    title = excluded.title,
                    created_at_utc = excluded.created_at_utc,
                    updated_at_utc = excluded.updated_at_utc,
                    session_id = excluded.session_id,
                    session_label = excluded.session_label,
                    mode = excluded.mode,
                    provider = excluded.provider,
                    model = excluded.model,
                    is_pinned = excluded.is_pinned,
                    is_archived = excluded.is_archived,
                    payload = excluded.payload;
                """;
            AddRecordParameters(command, conversation);
            command.ExecuteNonQuery();
            transaction.Commit();
        }
    }

    public void Delete(string conversationId)
    {
        if (string.IsNullOrWhiteSpace(conversationId))
            return;

        lock (_gate)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM conversations WHERE conversation_id = $conversation_id;";
            command.Parameters.AddWithValue("$conversation_id", conversationId);
            command.ExecuteNonQuery();
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM conversations;";
            command.ExecuteNonQuery();
        }
    }

    private SqliteConnection OpenConnection()
    {
        var directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = _filePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = false
        };
        var connection = new SqliteConnection(builder.ToString());
        connection.Open();
        return connection;
    }

    private void EnsureSchema()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode = WAL;
            CREATE TABLE IF NOT EXISTS conversations (
                conversation_id TEXT NOT NULL PRIMARY KEY,
                title TEXT NOT NULL,
                created_at_utc TEXT NOT NULL,
                updated_at_utc TEXT NOT NULL,
                session_id TEXT NULL,
                session_label TEXT NULL,
                mode INTEGER NOT NULL,
                provider TEXT NULL,
                model TEXT NULL,
                is_pinned INTEGER NOT NULL DEFAULT 0,
                is_archived INTEGER NOT NULL DEFAULT 0,
                payload TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_conversations_updated
                ON conversations(is_pinned DESC, updated_at_utc DESC);
            """;
        command.ExecuteNonQuery();
    }

    private void MigrateLegacyHistoryIfNeeded()
    {
        using var connection = OpenConnection();
        using var countCommand = connection.CreateCommand();
        countCommand.CommandText = "SELECT COUNT(*) FROM conversations;";
        if (Convert.ToInt64(countCommand.ExecuteScalar(), CultureInfo.InvariantCulture) > 0)
            return;

        if (!File.Exists(_legacyFilePath))
            return;

        var legacyStore = new JsonAgentConversationHistoryStore(_legacyFilePath);
        foreach (var conversation in legacyStore.Load())
            Save(conversation);
    }

    private static void AddRecordParameters(
        SqliteCommand command,
        AgentConversationHistoryRecord conversation)
    {
        var payload = new ConversationPayload(
            conversation.ContextMessages,
            conversation.Messages);
        var payloadJson = JsonSerializer.Serialize(payload, JsonOptions);
        var encryptedPayload = PasswordEncryptionService.Encrypt(payloadJson);

        command.Parameters.AddWithValue("$conversation_id", conversation.ConversationId);
        command.Parameters.AddWithValue("$title", conversation.Title);
        command.Parameters.AddWithValue("$created_at_utc", conversation.CreatedAtUtc.ToString("O"));
        command.Parameters.AddWithValue("$updated_at_utc", conversation.UpdatedAtUtc.ToString("O"));
        command.Parameters.AddWithValue("$session_id", (object?)conversation.SessionId ?? DBNull.Value);
        command.Parameters.AddWithValue("$session_label", (object?)conversation.SessionLabel ?? DBNull.Value);
        command.Parameters.AddWithValue("$mode", (int)conversation.Mode);
        command.Parameters.AddWithValue("$provider", (object?)conversation.Provider ?? DBNull.Value);
        command.Parameters.AddWithValue("$model", (object?)conversation.Model ?? DBNull.Value);
        command.Parameters.AddWithValue("$is_pinned", conversation.IsPinned ? 1 : 0);
        command.Parameters.AddWithValue("$is_archived", conversation.IsArchived ? 1 : 0);
        command.Parameters.AddWithValue("$payload", encryptedPayload);
    }

    private static AgentConversationHistoryRecord? ReadRecord(SqliteDataReader reader)
    {
        try
        {
            var payloadJson = PasswordEncryptionService.Decrypt(reader.GetString(11));
            var payload = JsonSerializer.Deserialize<ConversationPayload>(payloadJson, JsonOptions);
            if (payload?.Messages is not { Count: > 0 })
                return null;

            return new AgentConversationHistoryRecord(
                reader.GetString(0),
                reader.GetString(1),
                DateTimeOffset.Parse(reader.GetString(2), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                DateTimeOffset.Parse(reader.GetString(3), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                GetNullableString(reader, 4),
                GetNullableString(reader, 5),
                (AgentChatMode)reader.GetInt32(6),
                GetNullableString(reader, 7),
                GetNullableString(reader, 8),
                payload.ContextMessages,
                payload.Messages,
                reader.GetInt64(9) != 0,
                reader.GetInt64(10) != 0);
        }
        catch
        {
            return null;
        }
    }

    private static string? GetNullableString(SqliteDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static bool IsValid(AgentConversationHistoryRecord? conversation)
        => conversation != null &&
           Guid.TryParse(conversation.ConversationId, out _) &&
           !string.IsNullOrWhiteSpace(conversation.Title) &&
           conversation.Title.Length <= 160 &&
           conversation.Messages is { Count: > 0 } &&
           conversation.Messages.Count <= 1000;
}
