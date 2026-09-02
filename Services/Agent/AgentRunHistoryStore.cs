using System.Text;
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
/// Persists completed run summaries and a separate, bounded recovery file.
/// Tool output and provider credentials are deliberately excluded from both.
/// </summary>
public sealed class JsonAgentRunHistoryStore : IAgentRunHistoryStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly object _gate = new();
    private readonly string _filePath;
    private readonly string _recoveryFilePath;

    public JsonAgentRunHistoryStore(string? filePath = null)
    {
        _filePath = string.IsNullOrWhiteSpace(filePath)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "CxShell",
                "agent-runs.json")
            : Path.GetFullPath(filePath);
        _recoveryFilePath = _filePath + ".recovery";
    }

    public IReadOnlyList<AgentRuntimeRunSnapshot> Load()
    {
        lock (_gate)
        {
            try
            {
                if (!File.Exists(_filePath))
                    return [];

                var json = File.ReadAllText(_filePath, Encoding.UTF8);
                return JsonSerializer.Deserialize<List<AgentRuntimeRunSnapshot>>(json, JsonOptions)
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
            var directory = Path.GetDirectoryName(_filePath);
            if (string.IsNullOrWhiteSpace(directory))
                return;

            var temporaryPath = _filePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                Directory.CreateDirectory(directory);
                var json = JsonSerializer.Serialize(completed, JsonOptions);
                File.WriteAllText(temporaryPath, json, new UTF8Encoding(false));
                File.Move(temporaryPath, _filePath, overwrite: true);
            }
            catch
            {
                // Observability must never fail a live Agent run.
                TryDelete(temporaryPath);
            }
        }
    }

    public IReadOnlyList<AgentRunRecoveryState> LoadRecoverable()
    {
        lock (_gate)
        {
            try
            {
                if (!File.Exists(_recoveryFilePath))
                    return [];

                var stored = File.ReadAllText(_recoveryFilePath, Encoding.UTF8);
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
            var directory = Path.GetDirectoryName(_recoveryFilePath);
            if (string.IsNullOrWhiteSpace(directory))
                return;

            var temporaryPath = _recoveryFilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                Directory.CreateDirectory(directory);
                var json = JsonSerializer.Serialize(recoverable, JsonOptions);
                var encrypted = PasswordEncryptionService.Encrypt(json);
                File.WriteAllText(temporaryPath, encrypted, new UTF8Encoding(false));
                File.Move(temporaryPath, _recoveryFilePath, overwrite: true);
            }
            catch
            {
                TryDelete(temporaryPath);
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

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
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
