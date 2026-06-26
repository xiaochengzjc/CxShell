using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using ChiXueSsh.Models;
using ChiXueSsh.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ChiXueSsh.ViewModels;

public partial class SftpViewModel : ObservableObject
{
    private LocalizationService L => LocalizationService.Shared;

    public string UploadText => L.Text("Sftp.Upload");
    public string DownloadText => L.Text("Sftp.Download");
    public string NewDirectoryText => L.Text("Sftp.NewDirectory");
    public string NameText => L.Text("Sftp.Name");
    public string SizeText => L.Text("Sftp.Size");
    public string ModifiedText => L.Text("Sftp.Modified");
    public string RenameText => L.Text("Common.Rename");
    public string DeleteText => L.Text("Common.Delete");
    public string ConnectHintText => L.Text("Sftp.ConnectHint");
    public string LoadingText => L.Text("Sftp.Loading");

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
    private readonly List<SftpFileItem> _selectedFiles = new();

    public IReadOnlyList<SftpFileItem> SelectedFiles => _selectedFiles;
    public bool HasSelectedFiles => _selectedFiles.Count > 0;

    public Func<Task<string?>>? PickUploadFileAsync { get; set; }
    public Func<string, Task<string?>>? PickDownloadPathAsync { get; set; }
    public Func<string, Task<bool>>? ShowConfirmDialogAsync { get; set; }
    public Func<string, string, Task<string?>>? ShowInputDialogAsync { get; set; }

    public SftpViewModel()
    {
        _service = CreateService(SessionProtocol.SFTP);
        _service.ErrorOccurred += OnServiceError;
        LocalizationService.Shared.LanguageChanged += (_, _) => RefreshLocalization();
    }

    private void RefreshLocalization()
    {
        OnPropertyChanged(nameof(UploadText));
        OnPropertyChanged(nameof(DownloadText));
        OnPropertyChanged(nameof(NewDirectoryText));
        OnPropertyChanged(nameof(NameText));
        OnPropertyChanged(nameof(SizeText));
        OnPropertyChanged(nameof(ModifiedText));
        OnPropertyChanged(nameof(RenameText));
        OnPropertyChanged(nameof(DeleteText));
        OnPropertyChanged(nameof(ConnectHintText));
        OnPropertyChanged(nameof(LoadingText));
    }

    partial void OnSelectedFileChanged(SftpFileItem? oldValue, SftpFileItem? newValue)
    {
        if (oldValue != null)
            oldValue.IsSelected = false;

        if (newValue != null)
            newValue.IsSelected = true;
    }

    public void SetSelectedFiles(IEnumerable<SftpFileItem> items)
    {
        var next = items.Where(item => item != null).Distinct().ToList();
        if (_selectedFiles.SequenceEqual(next))
            return;

        _selectedFiles.Clear();
        _selectedFiles.AddRange(next);
        OnPropertyChanged(nameof(SelectedFiles));
        OnPropertyChanged(nameof(HasSelectedFiles));

        var primary = next.FirstOrDefault();
        if (!ReferenceEquals(SelectedFile, primary))
            SelectedFile = primary;
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
            SetSelectedFiles(Array.Empty<SftpFileItem>());
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
            SetSelectedFiles(Array.Empty<SftpFileItem>());
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
                SetSelectedFiles(Array.Empty<SftpFileItem>());
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

    public async Task UploadDroppedPathsAsync(IEnumerable<string> localPaths)
    {
        if (!_service.IsConnected)
            return;

        var paths = localPaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (paths.Count == 0)
            return;

        UpdateLocalDirectoryFromDroppedPath(paths[0]);

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            IsLoading = true;
            ErrorMessage = null;
        });

