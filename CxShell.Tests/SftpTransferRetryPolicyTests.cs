using System;
using System.IO;
using CxShell.Models;
using CxShell.Services;
using Renci.SshNet.Common;

namespace CxShell.Tests;

public sealed class SftpTransferRetryPolicyTests
{
    [Fact]
    public void RetriesTransientSftpConnectionFailures()
    {
        Assert.True(SftpTransferRetryPolicy.ShouldRetry(
            SessionProtocol.SFTP,
            supportsRetry: true,
            new SshConnectionException("The socket was closed.")));
        Assert.True(SftpTransferRetryPolicy.ShouldRetry(
            SessionProtocol.SFTP,
            supportsRetry: true,
            new IOException("The connection was reset by peer.")));
    }

    [Fact]
    public void DoesNotRetryDeterministicFailuresOrOtherProtocols()
    {
        Assert.False(SftpTransferRetryPolicy.ShouldRetry(
            SessionProtocol.SFTP,
            supportsRetry: true,
            new FileNotFoundException("The remote file does not exist.")));
        Assert.False(SftpTransferRetryPolicy.ShouldRetry(
            SessionProtocol.SFTP,
            supportsRetry: true,
            new UnauthorizedAccessException("Permission denied.")));
        Assert.False(SftpTransferRetryPolicy.ShouldRetry(
            SessionProtocol.FTP,
            supportsRetry: true,
            new IOException("The connection was reset by peer.")));
        Assert.False(SftpTransferRetryPolicy.ShouldRetry(
            SessionProtocol.SFTP,
            supportsRetry: false,
            new SshConnectionException("The socket was closed.")));
        Assert.False(SftpTransferRetryPolicy.ShouldRetry(
            SessionProtocol.SFTP,
            supportsRetry: true,
            new SshConnectionException("No key exchange algorithm is supported by both client and server.")));
    }

    [Fact]
    public void RetryDelaysUseBoundedBackoff()
    {
        Assert.Equal(TimeSpan.Zero, SftpTransferRetryPolicy.GetDelay(0));
        Assert.Equal(TimeSpan.FromSeconds(1), SftpTransferRetryPolicy.GetDelay(1));
        Assert.Equal(TimeSpan.FromSeconds(3), SftpTransferRetryPolicy.GetDelay(2));
        Assert.Equal(TimeSpan.FromSeconds(8), SftpTransferRetryPolicy.GetDelay(3));
        Assert.Equal(TimeSpan.FromSeconds(8), SftpTransferRetryPolicy.GetDelay(99));
    }
}
