using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using Avalonia.Threading;
using ChiXueSsh.Models;
using ChiXueSsh.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ChiXueSsh.ViewModels;

public partial class SftpViewModel : ObservableObject
{
    private IFileTransferService _service;
    private SessionInfo? _currentSession;
    private string? _currentPassword;
    private string _homeDirectory = "/";

    [ObservableProperty] private bool _isConnected;
    [ObservableProperty] private string _currentPath = "/";
    [ObservableProperty] private string _pathInput = "/";
    [ObservableProperty] private string _hostLabel = "Not connected";
    [ObservableProperty] private string _protocolLabel = "SFTP";
    [ObservableProperty] private string _localStartDirectory = string.Empty;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string? _errorMessage;
    [ObservableProperty] private SftpFileItem? _selectedFile;
    [ObservableProperty] private bool _isCreatingDirectory;
    [ObservableProperty] private string _newDirectoryName = "NewFolder";
    [ObservableProperty] private SftpFileItem? _renamingItem;

    public ObservableCollection<SftpFileItem> Files { get; } = new();
    public ObservableCollection<PathSegment> PathSegments { get; } = new();

    public Func<Task<string?>>? PickUploadFileAsync { get; set; }
    public Func<string, Task<string?>>? PickDownloadPathAsync { get; set; }
    public Func<string, Task<bool>>? ShowConfirmDialogAsync { get; set; }
    public Func<string, string, Task<string?>>? ShowInputDialogAsync { get; set; }

    public SftpViewModel()
    {
        _service = CreateService(SessionProtocol.SFTP);
        _service.ErrorOccurred += OnServiceError;
    }

    partial void OnSelectedFileChanged(SftpFileItem? oldValue, SftpFileItem? newValue)
    {
        if (oldValue != null)
            oldValue.IsSelected = false;

        if (newValue != null)
            newValue.IsSelected = true;
    }

    public void SwitchConnection(SessionInfo session, string? password)
    {
        _ = SwitchConnectionAsync(session, password);
    }

    public async Task<bool> SwitchConnectionAsync(SessionInfo session, string? password)
    {
        SetService(CreateService(session.Protocol));

        _currentSession = session;
        _currentPassword = password;
        ProtocolLabel = session.Protocol.ToString();
        HostLabel = $"{session.Username}@{session.Host}";
        LocalStartDirectory = session.SftpLocalStartDirectory ?? string.Empty;
        ErrorMessage = null;

        return await ConnectAndBrowseAsync();
    }

    public void StopBrowsing()
    {
        _service.Disconnect();
        Dispatcher.UIThread.Post(() =>
        {
            IsConnected = false;
            HostLabel = "Not connected";
            Files.Clear();
            PathSegments.Clear();
            CurrentPath = "/";
            PathInput = "/";
            ErrorMessage = null;
        });
    }

