using System.Text.Json;
using CxShell.Services;
using CxShell.Services.Agent;

namespace CxShell.Tests;

public sealed class AgentConversationHistoryStoreTests
{
    [Fact]
    public void SqliteStoreRoundTripsConversationAndSupportsDeleteAndClear()
    {
        var directory = CreateDirectory();
        var databasePath = Path.Combine(directory, "agent-history.db");
        try
        {
            var started = DateTimeOffset.UtcNow.AddMinutes(-1);
            var conversation = CreateConversation(started, "Inspect the host");
            var store = new SqliteAgentConversationHistoryStore(databasePath);
            store.Save(conversation);

            var loaded = Assert.Single(new SqliteAgentConversationHistoryStore(databasePath).Load());
            Assert.Equal(conversation.ConversationId, loaded.ConversationId);
            Assert.Equal(conversation.Title, loaded.Title);
            Assert.Equal("Inspect the host", loaded.Messages![0].Content);
            Assert.Equal("The host is healthy.", loaded.Messages[1].Content);
            Assert.Equal("Inspect the host", loaded.ContextMessages![1].Content);

            store.Delete(conversation.ConversationId);
            Assert.Empty(store.Load());

            store.Save(conversation);
            store.Clear();
            Assert.Empty(store.Load());
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public void SqliteStoreMigratesEncryptedJsonHistoryOnce()
    {
        var directory = CreateDirectory();
        var databasePath = Path.Combine(directory, "agent-history.db");
        var legacyPath = Path.Combine(directory, "agent-history.json");
        try
        {
            var conversation = CreateConversation(DateTimeOffset.UtcNow, "Migrated conversation");
            var legacyJson = JsonSerializer.Serialize(new[] { conversation });
            File.WriteAllText(legacyPath, PasswordEncryptionService.Encrypt(legacyJson));

            var store = new SqliteAgentConversationHistoryStore(databasePath, legacyPath);
            var loaded = Assert.Single(store.Load());
            Assert.Equal(conversation.ConversationId, loaded.ConversationId);
            Assert.Equal("The host is healthy.", loaded.Messages![1].Content);
            Assert.False(File.Exists(legacyPath));
            Assert.True(File.Exists(legacyPath + ".migrated.bak"));

            store.Clear();
            Assert.Empty(new SqliteAgentConversationHistoryStore(databasePath, legacyPath).Load());
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    private static AgentConversationHistoryRecord CreateConversation(
        DateTimeOffset timestamp,
        string title)
    {
        var conversationId = Guid.NewGuid().ToString("D");
        return new AgentConversationHistoryRecord(
            conversationId,
            title,
            timestamp,
            timestamp.AddSeconds(2),
            Mode: AgentChatMode.Agent,
            ContextMessages:
            [
                new AgentChatMessage("system", "You are the CxShell assistant."),
                new AgentChatMessage("user", "Inspect the host")
            ],
            Messages:
            [
                new AgentConversationMessageRecord("user", "Inspect the host", timestamp),
                new AgentConversationMessageRecord("assistant", "The host is healthy.", timestamp.AddSeconds(2))
            ]);
    }

    private static string CreateDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "CxShellTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void DeleteDirectory(string directory)
    {
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }
}
