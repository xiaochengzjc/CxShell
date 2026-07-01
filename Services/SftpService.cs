using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CxShell.Models;
using Renci.SshNet;

namespace CxShell.Services;

public class SftpService : IFileTransferService, IDisposable
{
    private SftpClient? _client;
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

        if (session.SftpUseCustomServer)
        {
            throw new NotSupportedException(
                "自定义 SFTP 服务器命令需要 SSH exec 通道承载 SFTP 协议；当前版本使用 SSH.NET 标准 sftp subsystem，暂不支持该模式。请取消“使用自定义SFTP服务器”后连接。");
        }

        var authMethods = SshAgentAuthService.CreateAuthenticationMethods(session, password);
        var connectionInfo = ProxyConnectionFactory.CreateSshConnectionInfo(session, authMethods);
        SshAlgorithmPreferenceService.Apply(connectionInfo, session);
        _client = new SftpClient(connectionInfo)
        {
            KeepAliveInterval = TimeSpan.FromSeconds(30)
        };

        try
        {
            await ConnectWithRetryAsync(session, password);
        }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke($"SFTP 连接失败: {ex.Message}");
            _client?.Dispose();
            _client = null;
            throw;
        }
    }

    public void Disconnect()
    {
        try
        {
            _client?.Disconnect();
            _client?.Dispose();
        }
        catch { }
        _client = null;
    }

    private async Task ConnectWithRetryAsync(SessionInfo session, string? password)
    {
        Exception? lastError = null;
        foreach (var delay in ConnectRetryDelays)
        {
            if (delay > TimeSpan.Zero)
                await Task.Delay(delay);

            _client?.Dispose();
            _client = CreateClient(session, password);

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

    private static SftpClient CreateClient(SessionInfo session, string? password)
    {
        var authMethods = SshAgentAuthService.CreateAuthenticationMethods(session, password);
        var connectionInfo = ProxyConnectionFactory.CreateSshConnectionInfo(session, authMethods);
        SshAlgorithmPreferenceService.Apply(connectionInfo, session);
        return new SftpClient(connectionInfo)
        {
            KeepAliveInterval = TimeSpan.FromSeconds(30)
        };
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

    public async Task UploadFileAsync(string localPath, string remotePath, Action<ulong>? progress = null)
    {
        if (_client == null || !_client.IsConnected)
            throw new InvalidOperationException("SFTP 未连接");

        await Task.Run(() =>
        {
            using var stream = File.OpenRead(localPath);
            _client.UploadFile(stream, remotePath, true, progress);
        });
    }

    public async Task DownloadFileAsync(string remotePath, string localPath, Action<ulong>? progress = null)
    {
        if (_client == null || !_client.IsConnected)
            throw new InvalidOperationException("SFTP 未连接");

        await Task.Run(() =>
        {
            using var stream = File.Create(localPath);
            _client.DownloadFile(remotePath, stream, progress);
        });
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
