using CxShell.Services;

namespace CxShell.Tests;

public sealed class RdpSystemShortcutPolicyTests
{
    [Theory]
    [InlineData(RdpSystemShortcutPolicy.VirtualKeyLeftWindows)]
    [InlineData(RdpSystemShortcutPolicy.VirtualKeyRightWindows)]
    [InlineData(RdpSystemShortcutPolicy.VirtualKeyApplications)]
    [InlineData(RdpSystemShortcutPolicy.VirtualKeyPrintScreen)]
    public void ShouldCapture_CapturesStandaloneSystemKeys(uint virtualKey)
    {
        Assert.True(RdpSystemShortcutPolicy.ShouldCapture(virtualKey, false, false, false));
    }

    [Theory]
    [InlineData(RdpSystemShortcutPolicy.VirtualKeyTab)]
    [InlineData(RdpSystemShortcutPolicy.VirtualKeyEscape)]
    [InlineData(RdpSystemShortcutPolicy.VirtualKeySpace)]
    public void ShouldCapture_CapturesAltSystemShortcuts(uint virtualKey)
    {
        Assert.True(RdpSystemShortcutPolicy.ShouldCapture(virtualKey, true, false, false));
    }

    [Fact]
    public void ShouldCapture_CapturesCtrlEscape()
    {
        Assert.True(RdpSystemShortcutPolicy.ShouldCapture(
            RdpSystemShortcutPolicy.VirtualKeyEscape,
            false,
            true,
            false));
    }

    [Fact]
    public void ShouldCapture_CapturesEntireWindowsKeyChord()
    {
        Assert.True(RdpSystemShortcutPolicy.ShouldCapture(0x44, false, false, true));
    }

    [Theory]
    [InlineData(0x41)]
    [InlineData(RdpSystemShortcutPolicy.VirtualKeyTab)]
    [InlineData(RdpSystemShortcutPolicy.VirtualKeySpace)]
    public void ShouldCapture_DoesNotCaptureOrdinaryLocalInput(uint virtualKey)
    {
        Assert.False(RdpSystemShortcutPolicy.ShouldCapture(virtualKey, false, false, false));
    }
}
