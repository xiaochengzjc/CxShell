using System;
using System.Globalization;
using AtomUI.Data;
using AtomUI.Theme.Styling;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace CxShell.ViewModels;

/// <summary>根据 IsConnected 返回连接状态颜色（IBrush）</summary>
public class SftpStatusColorConverter : IValueConverter
{
    public static readonly SftpStatusColorConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is true
            ? new SolidColorBrush(ThemeTokenColorHelper.GetColor(SharedTokenKind.ColorSuccess, Color.Parse("#52C41A")))
            : new SolidColorBrush(ThemeTokenColorHelper.GetColor(SharedTokenKind.ColorTextTertiary, Color.Parse("#808080")));
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>根据 IsDirectory 返回文件名颜色（IBrush）</summary>
public class SftpDirColorConverter : IValueConverter
{
    public static readonly SftpDirColorConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is true
            ? new SolidColorBrush(ThemeTokenColorHelper.GetColor(SharedTokenKind.ColorWarning, Color.Parse("#FAAD14")))
            : new SolidColorBrush(ThemeTokenColorHelper.GetColor(SharedTokenKind.ColorText, Color.Parse("#D0D0E8")));
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>根据 IsSelected 返回文件行背景色（IBrush）</summary>
public class SftpSelectedColorConverter : IValueConverter
{
    public static readonly SftpSelectedColorConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is true
            ? new SolidColorBrush(ThemeTokenColorHelper.GetColor(SharedTokenKind.ColorPrimaryBg, Color.Parse("#E6F4FF")))
            : new SolidColorBrush(Colors.Transparent);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Tab 选中状态 → 背景色（Color 用于 SolidColorBrush.Color 绑定）</summary>
public class TabSelectedBgConverter : IValueConverter
{
    public static readonly TabSelectedBgConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true
            ? ThemeTokenColorHelper.GetColor(SharedTokenKind.ColorBgContainer, Color.Parse("#1E1E1E"))
            : ThemeTokenColorHelper.GetColor(SharedTokenKind.ColorBgLayout, Color.Parse("#0D0D0D"));

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Tab 选中状态 → 边框色（Color）</summary>
public class TabSelectedBorderConverter : IValueConverter
{
    public static readonly TabSelectedBorderConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true
            ? ThemeTokenColorHelper.GetColor(SharedTokenKind.ColorPrimary, Color.Parse("#1677FF"))
            : ThemeTokenColorHelper.GetColor(SharedTokenKind.ColorBorder, Color.Parse("#333333"));

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Tab 选中状态 → 文字色（IBrush）</summary>
public class TabSelectedFgConverter : IValueConverter
{
    public static readonly TabSelectedFgConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => new SolidColorBrush(value is true
            ? ThemeTokenColorHelper.GetColor(SharedTokenKind.ColorPrimary, Color.Parse("#1677FF"))
            : ThemeTokenColorHelper.GetColor(SharedTokenKind.ColorText, Color.Parse("#262626")));

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

internal static class ThemeTokenColorHelper
{
    public static Color GetColor(SharedTokenKind kind, Color fallback)
    {
        var value = TokenResourceUtils.FindGlobalTokenResource(kind);
        return value switch
        {
            Color color => color,
            ISolidColorBrush brush => brush.Color,
            _ => fallback
        };
    }
}