        try
        {
            var targetDirectory = CurrentPath;
            foreach (var path in paths)
                await UploadLocalPathAsync(path, targetDirectory);

            await LoadDirectoryAsync(CurrentPath);
        }
        catch (Exception ex)
        {
            await ShowDropUploadErrorAsync(ex.Message);
        }
    }

    public async Task ShowDropUploadErrorAsync(string message)
    {
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            ErrorMessage = $"Upload failed: {message}";
            IsLoading = false;
        });
    }

    private async Task UploadLocalPathAsync(string localPath, string remoteDirectory)
    {
        if (File.Exists(localPath))
        {
            var fileName = Path.GetFileName(localPath);
            if (string.IsNullOrWhiteSpace(fileName))
                return;

            await _service.UploadFileAsync(localPath, CombineRemotePath(remoteDirectory, fileName));
            return;
        }

        if (!Directory.Exists(localPath))
            return;

        var directoryName = GetLocalDirectoryName(localPath);
        if (string.IsNullOrWhiteSpace(directoryName))
            return;

        var remotePath = CombineRemotePath(remoteDirectory, directoryName);
        await EnsureRemoteDirectoryAsync(remotePath);

        foreach (var file in Directory.EnumerateFiles(localPath))
        {
            var fileName = Path.GetFileName(file);
            if (!string.IsNullOrWhiteSpace(fileName))
                await _service.UploadFileAsync(file, CombineRemotePath(remotePath, fileName));
        }

        foreach (var directory in Directory.EnumerateDirectories(localPath))
            await UploadLocalPathAsync(directory, remotePath);
    }

    private async Task EnsureRemoteDirectoryAsync(string remotePath)
    {
        try
        {
            await _service.CreateDirectoryAsync(remotePath);
        }
        catch
        {
            // Continue so dropping an existing local folder can merge into an existing remote folder.
        }
    }

    private void UpdateLocalDirectoryFromDroppedPath(string localPath)
    {
        if (Directory.Exists(localPath))
        {
            LocalStartDirectory = localPath;
            return;
        }

        var directory = Path.GetDirectoryName(localPath);
        if (!string.IsNullOrWhiteSpace(directory))
            LocalStartDirectory = directory;
    }

    private static string GetLocalDirectoryName(string localPath)
    {
        return Path.GetFileName(localPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
    }

    public bool CanStreamDragOut(SftpFileItem item)
    {
        return _service is SftpService && _service.IsConnected;
    }

    public Stream OpenRemoteReadStream(SftpFileItem item)
    {
        if (_service is not SftpService sftpService)
            throw new NotSupportedException("Only SFTP supports streaming drag-out.");

        return sftpService.OpenReadStream(item.FullPath);
    }

    public async Task<List<VirtualDragFile>> CreateVirtualDragFilesAsync(SftpFileItem item)
    {
        if (_service is not SftpService sftpService)
            throw new NotSupportedException("Only SFTP supports streaming drag-out.");

        if (!item.IsDirectory)
        {
            return
            [
                new VirtualDragFile(
                    item.Name,
                    item.Size,
                    item.LastModified,
                    () => sftpService.OpenReadStream(item.FullPath))
            ];
        }

        var files = new List<VirtualDragFile>();
        var rootName = SanitizeLocalName(item.Name);
        await AddVirtualDragDirectoryFilesAsync(sftpService, item.FullPath, rootName, files);
        return files;
    }

    private async Task AddVirtualDragDirectoryFilesAsync(
        SftpService sftpService,
        string remoteDirectory,
        string relativeDirectory,
        List<VirtualDragFile> files)
    {
        var children = await _service.ListDirectoryAsync(remoteDirectory);
        foreach (var child in children)
        {
            var relativePath = relativeDirectory + "\\" + SanitizeLocalName(child.Name);
            if (child.IsDirectory)
            {
                await AddVirtualDragDirectoryFilesAsync(sftpService, child.FullPath, relativePath, files);
                continue;
            }

            var remotePath = child.FullPath;
            files.Add(new VirtualDragFile(
                relativePath,
                child.Size,
                child.LastModified,
                () => sftpService.OpenReadStream(remotePath)));
        }
    }

    public async Task<string?> ExportItemForDragAsync(SftpFileItem item)
    {
        if (!_service.IsConnected)
            return null;

        return await ExportItemForDragCoreAsync(item, true);
    }

    public string? ExportItemForDragBlocking(SftpFileItem item)
    {
        if (!_service.IsConnected)
            return null;

        try
        {
            return Task.Run(() => ExportItemForDragCoreAsync(item, false)).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Dispatcher.UIThread.Post(() =>
            {
                ErrorMessage = $"Drag export failed: {ex.Message}";
                IsLoading = false;
            });
            return null;
        }
    }

    private async Task<string?> ExportItemForDragCoreAsync(SftpFileItem item, bool useInvoke)
    {
        var exportRoot = Path.Combine(Path.GetTempPath(), "ChiXueSsh", "SftpDrag", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(exportRoot);

        await SetDragExportLoadingAsync(useInvoke, true, null);

        try
        {
            var localPath = Path.Combine(exportRoot, SanitizeLocalName(item.Name));
            if (item.IsDirectory)
                await DownloadRemoteDirectoryAsync(item.FullPath, localPath);
            else
                await _service.DownloadFileAsync(item.FullPath, localPath);

            await SetDragExportLoadingAsync(useInvoke, false, null);
            return localPath;
        }
        catch (Exception ex)
        {
            try
            {
                if (Directory.Exists(exportRoot))
                    Directory.Delete(exportRoot, true);
            }
            catch
            {
            }

            await SetDragExportLoadingAsync(useInvoke, false, $"Drag export failed: {ex.Message}");
            return null;
        }
    }

    private async Task SetDragExportLoadingAsync(bool useInvoke, bool isLoading, string? errorMessage)
    {
        void Update()
        {
            IsLoading = isLoading;
            ErrorMessage = errorMessage;
        }

        if (useInvoke)
            await Dispatcher.UIThread.InvokeAsync(Update);
        else
            Dispatcher.UIThread.Post(Update);
    }

    private async Task DownloadRemoteDirectoryAsync(string remotePath, string localPath)
    {
        Directory.CreateDirectory(localPath);

        var children = await _service.ListDirectoryAsync(remotePath);
        foreach (var child in children)
        {
            var childLocalPath = Path.Combine(localPath, SanitizeLocalName(child.Name));
            if (child.IsDirectory)
                await DownloadRemoteDirectoryAsync(child.FullPath, childLocalPath);
            else
                await _service.DownloadFileAsync(child.FullPath, childLocalPath);
        }
    }

    private static string SanitizeLocalName(string name)
    {
        var invalidChars = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Select(ch => invalidChars.Contains(ch) ? '_' : ch).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(cleaned) ? "download" : cleaned;
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
        if (!_service.IsConnected)
            return;

        var targets = SelectedFiles.Count > 0
            ? SelectedFiles.ToList()
            : SelectedFile != null
                ? [SelectedFile]
                : [];
        if (targets.Count == 0)
            return;

        if (ShowConfirmDialogAsync != null)
        {
            var confirmed = await ShowConfirmDialogAsync(BuildDeleteMessage(targets));
            if (!confirmed)
                return;
        }

        try
        {
            foreach (var item in targets)
                await _service.DeleteAsync(item.FullPath, item.IsDirectory);
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

    private string BuildDeleteMessage(IReadOnlyList<SftpFileItem> targets)
    {
        if (targets.Count == 1)
            return L.IsEnglish
                ? $"Delete '{targets[0].Name}'?"
                : $"确定删除“{targets[0].Name}”吗？";

        var sample = string.Join(", ", targets.Take(3).Select(item => item.Name));
        if (targets.Count > 3)
            sample += "...";

        return L.IsEnglish
            ? $"Delete {targets.Count} selected items?\n{sample}"
            : $"确定删除 {targets.Count} 个所选项吗？\n{sample}";
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
