using CxShell.Services;

namespace CxShell.Tests;

public sealed class SftpTransferPartPathsTests
{
    [Fact]
    public void LocalPathAppendsPartialSuffix()
    {
        Assert.Equal(
            @"C:\Downloads\archive.zip.cxshell.part",
            SftpTransferPartPaths.GetLocalPath(@"C:\Downloads\archive.zip"));
    }

    [Fact]
    public void RemotePathHidesPartialFileAlongsideNamedFile()
    {
        Assert.Equal(
            "/var/log/.archive.log.cxshell.part",
            SftpTransferPartPaths.GetRemotePath("/var/log/archive.log"));
    }

    [Fact]
    public void RemotePathWithoutDirectoryUsesSuffixOnly()
    {
        Assert.Equal(
            "archive.log.cxshell.part",
            SftpTransferPartPaths.GetRemotePath("archive.log"));
    }
}
