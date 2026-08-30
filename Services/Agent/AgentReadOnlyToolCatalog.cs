using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CxShell.Services.Agent;

/// <summary>
/// Additional fixed, read-only tools for common operations work. User input
/// selects a small, validated parameter set; it never becomes a shell script.
/// </summary>
public static class AgentReadOnlyToolCatalog
{
    public const string LogsToolName = "logs_read";
    public const string PortCheckToolName = "port_check";
    public const string ServiceDetailToolName = "service_detail";
    public const string FilePreviewToolName = "file_preview";
    public const string PackageQueryToolName = "package_query";
    public const string RuntimeCheckToolName = "runtime_check";
    public const string DiskCleanupAdviceToolName = "disk_cleanup_advice";

    public static IReadOnlyList<string> LogSources { get; } = ["system", "application", "security"];
    public static IReadOnlyList<string> FileTargets { get; } = ["hosts", "ssh-config", "recent-log"];
    public static IReadOnlyList<string> RuntimeNames { get; } = ["java", "python", "dotnet", "node", "powershell", "all"];
    public static IReadOnlyList<string> CleanupScopes { get; } = ["summary", "logs", "temp", "all"];

    private static readonly Regex SafeServiceName = new(
        "^[A-Za-z0-9_.@:-]{1,64}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex SafePackageName = new(
        "^[A-Za-z0-9_.+@:-]{1,100}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static bool TryCreatePlan(
        AgentSessionSnapshot session,
        string toolName,
        JsonElement arguments,
        out AgentDiagnosticPlan plan,
        out string? error)
    {
        ArgumentNullException.ThrowIfNull(session);
        plan = default!;
        error = null;

        if (!IsSupportedPlatform(session.Platform, out var isWindows))
        {
            error = "The remote platform is unknown. Refresh the SSH session before using a read-only tool.";
            return false;
        }

        var root = arguments.ValueKind == JsonValueKind.Object
            ? arguments
            : default;
        switch (toolName)
        {
            case LogsToolName:
                var source = ReadString(root, "source")?.Trim().ToLowerInvariant();
                if (source == null || !LogSources.Contains(source, StringComparer.Ordinal))
                {
                    error = $"{LogsToolName} source must be one of: {string.Join(", ", LogSources)}.";
                    return false;
                }

                var lines = ReadInt(root, "lines", 50);
                if (lines is < 1 or > 200)
                {
                    error = "logs_read lines must be between 1 and 200.";
                    return false;
                }

                plan = CreatePlan(
                    source,
                    isWindows ? "Windows" : "Linux/Unix",
                    $"logs {source} ({lines} lines)",
                    isWindows ? BuildWindowsLogs(source, lines) : BuildLinuxLogs(source, lines),
                    TimeSpan.FromSeconds(20));
                return true;

            case PortCheckToolName:
                var port = ReadInt(root, "port", 0);
                if (port is < 1 or > 65535)
                {
                    error = "port_check port must be between 1 and 65535.";
                    return false;
                }

                plan = CreatePlan(
                    $"port-{port}",
                    isWindows ? "Windows" : "Linux/Unix",
                    $"port check {port}",
                    isWindows ? BuildWindowsPortCheck(port) : BuildLinuxPortCheck(port),
                    TimeSpan.FromSeconds(15));
                return true;

            case ServiceDetailToolName:
                var service = ReadString(root, "service")?.Trim();
                if (string.IsNullOrWhiteSpace(service) || !SafeServiceName.IsMatch(service))
                {
                    error = "service_detail service must contain only letters, numbers, '.', '@', ':', '_' or '-'.";
                    return false;
                }

                plan = CreatePlan(
                    $"service-{service}",
                    isWindows ? "Windows" : "Linux/Unix",
                    $"service detail {service}",
                    isWindows ? BuildWindowsServiceDetail(service) : BuildLinuxServiceDetail(service),
                    TimeSpan.FromSeconds(15));
                return true;

            case FilePreviewToolName:
                var target = ReadString(root, "target")?.Trim().ToLowerInvariant();
                if (target == null || !FileTargets.Contains(target, StringComparer.Ordinal))
                {
                    error = $"{FilePreviewToolName} target is not available. Choose one of: {string.Join(", ", FileTargets)}.";
                    return false;
                }

                lines = ReadInt(root, "lines", 80);
                if (lines is < 1 or > 200)
                {
                    error = "file_preview lines must be between 1 and 200.";
                    return false;
                }

                var command = isWindows
                    ? BuildWindowsFilePreview(target, lines)
                    : BuildLinuxFilePreview(target, lines);
                if (command == null)
                {
                    error = $"The file preview target '{target}' is not available on {session.Platform}.";
                    return false;
                }

                plan = CreatePlan(
                    target,
                    isWindows ? "Windows" : "Linux/Unix",
                    $"file preview {target}",
                    command,
                    TimeSpan.FromSeconds(15));
                return true;

            case PackageQueryToolName:
                var packageName = ReadString(root, "name")?.Trim();
                if (string.IsNullOrWhiteSpace(packageName) || !SafePackageName.IsMatch(packageName))
                {
                    error = "package_query name must contain only letters, numbers, '.', '+', '@', ':', '_' or '-'.";
                    return false;
                }

                plan = CreatePlan(
                    $"package-{packageName}",
                    isWindows ? "Windows" : "Linux/Unix",
                    $"package query {packageName}",
                    isWindows ? BuildWindowsPackageQuery(packageName) : BuildLinuxPackageQuery(packageName),
                    TimeSpan.FromSeconds(20));
                return true;

            case RuntimeCheckToolName:
                var runtime = ReadString(root, "runtime")?.Trim().ToLowerInvariant() ?? "all";
                if (!RuntimeNames.Contains(runtime, StringComparer.Ordinal))
                {
                    error = $"runtime_check runtime must be one of: {string.Join(", ", RuntimeNames)}.";
                    return false;
                }

                plan = CreatePlan(
                    runtime,
                    isWindows ? "Windows" : "Linux/Unix",
                    $"runtime check {runtime}",
                    isWindows ? BuildWindowsRuntimeCheck(runtime) : BuildLinuxRuntimeCheck(runtime),
                    TimeSpan.FromSeconds(20));
                return true;

            case DiskCleanupAdviceToolName:
                var cleanupScope = ReadString(root, "scope")?.Trim().ToLowerInvariant() ?? "all";
                if (!CleanupScopes.Contains(cleanupScope, StringComparer.Ordinal))
                {
                    error = $"disk_cleanup_advice scope must be one of: {string.Join(", ", CleanupScopes)}.";
                    return false;
                }

                plan = CreatePlan(
                    cleanupScope,
                    isWindows ? "Windows" : "Linux/Unix",
                    $"disk cleanup advice {cleanupScope}",
                    isWindows ? BuildWindowsDiskCleanupAdvice(cleanupScope) : BuildLinuxDiskCleanupAdvice(cleanupScope),
                    TimeSpan.FromSeconds(30));
                return true;

            default:
                error = $"Unknown read-only tool '{toolName}'.";
                return false;
        }
    }

