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
    private readonly SftpService _service = new();
    private SessionInfo? _currentSession;
    private string? _currentPassword;
    private string _homeDirectory = "/";

    [ObservableProperty] private bool _isConnected;
    [ObservableProperty] private string _currentPath = "/";
    [ObservableProperty] private string _hostLabel = "未连接";
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string? _errorMessage;
    [ObservableProperty] private SftpFileItem? _selectedFile;
    [ObservableProperty] private bool _isCreatingDirectory;
    [ObservableProperty] private string _newDirectoryName = "新目录";
    [ObservableProperty] private SftpFileItem? _renamingItem;

    partial void OnSelectedFileChanged(SftpFileItem? oldValue, SftpFileItem? newValue)
    {
        if (oldValue != null) oldValue.IsSelected = false;
        if (newValue != null) newValue.IsSelected = true;
    }

    [RelayCommand]
    private void SelectFile(SftpFileItem item)
    {
        SelectedFile = item;
    }

    public ObservableCollection<SftpFileItem> Files { get; } = new();
    public ObservableCollection<PathSegment> PathSegments { get; } = new();

    // 由 View code-behind 注入的文件对话框委托
    public Func<Task<string?>>? PickUploadFileAsync { get; set; }
    public Func<string, Task<string?>>? PickDownloadPathAsync { get; set; }
    public Func<string, Task<bool>>? ShowConfirmDialogAsync { get; set; }
    public Func<string, string, Task<string?>>? ShowInputDialogAsync { get; set; }

    public SftpViewModel()
    {
        _service.ErrorOccurred += msg =>
            Dispatcher.UIThread.Post(() =>
            {
                ErrorMessage = msg;
                IsConnected = false;
            });
    }

    public void SwitchConnection(SessionInfo session, string? password)
    {
        // 先断开旧连接
        _service.Disconnect();

        _currentSession = session;
        _currentPassword = password;
        HostLabel = $"{session.Username}@{session.Host}";
        ErrorMessage = null;

        _ = ConnectAndBrowseAsync();
    }

    public void StopBrowsing()
    {
        _service.Disconnect();
        Dispatcher.UIThread.Post(() =>
        {
            IsConnected = false;
            HostLabel = "未连接";
            Files.Clear();
            PathSegments.Clear();
            CurrentPath = "/";
            ErrorMessage = null;
        });
    }

    private async Task ConnectAndBrowseAsync()
    {
        if (_currentSession == null) return;

        Dispatcher.UIThread.Post(() =>
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

            // 连接成功后立即标记，让 ListBox 显示出来
            Dispatcher.UIThread.Post(() => IsConnected = true);

            await LoadDirectoryAsync(_homeDirectory);
        }
        catch (Exception ex)
        {
            Dispatcher.UIThread.Post(() =>
            {
                ErrorMessage = $"连接失败: {ex.Message}";
                IsConnected = false;
                IsLoading = false;
            });
        }
    }

    private async Task LoadDirectoryAsync(string path)
    {
        Dispatcher.UIThread.Post(() =>
        {
            IsLoading = true;
            ErrorMessage = null;
        });

        try
        {
            var items = await _service.ListDirectoryAsync(path);
            System.Console.WriteLine($"[SFTP] ListDirectory '{path}' returned {items.Count} items");

            Dispatcher.UIThread.Post(() =>
            {
                CurrentPath = path;
                UpdatePathSegments(path);
                Files.Clear();
                foreach (var item in items)
                    Files.Add(item);
                IsLoading = false;
                System.Console.WriteLine($"[SFTP] Files.Count = {Files.Count}, IsConnected = {IsConnected}");
            });
        }
        catch (Exception ex)
        {
            Dispatcher.UIThread.Post(() =>
            {
                ErrorMessage = $"加载失败: {ex.Message}";
                IsLoading = false;
            });
        }
    }

    private void UpdatePathSegments(string path)
    {
        PathSegments.Clear();

        // 根目录
        PathSegments.Add(new PathSegment { Label = "/", FullPath = "/" });

        if (path == "/") return;

        var parts = path.TrimStart('/').Split('/');
        var accumulated = "";
        foreach (var part in parts)
        {
            if (string.IsNullOrEmpty(part)) continue;
            accumulated += "/" + part;
            PathSegments.Add(new PathSegment { Label = part, FullPath = accumulated });
        }
    }

    [RelayCommand]
    private async Task NavigateToPath(string path)
    {
        if (!_service.IsConnected) return;
        await LoadDirectoryAsync(path);
    }

    [RelayCommand]
    private async Task NavigateUp()
    {
        if (!_service.IsConnected) return;
        if (CurrentPath == "/") return;

        var parent = System.IO.Path.GetDirectoryName(CurrentPath.TrimEnd('/'))
                     ?? "/";
        // Linux 路径：确保使用正斜杠
        parent = parent.Replace('\\', '/');
        if (string.IsNullOrEmpty(parent)) parent = "/";

        await LoadDirectoryAsync(parent);
    }

    [RelayCommand]
    private async Task NavigateHome()
    {
        if (!_service.IsConnected) return;
        await LoadDirectoryAsync(_homeDirectory);
    }

    [RelayCommand]
    private async Task Refresh()
    {
        if (!_service.IsConnected) return;
        await LoadDirectoryAsync(CurrentPath);
    }

    [RelayCommand]
    private async Task OpenItem(SftpFileItem item)
    {
        if (!_service.IsConnected) return;
        if (item.IsDirectory)
            await LoadDirectoryAsync(item.FullPath);
    }

    [RelayCommand]
    private async Task Upload()
    {
        if (!_service.IsConnected || PickUploadFileAsync == null) return;

        var localPath = await PickUploadFileAsync();
        if (string.IsNullOrEmpty(localPath)) return;

        var fileName = System.IO.Path.GetFileName(localPath);
        var remotePath = CurrentPath.TrimEnd('/') + "/" + fileName;

        Dispatcher.UIThread.Post(() =>
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
            Dispatcher.UIThread.Post(() =>
            {
                ErrorMessage = $"上传失败: {ex.Message}";
                IsLoading = false;
            });
        }
    }

    [RelayCommand]
    private async Task Download()
    {
        if (!_service.IsConnected || SelectedFile == null || SelectedFile.IsDirectory) return;
        if (PickDownloadPathAsync == null) return;

        var localPath = await PickDownloadPathAsync(SelectedFile.Name);
        if (string.IsNullOrEmpty(localPath)) return;

        Dispatcher.UIThread.Post(() =>
        {
            IsLoading = true;
            ErrorMessage = null;
        });

        try
        {
            await _service.DownloadFileAsync(SelectedFile.FullPath, localPath);
            Dispatcher.UIThread.Post(() => IsLoading = false);
        }
        catch (Exception ex)
        {
            Dispatcher.UIThread.Post(() =>
            {
                ErrorMessage = $"下载失败: {ex.Message}";
                IsLoading = false;
            });
        }
    }

    [RelayCommand]
    private async Task Delete()
    {
        if (!_service.IsConnected || SelectedFile == null) return;

        if (ShowConfirmDialogAsync != null)
        {
            var confirmed = await ShowConfirmDialogAsync($"确认删除 '{SelectedFile.Name}'？");
            if (!confirmed) return;
        }

        try
        {
            await _service.DeleteAsync(SelectedFile.FullPath, SelectedFile.IsDirectory);
            await LoadDirectoryAsync(CurrentPath);
        }
        catch (Exception ex)
        {
            Dispatcher.UIThread.Post(() => ErrorMessage = $"删除失败: {ex.Message}");
        }
    }

    [RelayCommand]
    private void Rename()
    {
        if (!_service.IsConnected || SelectedFile == null) return;
        RenamingItem = SelectedFile;
        SelectedFile.IsRenaming = true;
    }

    [RelayCommand]
    private async Task ConfirmRename(SftpFileItem item)
    {
        if (!item.IsRenaming) return;
        item.IsRenaming = false;

        var newName = item.RenamingText?.Trim();
        if (string.IsNullOrEmpty(newName) || newName == item.Name) return;

        var newPath = CurrentPath.TrimEnd('/') + "/" + newName;
        try
        {
            await _service.RenameAsync(item.FullPath, newPath);
            await LoadDirectoryAsync(CurrentPath);
        }
        catch (Exception ex)
        {
            Dispatcher.UIThread.Post(() => ErrorMessage = $"重命名失败: {ex.Message}");
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
        if (!_service.IsConnected) return;
        NewDirectoryName = "新目录";
        IsCreatingDirectory = true;
    }

    [RelayCommand]
    private async Task ConfirmCreateDirectory()
    {
        if (!IsCreatingDirectory) return;
        IsCreatingDirectory = false;

        var name = NewDirectoryName?.Trim();
        if (string.IsNullOrEmpty(name)) return;

        var newPath = CurrentPath.TrimEnd('/') + "/" + name;
        try
        {
            await _service.CreateDirectoryAsync(newPath);
            await LoadDirectoryAsync(CurrentPath);
        }
        catch (Exception ex)
        {
            Dispatcher.UIThread.Post(() => ErrorMessage = $"创建目录失败: {ex.Message}");
        }
    }

    [RelayCommand]
    private void CancelCreateDirectory()
    {
        IsCreatingDirectory = false;
        NewDirectoryName = "新目录";
    }
}

public class PathSegment
{
    public string Label { get; set; } = "";
    public string FullPath { get; set; } = "";
}
