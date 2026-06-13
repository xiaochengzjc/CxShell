using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using ChiXueSsh.Models;
using FluentFTP;

namespace ChiXueSsh.Services;

public sealed class FtpService : IFileTransferService, IDisposable
{
    private AsyncFtpClient? _client;

    public bool IsConnected => _client?.IsConnected ?? false;

    public event Action<string>? ErrorOccurred;

    public async Task ConnectAsync(SessionInfo session, string? password)
    {
        Disconnect();

        if (session.AuthMethod == AuthMethod.PrivateKey)
            throw new NotSupportedException("FTP does not support private key authentication.");

        var credentials = new NetworkCredential(session.Username, password ?? string.Empty);
        _client = new AsyncFtpClient(session.Host, credentials, session.Port);

        try
        {
            await _client.Connect(CancellationToken.None);
        }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke($"FTP connection failed: {ex.Message}");
            _client?.Dispose();
            _client = null;
            throw;
        }
    }

    public void Disconnect()
    {
        try
        {
            if (_client != null)
                _client.Disconnect(CancellationToken.None).GetAwaiter().GetResult();
        }
        catch
        {
        }
        finally
        {
            _client?.Dispose();
            _client = null;
        }
    }

    public async Task<string> GetHomeDirectoryAsync()
    {
        EnsureConnected();
        return await _client!.GetWorkingDirectory(CancellationToken.None);
    }

    public async Task<List<SftpFileItem>> ListDirectoryAsync(string path)
    {
        EnsureConnected();
        var entries = await _client!.GetListing(path, FtpListOption.Modify | FtpListOption.Size, CancellationToken.None);

        return entries
            .Where(entry => entry.Name is not "." and not "..")
            .Select(entry => new SftpFileItem
            {
                Name = entry.Name,
                FullPath = string.IsNullOrWhiteSpace(entry.FullName) ? CombineRemotePath(path, entry.Name) : entry.FullName,
                IsDirectory = entry.Type == FtpObjectType.Directory,
                Size = entry.Type == FtpObjectType.Directory ? 0 : entry.Size,
                LastModified = entry.Modified == DateTime.MinValue ? entry.Created : entry.Modified,
                Permissions = entry.RawPermissions ?? entry.Chmod.ToString(),
                IsSymLink = entry.Type == FtpObjectType.Link,
                SymLinkTarget = entry.LinkTarget
            })
            .OrderByDescending(item => item.IsDirectory)
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task UploadFileAsync(string localPath, string remotePath, Action<ulong>? progress = null)
    {
        EnsureConnected();
        IProgress<FtpProgress>? ftpProgress = progress == null
            ? null
            : new Progress<FtpProgress>(p => progress((ulong)Math.Max(0L, p.TransferredBytes)));

        await _client!.UploadFile(
            localPath,
            remotePath,
            FtpRemoteExists.Overwrite,
            true,
            FtpVerify.None,
            ftpProgress,
            CancellationToken.None);
    }

    public async Task DownloadFileAsync(string remotePath, string localPath, Action<ulong>? progress = null)
    {
        EnsureConnected();
        IProgress<FtpProgress>? ftpProgress = progress == null
            ? null
            : new Progress<FtpProgress>(p => progress((ulong)Math.Max(0L, p.TransferredBytes)));

        await _client!.DownloadFile(
            localPath,
            remotePath,
            FtpLocalExists.Overwrite,
            FtpVerify.None,
            ftpProgress,
            CancellationToken.None);
    }

    public async Task DeleteAsync(string remotePath, bool isDirectory)
    {
        EnsureConnected();
        if (isDirectory)
            await _client!.DeleteDirectory(remotePath, CancellationToken.None);
        else
            await _client!.DeleteFile(remotePath, CancellationToken.None);
    }

    public async Task RenameAsync(string oldPath, string newPath)
    {
        EnsureConnected();
        var item = await FindItemAsync(oldPath);
        if (item?.Type == FtpObjectType.Directory)
            await _client!.MoveDirectory(oldPath, newPath, FtpRemoteExists.Overwrite, CancellationToken.None);
        else
            await _client!.MoveFile(oldPath, newPath, FtpRemoteExists.Overwrite, CancellationToken.None);
    }

    public async Task CreateDirectoryAsync(string path)
    {
        EnsureConnected();
        await _client!.CreateDirectory(path, CancellationToken.None);
    }

    private async Task<FtpListItem?> FindItemAsync(string fullPath)
    {
        var parent = GetParentPath(fullPath);
        var name = GetName(fullPath);
        var entries = await _client!.GetListing(parent, CancellationToken.None);
        return entries.FirstOrDefault(entry => string.Equals(entry.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    private void EnsureConnected()
    {
        if (_client == null || !_client.IsConnected)
            throw new InvalidOperationException("FTP is not connected.");
    }

    private static string CombineRemotePath(string parent, string name)
    {
        if (string.IsNullOrWhiteSpace(parent) || parent == "/")
            return "/" + name;

        return parent.TrimEnd('/') + "/" + name;
    }

    private static string GetParentPath(string path)
    {
        var trimmed = path.TrimEnd('/');
        var lastSlash = trimmed.LastIndexOf('/');
        if (lastSlash <= 0)
            return "/";

        return trimmed[..lastSlash];
    }

    private static string GetName(string path)
    {
        var trimmed = path.TrimEnd('/');
        var lastSlash = trimmed.LastIndexOf('/');
        return lastSlash < 0 ? trimmed : trimmed[(lastSlash + 1)..];
    }

    public void Dispose() => Disconnect();
}
