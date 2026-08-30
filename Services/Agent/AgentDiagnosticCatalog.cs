using System.Text;

namespace CxShell.Services.Agent;

/// <summary>
/// Fixed, read-only diagnostic plans exposed to the Agent. The model chooses
/// a scope, while CxShell owns the command behind that scope.
/// </summary>
public static class AgentDiagnosticCatalog
{
    public const string SystemScope = "system";
    public const string DiskScope = "disk";
    public const string NetworkScope = "network";
    public const string ServicesScope = "services";
    public const string ProcessesScope = "processes";
    public const string AllScope = "all";

    private static readonly string[] SupportedScopes =
        [SystemScope, DiskScope, NetworkScope, ServicesScope, ProcessesScope, AllScope];

    private const string LinuxSystem = """
printf '%s\n' '=== system ==='
printf 'platform=Linux/Unix\n'
printf 'hostname=%s\n' "$(hostname 2>/dev/null)"
printf 'kernel=%s\n' "$(uname -srmo 2>/dev/null)"
printf 'uptime=%s\n' "$(uptime 2>/dev/null)"
printf 'user=%s\n' "$(id -un 2>/dev/null)"
""";

    private const string LinuxDisk = """
printf '%s\n' '=== disk ==='
df -P -h -x tmpfs -x devtmpfs 2>/dev/null || df -P -h 2>/dev/null
""";

    private const string LinuxNetwork = """
printf '%s\n' '=== network ==='
printf '%s\n' '-- listening ports --'
(ss -lntup 2>/dev/null || netstat -lntup 2>/dev/null || true) | head -n 40
printf '%s\n' '-- routes --'
(ip route 2>/dev/null || route -n 2>/dev/null || true) | head -n 30
""";

    private const string LinuxServices = """
printf '%s\n' '=== services ==='
printf '%s\n' '-- failed services --'
(systemctl --failed --no-legend --no-pager 2>/dev/null || true) | head -n 40
""";

    private const string LinuxProcesses = """
printf '%s\n' '=== processes ==='
(ps -eo pid,comm,%cpu,%mem --sort=-%cpu 2>/dev/null || true) | head -n 21
""";

    private const string WindowsSystem = """
$ErrorActionPreference = 'SilentlyContinue'
$ProgressPreference = 'SilentlyContinue'
Write-Output '=== system ==='
Write-Output 'platform=Windows'
$os = Get-CimInstance Win32_OperatingSystem | Select-Object -First 1
if ($null -ne $os) {
    Write-Output ("caption={0}" -f $os.Caption)
    Write-Output ("version={0}" -f $os.Version)
    Write-Output ("build={0}" -f $os.BuildNumber)
    Write-Output ("lastBoot={0}" -f $os.LastBootUpTime)
}
Write-Output ("hostname={0}" -f $env:COMPUTERNAME)
Write-Output ("user={0}" -f [Environment]::UserName)
""";

    private const string WindowsDisk = """
$ErrorActionPreference = 'SilentlyContinue'
$ProgressPreference = 'SilentlyContinue'
Write-Output '=== disk ==='
Get-CimInstance Win32_LogicalDisk -Filter 'DriveType=3' |
    Select-Object DeviceID,VolumeName,@{Name='SizeGB';Expression={[math]::Round($_.Size / 1GB, 2)}},@{Name='FreeGB';Expression={[math]::Round($_.FreeSpace / 1GB, 2)}},@{Name='UsedPercent';Expression={if ($_.Size) {[math]::Round((1 - $_.FreeSpace / $_.Size) * 100, 1)} else {0}}} |
    Format-Table -AutoSize | Out-String -Width 240
""";

    private const string WindowsNetwork = """
$ErrorActionPreference = 'SilentlyContinue'
$ProgressPreference = 'SilentlyContinue'
Write-Output '=== network ==='
Write-Output '-- listening ports --'
Get-NetTCPConnection -State Listen |
    Select-Object LocalAddress,LocalPort,OwningProcess |
    Sort-Object LocalPort | Select-Object -First 40 |
    Format-Table -AutoSize | Out-String -Width 240
Write-Output '-- ip configuration --'
Get-NetIPConfiguration |
    Select-Object InterfaceAlias,IPv4Address,IPv6Address,IPv4DefaultGateway |
    Format-List | Out-String -Width 240
""";

