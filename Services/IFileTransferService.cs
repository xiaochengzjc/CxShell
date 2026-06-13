using ChiXueSsh.Models;

namespace ChiXueSsh.Services;

public interface IFileTransferService
{
    bool IsConnected { get; }

    event Action<string>? ErrorOccurred;

    Task ConnectAsync(SessionInfo session, string? password);

    void Disconnect();

    Task<string> GetHomeDirectoryAsync();

    Task<List<SftpFileItem>> ListDirectoryAsync(string path);

    Task UploadFileAsync(string localPath, string remotePath, Action<ulong>? progress = null);

    Task DownloadFileAsync(string remotePath, string localPath, Action<ulong>? progress = null);

    Task DeleteAsync(string remotePath, bool isDirectory);

    Task RenameAsync(string oldPath, string newPath);

    Task CreateDirectoryAsync(string path);
}
