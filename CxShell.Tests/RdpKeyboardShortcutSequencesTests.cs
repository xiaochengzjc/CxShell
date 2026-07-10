using CxShell.Services;

namespace CxShell.Tests;

public sealed class RdpKeyboardShortcutSequencesTests
{
    [Fact]
    public void CtrlAltDelete_UsesExtendedDeleteInsteadOfEnd()
    {
        Assert.Equal(
            new uint[] { 0x1D, 0x38, 0x0153 },
            RdpKeyboardShortcutSequences.CtrlAltDelete);
    }

    [Fact]
    public void SaveRemoteScreenshot_UsesWindowsPrintScreen()
    {
        Assert.Equal(
            new uint[] { 0x015B, 0x0137 },
            RdpKeyboardShortcutSequences.SaveRemoteScreenshot);
    }

    [Theory]
    [InlineData(true, true, RdpKeyboardShortcutSequences.ExtendedDeleteScancode)]
    [InlineData(true, false, RdpKeyboardShortcutSequences.ExtendedEndScancode)]
    [InlineData(false, true, RdpKeyboardShortcutSequences.ExtendedEndScancode)]
    [InlineData(false, false, RdpKeyboardShortcutSequences.ExtendedEndScancode)]
    public void TranslateCtrlAltEnd_OnlyMapsCompleteShortcut(
        bool controlDown,
        bool altDown,
        uint expected)
    {
        Assert.Equal(
            expected,
            RdpKeyboardShortcutSequences.TranslateCtrlAltEnd(
                RdpKeyboardShortcutSequences.ExtendedEndScancode,
                controlDown,
                altDown));
    }

    [Fact]
    public void TranslateCtrlAltEnd_DoesNotMapNumpadEnd()
    {
        Assert.Equal(
            0x4Fu,
            RdpKeyboardShortcutSequences.TranslateCtrlAltEnd(0x4F, true, true));
    }
}