    private const string WindowsServices = """
$ErrorActionPreference = 'SilentlyContinue'
$ProgressPreference = 'SilentlyContinue'
Write-Output '=== services ==='
Get-CimInstance Win32_Service |
    Where-Object { $_.State -eq 'Stopped' -and $_.StartMode -eq 'Auto' } |
    Select-Object Name,DisplayName,State,StartMode,StartName |
    Sort-Object Name | Select-Object -First 40 |
    Format-Table -AutoSize | Out-String -Width 240
""";

    private const string WindowsProcesses = """
$ErrorActionPreference = 'SilentlyContinue'
$ProgressPreference = 'SilentlyContinue'
Write-Output '=== processes ==='
Get-Process |
    Sort-Object CPU -Descending |
    Select-Object -First 20 Id,ProcessName,@{Name='CPUSeconds';Expression={[math]::Round($_.CPU, 1)}},WorkingSet64 |
    Format-Table -AutoSize | Out-String -Width 240
""";

    public static IReadOnlyList<string> Scopes => SupportedScopes;

    public static bool TryCreatePlan(
        AgentSessionSnapshot session,
        string? requestedScope,
        out AgentDiagnosticPlan plan,
        out string? error)
    {
        ArgumentNullException.ThrowIfNull(session);
        plan = default!;
        error = null;

        var scope = requestedScope?.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(scope) || !SupportedScopes.Contains(scope, StringComparer.Ordinal))
        {
            error = $"diagnostic_run scope must be one of: {string.Join(", ", SupportedScopes)}.";
            return false;
        }

        var isWindows = string.Equals(session.Platform, "Windows", StringComparison.OrdinalIgnoreCase);
        var isPosix = string.Equals(session.Platform, "Linux/Unix", StringComparison.OrdinalIgnoreCase) ||
                      string.Equals(session.Platform, "Linux", StringComparison.OrdinalIgnoreCase) ||
                      string.Equals(session.Platform, "Unix", StringComparison.OrdinalIgnoreCase);
        if (!isWindows && !isPosix)
        {
            error = "The remote platform is unknown. Refresh the SSH session before running diagnostics.";
            return false;
        }

        var command = BuildCommand(scope, isWindows);
        plan = new AgentDiagnosticPlan(
            scope,
            isWindows ? "Windows" : "Linux/Unix",
            $"diagnostic {scope}",
            command,
            scope == AllScope ? TimeSpan.FromSeconds(30) : TimeSpan.FromSeconds(15));
        return true;
    }

    private static string BuildCommand(string scope, bool isWindows)
    {
        var script = scope switch
        {
            SystemScope => isWindows ? WindowsSystem : LinuxSystem,
            DiskScope => isWindows ? WindowsDisk : LinuxDisk,
            NetworkScope => isWindows ? WindowsNetwork : LinuxNetwork,
            ServicesScope => isWindows ? WindowsServices : LinuxServices,
            ProcessesScope => isWindows ? WindowsProcesses : LinuxProcesses,
            AllScope => string.Join(
                Environment.NewLine,
                isWindows
                    ? [WindowsSystem, WindowsDisk, WindowsNetwork, WindowsServices, WindowsProcesses]
                    : [LinuxSystem, LinuxDisk, LinuxNetwork, LinuxServices, LinuxProcesses]),
            _ => throw new ArgumentOutOfRangeException(nameof(scope), scope, null)
        };

        return isWindows
            ? BuildPowerShellCommand(script)
            : script.Trim();
    }

    private static string BuildPowerShellCommand(string script)
        => AgentPowerShellCommandBuilder.BuildEncodedCommand(script);
}

public sealed record AgentDiagnosticPlan(
    string Scope,
    string Platform,
    string DisplayCommand,
    string Command,
    TimeSpan Timeout);
