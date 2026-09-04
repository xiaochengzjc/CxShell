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

    public AgentAuditLog(string? filePath = null)
    {
        _filePath = string.IsNullOrWhiteSpace(filePath) ? null : Path.GetFullPath(filePath);
        if (_filePath != null)
            LoadFromDisk();
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
            SaveToDiskLocked();
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
            SaveToDiskLocked();
        }
    }

    private void LoadFromDisk()
    {
        try
        {
            if (!File.Exists(_filePath))
                return;

            var stored = File.ReadAllText(_filePath, Encoding.UTF8);
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

    private void SaveToDiskLocked()
    {
        if (_filePath == null)
            return;

        var temporaryPath = _filePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var directory = Path.GetDirectoryName(_filePath);
            if (string.IsNullOrWhiteSpace(directory))
                return;

            Directory.CreateDirectory(directory);
            var json = JsonSerializer.Serialize(_entries.Take(MaximumEntries).ToArray());
            var encrypted = PasswordEncryptionService.Encrypt(json);
            File.WriteAllText(temporaryPath, encrypted, new UTF8Encoding(false));
            File.Move(temporaryPath, _filePath, overwrite: true);
        }
        catch
        {
            // Observability must never break a live command.
            try
            {
                if (File.Exists(temporaryPath))
                    File.Delete(temporaryPath);
            }
            catch
            {
            }
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
