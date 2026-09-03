using Avalonia.Input;
using CxShell.Terminal;

namespace CxShell.Tests;

public sealed class TerminalKeyboardEncoderTests
{
    [Fact]
    public void ModifyOtherKeysEncodesControlLetterWithItsPrintableCodePoint()
    {
        var encoded = TerminalKeyboardEncoder.TryEncode(
            Key.C,
            KeyModifiers.Control,
            modifyOtherKeysMode: 2,
            kittyKeyboardFlags: 0,
            out var sequence);

        Assert.True(encoded);
        Assert.Equal("\x1b[27;5;99~", sequence);
    }

    [Fact]
    public void KittyProtocolUsesUnicodeKeyCodeAndModifierBits()
    {
        var encoded = TerminalKeyboardEncoder.TryEncode(
            Key.Left,
            KeyModifiers.Control | KeyModifiers.Alt,
            modifyOtherKeysMode: 0,
            kittyKeyboardFlags: 1,
            out var sequence);

        Assert.True(encoded);
        Assert.Equal("\x1b[57361;7u", sequence);
    }

    [Fact]
    public void KittyProtocolEncodesSpecialKeysWithoutModifiers()
    {
        var encoded = TerminalKeyboardEncoder.TryEncode(
            Key.Up,
            KeyModifiers.None,
            modifyOtherKeysMode: 0,
            kittyKeyboardFlags: 1,
            out var sequence);

        Assert.True(encoded);
        Assert.Equal("\x1b[57362;1u", sequence);
    }

    [Fact]
    public void KittyProtocolEncodesFunctionKeysWithPrivateUseCodePoints()
    {
        var encoded = TerminalKeyboardEncoder.TryEncode(
            Key.F1,
            KeyModifiers.Shift,
            modifyOtherKeysMode: 0,
            kittyKeyboardFlags: 1,
            out var sequence);

        Assert.True(encoded);
        Assert.Equal("\x1b[57376;2u", sequence);
    }

    [Fact]
    public void KittyProtocolUsesDedicatedCodesForInsertAndDelete()
    {
        Assert.True(TerminalKeyboardEncoder.TryEncode(
            Key.Insert,
            KeyModifiers.None,
            modifyOtherKeysMode: 0,
            kittyKeyboardFlags: 1,
            out var insert));
        Assert.Equal("\x1b[57357;1u", insert);

        Assert.True(TerminalKeyboardEncoder.TryEncode(
            Key.Delete,
            KeyModifiers.None,
            modifyOtherKeysMode: 0,
            kittyKeyboardFlags: 1,
            out var delete));
        Assert.Equal("\x1b[57358;1u", delete);
    }

    [Fact]
    public void ModifyOtherKeysUsesColonCodePointForCtrlColon()
    {
        var encoded = TerminalKeyboardEncoder.TryEncode(
            Key.OemSemicolon,
            KeyModifiers.Control | KeyModifiers.Shift,
            modifyOtherKeysMode: 2,
            kittyKeyboardFlags: 0,
            out var sequence);

        Assert.True(encoded);
        Assert.Equal("\x1b[27;6;58~", sequence);
    }

    [Fact]
    public void KittyProtocolPreservesShiftedPunctuationCodePoint()
    {
        var encoded = TerminalKeyboardEncoder.TryEncode(
            Key.OemSemicolon,
            KeyModifiers.Control | KeyModifiers.Shift,
            modifyOtherKeysMode: 0,
            kittyKeyboardFlags: 1,
            out var sequence);

        Assert.True(encoded);
        Assert.Equal("\x1b[58;6u", sequence);
    }

    [Fact]
    public void NoProtocolLeavesUnmodifiedKeysToTheNormalEncoder()
    {
        Assert.False(TerminalKeyboardEncoder.TryEncode(
            Key.A,
            KeyModifiers.None,
            modifyOtherKeysMode: 2,
            kittyKeyboardFlags: 1,
            out _));
    }

    [Fact]
    public void KittyProtocolReportsAlternateKeyAndAssociatedText()
    {
        var flags = (int)(KittyKeyboardFlags.DisambiguateEscapeCodes |
                          KittyKeyboardFlags.ReportAlternateKeys |
                          KittyKeyboardFlags.ReportAllKeysAsEscapeCodes |
                          KittyKeyboardFlags.ReportAssociatedText);

        var encoded = TerminalKeyboardEncoder.TryEncodeTextInput(
            "A",
            Key.A,
            KeyModifiers.Shift,
            flags,
            out var sequence);

        Assert.True(encoded);
        Assert.Equal("\x1b[97:65;2;65u", sequence);
    }

    [Fact]
    public void KittyProtocolReportsReleaseWhenEventReportingIsEnabled()
    {
        var flags = (int)(KittyKeyboardFlags.DisambiguateEscapeCodes |
                          KittyKeyboardFlags.ReportEventTypes);

        Assert.True(TerminalKeyboardEncoder.TryEncode(
            Key.F5,
            KeyModifiers.None,
            modifyOtherKeysMode: 0,
            kittyKeyboardFlags: flags,
            TerminalKeyEventType.Press,
            out var press));
        Assert.Equal("\x1b[57380;1:1u", press);

        Assert.True(TerminalKeyboardEncoder.TryEncode(
            Key.F5,
            KeyModifiers.None,
            modifyOtherKeysMode: 0,
            kittyKeyboardFlags: flags,
            TerminalKeyEventType.Release,
            out var release));
        Assert.Equal("\x1b[57380;1:3u", release);
    }

    [Fact]
    public void KittyProtocolEncodesAdditionalKeypadAndLockKeys()
    {
        Assert.True(TerminalKeyboardEncoder.TryEncode(
            Key.NumPad9,
            KeyModifiers.None,
            modifyOtherKeysMode: 0,
            kittyKeyboardFlags: 1,
            out var keypad));
        Assert.Equal("\x1b[57408;1u", keypad);

        Assert.True(TerminalKeyboardEncoder.TryEncode(
            Key.PrintScreen,
            KeyModifiers.None,
            modifyOtherKeysMode: 0,
            kittyKeyboardFlags: 1,
            out var printScreen));
        Assert.Equal("\x1b[57361;1u", printScreen);
    }

    [Fact]
    public void KittyProtocolRejectsControlCharactersInAssociatedText()
    {
        Assert.False(TerminalKeyboardEncoder.TryEncodeTextInput(
            "line\nfeed",
            Key.None,
            KeyModifiers.None,
            (int)KittyKeyboardFlags.ReportAllKeysAsEscapeCodes |
            (int)KittyKeyboardFlags.ReportAssociatedText,
            out _));
    }
}
