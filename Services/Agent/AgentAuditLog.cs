using System.Security.Cryptography;
using System.Text;

namespace CxShell.Services.Agent;

public sealed class AgentAuditLog
{
    public const int MaximumEntries = 500;

    private readonly object _gate = new();
    private readonly List<AgentAuditEntry> _entries = [];

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
            Detail = TrimDetail(detail ?? result.Message)
        };

        lock (_gate)
        {
            _entries.Insert(0, entry);
            if (_entries.Count > MaximumEntries)
                _entries.RemoveRange(MaximumEntries, _entries.Count - MaximumEntries);
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
            _entries.Clear();
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
