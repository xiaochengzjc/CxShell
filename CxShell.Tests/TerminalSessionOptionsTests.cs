using System.Text;
using CxShell.Models;
using CxShell.Services;

namespace CxShell.Tests;

public sealed class TerminalSessionOptionsTests
{
    [Theory]
    [InlineData("utf-8", "utf-8")]
    [InlineData("GBK", "gb2312")]
    [InlineData("gb18030", "gb18030")]
    public void GetEncoding_ResolvesConfiguredEncoding(string configured, string expectedWebName)
    {
        var encoding = TerminalSessionOptions.GetEncoding(new SessionInfo { TerminalEncoding = configured });

        Assert.Equal(expectedWebName, encoding.WebName);
    }

    [Fact]
    public void GetEncoding_FallsBackToUtf8ForUnknownEncoding()
    {
        var encoding = TerminalSessionOptions.GetEncoding(new SessionInfo { TerminalEncoding = "not-an-encoding" });

        Assert.Equal(Encoding.UTF8.WebName, encoding.WebName);
    }

    [Theory]
    [InlineData("CR", "first\rsecond\rthird\rfourth")]
    [InlineData("LF", "first\nsecond\nthird\nfourth")]
    [InlineData("CRLF", "first\r\nsecond\r\nthird\r\nfourth")]
    public void NormalizeSendLineEndings_UsesConfiguredEnding(string mode, string expected)
    {
        var session = new SessionInfo { TerminalSendLineEnding = mode };

        Assert.Equal(expected, TerminalSessionOptions.NormalizeSendLineEndings("first\r\nsecond\nthird\rfourth", session));
    }

    [Fact]
    public void NormalizeReceiveLineEndings_AutoPreservesInput()
    {
        const string input = "first\r\nsecond\nthird\rfourth";
        var session = new SessionInfo { TerminalReceiveLineEnding = "AUTO" };

        Assert.Equal(input, TerminalSessionOptions.NormalizeReceiveLineEndings(input, session));
    }

    [Fact]
    public void NormalizeReceiveLineEndings_CrLfDoesNotDuplicateExistingPairs()
    {
        var session = new SessionInfo { TerminalReceiveLineEnding = "CRLF" };

        Assert.Equal(
            "first\r\nsecond\r\nthird\r",
            TerminalSessionOptions.NormalizeReceiveLineEndings("first\r\nsecond\nthird\r", session));
    }
}
