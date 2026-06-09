using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ChiXueSsh.Models;
using Renci.SshNet;

namespace ChiXueSsh.Services;

public class SftpService : IDisposable
{
    private SftpClient? _client;

    public bool IsConnected => _client?.IsConnected ?? false;

    public event Action<string>? ErrorOccurred;

    public async Task ConnectAsync(SessionInfo session, string? password)
    {
        Disconnect();

        AuthenticationMethod auth;
        if (session.AuthMethod == AuthMethod.PrivateKey && !string.IsNullOrEmpty(session.PrivateKeyPath))
        {
            var expandedPath = ExpandPath(session.PrivateKeyPath);
            var keyFile = new PrivateKeyFile(expandedPath);
            auth = new PrivateKeyAuthenticationMethod(session.Username, keyFile);
        }
        else
        {
            auth = new PasswordAuthenticationMethod(session.Username, password ?? string.Empty);
        }

        var connectionInfo = new ConnectionInfo(session.Host, session.Port, session.Username, auth);
        _client = new SftpClient(connectionInfo)
        {
            KeepAliveInterval = TimeSpan.FromSeconds(30)
        };

        try
        {
            await Task.Run(() => _client.Connect());
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

    private static string ExpandPath(string path)
    {
        if (path.StartsWith("~"))
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                path[2..]);
        return Path.GetFullPath(path);
    }

    public void Dispose() => Disconnect();
}
