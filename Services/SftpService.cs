using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CxShell.Models;
using Renci.SshNet;

namespace CxShell.Services;

public class SftpService : IFileTransferService, IDisposable
{
    private SftpClient? _client;
    private SshConnectionContext? _connectionContext;
    private static readonly TimeSpan[] ConnectRetryDelays =
    [
        TimeSpan.Zero,
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(5)
    ];

    public bool IsConnected => _client?.IsConnected ?? false;

    public event Action<string>? ErrorOccurred;

    public async Task ConnectAsync(SessionInfo session, string? password)
    {
        Disconnect();

        try
        {
            await ConnectWithRetryAsync(session, password);
        }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke($"SFTP 连接失败: {ex.Message}");
            _client?.Dispose();
            _client = null;
            _connectionContext?.Dispose();
            _connectionContext = null;
            throw;
        }
    }

    public void Disconnect()
    {
        try
        {
            _client?.Disconnect();
            _client?.Dispose();
            _connectionContext?.Dispose();
        }
        catch { }
        _client = null;
        _connectionContext = null;
    }

    private async Task ConnectWithRetryAsync(SessionInfo session, string? password)
    {
        Exception? lastError = null;
        foreach (var delay in ConnectRetryDelays)
        {
            if (delay > TimeSpan.Zero)
                await Task.Delay(delay);

            _client?.Dispose();
            _connectionContext?.Dispose();
            (_client, _connectionContext) = await Task.Run(() => CreateClient(session, password));

            try
            {
                await Task.Run(() => _client.Connect());
                return;
            }
            catch (Exception ex) when (delay != ConnectRetryDelays[^1] && SshServerInfo.IsLikelyTransientOpenFailure(ex))
            {
                lastError = ex;
            }
        }

        if (lastError != null)
            throw lastError;
    }

    private static (SftpClient Client, SshConnectionContext Context) CreateClient(SessionInfo session, string? password)
    {
        var authMethods = SshAgentAuthService.CreateAuthenticationMethods(session, password);
        var context = ProxyConnectionFactory.CreateSshConnectionContext(session, authMethods);
        try
        {
            var connectionInfo = context.ConnectionInfo;
            SshAlgorithmPreferenceService.Apply(connectionInfo, session);
            return (new SftpClient(connectionInfo)
            {
                KeepAliveInterval = TimeSpan.FromSeconds(30),
                OperationTimeout = TimeSpan.FromSeconds(15)
            }, context);
        }
        catch
        {
            context.Dispose();
            throw;
        }
    }

    public async Task<string> GetHomeDirectoryAsync()
    {
        if (_client == null || !_client.IsConnected)
            return "/";

        return await Task.Run(() => _client.WorkingDirectory);
    }

    public async Task<List<SftpFileItem>> ListDirectoryAsync(string path)
    {
        if (_client == null || !_client.IsConnected)
            return new List<SftpFileItem>();

        return await Task.Run(() =>
        {
            var entries = _client.ListDirectory(path);
            var items = new List<SftpFileItem>();

            foreach (var entry in entries)
            {
                if (entry.Name == "." || entry.Name == "..") continue;

                string? symTarget = null;
                if (entry.IsSymbolicLink)
                {
                    // SSH.NET ISftpFile 不暴露 SymbolicLinkTarget，留空即可
                    symTarget = null;
                }

                items.Add(new SftpFileItem
                {
                    Name = entry.Name,
                    FullPath = entry.FullName,
                    IsDirectory = entry.IsDirectory,
                    Size = entry.IsDirectory ? 0 : entry.Length,
                    LastModified = entry.LastWriteTime,
                    Permissions = entry.Attributes.GetBytes().Length > 0
                        ? FormatPermissions(entry.Attributes)
                        : "",
                    IsSymLink = entry.IsSymbolicLink,
                    SymLinkTarget = symTarget
                });
            }

            // 目录排前，相同类型按名称排序
            return items
                .OrderByDescending(i => i.IsDirectory)
                .ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        });
    }

    public async Task UploadFileAsync(
        string localPath,
        string remotePath,
        Action<ulong>? progress = null,
        CancellationToken cancellationToken = default,
        bool resume = false)
    {
        if (_client == null || !_client.IsConnected)
            throw new InvalidOperationException("SFTP is not connected.");

        cancellationToken.ThrowIfCancellationRequested();
        if (resume)
        {
            await UploadFileResumableAsync(localPath, remotePath, progress, cancellationToken).ConfigureAwait(false);
            return;
        }

        var totalBytes = new FileInfo(localPath).Length;
        using var localStream = File.OpenRead(localPath);
        using var remoteStream = _client.Open(remotePath, FileMode.Create, FileAccess.Write);
        await CopyStreamWithProgressAsync(
            localStream,
            remoteStream,
            0,
            totalBytes,
            progress,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task DownloadFileAsync(
        string remotePath,
        string localPath,
        Action<ulong>? progress = null,
        CancellationToken cancellationToken = default,
        bool resume = false)
    {
        if (_client == null || !_client.IsConnected)
            throw new InvalidOperationException("SFTP is not connected.");

        cancellationToken.ThrowIfCancellationRequested();
        if (resume)
        {
            await DownloadFileResumableAsync(remotePath, localPath, progress, cancellationToken).ConfigureAwait(false);
            return;
        }

        var totalBytes = _client.GetAttributes(remotePath).Size;
        EnsureLocalParentDirectory(localPath);
        using var remoteStream = _client.OpenRead(remotePath);
        using var localStream = File.Create(localPath);
        await CopyStreamWithProgressAsync(
            remoteStream,
            localStream,
            0,
            totalBytes,
            progress,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task UploadFileResumableAsync(
        string localPath,
        string remotePath,
        Action<ulong>? progress,
        CancellationToken cancellationToken)
    {
        if (_client == null)
            throw new InvalidOperationException("SFTP is not connected.");

        var fileInfo = new FileInfo(localPath);
        var totalBytes = fileInfo.Length;
        var partPath = GetRemotePartPath(remotePath);
        var offset = GetRemoteFileSize(partPath);
        if (offset > totalBytes)
        {
            TryDeleteRemoteFile(partPath);
            offset = 0;
        }

        progress?.Invoke((ulong)offset);

        using (var localStream = File.OpenRead(localPath))
        using (var remoteStream = _client.Open(partPath, FileMode.OpenOrCreate, FileAccess.Write))
        {
            localStream.Seek(offset, SeekOrigin.Begin);
            remoteStream.Seek(offset, SeekOrigin.Begin);

            await CopyStreamWithProgressAsync(
                localStream,
                remoteStream,
                offset,
                totalBytes,
                progress,
                cancellationToken).ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();
        TryDeleteRemoteFile(remotePath);
        _client.RenameFile(partPath, remotePath);
        progress?.Invoke((ulong)totalBytes);
    }

    private async Task DownloadFileResumableAsync(
        string remotePath,
        string localPath,
        Action<ulong>? progress,
        CancellationToken cancellationToken)
    {
        if (_client == null)
            throw new InvalidOperationException("SFTP is not connected.");

        var totalBytes = _client.GetAttributes(remotePath).Size;
        var partPath = GetLocalPartPath(localPath);
        EnsureLocalParentDirectory(partPath);

        var offset = File.Exists(partPath) ? new FileInfo(partPath).Length : 0;
        if (offset > totalBytes)
        {
            File.Delete(partPath);
            offset = 0;
        }

        progress?.Invoke((ulong)offset);

        using (var remoteStream = _client.OpenRead(remotePath))
        using (var localStream = File.Open(partPath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read))
        {
            remoteStream.Seek(offset, SeekOrigin.Begin);
            localStream.Seek(offset, SeekOrigin.Begin);

            await CopyStreamWithProgressAsync(
                remoteStream,
                localStream,
                offset,
                totalBytes,
                progress,
                cancellationToken).ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (File.Exists(localPath))
            File.Delete(localPath);
        File.Move(partPath, localPath);
        progress?.Invoke((ulong)totalBytes);
    }

    private static async Task CopyStreamWithProgressAsync(
        Stream source,
        Stream destination,
        long initialTransferred,
        long totalBytes,
        Action<ulong>? progress,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[128 * 1024];
        var transferred = initialTransferred;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = await source.ReadAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false);
            if (read <= 0)
                break;

            cancellationToken.ThrowIfCancellationRequested();
            await destination.WriteAsync(buffer, 0, read, cancellationToken).ConfigureAwait(false);
            transferred += read;
            progress?.Invoke((ulong)Math.Min(transferred, totalBytes));
        }

        await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private long GetRemoteFileSize(string path)
    {
        if (_client == null)
            return 0;

        try
        {
            return _client.Exists(path) ? _client.GetAttributes(path).Size : 0;
        }
        catch
        {
            return 0;
        }
    }

    private void TryDeleteRemoteFile(string path)
    {
        if (_client == null)
            return;

        try
        {
            if (_client.Exists(path))
                _client.DeleteFile(path);
        }
        catch
        {
        }
    }

    private static string GetRemotePartPath(string remotePath)
    {
        var index = remotePath.LastIndexOf('/');
        if (index < 0)
            return remotePath + ".cxshell.part";

        return remotePath[..(index + 1)] + "." + remotePath[(index + 1)..] + ".cxshell.part";
    }

    private static string GetLocalPartPath(string localPath)
        => localPath + ".cxshell.part";

    private static void EnsureLocalParentDirectory(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);
    }

    public Stream OpenReadStream(string remotePath)
    {
        if (_client == null || !_client.IsConnected)
            throw new InvalidOperationException("SFTP 未连接");

        return _client.OpenRead(remotePath);
    }

    public async Task DeleteAsync(string remotePath, bool isDirectory)
    {
        if (_client == null || !_client.IsConnected)
            throw new InvalidOperationException("SFTP 未连接");

        await Task.Run(() =>
        {
            if (isDirectory)
                DeleteDirectoryRecursive(remotePath);
            else
                _client.DeleteFile(remotePath);
        });
    }

    private void DeleteDirectoryRecursive(string path)
    {
        if (_client == null) return;
        foreach (var entry in _client.ListDirectory(path))
        {
            if (entry.Name == "." || entry.Name == "..") continue;
            if (entry.IsDirectory)
                DeleteDirectoryRecursive(entry.FullName);
            else
                _client.DeleteFile(entry.FullName);
        }
        _client.DeleteDirectory(path);
    }

    public async Task RenameAsync(string oldPath, string newPath)
    {
        if (_client == null || !_client.IsConnected)
            throw new InvalidOperationException("SFTP 未连接");

        await Task.Run(() => _client.RenameFile(oldPath, newPath));
    }

    public async Task CreateDirectoryAsync(string path)
    {
        if (_client == null || !_client.IsConnected)
            throw new InvalidOperationException("SFTP 未连接");

        await Task.Run(() => _client.CreateDirectory(path));
    }

    private static string FormatPermissions(Renci.SshNet.Sftp.SftpFileAttributes attrs)
    {
        try
        {
            return $"{(attrs.IsDirectory ? 'd' : '-')}" +
                   $"{(attrs.OwnerCanRead ? 'r' : '-')}{(attrs.OwnerCanWrite ? 'w' : '-')}{(attrs.OwnerCanExecute ? 'x' : '-')}" +
                   $"{(attrs.GroupCanRead ? 'r' : '-')}{(attrs.GroupCanWrite ? 'w' : '-')}{(attrs.GroupCanExecute ? 'x' : '-')}" +
                   $"{(attrs.OthersCanRead ? 'r' : '-')}{(attrs.OthersCanWrite ? 'w' : '-')}{(attrs.OthersCanExecute ? 'x' : '-')}";
        }
        catch { return ""; }
    }

    public void Dispose() => Disconnect();
}
