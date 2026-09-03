using System.Text;
using CxShell.Terminal;

namespace CxShell.Tests;

public sealed class TerminalMouseEncoderTests
{
    [Fact]
    public void TrackingDisabledDoesNotProduceInput()
    {
        var result = TerminalMouseEncoder.Encode(
            TerminalMouseEventType.Press,
            TerminalMouseButton.Left,
            0,
            0,
            false,
            false,
            false,
            TerminalMouseTracking.None,
            TerminalMouseEncoding.Default);

        Assert.Null(result);
    }

    [Fact]
    public void X10PressUsesLegacyEncodingAndCoordinates()
    {
        var result = TerminalMouseEncoder.Encode(
            TerminalMouseEventType.Press,
            TerminalMouseButton.Left,
            0,
            0,
            false,
            false,
            false,
            TerminalMouseTracking.X10,
            TerminalMouseEncoding.Default);

        Assert.Equal(new byte[] { 0x1b, (byte)'[', (byte)'M', 32, 33, 33 }, result);
    }

    [Fact]
    public void X10DoesNotReportReleaseMoveOrWheel()
    {
        Assert.Null(Encode(TerminalMouseEventType.Release, TerminalMouseButton.Left,
            TerminalMouseTracking.X10, TerminalMouseEncoding.Default));
        Assert.Null(Encode(TerminalMouseEventType.Move, TerminalMouseButton.None,
            TerminalMouseTracking.X10, TerminalMouseEncoding.Default));
        Assert.Null(Encode(TerminalMouseEventType.Press, TerminalMouseButton.WheelUp,
            TerminalMouseTracking.X10, TerminalMouseEncoding.Default));
    }

    [Fact]
    public void SgrUsesButtonAndReleaseTerminators()
    {
        var press = Encode(TerminalMouseEventType.Press, TerminalMouseButton.Left,
            TerminalMouseTracking.Normal, TerminalMouseEncoding.Sgr, column: 4, row: 2);
        var release = Encode(TerminalMouseEventType.Release, TerminalMouseButton.Left,
            TerminalMouseTracking.Normal, TerminalMouseEncoding.Sgr, column: 4, row: 2);

        Assert.Equal("\x1b[<0;5;3M", Ascii(press));
        Assert.Equal("\x1b[<0;5;3m", Ascii(release));
    }

    [Fact]
    public void ButtonEventMoveSetsMotionBit()
    {
        var result = Encode(TerminalMouseEventType.Move, TerminalMouseButton.Left,
            TerminalMouseTracking.ButtonEvent, TerminalMouseEncoding.Sgr, column: 1, row: 1);

        Assert.Equal("\x1b[<32;2;2M", Ascii(result));
    }

    [Fact]
    public void AnyEventWithoutButtonUsesButtonThree()
    {
        var result = Encode(TerminalMouseEventType.Move, TerminalMouseButton.None,
            TerminalMouseTracking.AnyEvent, TerminalMouseEncoding.Sgr);

        Assert.Equal("\x1b[<35;1;1M", Ascii(result));
    }

    [Fact]
    public void ModifiersAreEncodedForNonX10Modes()
    {
        Assert.Equal("\x1b[<4;1;1M", Ascii(Encode(TerminalMouseEventType.Press,
            TerminalMouseButton.Left, TerminalMouseTracking.Normal, TerminalMouseEncoding.Sgr, shift: true)));
        Assert.Equal("\x1b[<8;1;1M", Ascii(Encode(TerminalMouseEventType.Press,
            TerminalMouseButton.Left, TerminalMouseTracking.Normal, TerminalMouseEncoding.Sgr, alt: true)));
        Assert.Equal("\x1b[<16;1;1M", Ascii(Encode(TerminalMouseEventType.Press,
            TerminalMouseButton.Left, TerminalMouseTracking.Normal, TerminalMouseEncoding.Sgr, control: true)));
    }

    [Fact]
    public void SgrAndUrxvtEncodeWheelButtons()
    {
        Assert.Equal("\x1b[<64;1;1M", Ascii(Encode(TerminalMouseEventType.Press,
            TerminalMouseButton.WheelUp, TerminalMouseTracking.Normal, TerminalMouseEncoding.Sgr)));
        Assert.Equal("\x1b[97;1;1M", Ascii(Encode(TerminalMouseEventType.Press,
            TerminalMouseButton.WheelDown, TerminalMouseTracking.Normal, TerminalMouseEncoding.Urxvt)));
    }

    private static byte[]? Encode(
        TerminalMouseEventType type,
        TerminalMouseButton button,
        TerminalMouseTracking tracking,
        TerminalMouseEncoding encoding,
        int column = 0,
        int row = 0,
        bool shift = false,
        bool alt = false,
        bool control = false)
    {
        return TerminalMouseEncoder.Encode(type, button, column, row, shift, alt, control, tracking, encoding);
    }

    private static string Ascii(byte[]? bytes) => Encoding.ASCII.GetString(bytes!);
}
