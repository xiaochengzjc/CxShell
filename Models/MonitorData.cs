using System;
using System.Collections.Generic;
using ChiXueSsh.Services;

namespace ChiXueSsh.Models;

public class CpuCoreInfo
{
    public int CoreIndex { get; set; }
    public string Label => CoreIndex == 0
        ? LocalizationService.Shared.Text("Monitor.CpuTotal")
        : string.Format(LocalizationService.Shared.Text("Monitor.CpuCore"), CoreIndex);
    public double UsagePercent { get; set; }
}

public class MemoryInfo
{
    public long TotalKB { get; set; }
    public long UsedKB { get; set; }
    public long CachedKB { get; set; }
    public long FreeKB { get; set; }
    public long BuffersKB { get; set; }

    public double UsedPercent => TotalKB > 0 ? (double)UsedKB / TotalKB * 100 : 0;
    public double CachedPercent => TotalKB > 0 ? (double)CachedKB / TotalKB * 100 : 0;
    public double FreePercent => TotalKB > 0 ? (double)FreeKB / TotalKB * 100 : 0;

    public string TotalFormatted => FormatBytes(TotalKB * 1024);
    public string UsedFormatted => FormatBytes(UsedKB * 1024);
    public string CachedFormatted => FormatBytes(CachedKB * 1024);
    public string FreeFormatted => FormatBytes(FreeKB * 1024);

    private static string FormatBytes(long bytes)
    {
        if (bytes >= 1024L * 1024 * 1024)
            return $"{bytes / (1024.0 * 1024 * 1024):F1}G";
        if (bytes >= 1024 * 1024)
            return $"{bytes / (1024.0 * 1024):F1}M";
        if (bytes >= 1024)
            return $"{bytes / 1024.0:F1}K";
        return $"{bytes}B";
    }
}

public class NetworkSpeed
{
    public double RxBytesPerSec { get; set; }
    public double TxBytesPerSec { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.Now;

    public string RxFormatted => FormatSpeed(RxBytesPerSec);
    public string TxFormatted => FormatSpeed(TxBytesPerSec);

    private static string FormatSpeed(double bytesPerSec)
    {
        if (bytesPerSec >= 1024 * 1024)
            return $"{bytesPerSec / (1024 * 1024):F1} MB/s";
        if (bytesPerSec >= 1024)
            return $"{bytesPerSec / 1024:F1} KB/s";
        return $"{bytesPerSec:F0} B/s";
    }
}

public class DiskPartitionInfo
{
    public string Device { get; set; } = string.Empty;
    public string MountPoint { get; set; } = string.Empty;
    public long TotalMB { get; set; }
    public long UsedMB { get; set; }
    public double UsagePercent { get; set; }

    public string TotalFormatted => FormatSize(TotalMB);
    public string UsedFormatted => FormatSize(UsedMB);

    private static string FormatSize(long mb)
    {
        if (mb >= 1024)
            return $"{mb / 1024.0:F1}G";
        return $"{mb}M";
    }
}

public class DiskIoInfo
{
    public string Device { get; set; } = string.Empty;
    public double ReadKBPerSec { get; set; }
    public double WriteKBPerSec { get; set; }

    public string ReadFormatted => ReadKBPerSec >= 1024
        ? $"{ReadKBPerSec / 1024:F1} MB/s"
        : $"{ReadKBPerSec:F0} KB/s";

    public string WriteFormatted => WriteKBPerSec >= 1024
        ? $"{WriteKBPerSec / 1024:F1} MB/s"
        : $"{WriteKBPerSec:F0} KB/s";
}

public class MonitorSnapshot
{
    public List<CpuCoreInfo> CpuCores { get; set; } = new();
    public MemoryInfo? Memory { get; set; }
    public NetworkSpeed? NetworkSpeed { get; set; }
    public List<DiskPartitionInfo> DiskPartitions { get; set; } = new();
    public List<DiskIoInfo> DiskIo { get; set; } = new();
}
