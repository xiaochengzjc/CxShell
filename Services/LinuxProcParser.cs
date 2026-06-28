using System;
using System.Collections.Generic;
using CxShell.Models;

namespace CxShell.Services;

/// <summary>
/// 纯静态工具类，解析 Linux /proc 虚拟文件系统内容
/// </summary>
public static class LinuxProcParser
{
    /// <summary>
    /// 解析 /proc/stat，返回每行 CPU 的时间片数组
    /// 索引0=cpu总计, 1=cpu0, 2=cpu1 ...
    /// 每行格式: cpu  user nice system idle iowait irq softirq steal guest guest_nice
    /// </summary>
    public static List<long[]> ParseProcStat(string content)
    {
        var result = new List<long[]>();
        foreach (var line in content.Split('\n'))
        {
            if (!line.StartsWith("cpu")) break;
            var parts = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 5) continue;

            var times = new long[parts.Length - 1];
            for (int i = 1; i < parts.Length; i++)
            {
                if (long.TryParse(parts[i], out var val))
                    times[i - 1] = val;
            }
            result.Add(times);
        }
        return result;
    }

    /// <summary>
    /// 根据两次 /proc/stat 采样计算 CPU 使用率
    /// </summary>
    public static List<CpuCoreInfo> CalculateCpuUsage(List<long[]> prev, List<long[]> curr)
    {
        var result = new List<CpuCoreInfo>();
        int count = Math.Min(prev.Count, curr.Count);

        for (int i = 0; i < count; i++)
        {
            var p = prev[i];
            var c = curr[i];
            if (p.Length < 4 || c.Length < 4) continue;

            // idle = index 3, iowait = index 4 (if present)
            long prevIdle = p[3] + (p.Length > 4 ? p[4] : 0);
            long currIdle = c[3] + (c.Length > 4 ? c[4] : 0);

            long prevTotal = 0, currTotal = 0;
            foreach (var v in p) prevTotal += v;
            foreach (var v in c) currTotal += v;

            long totalDiff = currTotal - prevTotal;
            long idleDiff = currIdle - prevIdle;

            double usage = totalDiff > 0 ? (1.0 - (double)idleDiff / totalDiff) * 100.0 : 0;
            usage = Math.Max(0, Math.Min(100, usage));

            result.Add(new CpuCoreInfo { CoreIndex = i, UsagePercent = Math.Round(usage, 1) });
        }
        return result;
    }

    /// <summary>
    /// 解析 /proc/meminfo
    /// </summary>
    public static MemoryInfo ParseProcMeminfo(string content)
    {
        var info = new MemoryInfo();
        var dict = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        foreach (var line in content.Split('\n'))
        {
            var idx = line.IndexOf(':');
            if (idx < 0) continue;
            var key = line[..idx].Trim();
            var rest = line[(idx + 1)..].Trim();
            var parts = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length > 0 && long.TryParse(parts[0], out var val))
                dict[key] = val;
        }

        dict.TryGetValue("MemTotal", out var total);
        dict.TryGetValue("MemFree", out var free);
        dict.TryGetValue("Cached", out var cached);
        dict.TryGetValue("Buffers", out var buffers);
        dict.TryGetValue("MemAvailable", out var available);

        info.TotalKB = total;
        info.FreeKB = free;
        info.CachedKB = cached;
        info.BuffersKB = buffers;
        // UsedKB = Total - Available（更准确，等同于 htop 的 Used）
        info.UsedKB = available > 0 ? total - available : total - free - cached - buffers;
        return info;
    }

    /// <summary>
    /// 解析 /proc/net/dev，返回各接口的收发字节数
    /// key=接口名, value=(rxBytes, txBytes)
    /// 跳过 lo（本地回环）
    /// </summary>
    public static Dictionary<string, (long rx, long tx)> ParseProcNetDev(string content)
    {
        var result = new Dictionary<string, (long, long)>();
        var lines = content.Split('\n');
        // 前两行是表头
        for (int i = 2; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (string.IsNullOrEmpty(line)) continue;
            var colonIdx = line.IndexOf(':');
            if (colonIdx < 0) continue;

            var iface = line[..colonIdx].Trim();
            if (iface == "lo") continue;

            var parts = line[(colonIdx + 1)..].Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 9) continue;

            if (long.TryParse(parts[0], out var rx) && long.TryParse(parts[8], out var tx))
                result[iface] = (rx, tx);
        }
        return result;
    }

    /// <summary>
    /// 根据两次 /proc/net/dev 采样和时间差计算网速
    /// </summary>
    public static NetworkSpeed CalculateNetworkSpeed(
        Dictionary<string, (long rx, long tx)> prev,
        Dictionary<string, (long rx, long tx)> curr,
        double elapsedSeconds)
    {
        long totalRxDiff = 0, totalTxDiff = 0;
        foreach (var kv in curr)
        {
            if (!prev.TryGetValue(kv.Key, out var prevVal)) continue;
            totalRxDiff += Math.Max(0, kv.Value.rx - prevVal.rx);
            totalTxDiff += Math.Max(0, kv.Value.tx - prevVal.tx);
        }

        double secs = elapsedSeconds > 0 ? elapsedSeconds : 2.0;
        return new NetworkSpeed
        {
            RxBytesPerSec = totalRxDiff / secs,
            TxBytesPerSec = totalTxDiff / secs,
            Timestamp = DateTime.Now
        };
    }

    /// <summary>
    /// 解析 /proc/diskstats，返回各设备的读写扇区数
    /// key=设备名, value=(readSectors, writeSectors)
    /// 只返回主设备（如 sda、vda、nvme0n1），过滤分区
    /// </summary>
    public static Dictionary<string, (long readSectors, long writeSectors)> ParseProcDiskstats(string content)
    {
        var result = new Dictionary<string, (long, long)>();
        foreach (var line in content.Split('\n'))
        {
            var parts = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 14) continue;

            var name = parts[2];
            // 只取主设备：排除分区（末尾是数字且不是 nvme*）
            if (IsPartition(name)) continue;

            if (long.TryParse(parts[5], out var reads) && long.TryParse(parts[9], out var writes))
                result[name] = (reads, writes);
        }
        return result;
    }

    private static bool IsPartition(string name)
    {
        // nvme0n1p1 是分区，nvme0n1 是主设备
        if (name.StartsWith("nvme") && name.Contains('p') &&
            char.IsDigit(name[^1]) && name.Contains("n1p"))
            return true;
        // sda1, vda1 等是分区
        if (!name.StartsWith("nvme") && char.IsDigit(name[^1]))
            return true;
        return false;
    }

    /// <summary>
    /// 根据两次磁盘 IO 采样计算 IO 速率（KB/s）
    /// 扇区大小默认 512 字节
    /// </summary>
    public static List<DiskIoInfo> CalculateDiskIo(
        Dictionary<string, (long readSectors, long writeSectors)> prev,
        Dictionary<string, (long readSectors, long writeSectors)> curr,
        double elapsedSeconds)
    {
        var result = new List<DiskIoInfo>();
        double secs = elapsedSeconds > 0 ? elapsedSeconds : 2.0;
        const int sectorSize = 512;

        foreach (var kv in curr)
        {
            if (!prev.TryGetValue(kv.Key, out var prevVal)) continue;
            long readDiff = Math.Max(0, kv.Value.readSectors - prevVal.readSectors);
            long writeDiff = Math.Max(0, kv.Value.writeSectors - prevVal.writeSectors);

            result.Add(new DiskIoInfo
            {
                Device = kv.Key,
                ReadKBPerSec = readDiff * sectorSize / 1024.0 / secs,
                WriteKBPerSec = writeDiff * sectorSize / 1024.0 / secs
            });
        }
        return result;
    }

    /// <summary>
    /// 解析 df -P 输出
    /// 格式: Filesystem 1024-blocks Used Available Capacity% Mounted on
    /// </summary>
    public static List<DiskPartitionInfo> ParseDf(string content)
    {
        var result = new List<DiskPartitionInfo>();
        var lines = content.Split('\n');
        // 跳过表头
        for (int i = 1; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (string.IsNullOrEmpty(line)) continue;

            var parts = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 6) continue;

            var device = parts[0];
            // 跳过 tmpfs、devtmpfs 等虚拟文件系统
            if (device.StartsWith("tmpfs") || device.StartsWith("devtmpfs") ||
                device.StartsWith("udev") || device.StartsWith("none"))
                continue;

            if (!long.TryParse(parts[1], out var total1k)) continue;
            if (!long.TryParse(parts[2], out var used1k)) continue;

            var percentStr = parts[4].TrimEnd('%');
            double.TryParse(percentStr, out var percent);

            result.Add(new DiskPartitionInfo
            {
                Device = device,
                MountPoint = parts[5],
                TotalMB = total1k / 1024,
                UsedMB = used1k / 1024,
                UsagePercent = percent
            });
        }
        return result;
    }
}
