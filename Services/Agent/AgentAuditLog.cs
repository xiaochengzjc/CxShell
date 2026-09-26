using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CxShell.Services.Agent;

public sealed class AgentAuditLog
{
    public const int MaximumEntries = 500;

    private readonly object _gate = new();
    private readonly List<AgentAuditEntry> _entries = [];
    private readonly string? _filePath;
    private readonly SqliteAppDataStore? _store;

    public AgentAuditLog(string? filePath = null)
    {
        _filePath = string.IsNullOrWhiteSpace(filePath) ? null : Path.GetFullPath(filePath);
        if (_filePath != null)
        {
            _store = new SqliteAppDataStore(Path.GetDirectoryName(_filePath));
            LoadFromStore();
        }
    }

    public void Record(
        AgentCommandRequest request,
        AgentCommandResult result,
        string? detail = null,
        AgentPermissionResult? permission = null,
        bool approvalGranted = false)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(result);

        var entry = new AgentAuditEntry
        {
            RequestId = request.RequestId,
            SessionId = request.SessionId,
            TimestampUtc = result.CompletedAtUtc,
            Status = result.Status,
            Risk = permission?.Risk ?? AgentCommandRisk.ReadOnly,
            PermissionDecision = permission?.Decision,
            ApprovalRequired = result.ApprovalRequired || permission?.ApprovalRequired == true,
            ApprovalGranted = approvalGranted,
            CommandLength = request.Command?.Length ?? 0,
            CommandFingerprint = Fingerprint(request.Command),
            Detail = TrimDetail(AgentSensitiveDataRedactor.Redact(
                detail ?? result.Message,
                request.SensitiveInput is { Length: > 0 }
                    ? [request.SensitiveInput]
                    : null))
        };

        lock (_gate)
        {
            _entries.Insert(0, entry);
            if (_entries.Count > MaximumEntries)
                _entries.RemoveRange(MaximumEntries, _entries.Count - MaximumEntries);
            SaveToStoreLocked();
        }
    }

    public IReadOnlyList<AgentAuditEntry> ReadRecent(int limit = MaximumEntries)
    {
        lock (_gate)
        {
            return _entries.Take(Math.Clamp(limit, 1, MaximumEntries)).ToList();
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
            SaveToStoreLocked();
        }
    }

    private void LoadFromStore()
    {
        try
        {
            var stored = _store!.Read("agent_audit", "entries");
            if (stored == null)
            {
                _store.ImportLegacyFile(
                    "agent_audit",
                    "entries",
                    _filePath!,
                    static payload => DeserializeEntries(payload) != null);
                stored = _store.Read("agent_audit", "entries");
            }
            if (stored == null)
                return;

            var json = PasswordEncryptionService.DecryptEncrypted(stored.Trim());
            if (string.IsNullOrWhiteSpace(json))
                return;

            var entries = JsonSerializer.Deserialize<List<AgentAuditEntry>>(json);
            if (entries == null)
                return;

            lock (_gate)
                _entries.AddRange(entries
                    .Where(entry => entry != null)
                    .Take(MaximumEntries));
        }
        catch
        {
            // Audit history is optional. A corrupt or inaccessible file must
            // never prevent Agent startup.
        }
    }

    private void SaveToStoreLocked()
    {
        if (_store == null)
            return;
        try
        {
            var json = JsonSerializer.Serialize(_entries.Take(MaximumEntries).ToArray());
            var encrypted = PasswordEncryptionService.Encrypt(json);
            _store.Write("agent_audit", "entries", encrypted);
        }
        catch
        {
            // Observability must never break a live command.
        }
    }

    private static List<AgentAuditEntry>? DeserializeEntries(string stored)
    {
        var json = PasswordEncryptionService.DecryptEncrypted(stored.Trim());
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            return JsonSerializer.Deserialize<List<AgentAuditEntry>>(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal static string Fingerprint(string? command)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(command ?? string.Empty));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static string TrimDetail(string? detail)
    {
        if (string.IsNullOrWhiteSpace(detail))
            return string.Empty;

        var normalized = detail.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return normalized.Length <= 500 ? normalized : normalized[..500] + "...";
    }
}