    private static AgentDiagnosticPlan CreatePlan(
        string scope,
        string platform,
        string displayCommand,
        string command,
        TimeSpan timeout)
        => new(scope, platform, displayCommand, command, timeout);

    private static string BuildLinuxLogs(string source, int lines)
        => source switch
        {
            "system" => $"printf '%s\\n' '=== logs: system ==='; (journalctl -n {lines} --no-pager -o short-iso 2>/dev/null || tail -n {lines} /var/log/syslog 2>/dev/null || true)",
            "application" => $"printf '%s\\n' '=== logs: application ==='; (journalctl -n {lines} --no-pager -p warning..alert 2>/dev/null || tail -n {lines} /var/log/messages 2>/dev/null || true)",
            "security" => $"printf '%s\\n' '=== logs: security ==='; (tail -n {lines} /var/log/auth.log 2>/dev/null || tail -n {lines} /var/log/secure 2>/dev/null || journalctl -n {lines} --no-pager _SYSTEMD_UNIT=sshd.service 2>/dev/null || true)",
            _ => throw new ArgumentOutOfRangeException(nameof(source))
        };

    private static string BuildWindowsLogs(string source, int lines)
    {
        var logName = source switch
        {
            "system" => "System",
            "application" => "Application",
            "security" => "Security",
            _ => throw new ArgumentOutOfRangeException(nameof(source))
        };
        return BuildPowerShellCommand($"""
$ErrorActionPreference = 'SilentlyContinue'
$ProgressPreference = 'SilentlyContinue'
Write-Output '=== logs: {source} ==='
Get-WinEvent -LogName '{logName}' -MaxEvents {lines} |
    Select-Object TimeCreated,Id,LevelDisplayName,ProviderName,Message |
    Format-List | Out-String -Width 240
""");
    }

