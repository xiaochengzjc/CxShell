using System.Text.Json;
using CxShell.Services;

namespace CxShell.Services.Agent;

public interface IAgentRunHistoryStore
{
    IReadOnlyList<AgentRuntimeRunSnapshot> Load();
    void Save(IReadOnlyCollection<AgentRuntimeRunSnapshot> runs);
    IReadOnlyList<AgentRunRecoveryState> LoadRecoverable() => [];
    void SaveRecoverable(IReadOnlyCollection<AgentRunRecoveryState> runs)
    {
    }
}

/// <summary>
/// Persists completed Agent runs and resumable checkpoints in the shared
/// application database. Sensitive recovery state remains encrypted.
/// </summary>
public sealed class SqliteAgentRunHistoryStore : IAgentRunHistoryStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private const string Collection = "agent_runs";
    private readonly object _gate = new();
    private readonly string _legacyHistoryPath;
    private readonly string _legacyRecoveryPath;
    private readonly SqliteAppDataStore _store;

    public SqliteAgentRunHistoryStore(string? legacyHistoryPath = null)
    {
        var isDefaultPath = string.IsNullOrWhiteSpace(legacyHistoryPath);
        _legacyHistoryPath = isDefaultPath
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "CxShell",
                "agent-runs.json")
            : Path.GetFullPath(legacyHistoryPath!);
        _legacyRecoveryPath = _legacyHistoryPath + ".recovery";
        var storageDirectory = isDefaultPath
            ? SessionStorageService.GetStorageDirectory()
            : Path.GetDirectoryName(_legacyHistoryPath)!;
        _store = new SqliteAppDataStore(storageDirectory);
    }

    public IReadOnlyList<AgentRuntimeRunSnapshot> Load()
    {
        lock (_gate)
        {
            try
            {
                var payload = _store.Read(Collection, "completed");
                if (payload == null)
                {
                    _store.ImportLegacyFile(Collection, "completed", _legacyHistoryPath, IsValidLegacyHistory);
                    payload = _store.Read(Collection, "completed");
                }
                if (payload == null)
                    return [];

                return JsonSerializer.Deserialize<List<AgentRuntimeRunSnapshot>>(payload, JsonOptions)
                    ?.Where(IsCompleted)
                    .ToList()
                    ?? [];
            }
            catch
            {
                // History is optional and must not prevent Runtime startup.
                return [];
            }
        }
    }

    public void Save(IReadOnlyCollection<AgentRuntimeRunSnapshot> runs)
    {
        ArgumentNullException.ThrowIfNull(runs);
        lock (_gate)
        {
            var completed = runs
                .Where(IsCompleted)
                .OrderByDescending(run => run.StartedAtUtc)
                .Take(AgentRunCoordinator.MaximumRetainedRuns)
                .ToArray();
            try
            {
                var json = JsonSerializer.Serialize(completed, JsonOptions);
                _store.Write(Collection, "completed", json);
            }
            catch
            {
                // Observability must never fail a live Agent run.
            }
        }
    }

    public IReadOnlyList<AgentRunRecoveryState> LoadRecoverable()
    {
        lock (_gate)
        {
            try
            {
                var stored = _store.Read(Collection, "recovery");
                if (stored == null)
                {
                    _store.ImportLegacyFile(Collection, "recovery", _legacyRecoveryPath, IsValidLegacyRecoveryPayload);
                    stored = _store.Read(Collection, "recovery");
                }
                if (stored == null)
                    return [];

                var json = DecryptRecoveryPayload(stored);
                if (json == null)
                    return [];

                var now = DateTimeOffset.UtcNow;
                return JsonSerializer.Deserialize<List<AgentRunRecoveryState>>(json, JsonOptions)
                    ?.Select(NormalizeRecovery)
                    .Where(recovery => IsValidRecovery(recovery, now))
                    .OrderByDescending(run => run.Snapshot.StartedAtUtc)
                    .Take(AgentRunCoordinator.MaximumRetainedRuns)
                    .ToList()
                    ?? [];
            }
            catch
            {
                return [];
            }
        }
    }

    public void SaveRecoverable(IReadOnlyCollection<AgentRunRecoveryState> runs)
    {
        ArgumentNullException.ThrowIfNull(runs);
        lock (_gate)
        {
            var now = DateTimeOffset.UtcNow;
            var recoverable = runs
                .Select(NormalizeRecovery)
                .Where(recovery => IsValidRecovery(recovery, now))
                .GroupBy(run => run.Snapshot.RunId, StringComparer.Ordinal)
                .Select(group => group.OrderByDescending(run => run.Snapshot.StartedAtUtc).First())
                .OrderByDescending(run => run.Snapshot.StartedAtUtc)
                .Take(AgentRunCoordinator.MaximumRetainedRuns)
                .ToArray();
            try
            {
                var json = JsonSerializer.Serialize(recoverable, JsonOptions);
                var encrypted = PasswordEncryptionService.Encrypt(json);
                _store.Write(Collection, "recovery", encrypted);
            }
            catch
            {
                // Recovery persistence must not interrupt a live Agent run.
            }
        }
    }

    private static bool IsCompleted(AgentRuntimeRunSnapshot run)
        => run != null &&
           !string.IsNullOrWhiteSpace(run.RunId) &&
           Guid.TryParse(run.SessionId, out var sessionId) &&
           sessionId != Guid.Empty &&
           !AgentRunStates.IsActive(run.Status);

    private static AgentRunRecoveryState NormalizeRecovery(AgentRunRecoveryState recovery)
    {
        var checkpoint = recovery.Checkpoint ?? recovery.Snapshot.Checkpoint;
        if (recovery.ExpiresAtUtc.HasValue && checkpoint == recovery.Checkpoint)
            return recovery;

        // Older recovery files did not carry an expiry. Give those records the
        // same bounded lifetime and migrate them when the coordinator saves.
        return recovery with
        {
            ExpiresAtUtc = recovery.ExpiresAtUtc ??
                           recovery.Snapshot.StartedAtUtc + AgentRunCoordinator.RecoveryLifetime,
            Checkpoint = checkpoint
        };
    }

    private static bool IsValidRecovery(AgentRunRecoveryState? recovery, DateTimeOffset now)
        => recovery?.Snapshot != null &&
           !string.IsNullOrWhiteSpace(recovery.Snapshot.RunId) &&
           recovery.Snapshot.CanResume &&
           Guid.TryParse(recovery.Snapshot.SessionId, out var sessionId) &&
           sessionId != Guid.Empty &&
           recovery.ExpiresAtUtc > now &&
           recovery.Messages is { Count: > 0 } &&
           recovery.Messages.Count <= AgentRuntimeContract.MaximumMessageCount &&
           recovery.Messages.All(message =>
               message != null &&
               (string.Equals(message.Role, "system", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(message.Role, "user", StringComparison.OrdinalIgnoreCase)) &&
               !string.IsNullOrWhiteSpace(message.Content) &&
               message.Content.Length <= AgentRuntimeContract.MaximumMessageCharacters &&
               message.ToolCallId == null &&
               message.ToolName == null &&
               message.ToolArguments == null &&
               message.ToolCalls == null) &&
            recovery.TimeoutMs is 0 or >= 100;

    private static string? DecryptRecoveryPayload(string stored)
    {
        if (string.IsNullOrWhiteSpace(stored))
            return null;

        var trimmed = stored.Trim();
        var decrypted = PasswordEncryptionService.Decrypt(trimmed);
        return trimmed.StartsWith("cxaes:", StringComparison.Ordinal) &&
               string.IsNullOrEmpty(decrypted)
            ? null
            : decrypted;
    }

    private static bool IsValidLegacyHistory(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<List<AgentRuntimeRunSnapshot>>(json, JsonOptions) != null;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool IsValidLegacyRecoveryPayload(string stored)
    {
        var json = DecryptRecoveryPayload(stored);
        if (json == null)
            return false;

        try
        {
            return JsonSerializer.Deserialize<List<AgentRunRecoveryState>>(json, JsonOptions) != null;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}

internal sealed class NullAgentRunHistoryStore : IAgentRunHistoryStore
{
    public IReadOnlyList<AgentRuntimeRunSnapshot> Load() => [];
    public IReadOnlyList<AgentRunRecoveryState> LoadRecoverable() => [];

    public void Save(IReadOnlyCollection<AgentRuntimeRunSnapshot> runs)
    {
    }
}
