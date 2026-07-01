using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CxShell.Models;
using Renci.SshNet;

namespace CxShell.Services;

public class ServerMonitorService : IDisposable
{
    private enum MonitorTargetKind
    {
        Linux,
        Windows
    }

    private const string LinuxSectionSeparator = "---SEP---";
    private const string WindowsMonitorScript = """
$ErrorActionPreference = 'SilentlyContinue'

function To-Int64Value($value) {
    if ($null -eq $value) { return 0 }
    return [int64]$value
}

function To-DoubleValue($value) {
    if ($null -eq $value) { return 0.0 }
    return [double]$value
}

$invariant = [System.Globalization.CultureInfo]::InvariantCulture

$cpuCounters = Get-CimInstance Win32_PerfFormattedData_PerfOS_Processor
$totalCpu = $cpuCounters | Where-Object { $_.Name -eq '_Total' } | Select-Object -First 1
if ($null -ne $totalCpu) {
    $cpuValue = (To-DoubleValue $totalCpu.PercentProcessorTime).ToString($invariant)
    Write-Output ("CPU|0|{0}" -f $cpuValue)
}

$coreIndex = 1
foreach ($cpu in ($cpuCounters | Where-Object { $_.Name -ne '_Total' } | Sort-Object Name)) {
    $cpuValue = (To-DoubleValue $cpu.PercentProcessorTime).ToString($invariant)
    Write-Output ("CPU|{0}|{1}" -f $coreIndex, $cpuValue)
    $coreIndex++
}

$os = Get-CimInstance Win32_OperatingSystem | Select-Object -First 1
if ($null -ne $os) {
    Write-Output ("MEM|{0}|{1}" -f (To-Int64Value $os.TotalVisibleMemorySize), (To-Int64Value $os.FreePhysicalMemory))
}

$rx = [int64]0
$tx = [int64]0
$networkCounters = Get-CimInstance Win32_PerfFormattedData_Tcpip_NetworkInterface
foreach ($nic in $networkCounters) {
    $name = [string]$nic.Name
    if ($name -match 'Loopback|isatap|Teredo') { continue }
    $rx += To-Int64Value $nic.BytesReceivedPersec
    $tx += To-Int64Value $nic.BytesSentPersec
}
Write-Output ("NET|{0}|{1}" -f $rx, $tx)

Get-CimInstance Win32_LogicalDisk -Filter 'DriveType=3' | ForEach-Object {
    Write-Output ("DISK|{0}|{1}|{2}" -f $_.DeviceID, (To-Int64Value $_.Size), (To-Int64Value $_.FreeSpace))
}

Get-CimInstance Win32_PerfFormattedData_PerfDisk_LogicalDisk |
    Where-Object { $_.Name -ne '_Total' -and $_.Name -notmatch '^HarddiskVolume' } |
    ForEach-Object {
        Write-Output ("DIO|{0}|{1}|{2}" -f $_.Name, (To-Int64Value $_.DiskReadBytesPersec), (To-Int64Value $_.DiskWriteBytesPersec))
    }
""";

    private SshClient? _sshClient;
    private CancellationTokenSource? _cts;
    private Task? _monitorTask;
    private DateTime _lastSampleTime;
    private MonitorTargetKind _targetKind = MonitorTargetKind.Linux;
    private Func<string, TimeSpan, CancellationToken, Task<string>>? _commandRunner;
    private bool _ownsSshClient;

    private List<long[]>? _prevCpuStat;
    private Dictionary<string, (long rx, long tx)>? _prevNetStat;
    private Dictionary<string, (long readSectors, long writeSectors)>? _prevDiskStat;

    public bool IsMonitoring => _monitorTask != null && !_monitorTask.IsCompleted;

    public event Action<MonitorSnapshot>? DataUpdated;
    public event Action<string>? ErrorOccurred;

    public async Task StartAsync(
        SessionInfo session,
        string? password,
        Func<string, TimeSpan, CancellationToken, Task<string>>? commandRunner = null,
        bool isWindowsOpenSsh = false)
    {
        Stop();
        _commandRunner = commandRunner;
        _ownsSshClient = commandRunner == null;
        _targetKind = isWindowsOpenSsh ? MonitorTargetKind.Windows : MonitorTargetKind.Linux;

        if (commandRunner != null)
        {
            StartMonitorLoop();
            return;
        }

        var authMethods = SshAgentAuthService.CreateAuthenticationMethods(session, password);
        var connectionInfo = ProxyConnectionFactory.CreateSshConnectionInfo(session, authMethods);
        SshAlgorithmPreferenceService.Apply(connectionInfo, session);
        _sshClient = new SshClient(connectionInfo)
        {
            KeepAliveInterval = TimeSpan.FromSeconds(30)
        };

        try
        {
            await ConnectWithRetryAsync(_sshClient, CancellationToken.None).ConfigureAwait(false);
            _targetKind = SshServerInfo.IsWindowsOpenSshServer(connectionInfo.ServerVersion)
                ? MonitorTargetKind.Windows
                : MonitorTargetKind.Linux;
        }
        catch (Exception ex)
        {
            var displayMessage = SshServerInfo.BuildConnectionErrorMessage(ex);
            ErrorOccurred?.Invoke(string.Format(LocalizationService.Shared.Text("Monitor.ConnectionFailed"), displayMessage));
            _sshClient?.Dispose();
            _sshClient = null;
            return;
        }

        StartMonitorLoop();
    }