    private static string BuildLinuxPortCheck(int port)
        => $"printf '%s\\n' '=== port: {port} ==='; (ss -lntup 2>/dev/null || netstat -lntup 2>/dev/null || true) | grep -E '[:.]\\b{port}([[:space:]]|$)' || true";

    private static string BuildWindowsPortCheck(int port)
    {
        var script = $"$ErrorActionPreference = 'SilentlyContinue'\n" +
                     "$ProgressPreference = 'SilentlyContinue'\n" +
                     $"Write-Output '=== port: {port} ==='\n" +
                     $"$listeners = @(Get-NetTCPConnection -State Listen -LocalPort {port})\n" +
                     "if ($listeners.Count -eq 0) { Write-Output 'No listener found' }\n" +
                     "else { $listeners | Select-Object LocalAddress,LocalPort,OwningProcess | " +
                     "Format-Table -AutoSize | Out-String -Width 240 }\n";
        return BuildPowerShellCommand(script);
    }

    private static string BuildLinuxServiceDetail(string service)
        => $"printf '%s\\n' '=== service: {service} ==='; if command -v systemctl >/dev/null 2>&1; then systemctl show '{service}' --no-pager --property=Id,Description,LoadState,ActiveState,SubState,UnitFileState,MainPID 2>/dev/null || true; else service '{service}' status 2>&1 | head -n 40; fi";

    private static string BuildWindowsServiceDetail(string service)
        => BuildPowerShellCommand($"""
$ErrorActionPreference = 'SilentlyContinue'
$ProgressPreference = 'SilentlyContinue'
Write-Output '=== service: {service} ==='
Get-CimInstance Win32_Service -Filter "Name = '{service}'" |
    Select-Object Name,DisplayName,State,Status,StartMode,StartName,PathName |
    Format-List | Out-String -Width 240
""");

    private static string? BuildLinuxFilePreview(string target, int lines)
        => target switch
        {
            "hosts" => $"printf '%s\\n' '=== /etc/hosts ==='; sed -n '1,{lines}p' /etc/hosts 2>/dev/null || true",
            "ssh-config" => $"printf '%s\\n' '=== /etc/ssh/sshd_config ==='; sed -n '1,{lines}p' /etc/ssh/sshd_config 2>/dev/null || true",
            "recent-log" => $"printf '%s\\n' '=== recent log ==='; (tail -n {lines} /var/log/syslog 2>/dev/null || tail -n {lines} /var/log/messages 2>/dev/null || true)",
            _ => null
        };

    private static string? BuildWindowsFilePreview(string target, int lines)
    {
        var path = target switch
        {
            "hosts" => "C:\\Windows\\System32\\drivers\\etc\\hosts",
            "ssh-config" => "C:\\ProgramData\\ssh\\sshd_config",
            "recent-log" => "C:\\Windows\\Logs\\CBS\\CBS.log",
            _ => null
        };
        return path == null
            ? null
            : BuildPowerShellCommand($"""
$ErrorActionPreference = 'SilentlyContinue'
$ProgressPreference = 'SilentlyContinue'
Write-Output '=== {path} ==='
Get-Content -LiteralPath '{path}' -TotalCount {lines}
""");
    }

    private static string BuildLinuxPackageQuery(string packageName)
        => $"printf '%s\\n' '=== package: {packageName} ==='; if command -v dpkg-query >/dev/null 2>&1; then dpkg-query -W -f='${{Package}} ${{Version}} ${{Status}}\\n' '{packageName}' 2>/dev/null || true; elif command -v rpm >/dev/null 2>&1; then rpm -q --queryformat '%{{NAME}} %{{VERSION}}-%{{RELEASE}}\\n' '{packageName}' 2>/dev/null || true; elif command -v apk >/dev/null 2>&1; then apk info -e '{packageName}' 2>/dev/null || true; else printf '%s\\n' 'No supported package manager found'; fi";