    private async Task<bool> ConnectAndBrowseAsync()
    {
        if (_currentSession == null)
            return false;

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            IsConnected = false;
            IsLoading = true;
            ErrorMessage = null;
            Files.Clear();
            PathSegments.Clear();
        });

        try
        {
            await _service.ConnectAsync(_currentSession, _currentPassword);
            _homeDirectory = await _service.GetHomeDirectoryAsync();
            var startDirectory = string.IsNullOrWhiteSpace(_currentSession.SftpRemoteStartDirectory)
                ? _homeDirectory
                : _currentSession.SftpRemoteStartDirectory.Trim();
            await Dispatcher.UIThread.InvokeAsync(() => IsConnected = true);
            await LoadDirectoryAsync(startDirectory);
            return true;
        }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                ErrorMessage = $"Connection failed: {ex.Message}";
                IsConnected = false;
                IsLoading = false;
            });
            return false;
        }
    }

    private async Task LoadDirectoryAsync(string path)
    {
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            IsLoading = true;
            ErrorMessage = null;
        });

        try
        {
            var items = await _service.ListDirectoryAsync(path);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                CurrentPath = path;
                PathInput = path;
                UpdatePathSegments(path);
                Files.Clear();
                foreach (var item in items)
                    Files.Add(item);
                IsLoading = false;
            });
        }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                ErrorMessage = $"Load failed: {ex.Message}";
                PathInput = CurrentPath;
                IsLoading = false;
            });
        }
    }

    private void UpdatePathSegments(string path)
    {
        PathSegments.Clear();
        PathSegments.Add(new PathSegment { Label = "/", FullPath = "/" });

        if (path == "/")
            return;

        var parts = path.TrimStart('/').Split('/');
        var accumulated = "";
        foreach (var part in parts)
        {
            if (string.IsNullOrEmpty(part))
                continue;

            accumulated += "/" + part;
            PathSegments.Add(new PathSegment { Label = part, FullPath = accumulated });
        }
    }

    [RelayCommand]
    private void SelectFile(SftpFileItem item)
    {
        SelectedFile = item;
    }

    [RelayCommand]
    private async Task NavigateToPath(string path)
    {
        if (!_service.IsConnected)
            return;

        await LoadDirectoryAsync(path);
    }

    [RelayCommand]
    private async Task NavigateToTypedPath()
    {
        if (!_service.IsConnected)
            return;

        var targetPath = NormalizeRemotePath(PathInput);
        if (string.IsNullOrWhiteSpace(targetPath))
        {
            PathInput = CurrentPath;
            return;
        }

        await LoadDirectoryAsync(targetPath);
    }

    [RelayCommand]
    private async Task NavigateUp()
    {
        if (!_service.IsConnected || CurrentPath == "/")
            return;

        var parent = System.IO.Path.GetDirectoryName(CurrentPath.TrimEnd('/')) ?? "/";
        parent = parent.Replace('\\', '/');
        if (string.IsNullOrEmpty(parent))
            parent = "/";

        await LoadDirectoryAsync(parent);
    }

    [RelayCommand]
    private async Task NavigateHome()
    {
        if (!_service.IsConnected)
            return;

        await LoadDirectoryAsync(_homeDirectory);
    }

    [RelayCommand]
    private async Task Refresh()
    {
        if (!_service.IsConnected)
            return;

        await LoadDirectoryAsync(CurrentPath);
    }

    private string NormalizeRemotePath(string? path)
    {
        var value = path?.Trim();
        if (string.IsNullOrWhiteSpace(value))
            return CurrentPath;

        value = value.Replace('\\', '/');

        if (value == "~")
            return _homeDirectory;

        if (value.StartsWith("~/", StringComparison.Ordinal))
            return CombineRemotePath(_homeDirectory, value[2..]);

        if (value.StartsWith("/", StringComparison.Ordinal))
            return CollapseRemotePath(value);

        return CollapseRemotePath(CombineRemotePath(CurrentPath, value));
    }

    private static string CombineRemotePath(string parent, string child)
    {
        if (string.IsNullOrWhiteSpace(parent) || parent == "/")
            return "/" + child.TrimStart('/');

        return parent.TrimEnd('/') + "/" + child.TrimStart('/');
    }

    private static string CollapseRemotePath(string path)
    {
        var parts = new Stack<string>();
        foreach (var rawPart in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (rawPart == ".")
                continue;

            if (rawPart == "..")
            {
                if (parts.Count > 0)
                    parts.Pop();
                continue;
            }

            parts.Push(rawPart);
        }

        if (parts.Count == 0)
            return "/";

        return "/" + string.Join("/", parts.Reverse());
    }

    [RelayCommand]
    private async Task OpenItem(SftpFileItem item)
    {
        if (!_service.IsConnected)
            return;

        if (item.IsDirectory)
            await LoadDirectoryAsync(item.FullPath);
    }

    [RelayCommand]
    private async Task Upload()
    {
        if (!_service.IsConnected || PickUploadFileAsync == null)
            return;

        var localPath = await PickUploadFileAsync();
        if (string.IsNullOrEmpty(localPath))
            return;

        var fileName = System.IO.Path.GetFileName(localPath);
        var remotePath = CurrentPath.TrimEnd('/') + "/" + fileName;

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            IsLoading = true;
            ErrorMessage = null;
        });

        try
        {
            await _service.UploadFileAsync(localPath, remotePath);
            await LoadDirectoryAsync(CurrentPath);
        }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                ErrorMessage = $"Upload failed: {ex.Message}";
                IsLoading = false;
            });
        }
    }

    [RelayCommand]
    private async Task Download()
    {
        if (!_service.IsConnected || SelectedFile == null || SelectedFile.IsDirectory)
            return;

        if (PickDownloadPathAsync == null)
            return;

        var localPath = await PickDownloadPathAsync(SelectedFile.Name);
        if (string.IsNullOrEmpty(localPath))
            return;

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            IsLoading = true;
            ErrorMessage = null;
        });

        try
        {
            await _service.DownloadFileAsync(SelectedFile.FullPath, localPath);
            await Dispatcher.UIThread.InvokeAsync(() => IsLoading = false);
        }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                ErrorMessage = $"Download failed: {ex.Message}";
                IsLoading = false;
            });
        }
    }

    [RelayCommand]
    private async Task Delete()
    {
        if (!_service.IsConnected || SelectedFile == null)
            return;

        if (ShowConfirmDialogAsync != null)
        {
            var confirmed = await ShowConfirmDialogAsync($"Delete '{SelectedFile.Name}'?");
            if (!confirmed)
                return;
        }

        try
        {
            await _service.DeleteAsync(SelectedFile.FullPath, SelectedFile.IsDirectory);
            await LoadDirectoryAsync(CurrentPath);
        }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() => ErrorMessage = $"Delete failed: {ex.Message}");
        }
    }

    [RelayCommand]
    private void Rename()
    {
        if (!_service.IsConnected || SelectedFile == null)
            return;

        RenamingItem = SelectedFile;
        SelectedFile.IsRenaming = true;
    }

    [RelayCommand]
    private async Task ConfirmRename(SftpFileItem item)
    {
        if (!item.IsRenaming)
            return;

        item.IsRenaming = false;

        var newName = item.RenamingText?.Trim();
        if (string.IsNullOrEmpty(newName) || newName == item.Name)
            return;

        var newPath = CurrentPath.TrimEnd('/') + "/" + newName;
        try
        {
            await _service.RenameAsync(item.FullPath, newPath);
            await LoadDirectoryAsync(CurrentPath);
        }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() => ErrorMessage = $"Rename failed: {ex.Message}");
        }
    }

    [RelayCommand]
    private void CancelRename(SftpFileItem item)
    {
        item.IsRenaming = false;
        item.RenamingText = item.Name;
    }

    [RelayCommand]
    private void CreateDirectory()
    {
        if (!_service.IsConnected)
            return;

        NewDirectoryName = "NewFolder";
        IsCreatingDirectory = true;
    }

    [RelayCommand]
    private async Task ConfirmCreateDirectory()
    {
        if (!IsCreatingDirectory)
            return;

        IsCreatingDirectory = false;

        var name = NewDirectoryName?.Trim();
        if (string.IsNullOrEmpty(name))
            return;

        var newPath = CurrentPath.TrimEnd('/') + "/" + name;
        try
        {
            await _service.CreateDirectoryAsync(newPath);
            await LoadDirectoryAsync(CurrentPath);
        }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() => ErrorMessage = $"Create directory failed: {ex.Message}");
        }
    }

    [RelayCommand]
    private void CancelCreateDirectory()
    {
        IsCreatingDirectory = false;
        NewDirectoryName = "NewFolder";
    }

    private static IFileTransferService CreateService(SessionProtocol protocol)
    {
        return protocol switch
        {
            SessionProtocol.FTP => new FtpService(),
            _ => new SftpService()
        };
    }

    private void SetService(IFileTransferService service)
    {
        _service.ErrorOccurred -= OnServiceError;
        _service.Disconnect();
        if (_service is IDisposable disposable)
            disposable.Dispose();

        _service = service;
        _service.ErrorOccurred += OnServiceError;
    }

    private void OnServiceError(string message)
    {
        Dispatcher.UIThread.Post(() =>
        {
            ErrorMessage = message;
            IsConnected = false;
        });
    }
}

public class PathSegment
{
    public string Label { get; set; } = "";
    public string FullPath { get; set; } = "";
}
