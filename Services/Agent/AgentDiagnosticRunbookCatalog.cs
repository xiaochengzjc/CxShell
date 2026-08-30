using System.Text;

namespace CxShell.Services.Agent;

/// <summary>
/// Fixed read-only workflows for common connection problems. A Runbook is
/// deliberately narrower than session_command: the model selects a known
/// workflow and CxShell supplies every command in it.
/// </summary>
public static class AgentDiagnosticRunbookCatalog
{
    public const string SshScope = "ssh";
    public const string RdpScope = "rdp";
    public const string HealthScope = "health";

    private static readonly string[] SupportedRunbooks =
        [SshScope, RdpScope, HealthScope];

    private const string LinuxSshRunbook = """
printf '%s\n' '=== ssh ==='
printf '%s\n' '-- service --'
if command -v systemctl >/dev/null 2>&1; then
    systemctl is-active sshd 2>/dev/null || systemctl is-active ssh 2>/dev/null || true
else
    ps -eo pid,comm,args 2>/dev/null | grep -E '[s]shd' | head -n 20 || true
fi
printf '%s\n' '-- listening on port 22 --'
(ss -lnt 2>/dev/null || netstat -lnt 2>/dev/null || true) | grep -E '(^|[[:space:]])[^[:space:]]*:22([[:space:]]|$)' || true
printf '%s\n' '-- ssh processes --'
(ps -eo pid,comm,args 2>/dev/null || true) | grep -E '[s]shd' | head -n 20 || true
""";

    private const string WindowsSshRunbook = """
$ErrorActionPreference = 'SilentlyContinue'
$ProgressPreference = 'SilentlyContinue'
Write-Output '=== ssh ==='
Write-Output '-- service --'
$sshService = Get-CimInstance Win32_Service | Where-Object { $_.Name -in @('sshd', 'OpenSSHd') } | Select-Object -First 1
if ($null -eq $sshService) {
    Write-Output 'OpenSSH service not found'
} else {
    Write-Output ("Name={0}; State={1}; StartMode={2}; StartName={3}" -f $sshService.Name, $sshService.State, $sshService.StartMode, $sshService.StartName)
}
Write-Output '-- listening on port 22 --'
$listeners = @(Get-NetTCPConnection -State Listen -LocalPort 22)
if ($listeners.Count -eq 0) { Write-Output 'No listener found' }
else { $listeners | Select-Object LocalAddress,LocalPort,OwningProcess | Format-Table -AutoSize | Out-String -Width 240 }
Write-Output '-- OpenSSH firewall rules --'
$rules = @(Get-NetFirewallRule -DisplayGroup 'OpenSSH Server')
if ($rules.Count -eq 0) { Write-Output 'No OpenSSH Server rule found' }
else { $rules | Select-Object DisplayName,Enabled,Direction,Action | Format-Table -AutoSize | Out-String -Width 240 }
""";

    private const string WindowsRdpRunbook = """
$ErrorActionPreference = 'SilentlyContinue'
$ProgressPreference = 'SilentlyContinue'
Write-Output '=== rdp ==='
Write-Output '-- TermService --'
$service = Get-Service -Name TermService
if ($null -eq $service) { Write-Output 'TermService not found' }
else { Write-Output ("Status={0}; StartType={1}" -f $service.Status, $service.StartType) }
Write-Output '-- remote desktop policy --'
$policy = Get-ItemProperty -Path 'HKLM:\SYSTEM\CurrentControlSet\Control\Terminal Server' -Name fDenyTSConnections
if ($null -eq $policy) { Write-Output 'fDenyTSConnections unavailable' }
else { Write-Output ("fDenyTSConnections={0}" -f $policy.fDenyTSConnections) }
Write-Output '-- listening on port 3389 --'
$listeners = @(Get-NetTCPConnection -State Listen -LocalPort 3389)
if ($listeners.Count -eq 0) { Write-Output 'No listener found' }
else { $listeners | Select-Object LocalAddress,LocalPort,OwningProcess | Format-Table -AutoSize | Out-String -Width 240 }
Write-Output '-- Remote Desktop firewall rules --'
$rules = @(Get-NetFirewallRule -DisplayGroup 'Remote Desktop')
if ($rules.Count -eq 0) { Write-Output 'No Remote Desktop rule found' }
else { $rules | Select-Object DisplayName,Enabled,Direction,Action | Format-Table -AutoSize | Out-String -Width 240 }
""";

    public static IReadOnlyList<string> Runbooks => SupportedRunbooks;

    public static bool TryCreatePlan(
        AgentSessionSnapshot session,
        string? requestedRunbook,
        out AgentDiagnosticPlan plan,
        out string? error)
    {
        ArgumentNullException.ThrowIfNull(session);
        plan = default!;
        error = null;

        var runbook = requestedRunbook?.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(runbook) || !SupportedRunbooks.Contains(runbook, StringComparer.Ordinal))
        {
            error = $"runbook_run scope must be one of: {string.Join(", ", SupportedRunbooks)}.";
            return false;
        }

        var isWindows = string.Equals(session.Platform, "Windows", StringComparison.OrdinalIgnoreCase);
        var isPosix = string.Equals(session.Platform, "Linux/Unix", StringComparison.OrdinalIgnoreCase) ||
                      string.Equals(session.Platform, "Linux", StringComparison.OrdinalIgnoreCase) ||
                      string.Equals(session.Platform, "Unix", StringComparison.OrdinalIgnoreCase);
        if (!isWindows && !isPosix)
        {
            error = "The remote platform is unknown. Refresh the SSH session before running a runbook.";
            return false;
        }

        if (runbook == RdpScope && !isWindows)
        {
            error = "The RDP runbook is available only for Windows SSH sessions.";
            return false;
        }

        AgentDiagnosticPlan? healthPlan = null;
        if (runbook == HealthScope &&
            !AgentDiagnosticCatalog.TryCreatePlan(session, AgentDiagnosticCatalog.AllScope, out healthPlan, out error))
        {
            return false;
        }

        var command = runbook switch
        {
            SshScope => isWindows ? BuildPowerShellCommand(WindowsSshRunbook) : LinuxSshRunbook.Trim(),
            RdpScope => BuildPowerShellCommand(WindowsRdpRunbook),
            HealthScope => healthPlan!.Command,
            _ => throw new ArgumentOutOfRangeException(nameof(runbook), runbook, null)
        };
        var timeout = runbook == HealthScope ? healthPlan!.Timeout : TimeSpan.FromSeconds(20);
        plan = new AgentDiagnosticPlan(
            runbook,
            isWindows ? "Windows" : "Linux/Unix",
            $"runbook {runbook}",
            command,
            timeout);
        return true;
    }

    private static string BuildPowerShellCommand(string script)
        => AgentPowerShellCommandBuilder.BuildEncodedCommand(script);
}