    private static string BuildWindowsPackageQuery(string packageName)
        => BuildPowerShellCommand($$"""
$ErrorActionPreference = 'SilentlyContinue'
$ProgressPreference = 'SilentlyContinue'
Write-Output '=== package: {{packageName}} ==='
$packages = @(Get-Package -Name '{{packageName}}' -ErrorAction SilentlyContinue)
if ($packages.Count -gt 0) {
    $packages | Select-Object Name,Version,ProviderName,Source | Format-List | Out-String -Width 240
} else {
    $commands = @(Get-Command '{{packageName}}' -ErrorAction SilentlyContinue)
    if ($commands.Count -eq 0) { Write-Output 'Package or command not found' }
    else { $commands | Select-Object Name,CommandType,Source,Version | Format-List | Out-String -Width 240 }
}
""");

    private static string BuildLinuxRuntimeCheck(string runtime)
    {
        const string all = """
printf '%s\n' '=== runtimes ==='
for candidate in java python3 python dotnet node pwsh; do
    if command -v "$candidate" >/dev/null 2>&1; then
        printf '%s: ' "$candidate"
        case "$candidate" in
            java) java -version 2>&1 | head -n 1 ;;
            python3|python) "$candidate" --version 2>&1 | head -n 1 ;;
            dotnet) dotnet --version 2>&1 | head -n 1 ;;
            node) node --version 2>&1 | head -n 1 ;;
            pwsh) pwsh --version 2>&1 | head -n 1 ;;
        esac
    fi
done
""";
        if (runtime == "all")
            return all.Trim();

        var command = runtime switch
        {
            "java" => "if command -v java >/dev/null 2>&1; then java -version 2>&1 | head -n 1; else printf '%s\\n' 'java: not found'; fi",
            "python" => "if command -v python3 >/dev/null 2>&1; then python3 --version 2>&1; elif command -v python >/dev/null 2>&1; then python --version 2>&1; else printf '%s\\n' 'python: not found'; fi",
            "dotnet" => "if command -v dotnet >/dev/null 2>&1; then dotnet --version 2>&1; else printf '%s\\n' 'dotnet: not found'; fi",
            "node" => "if command -v node >/dev/null 2>&1; then node --version 2>&1; else printf '%s\\n' 'node: not found'; fi",
            "powershell" => "if command -v pwsh >/dev/null 2>&1; then pwsh --version 2>&1; else printf '%s\\n' 'powershell: not found'; fi",
            _ => throw new ArgumentOutOfRangeException(nameof(runtime), runtime, null)
        };
        return $"printf '%s\\n' '=== runtime: {runtime} ==='; {command}";
    }

    private static string BuildWindowsRuntimeCheck(string runtime)
    {
        IReadOnlyList<string> targets = runtime == "all"
            ? ["java", "python", "dotnet", "node", "powershell"]
            : [runtime];
        var targetList = string.Join(", ", targets.Select(target => $"'{target}'"));
        return BuildPowerShellCommand($$"""
$ErrorActionPreference = 'SilentlyContinue'
$ProgressPreference = 'SilentlyContinue'
Write-Output '=== runtimes ==='
$targets = @({{targetList}})
foreach ($target in $targets) {
    $commandName = if ($target -eq 'python') { 'python' } elseif ($target -eq 'powershell') { 'pwsh' } else { $target }
    $command = Get-Command $commandName -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($null -eq $command) {
        Write-Output ("{0}: not found" -f $target)
        continue
    }
    $version = switch ($target) {
        'java' { & $command.Source -version 2>&1 | Select-Object -First 1 }
        'python' { & $command.Source --version 2>&1 | Select-Object -First 1 }
        'dotnet' { & $command.Source --version 2>&1 | Select-Object -First 1 }
        'node' { & $command.Source --version 2>&1 | Select-Object -First 1 }
        'powershell' { & $command.Source --version 2>&1 | Select-Object -First 1 }
    }
    Write-Output ("{0}: {1} ({2})" -f $target, ($version -join ' '), $command.Source)
}
""");
    }

