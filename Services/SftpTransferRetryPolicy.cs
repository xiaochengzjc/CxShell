using System;
using System.IO;
using System.Net.Sockets;
using CxShell.Models;
using Renci.SshNet.Common;

namespace CxShell.Services;

public static class SftpTransferRetryPolicy
{
    public const int MaxAutomaticRetries = 3;

    private static readonly TimeSpan[] RetryDelays =
    [
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(3),
        TimeSpan.FromSeconds(8)
    ];

    public static TimeSpan GetDelay(int retryNumber)
    {
        if (retryNumber <= 0)
            return TimeSpan.Zero;

        var index = Math.Min(retryNumber - 1, RetryDelays.Length - 1);
        return RetryDelays[index];
    }

    public static bool ShouldRetry(
        SessionProtocol protocol,
        bool supportsRetry,
        Exception exception)
    {
        if (protocol != SessionProtocol.SFTP || !supportsRetry || exception is OperationCanceledException)
            return false;

        if (exception is FileNotFoundException or
            DirectoryNotFoundException or
            UnauthorizedAccessException or
            ArgumentException or
            NotSupportedException)
        {
            return false;
        }

        if (exception is SshConnectionException)
            return SshServerInfo.IsLikelyTransientOpenFailure(exception) || HasTransientMessage(exception);

        if (exception is SocketException or TimeoutException or ObjectDisposedException)
            return true;

        if (exception is IOException)
            return HasTransientMessage(exception);

        if (exception is InvalidOperationException)
            return HasTransientMessage(exception);

        return SshServerInfo.IsLikelyTransientOpenFailure(exception) || HasTransientMessage(exception);
    }

    private static bool HasTransientMessage(Exception exception)
    {
        for (var current = exception; current != null; current = current.InnerException)
        {
            var message = current.Message;
            if (message.Contains("connection", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("socket", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("network", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("timed out", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("timeout", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("reset", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("closed", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("not connected", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("end of stream", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("broken pipe", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
