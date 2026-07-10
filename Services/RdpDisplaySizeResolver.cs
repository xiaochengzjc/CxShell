using System;
using Avalonia;

namespace CxShell.Services;

public readonly record struct RdpDesktopSize(int Width, int Height);

public static class RdpDisplaySizeResolver
{
    private const int MinimumWidth = 320;
    private const int MinimumHeight = 240;
    private const int MaximumWidth = 7680;
    private const int MaximumHeight = 4320;

    public static RdpDesktopSize? ResolveInitial(
        string? windowSizeMode,
        Size viewportSize,
        PixelSize? monitorPixelSize,
        double renderScaling)
    {
        if (string.Equals(windowSizeMode, "FullScreen", StringComparison.OrdinalIgnoreCase) &&
            monitorPixelSize is { Width: >= MinimumWidth, Height: >= MinimumHeight } monitorSize)
        {
            return Clamp(monitorSize.Width, monitorSize.Height);
        }

        return string.Equals(windowSizeMode, "WorkSpace", StringComparison.OrdinalIgnoreCase)
            ? ResolveViewport(viewportSize, renderScaling)
            : null;
    }

    public static RdpDesktopSize? ResolveViewport(Size viewportSize, double renderScaling)
    {
        if (viewportSize.Width < MinimumWidth || viewportSize.Height < MinimumHeight)
            return null;

        var scale = NormalizeRenderScaling(renderScaling);
        return Clamp(
            (int)Math.Round(viewportSize.Width * scale),
            (int)Math.Round(viewportSize.Height * scale));
    }

    private static RdpDesktopSize Clamp(int width, int height)
    {
        return new RdpDesktopSize(
            Math.Clamp(width, MinimumWidth, MaximumWidth),
            Math.Clamp(height, MinimumHeight, MaximumHeight));
    }

    private static double NormalizeRenderScaling(double renderScaling)
    {
        return double.IsFinite(renderScaling)
            ? Math.Clamp(renderScaling, 0.5, 8)
            : 1;
    }
}
