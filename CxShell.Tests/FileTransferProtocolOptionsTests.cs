using CxShell.Services;

namespace CxShell.Tests;

public sealed class FileTransferProtocolOptionsTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Auto")]
    [InlineData("Zmodem")]
    [InlineData("ftp")]
    [InlineData("unsupported")]
    public void NormalizeUploadProtocol_FallsBackToAuto(string? value)
    {
        Assert.Equal("Auto", FileTransferProtocolOptions.NormalizeUploadProtocol(value));
    }

    [Theory]
    [InlineData("xmodem", "Xmodem")]
    [InlineData(" XMODEM ", "Xmodem")]
    [InlineData("ymodem", "Ymodem")]
    [InlineData(" YMODEM ", "Ymodem")]
    public void NormalizeUploadProtocol_PreservesSupportedExplicitProtocols(string value, string expected)
    {
        Assert.Equal(expected, FileTransferProtocolOptions.NormalizeUploadProtocol(value));
    }
}
