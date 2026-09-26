using System.Text.Json;
using CxShell.Services;
using CxShell.Services.Agent;

namespace CxShell.Tests;

public sealed class AgentRunHistoryStoreTests
{
    [Fact]
    public void StoreRoundTripsCompletedSummariesAndSkipsActiveRuns()
    {
        var directory = Path.Combine(Path.GetTempPath(), "CxShellTests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "agent-runs.json");
        try
        {
            var sessionId = Guid.NewGuid();
            var started = DateTimeOffset.UtcNow.AddSeconds(-3);
            var store = new SqliteAgentRunHistoryStore(path);
            store.Save(
            [
                new AgentRuntimeRunSnapshot(
                    "completed-run",
                    sessionId.ToString("D"),
                    started,
                    "failed",
                    started.AddSeconds(2),
                    "provider_error",
                    7,
                    "provider",
                    "model",
                    "check the host",
                    "provider failed",
                    "Network",
                    2,
                    3,
                    2000,
                    started.AddSeconds(2)),
                new AgentRuntimeRunSnapshot(
                    "active-run",
                    sessionId.ToString("D"),
                    started,
                    "running")
            ]);

            var loaded = Assert.Single(store.Load());
            Assert.Equal("completed-run", loaded.RunId);
            Assert.Equal("check the host", loaded.PromptPreview);
            Assert.Equal(2, loaded.ToolCallCount);
            Assert.Equal(3, loaded.ModelRequestCount);
            Assert.Equal(2000, loaded.DurationMs);
            Assert.DoesNotContain("active-run", store.Load().Select(run => run.RunId));
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void StoreMigratesLegacyCompletedHistoryIntoSharedDatabase()
    {
        var directory = Path.Combine(Path.GetTempPath(), "CxShellTests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "agent-runs.json");
        try
        {
            Directory.CreateDirectory(directory);
            var legacy = new AgentRuntimeRunSnapshot(
                "legacy-run",
                Guid.NewGuid().ToString("D"),
                DateTimeOffset.UtcNow,
                "completed");
            File.WriteAllText(path, JsonSerializer.Serialize(new[] { legacy }));

            var store = new SqliteAgentRunHistoryStore(path);

            Assert.Equal("legacy-run", Assert.Single(store.Load()).RunId);
            Assert.False(File.Exists(path));
            Assert.True(File.Exists(path + ".migrated.bak"));
            Assert.True(File.Exists(Path.Combine(directory, "cxshell.db")));
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ClearCompletedSummariesWritesAnEmptyHistory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "CxShellTests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "agent-runs.json");
        try
        {
            var store = new SqliteAgentRunHistoryStore(path);
            store.Save(
            [
                new AgentRuntimeRunSnapshot(
                    "completed-run",
                    Guid.NewGuid().ToString("D"),
                    DateTimeOffset.UtcNow,
                    "completed")
            ]);

            using var gateway = new AgentSessionGateway(new DelegateAgentSessionHost(() => []));
            using var coordinator = new AgentRunCoordinator(gateway, historyStore: store);

            Assert.Equal(1, coordinator.ClearCompletedRuns());
            Assert.Empty(store.Load());
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void StoreRoundTripsRecoverableMetadataSeparatelyFromCompletedHistory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "CxShellTests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "agent-runs.json");
        try
        {
            var sessionId = Guid.NewGuid();
            var checkpoint = new AgentRunCheckpoint(
                2,
                "tool_call",
                "waiting_for_input",
                ToolCallId: "tool-1",
                ToolName: "session_command",
                ModelRequestCount: 2,
                ToolCallCount: 1,
                UpdatedAtUtc: DateTimeOffset.UtcNow,
                Detail: "Waiting for user input.");
            var snapshot = new AgentRuntimeRunSnapshot(
                "interrupted-run",
                sessionId.ToString("D"),
                DateTimeOffset.UtcNow,
                "interrupted",
                EndReason: "application_restart",
                CanResume: true,
                Checkpoint: checkpoint);
            var store = new SqliteAgentRunHistoryStore(path);
            store.SaveRecoverable(
            [
                new AgentRunRecoveryState(
                    snapshot,
                    [
                        new AgentChatMessage("system", "system prompt"),
                        new AgentChatMessage("user", "inspect the host")
                    ],
                    TimeoutMs: 600_000,
                    Checkpoint: checkpoint)
            ]);

            var loaded = Assert.Single(store.LoadRecoverable());
            Assert.Equal("interrupted-run", loaded.Snapshot.RunId);
            Assert.Equal("inspect the host", loaded.Messages[^1].Content);
            Assert.Empty(store.Load());
            Assert.True(loaded.ExpiresAtUtc > DateTimeOffset.UtcNow);
            Assert.Equal(checkpoint, loaded.Checkpoint);
            Assert.Equal(checkpoint, loaded.Snapshot.Checkpoint);
            var persisted = new SqliteAppDataStore(directory).Read("agent_runs", "recovery")!;
            Assert.StartsWith("cxaes:", persisted, StringComparison.Ordinal);
            Assert.DoesNotContain("inspect the host", persisted, StringComparison.Ordinal);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
            var recoveryPath = path + ".recovery";
            if (File.Exists(recoveryPath))
                File.Delete(recoveryPath);
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void StoreMigratesLegacyPlaintextRecoveryAndDropsExpiredRecords()
    {
        var directory = Path.Combine(Path.GetTempPath(), "CxShellTests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "agent-runs.json");
        try
        {
            Directory.CreateDirectory(directory);
            var recoveryPath = path + ".recovery";
            var sessionId = Guid.NewGuid();
            var valid = new AgentRunRecoveryState(
                new AgentRuntimeRunSnapshot(
                    "legacy-valid",
                    sessionId.ToString("D"),
                    DateTimeOffset.UtcNow,
                    "interrupted",
                    CanResume: true),
                [new AgentChatMessage("user", "legacy prompt")]);
            var expired = valid with
            {
                Snapshot = valid.Snapshot with { RunId = "legacy-expired" },
                ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1)
            };
            File.WriteAllText(
                recoveryPath,
                JsonSerializer.Serialize(new[] { valid, expired }));

            var store = new SqliteAgentRunHistoryStore(path);
            var loaded = Assert.Single(store.LoadRecoverable());

            Assert.Equal("legacy-valid", loaded.Snapshot.RunId);
            Assert.DoesNotContain("legacy-expired", store.LoadRecoverable().Select(item => item.Snapshot.RunId));

            store.SaveRecoverable([loaded]);
            var persisted = new SqliteAppDataStore(directory).Read("agent_runs", "recovery")!;
            Assert.StartsWith("cxaes:", persisted, StringComparison.Ordinal);
            Assert.DoesNotContain("legacy prompt", persisted, StringComparison.Ordinal);
            Assert.True(File.Exists(recoveryPath + ".migrated.bak"));
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }
}