    private static string BuildLinuxDiskCleanupAdvice(string scope)
    {
        var sections = scope switch
        {
            "summary" => "df -P -h 2>/dev/null || true",
            "logs" => "for path in /var/log; do if [ -d \"$path\" ]; then printf '%s\\n' \"--- $path ---\"; du -x -h --max-depth=1 \"$path\" 2>/dev/null | sort -h | tail -n 20; find \"$path\" -type f -printf '%TY-%Tm-%Td %s %p\\n' 2>/dev/null | sort -k2 -nr | head -n 20; fi; done",
            "temp" => "for path in /tmp /var/tmp; do if [ -d \"$path\" ]; then printf '%s\\n' \"--- $path ---\"; du -x -h --max-depth=1 \"$path\" 2>/dev/null | sort -h | tail -n 20; fi; done",
            "all" => "df -P -h 2>/dev/null || true; for path in /var/log /tmp /var/tmp; do if [ -d \"$path\" ]; then printf '%s\\n' \"--- $path ---\"; du -x -h --max-depth=1 \"$path\" 2>/dev/null | sort -h | tail -n 20; fi; done",
            _ => throw new ArgumentOutOfRangeException(nameof(scope), scope, null)
        };
        return $"printf '%s\\n' '=== disk cleanup advice: {scope} ==='; printf '%s\\n' 'Read-only analysis; no files will be deleted.'; {sections}";
    }

    private static string BuildWindowsDiskCleanupAdvice(string scope)
    {
        var script = scope switch
        {
            "summary" => """
Write-Output '=== disk cleanup advice: summary ==='
Write-Output 'Read-only analysis; no files will be deleted.'
Get-CimInstance Win32_LogicalDisk -Filter 'DriveType=3' |
    Select-Object DeviceID,VolumeName,@{Name='SizeGB';Expression={[math]::Round($_.Size / 1GB, 2)}},@{Name='FreeGB';Expression={[math]::Round($_.FreeSpace / 1GB, 2)}},@{Name='UsedPercent';Expression={if ($_.Size) {[math]::Round((1 - $_.FreeSpace / $_.Size) * 100, 1)} else {0}}} |
    Format-Table -AutoSize | Out-String -Width 240
""",
            "logs" => BuildWindowsCleanupPathSection("logs", ["$env:WINDIR\\Logs", "$env:WINDIR\\Temp"]),
            "temp" => BuildWindowsCleanupPathSection("temp", ["$env:TEMP", "$env:WINDIR\\Temp"]),
            "all" => BuildWindowsCleanupPathSection("all", ["$env:WINDIR\\Logs", "$env:TEMP", "$env:WINDIR\\Temp"]),
            _ => throw new ArgumentOutOfRangeException(nameof(scope), scope, null)
        };
        return BuildPowerShellCommand(script);
    }

    private static string BuildWindowsCleanupPathSection(string scope, IReadOnlyList<string> paths)
    {
        var pathList = string.Join(", ", paths.Select(path => $"'{path}'"));
        return $$"""
Write-Output '=== disk cleanup advice: {{scope}} ==='
Write-Output 'Read-only analysis; no files will be deleted.'
$paths = @({{pathList}})
foreach ($path in $paths) {
    if (-not (Test-Path -LiteralPath $path)) { continue }
    Write-Output ("--- {0} ---" -f $path)
    $files = @(Get-ChildItem -LiteralPath $path -File -Recurse -Force -ErrorAction SilentlyContinue)
    $total = ($files | Measure-Object -Property Length -Sum).Sum
    Write-Output ("Files={0}; SizeGB={1}" -f $files.Count, [math]::Round($total / 1GB, 2))
    $files | Sort-Object Length -Descending | Select-Object -First 20 FullName,Length,LastWriteTime |
        Format-Table -AutoSize | Out-String -Width 240
}
""";
    }

    private static string BuildPowerShellCommand(string script)
        => AgentPowerShellCommandBuilder.BuildEncodedCommand(script);

    private static bool IsSupportedPlatform(string? platform, out bool isWindows)
    {
        isWindows = string.Equals(platform, "Windows", StringComparison.OrdinalIgnoreCase);
        return isWindows ||
               string.Equals(platform, "Linux/Unix", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(platform, "Linux", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(platform, "Unix", StringComparison.OrdinalIgnoreCase);
    }

    private static string? ReadString(JsonElement root, string name)
        => root.ValueKind == JsonValueKind.Object &&
           root.TryGetProperty(name, out var value) &&
           value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int ReadInt(JsonElement root, string name, int defaultValue)
        => root.ValueKind == JsonValueKind.Object &&
           root.TryGetProperty(name, out var value) &&
           value.TryGetInt32(out var parsed)
            ? parsed
            : defaultValue;
}
