using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using CxShell.Controls;
using CxShell.Models;
using CxShell.Services;

namespace CxShell.Controls;

/// <summary>bool → 监控状态颜色 (true=绿, false=灰)</summary>
public class MonitorColorConverter : IValueConverter
{
    public static readonly MonitorColorConverter Instance = new();
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? Color.Parse("#52C41A") : Color.Parse("#888888");
    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
}

/// <summary>bool → 监控状态文字</summary>
public class MonitorStatusConverter : IValueConverter
{
    public static readonly MonitorStatusConverter Instance = new();
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true
            ? LocalizationService.Shared.Text("Monitor.StatusRunning")
            : LocalizationService.Shared.Text("Monitor.StatusStopped");
    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
}

/// <summary>null → false, 非null → true</summary>
public class NullToBoolConverter : IValueConverter
{
    public static readonly NullToBoolConverter Instance = new();
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is not null && value.ToString() != string.Empty;
    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
}

/// <summary>Count > 0 → true</summary>
public class CountToBoolConverter : IValueConverter
{
    public static readonly CountToBoolConverter Instance = new();
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is int count && count > 0;
    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
}

/// <summary>CPU 使用率 → 颜色（低=绿，高=红）</summary>
public class CpuColorConverter : IValueConverter
{
    public static readonly CpuColorConverter Instance = new();
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        double pct = value is double d ? d : 0;
        if (pct < 50) return Color.Parse("#52C41A");
        if (pct < 80) return Color.Parse("#FAAD14");
        return Color.Parse("#FF4D4F");
    }
    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
}

/// <summary>磁盘使用率 → 颜色</summary>
public class DiskColorConverter : IValueConverter
{
    public static readonly DiskColorConverter Instance = new();
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        double pct = value is double d ? d : 0;
        if (pct < 70) return Color.Parse("#52C41A");
        if (pct < 90) return Color.Parse("#FAAD14");
        return Color.Parse("#FF4D4F");
    }
    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
}

/// <summary>MemoryInfo → PieSegment 列表（已用/缓存/空闲）</summary>
public class MemoryToPieConverter : IValueConverter
{
    public static readonly MemoryToPieConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not MemoryInfo mem || mem.TotalKB <= 0)
            return null;

        return new List<PieSegment>
        {
            new() { Value = mem.UsedKB,   Color = Color.Parse("#FF7875"), Label = LocalizationService.Shared.Text("Monitor.MemoryUsed") },
            new() { Value = mem.CachedKB, Color = Color.Parse("#69B1FF"), Label = LocalizationService.Shared.Text("Monitor.MemoryCached") },
            new() { Value = mem.FreeKB,   Color = Color.Parse("#95DE64"), Label = LocalizationService.Shared.Text("Monitor.MemoryFree") },
        };
    }
    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
}