    public void Stop()
    {
        _cts?.Cancel();
        try { _monitorTask?.Wait(TimeSpan.FromSeconds(3)); } catch { }
        if (_ownsSshClient)
        {
            _sshClient?.Disconnect();
            _sshClient?.Dispose();
        }

        _sshClient = null;
        _cts?.Dispose();
        _cts = null;
        _monitorTask = null;
        _lastSampleTime = default;
        _targetKind = MonitorTargetKind.Linux;
        _commandRunner = null;
        _ownsSshClient = false;
        _prevCpuStat = null;
        _prevNetStat = null;
        _prevDiskStat = null;
    }

    private void StartMonitorLoop()
    {
        _lastSampleTime = default;
        _prevCpuStat = null;
        _prevNetStat = null;
        _prevDiskStat = null;

        _cts = new CancellationTokenSource();
        _monitorTask = Task.Run(() => MonitorLoop(_cts.Token));
    }

    private static async Task ConnectWithRetryAsync(SshClient client, CancellationToken cancellationToken)
    {
        Exception? lastError = null;
        var delays = new[] { TimeSpan.Zero, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4) };
        foreach (var delay in delays)
        {
            if (delay > TimeSpan.Zero)
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);

            try
            {
                await Task.Run(() => client.Connect(), cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (Exception ex)
            {
                lastError = ex;
                if (client.IsConnected)
                    return;
            }
        }

        throw lastError ?? new InvalidOperationException("SSH monitor connection failed.");
    }

