using Avalonia;
using CxShell.Services;

namespace CxShell.Tests;

public sealed class RdpDisplaySizeResolverTests
{
    [Theory]
    [InlineData(1.0, 1200, 800)]
    [InlineData(1.25, 1500, 1000)]
    [InlineData(1.5, 1800, 1200)]
    public void ResolveInitial_UsesPhysicalWorkspacePixels(double scale, int expectedWidth, int expectedHeight)
    {
        var size = RdpDisplaySizeResolver.ResolveInitial("WorkSpace", new Size(1200, 800), null, scale);

        Assert.Equal(new RdpDesktopSize(expectedWidth, expectedHeight), size);
    }

    [Fact]
    public void ResolveInitial_UsesMonitorPixelsForFullScreen()
    {
        var size = RdpDisplaySizeResolver.ResolveInitial(
            "FullScreen",
            new Size(1200, 800),
            new PixelSize(2560, 1440),
            1.5);

        Assert.Equal(new RdpDesktopSize(2560, 1440), size);
    }

    [Theory]
    [InlineData("Custom")]
    [InlineData("1024x768")]
    [InlineData(null)]
    public void ResolveInitial_DoesNotOverrideFixedSizes(string? mode)
    {
        Assert.Null(RdpDisplaySizeResolver.ResolveInitial(mode, new Size(1200, 800), null, 1));
    }

    [Fact]
    public void ResolveViewport_RejectsInvalidViewportAndClampsMaximum()
    {
        Assert.Null(RdpDisplaySizeResolver.ResolveViewport(new Size(100, 100), 1));
        Assert.Equal(
            new RdpDesktopSize(7680, 4320),
            RdpDisplaySizeResolver.ResolveViewport(new Size(4000, 3000), 8));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void ResolveViewport_UsesOneForNonFiniteScaling(double scale)
    {
        Assert.Equal(
            new RdpDesktopSize(1200, 800),
            RdpDisplaySizeResolver.ResolveViewport(new Size(1200, 800), scale));
    }
}
