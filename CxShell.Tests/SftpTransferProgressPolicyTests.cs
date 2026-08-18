using CxShell.Services;

namespace CxShell.Tests;

public sealed class SftpTransferProgressPolicyTests
{
    [Fact]
    public void BeforeFinalizeNeverReportsCompleteForNonEmptyTransfer()
    {
        Assert.Equal(0UL, SftpTransferProgressPolicy.BeforeFinalize(0, 1));
        Assert.Equal(99UL, SftpTransferProgressPolicy.BeforeFinalize(100, 100));
        Assert.Equal(99UL, SftpTransferProgressPolicy.BeforeFinalize(99, 100));
    }

    [Fact]
    public void BeforeFinalizeKeepsZeroLengthTransferAtZero()
    {
        Assert.Equal(0UL, SftpTransferProgressPolicy.BeforeFinalize(0, 0));
    }
}
