using CxShell.Controls;
using Avalonia.Input;

namespace CxShell.Tests;

public sealed class TerminalControlInputTests
{
    [Fact]
    public void BuildPastePayloadNormalizesLineEndings()
    {
        Assert.Equal("one\rtwo\rthree", TerminalControl.BuildPastePayload("one\r\ntwo\nthree", false));
    }

    [Fact]
    public void BuildPastePayloadWrapsBracketedPasteMarkers()
    {
        Assert.Equal("\x1b[200~one\rtwo\x1b[201~",
            TerminalControl.BuildPastePayload("one\r\ntwo", true));
    }

    [Theory]
    [InlineData(Key.O, 0x0F)]
    [InlineData(Key.X, 0x18)]
    [InlineData(Key.G, 0x07)]
    [InlineData(Key.J, 0x0A)]
    [InlineData(Key.T, 0x14)]
    [InlineData(Key.Z, 0x1A)]
    public void ControlLettersUseTheStandardAsciiControlCodes(Key key, int expected)
    {
        var value = TerminalControl.GetControlCharacter(key);

        Assert.NotNull(value);
        Assert.Equal(expected, value![0]);
    }

    [Fact]
    public void ControlSpaceProducesNulForNanoMarkMode()
    {
        Assert.Equal("\0", TerminalControl.GetControlCharacter(Key.Space));
    }

    [Theory]
    [InlineData(Key.D6, 0x1E)]
    [InlineData(Key.OemOpenBrackets, 0x1B)]
    [InlineData(Key.OemBackslash, 0x1C)]
    [InlineData(Key.OemCloseBrackets, 0x1D)]
    [InlineData(Key.OemMinus, 0x1F)]
    public void ControlPunctuationUsesTheStandardAsciiControlCodes(Key key, int expected)
    {
        var value = TerminalControl.GetControlCharacter(key);

        Assert.NotNull(value);
        Assert.Equal(expected, value![0]);
    }

    [Fact]
    public void NonControlKeyDoesNotProduceAControlCharacter()
    {
        Assert.Null(TerminalControl.GetControlCharacter(Key.Enter));
    }
}