    private async Task MonitorLoop(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                var snapshot = await CollectAsync(ct).ConfigureAwait(false);
                if (snapshot != null)
                    DataUpdated?.Invoke(snapshot);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                ErrorOccurred?.Invoke(string.Format(LocalizationService.Shared.Text("Monitor.CollectFailed"), ex.Message));
            }
        }
    }

    private Task<MonitorSnapshot?> CollectAsync(CancellationToken ct)
    {
        return _targetKind == MonitorTargetKind.Windows
            ? CollectWindowsAsync(ct)
            : CollectLinuxAsync(ct);
    }

    private async Task<MonitorSnapshot?> CollectLinuxAsync(CancellationToken ct)
    {
        if (!HasRemoteCommandSource())
            return null;

        var cmd =
            $"cat /proc/stat; echo '{LinuxSectionSeparator}'; " +
            $"cat /proc/meminfo; echo '{LinuxSectionSeparator}'; " +
            $"cat /proc/net/dev; echo '{LinuxSectionSeparator}'; " +
            $"cat /proc/diskstats; echo '{LinuxSectionSeparator}'; " +
            $"df -P";

        var output = await TryRunRemoteCommandAsync(cmd, TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
        if (output == null)
            return null;

        var now = DateTime.Now;
        var elapsed = _lastSampleTime == default ? 2.0 : (now - _lastSampleTime).TotalSeconds;
        _lastSampleTime = now;

        var normalizedOutput = output.Replace("\r\n", "\n", StringComparison.Ordinal);
        var sections = normalizedOutput.Split($"\n{LinuxSectionSeparator}\n", StringSplitOptions.None);
        if (sections.Length < 5)
            sections = normalizedOutput.Split($"{LinuxSectionSeparator}\n", StringSplitOptions.None);

        if (sections.Length < 5)
            return null;

        var currCpuStat = LinuxProcParser.ParseProcStat(sections[0]);
        List<CpuCoreInfo> cpuCores;
        if (_prevCpuStat != null)
            cpuCores = LinuxProcParser.CalculateCpuUsage(_prevCpuStat, currCpuStat);
        else
            cpuCores = new List<CpuCoreInfo>();
        _prevCpuStat = currCpuStat;

        var memory = LinuxProcParser.ParseProcMeminfo(sections[1]);

        var currNetStat = LinuxProcParser.ParseProcNetDev(sections[2]);
        NetworkSpeed? netSpeed = null;
        if (_prevNetStat != null)
            netSpeed = LinuxProcParser.CalculateNetworkSpeed(_prevNetStat, currNetStat, elapsed);
        _prevNetStat = currNetStat;

        var currDiskStat = LinuxProcParser.ParseProcDiskstats(sections[3]);
        List<DiskIoInfo> diskIo;
        if (_prevDiskStat != null)
            diskIo = LinuxProcParser.CalculateDiskIo(_prevDiskStat, currDiskStat, elapsed);
        else
            diskIo = new List<DiskIoInfo>();
        _prevDiskStat = currDiskStat;

        var diskPartitions = LinuxProcParser.ParseDf(sections[4]);

        return new MonitorSnapshot
        {
            CpuCores = cpuCores,
            Memory = memory,
            NetworkSpeed = netSpeed,
            DiskPartitions = diskPartitions,
            DiskIo = diskIo
        };
    }

    private async Task<MonitorSnapshot?> CollectWindowsAsync(CancellationToken ct)
    {
        if (!HasRemoteCommandSource())
            return null;

        var encodedScript = Convert.ToBase64String(Encoding.Unicode.GetBytes(WindowsMonitorScript));
        var cmd = $"powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {encodedScript}";
        var output = await TryRunRemoteCommandAsync(cmd, TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
        return output == null ? null : ParseWindowsMonitorOutput(output);
    }

    private bool HasRemoteCommandSource()
    {
        return _commandRunner != null || _sshClient?.IsConnected == true;
    }

    private async Task<string?> TryRunRemoteCommandAsync(string commandText, TimeSpan timeout, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            if (_commandRunner != null)
                return await _commandRunner(commandText, timeout, cts.Token).ConfigureAwait(false);

            return await Task.Run(() =>
            {
                if (_sshClient == null || !_sshClient.IsConnected)
                    return null;

                using var command = _sshClient.CreateCommand(commandText);
                command.CommandTimeout = timeout;
                var result = command.Execute();
                if (command.ExitStatus != 0)
                {
                    var error = string.IsNullOrWhiteSpace(command.Error)
                        ? $"Remote command exited with code {command.ExitStatus}."
                        : command.Error.Trim();
                    throw new InvalidOperationException(error);
                }

                return result;
            }, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke(string.Format(LocalizationService.Shared.Text("Monitor.CommandFailed"), ex.Message));
            return null;
        }
    }

    private static MonitorSnapshot ParseWindowsMonitorOutput(string output)
    {
        var snapshot = new MonitorSnapshot();
        foreach (var rawLine in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
                continue;

            var parts = line.Split('|');
            switch (parts[0])
            {
                case "CPU" when parts.Length >= 3:
                    snapshot.CpuCores.Add(new CpuCoreInfo
                    {
                        CoreIndex = ParseIntOrZero(parts[1]),
                        UsagePercent = Math.Clamp(Math.Round(ParseDoubleOrZero(parts[2]), 1), 0, 100)
                    });
                    break;

                case "MEM" when parts.Length >= 3:
                    var totalKb = ParseLongOrZero(parts[1]);
                    var freeKb = ParseLongOrZero(parts[2]);
                    snapshot.Memory = new MemoryInfo
                    {
                        TotalKB = totalKb,
                        FreeKB = freeKb,
                        UsedKB = Math.Max(0, totalKb - freeKb),
                        CachedKB = 0,
                        BuffersKB = 0
                    };
                    break;

                case "NET" when parts.Length >= 3:
                    snapshot.NetworkSpeed = new NetworkSpeed
                    {
                        RxBytesPerSec = Math.Max(0, ParseDoubleOrZero(parts[1])),
                        TxBytesPerSec = Math.Max(0, ParseDoubleOrZero(parts[2])),
                        Timestamp = DateTime.Now
                    };
                    break;

                case "DISK" when parts.Length >= 4:
                    var totalBytes = ParseLongOrZero(parts[2]);
                    var freeBytes = ParseLongOrZero(parts[3]);
                    var usedBytes = Math.Max(0, totalBytes - freeBytes);
                    snapshot.DiskPartitions.Add(new DiskPartitionInfo
                    {
                        Device = parts[1],
                        MountPoint = parts[1],
                        TotalMB = totalBytes / 1024 / 1024,
                        UsedMB = usedBytes / 1024 / 1024,
                        UsagePercent = totalBytes > 0 ? Math.Round(usedBytes * 100.0 / totalBytes, 1) : 0
                    });
                    break;

                case "DIO" when parts.Length >= 4:
                    snapshot.DiskIo.Add(new DiskIoInfo
                    {
                        Device = parts[1],
                        ReadKBPerSec = Math.Max(0, ParseDoubleOrZero(parts[2]) / 1024.0),
                        WriteKBPerSec = Math.Max(0, ParseDoubleOrZero(parts[3]) / 1024.0)
                    });
                    break;
            }
        }

        snapshot.CpuCores = snapshot.CpuCores
            .GroupBy(core => core.CoreIndex)
            .Select(group => group.First())
            .OrderBy(core => core.CoreIndex)
            .ToList();

        if (snapshot.CpuCores.Count > 0 && snapshot.CpuCores.All(core => core.CoreIndex != 0))
        {
            snapshot.CpuCores.Insert(0, new CpuCoreInfo
            {
                CoreIndex = 0,
                UsagePercent = Math.Round(snapshot.CpuCores.Average(core => core.UsagePercent), 1)
            });
        }

        return snapshot;
    }

    private static int ParseIntOrZero(string text)
    {
        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : 0;
    }

    private static long ParseLongOrZero(string text)
    {
        return long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : 0;
    }

    private static double ParseDoubleOrZero(string text)
    {
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : 0;
    }

    public void Dispose() => Stop();
}
