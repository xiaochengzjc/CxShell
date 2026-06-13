using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ChiXueSsh.Models;
using Renci.SshNet;

namespace ChiXueSsh.Services;

public class ServerMonitorService : IDisposable
{
    private SshClient? _sshClient;
    private CancellationTokenSource? _cts;
    private Task? _monitorTask;
    private DateTime _lastSampleTime;

    // 保存上次采样原始值，用于差值计算
    private List<long[]>? _prevCpuStat;
    private Dictionary<string, (long rx, long tx)>? _prevNetStat;
    private Dictionary<string, (long readSectors, long writeSectors)>? _prevDiskStat;

    public bool IsMonitoring => _monitorTask != null && !_monitorTask.IsCompleted;

    public event Action<MonitorSnapshot>? DataUpdated;
    public event Action<string>? ErrorOccurred;

    public async Task StartAsync(SessionInfo session, string? password)
    {
        Stop();

        var authMethods = SshAgentAuthService.CreateAuthenticationMethods(session, password);
        var connectionInfo = new ConnectionInfo(session.Host, session.Port, session.Username, authMethods.ToArray());
        SshAlgorithmPreferenceService.Apply(connectionInfo, session);
        _sshClient = new SshClient(connectionInfo)
        {
            KeepAliveInterval = TimeSpan.FromSeconds(30)
        };

        try
        {
            await Task.Run(() => _sshClient.Connect());
        }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke($"监控连接失败: {ex.Message}");
            _sshClient?.Dispose();
            _sshClient = null;
            return;
        }

        _prevCpuStat = null;
        _prevNetStat = null;
        _prevDiskStat = null;

        _cts = new CancellationTokenSource();
        _monitorTask = Task.Run(() => MonitorLoop(_cts.Token));
    }

    public void Stop()
    {
        _cts?.Cancel();
        try { _monitorTask?.Wait(TimeSpan.FromSeconds(3)); } catch { }
        _sshClient?.Disconnect();
        _sshClient?.Dispose();
        _sshClient = null;
        _cts?.Dispose();
        _cts = null;
        _monitorTask = null;
        _prevCpuStat = null;
        _prevNetStat = null;
        _prevDiskStat = null;
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
                ErrorOccurred?.Invoke($"采集失败: {ex.Message}");
            }
        }
    }

    private async Task<MonitorSnapshot?> CollectAsync(CancellationToken ct)
    {
        if (_sshClient == null || !_sshClient.IsConnected)
            return null;

        // 合并所有命令一次执行，减少 SSH 往返
        const string sep = "---SEP---";
        var cmd =
            $"cat /proc/stat; echo '{sep}'; " +
            $"cat /proc/meminfo; echo '{sep}'; " +
            $"cat /proc/net/dev; echo '{sep}'; " +
            $"cat /proc/diskstats; echo '{sep}'; " +
            $"df -P";

        string output;
        try
        {
            using var cts2 = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts2.CancelAfter(TimeSpan.FromSeconds(5));
            output = await Task.Run(() =>
            {
                using var command = _sshClient.RunCommand(cmd);
                return command.Result;
            }, cts2.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke($"命令执行超时或失败: {ex.Message}");
            return null;
        }

        var now = DateTime.Now;
        var elapsed = _lastSampleTime == default ? 2.0 : (now - _lastSampleTime).TotalSeconds;
        _lastSampleTime = now;

        var sections = output.Split($"\n{sep}\n", StringSplitOptions.None);
        if (sections.Length < 5)
            sections = output.Split($"{sep}\n", StringSplitOptions.None);

        if (sections.Length < 5)
            return null;

        // CPU
        var currCpuStat = LinuxProcParser.ParseProcStat(sections[0]);
        List<CpuCoreInfo> cpuCores;
        if (_prevCpuStat != null)
            cpuCores = LinuxProcParser.CalculateCpuUsage(_prevCpuStat, currCpuStat);
        else
            cpuCores = new List<CpuCoreInfo>();
        _prevCpuStat = currCpuStat;

        // Memory
        var memory = LinuxProcParser.ParseProcMeminfo(sections[1]);

        // Network
        var currNetStat = LinuxProcParser.ParseProcNetDev(sections[2]);
        NetworkSpeed? netSpeed = null;
        if (_prevNetStat != null)
            netSpeed = LinuxProcParser.CalculateNetworkSpeed(_prevNetStat, currNetStat, elapsed);
        _prevNetStat = currNetStat;

        // Disk IO
        var currDiskStat = LinuxProcParser.ParseProcDiskstats(sections[3]);
        List<DiskIoInfo> diskIo;
        if (_prevDiskStat != null)
            diskIo = LinuxProcParser.CalculateDiskIo(_prevDiskStat, currDiskStat, elapsed);
        else
            diskIo = new List<DiskIoInfo>();
        _prevDiskStat = currDiskStat;

        // Disk partitions
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

    public void Dispose() => Stop();
}
