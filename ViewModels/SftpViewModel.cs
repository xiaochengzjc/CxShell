using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CxShell.Models;
using CxShell.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CxShell.ViewModels;

public partial class SftpViewModel : ObservableObject
{
    private LocalizationService L => LocalizationService.Shared;

    public string UploadText => L.Text("Sftp.Upload");
    public string DownloadText => L.Text("Sftp.Download");
    public string EditText => L.Text("Sftp.Edit");
    public string NewDirectoryText => L.Text("Sftp.NewDirectory");
    public string NameText => L.Text("Sftp.Name");
    public string SizeText => L.Text("Sftp.Size");
    public string ModifiedText => L.Text("Sftp.Modified");
    public string RenameText => L.Text("Common.Rename");
    public string DeleteText => L.Text("Common.Delete");
    public string ConnectHintText => L.Text("Sftp.ConnectHint");
    public string LoadingText => L.Text("Sftp.Loading");
    public string TransferQueueTitleText => L.Text("Sftp.TransferQueue");
    public string TransferStatusText => L.Text("Sftp.TransferStatus");
    public string TransferTypeText => L.Text("Sftp.TransferType");
    public string TransferFileText => L.Text("Sftp.TransferFile");
    public string TransferProgressText => L.Text("Sftp.TransferProgress");
    public string TransferSpeedText => L.Text("Sftp.TransferSpeed");
    public string TransferRemainingText => L.Text("Sftp.TransferRemaining");
    public string TransferTargetText => L.Text("Sftp.TransferTarget");
    public string TransferOperationText => L.Text("Sftp.TransferOperation");
    public string CancelTransferText => L.Text("Sftp.CancelTransfer");
    public string RetryTransferText => L.Text("Sftp.RetryTransfer");
    public string RemoveTransferText => L.Text("Sftp.RemoveTransfer");
    public string ClearCompletedTransfersText => L.Text("Sftp.ClearCompletedTransfers");
    public string TransferPanelToggleText => IsTransferPanelExpanded
        ? L.Text("Sftp.CollapseTransfers")
        : L.Text("Sftp.ExpandTransfers");

    private IFileTransferService _service;
    private SessionInfo? _currentSession;
    private string? _currentPassword;
    private string _homeDirectory = "/";
    private readonly SemaphoreSlim _serviceGate = new(1, 1);
    private readonly SemaphoreSlim _transferGate = new(1, 1);
    private readonly Dictionary<Guid, (SessionInfo Session, string? Password)> _transferSessions = new();
    private static int _dragCacheCleanupStarted;
    private const long MaxEditableFileSize = 5 * 1024 * 1024;

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
    [ObservableProperty] private bool _isPathSuggestionOpen;
    [ObservableProperty] private int _selectedPathSuggestionIndex = -1;
    [ObservableProperty] private bool _isTransferPanelExpanded = true;

    public ObservableCollection<SftpFileItem> Files { get; } = new();
    public ObservableCollection<PathSegment> PathSegments { get; } = new();
    public ObservableCollection<SftpPathSuggestionItem> PathSuggestions { get; } = new();
    public ObservableCollection<SftpTransferTaskItem> TransferTasks { get; } = new();
    private readonly List<SftpFileItem> _selectedFiles = new();
    private bool _isPathInputActive;
    private bool _isApplyingPathSuggestion;

    public IReadOnlyList<SftpFileItem> SelectedFiles => _selectedFiles;
    public int SelectedFileCount => _selectedFiles.Count;
    public bool HasMultipleSelectedFiles => _selectedFiles.Count > 1;
    public bool HasSelectedFiles => _selectedFiles.Count > 0;
    public bool HasTransferTasks => TransferTasks.Count > 0;
    public bool HasCompletedTransfers => TransferTasks.Any(task => task.Status == SftpTransferStatus.Completed);
    public string TransferSummaryText
    {
        get
        {
            var active = TransferTasks.Count(task => task.Status is
                SftpTransferStatus.Pending or
                SftpTransferStatus.Running or
                SftpTransferStatus.Cancelling);
            var failed = TransferTasks.Count(task => task.Status == SftpTransferStatus.Failed);
            var completed = TransferTasks.Count(task => task.Status == SftpTransferStatus.Completed);
            return L.IsEnglish
                ? $"{active} active, {failed} failed, {completed} completed"
                : $"{active} \u4e2a\u8fdb\u884c\u4e2d\uff0c{failed} \u4e2a\u5931\u8d25\uff0c{completed} \u4e2a\u5b8c\u6210";
        }
    }

    public Func<Task<string?>>? PickUploadFileAsync { get; set; }
    public Func<string, Task<string?>>? PickDownloadPathAsync { get; set; }
    public Func<string, Task<bool>>? ShowConfirmDialogAsync { get; set; }
    public Func<string, string, Task<string?>>? ShowInputDialogAsync { get; set; }
    public Func<string, Task<bool>>? TryActivateRemoteFileEditorAsync { get; set; }
    public Func<RemoteFileEditorViewModel, Task>? ShowRemoteFileEditorAsync { get; set; }
    public string RemoteEditorConnectionKey { get; private set; } = string.Empty;

    public bool IsBrowsingSession(SessionInfo session)
    {
        return _service.IsConnected &&
               string.Equals(RemoteEditorConnectionKey, BuildConnectionKey(session), StringComparison.Ordinal);
    }

    public SftpViewModel()
    {
        _service = CreateService(SessionProtocol.SFTP);
        _service.ErrorOccurred += OnServiceError;
        TransferTasks.CollectionChanged += OnTransferTasksCollectionChanged;
        LocalizationService.Shared.LanguageChanged += (_, _) => RefreshLocalization();
        if (Interlocked.Exchange(ref _dragCacheCleanupStarted, 1) == 0)
            _ = Task.Run(CleanupExpiredDragCache);
    }

    private void RefreshLocalization()
    {
        OnPropertyChanged(nameof(UploadText));
        OnPropertyChanged(nameof(DownloadText));
        OnPropertyChanged(nameof(EditText));
        OnPropertyChanged(nameof(NewDirectoryText));
        OnPropertyChanged(nameof(NameText));
        OnPropertyChanged(nameof(SizeText));
        OnPropertyChanged(nameof(ModifiedText));
        OnPropertyChanged(nameof(RenameText));
        OnPropertyChanged(nameof(DeleteText));
        OnPropertyChanged(nameof(ConnectHintText));
        OnPropertyChanged(nameof(LoadingText));
        OnPropertyChanged(nameof(TransferQueueTitleText));
        OnPropertyChanged(nameof(TransferStatusText));
        OnPropertyChanged(nameof(TransferTypeText));
        OnPropertyChanged(nameof(TransferFileText));
        OnPropertyChanged(nameof(TransferProgressText));
        OnPropertyChanged(nameof(TransferSpeedText));
        OnPropertyChanged(nameof(TransferRemainingText));
        OnPropertyChanged(nameof(TransferTargetText));
        OnPropertyChanged(nameof(TransferOperationText));
        OnPropertyChanged(nameof(CancelTransferText));
        OnPropertyChanged(nameof(RetryTransferText));
        OnPropertyChanged(nameof(RemoveTransferText));
        OnPropertyChanged(nameof(ClearCompletedTransfersText));
        OnPropertyChanged(nameof(TransferPanelToggleText));
        OnPropertyChanged(nameof(TransferSummaryText));

        foreach (var task in TransferTasks)
            task.RefreshLocalization();
    }

    partial void OnIsTransferPanelExpandedChanged(bool value)
    {
        OnPropertyChanged(nameof(TransferPanelToggleText));
    }

    private void OnTransferTasksCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems != null)
        {
            foreach (SftpTransferTaskItem task in e.NewItems)
                task.PropertyChanged += OnTransferTaskPropertyChanged;
        }

        if (e.OldItems != null)
        {
            foreach (SftpTransferTaskItem task in e.OldItems)
            {
                task.PropertyChanged -= OnTransferTaskPropertyChanged;
                _transferSessions.Remove(task.Id);
            }
        }

        OnPropertyChanged(nameof(HasTransferTasks));
        OnPropertyChanged(nameof(HasCompletedTransfers));
        OnPropertyChanged(nameof(TransferSummaryText));
    }

    private void OnTransferTaskPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SftpTransferTaskItem.Status) or nameof(SftpTransferTaskItem.TransferredBytes))
        {
            OnPropertyChanged(nameof(HasCompletedTransfers));
            OnPropertyChanged(nameof(TransferSummaryText));
        }
    }

    partial void OnSelectedFileChanged(SftpFileItem? oldValue, SftpFileItem? newValue)
    {
        if (oldValue != null)
            oldValue.IsSelected = false;

        if (newValue != null)
            newValue.IsSelected = true;
    }

    partial void OnPathInputChanged(string value)
    {
        RefreshPathSuggestions();
    }

    partial void OnIsConnectedChanged(bool value)
    {
        if (!value)
            HidePathSuggestions();
    }

    public void SetPathInputActive(bool isActive)
    {
        _isPathInputActive = isActive;
        if (isActive)
            RefreshPathSuggestions();
        else
            HidePathSuggestions();
    }

    public void HidePathSuggestions()
    {
        IsPathSuggestionOpen = false;
        SelectedPathSuggestionIndex = -1;
    }

    public bool MoveSelectedPathSuggestion(int offset)
    {
        if (!IsPathSuggestionOpen || PathSuggestions.Count == 0)
            return false;

        var next = SelectedPathSuggestionIndex < 0
            ? 0
            : SelectedPathSuggestionIndex + offset;

        if (next < 0)
            next = PathSuggestions.Count - 1;
        else if (next >= PathSuggestions.Count)
            next = 0;

        SelectedPathSuggestionIndex = next;
        return true;
    }

    public bool AcceptSelectedPathSuggestion()
    {
        if (!IsPathSuggestionOpen ||
            SelectedPathSuggestionIndex < 0 ||
            SelectedPathSuggestionIndex >= PathSuggestions.Count)
        {
            return false;
        }

        return AcceptPathSuggestion(PathSuggestions[SelectedPathSuggestionIndex]);
    }

    public bool AcceptPathSuggestion(SftpPathSuggestionItem suggestion)
    {
        _isApplyingPathSuggestion = true;
        try
        {
            PathInput = suggestion.CompletionPath;
            HidePathSuggestions();
            return true;
        }
        finally
        {
            _isApplyingPathSuggestion = false;
        }
    }

    public void SetSelectedFiles(IEnumerable<SftpFileItem> items)
    {
        var next = items.Where(item => item != null).Distinct().ToList();
        if (_selectedFiles.SequenceEqual(next))
            return;

        _selectedFiles.Clear();
        _selectedFiles.AddRange(next);
        OnPropertyChanged(nameof(SelectedFiles));
        OnPropertyChanged(nameof(SelectedFileCount));
        OnPropertyChanged(nameof(HasMultipleSelectedFiles));
        OnPropertyChanged(nameof(HasSelectedFiles));
        OnPropertyChanged(nameof(DeleteText));

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
        RemoteEditorConnectionKey = BuildConnectionKey(session);
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

    public async Task TryNavigateToRemotePathAsync(string path)
    {
        if (!_service.IsConnected)
            return;

        var targetPath = NormalizeRemotePath(path);
        if (string.IsNullOrWhiteSpace(targetPath) ||
            string.Equals(targetPath, CurrentPath, StringComparison.Ordinal))
        {
            return;
        }

        await LoadDirectoryAsync(targetPath);
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
            await RunServiceAsync(service => service.ConnectAsync(_currentSession, _currentPassword));
            _homeDirectory = await RunServiceAsync(service => service.GetHomeDirectoryAsync());
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
            var items = await RunServiceAsync(service => service.ListDirectoryAsync(path));
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
                RefreshPathSuggestions();
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

        HidePathSuggestions();
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

    private void RefreshPathSuggestions()
    {
        if (!_isPathInputActive ||
            _isApplyingPathSuggestion ||
            !IsConnected ||
            IsLoading ||
            Files.Count == 0)
        {
            if (!_isApplyingPathSuggestion)
                HidePathSuggestions();
            return;
        }

        var suggestions = BuildPathSuggestions(PathInput);
        PathSuggestions.Clear();
        foreach (var suggestion in suggestions)
            PathSuggestions.Add(suggestion);

        IsPathSuggestionOpen = PathSuggestions.Count > 0;
        SelectedPathSuggestionIndex = IsPathSuggestionOpen ? 0 : -1;
    }

    private IReadOnlyList<SftpPathSuggestionItem> BuildPathSuggestions(string? input)
    {
        var value = input?.Trim() ?? string.Empty;
        if (value.Length == 0)
            return [];

        value = value.Replace('\\', '/');
        if (!TryGetCurrentDirectorySuggestionPrefix(value, out var prefix))
            return [];

        return Files
            .Where(item => item.Name is not "." and not "..")
            .Where(item => string.IsNullOrEmpty(prefix) ||
                           item.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(item => item.IsDirectory)
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .Take(10)
            .Select(item =>
            {
                var path = CombineRemotePath(CurrentPath, item.Name);
                if (item.IsDirectory)
                    path = path.TrimEnd('/') + "/";

                return new SftpPathSuggestionItem(
                    item.Name,
                    path,
                    item.Icon,
                    item.IsDirectory);
            })
            .ToList();
    }

    private bool TryGetCurrentDirectorySuggestionPrefix(string value, out string prefix)
    {
        prefix = string.Empty;

        if (value == "~" || value.StartsWith("~/", StringComparison.Ordinal))
        {
            var rest = value.Length == 1 ? string.Empty : value[2..];
            if (rest.Contains('/'))
                return false;

            if (!string.Equals(CollapseRemotePath(_homeDirectory), CurrentPath, StringComparison.Ordinal))
                return false;

            prefix = rest;
            return true;
        }

        var slashIndex = value.LastIndexOf('/');
        if (slashIndex < 0)
        {
            prefix = value;
            return true;
        }

        var parent = slashIndex == 0
            ? "/"
            : value[..slashIndex];
        prefix = value[(slashIndex + 1)..];

        var normalizedParent = value.StartsWith("/", StringComparison.Ordinal)
            ? CollapseRemotePath(parent)
            : NormalizeRemotePath(parent);

        return string.Equals(normalizedParent, CurrentPath, StringComparison.Ordinal);
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
        else
            await EditRemoteFile(item);
    }

    [RelayCommand]
    private async Task EditRemoteFile(SftpFileItem? item)
    {
        if (!_service.IsConnected)
            return;

        item ??= SelectedFile;
        if (item == null || item.IsDirectory)
            return;

        if (TryActivateRemoteFileEditorAsync != null &&
            await TryActivateRemoteFileEditorAsync(item.FullPath))
        {
            return;
        }

        if (ShowRemoteFileEditorAsync == null)
        {
            ErrorMessage = "Editor is not available.";
            return;
        }

        if (item.Size > MaxEditableFileSize)
        {
            ErrorMessage = $"File is too large to edit online. Limit: {FormatByteSize(MaxEditableFileSize)}.";
            return;
        }

        if (_currentSession == null)
        {
            ErrorMessage = "Current session is not available.";
            return;
        }

        var editorSession = CloneTransferSession(_currentSession);
        var editorPassword = _currentPassword;
        var editorConnectionKey = RemoteEditorConnectionKey;
        string? tempPath = null;
        IFileTransferService? editorService = null;
        var editorShown = false;
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            IsLoading = true;
            ErrorMessage = null;
        });

        try
        {
            var remotePath = item.FullPath;
            var fileName = item.Name;
            editorService = CreateService(editorSession.Protocol);
            await RunServiceAsync(service => service.ConnectAsync(editorSession, editorPassword), editorService);

            tempPath = CreateTempEditFilePath(fileName);
            await RunServiceAsync(service => service.DownloadFileAsync(remotePath, tempPath), editorService);
            var bytes = await File.ReadAllBytesAsync(tempPath);
            if (LooksBinary(bytes))
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    ErrorMessage = "Binary files cannot be edited online.";
                    IsLoading = false;
                });
                return;
            }

            var snapshot = DecodeEditableText(bytes);
            var editorVm = new RemoteFileEditorViewModel(
                fileName,
                remotePath,
                snapshot.Text,
                $"{FormatByteSize(bytes.Length)} · {snapshot.Encoding.WebName}",
                text => SaveEditedRemoteFileAsync(editorService!, editorConnectionKey, fileName, remotePath, snapshot, text),
                () => DisposeFileTransferService(editorService!));

            await Dispatcher.UIThread.InvokeAsync(() => IsLoading = false);
            await ShowRemoteFileEditorAsync(editorVm);
            editorShown = true;
        }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                ErrorMessage = $"Open editor failed: {ex.Message}";
                IsLoading = false;
            });
        }
        finally
        {
            if (tempPath != null)
                TryDeleteFile(tempPath);

            if (!editorShown && editorService != null)
                DisposeFileTransferService(editorService);
        }
    }

    private async Task SaveEditedRemoteFileAsync(
        IFileTransferService editorService,
        string editorConnectionKey,
        string fileName,
        string remotePath,
        EditableTextSnapshot snapshot,
        string text)
    {
        string? tempPath = null;
        try
        {
            tempPath = CreateTempEditFilePath(fileName);
            var bytes = EncodeEditableText(snapshot, text);
            await File.WriteAllBytesAsync(tempPath, bytes);
            await RunServiceAsync(service => service.UploadFileAsync(tempPath, remotePath), editorService);

            if (string.Equals(editorConnectionKey, RemoteEditorConnectionKey, StringComparison.Ordinal) &&
                _service.IsConnected &&
                string.Equals(GetRemoteParentPath(remotePath), CurrentPath, StringComparison.Ordinal))
            {
                await LoadDirectoryAsync(CurrentPath);
            }
        }
        finally
        {
            if (tempPath != null)
                TryDeleteFile(tempPath);
        }
    }

    private sealed record EditableTextSnapshot(
        string Text,
        Encoding Encoding,
        byte[] Preamble,
        string NewLine);

    private static EditableTextSnapshot DecodeEditableText(byte[] bytes)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        var offset = 0;
        Encoding encoding;
        byte[] preamble;
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            encoding = new UTF8Encoding(false, true);
            preamble = [0xEF, 0xBB, 0xBF];
            offset = 3;
        }
        else if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            encoding = Encoding.Unicode;
            preamble = [0xFF, 0xFE];
            offset = 2;
        }
        else if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            encoding = Encoding.BigEndianUnicode;
            preamble = [0xFE, 0xFF];
            offset = 2;
        }
        else
        {
            encoding = new UTF8Encoding(false, true);
            preamble = [];
        }

        string text;
        try
        {
            text = encoding.GetString(bytes, offset, bytes.Length - offset);
        }
        catch (DecoderFallbackException)
        {
            encoding = Encoding.GetEncoding("GB18030");
            preamble = [];
            offset = 0;
            text = encoding.GetString(bytes);
        }

        return new EditableTextSnapshot(text, encoding, preamble, DetectNewLine(text));
    }

    private static byte[] EncodeEditableText(EditableTextSnapshot snapshot, string text)
    {
        var normalized = NormalizeNewLines(text, snapshot.NewLine);
        var body = snapshot.Encoding.GetBytes(normalized);
        if (snapshot.Preamble.Length == 0)
            return body;

        var result = new byte[snapshot.Preamble.Length + body.Length];
        Buffer.BlockCopy(snapshot.Preamble, 0, result, 0, snapshot.Preamble.Length);
        Buffer.BlockCopy(body, 0, result, snapshot.Preamble.Length, body.Length);
        return result;
    }

    private static string DetectNewLine(string text)
    {
        var crlf = text.IndexOf("\r\n", StringComparison.Ordinal);
        if (crlf >= 0)
            return "\r\n";

        var lf = text.IndexOf('\n');
        if (lf >= 0)
            return "\n";

        var cr = text.IndexOf('\r');
        return cr >= 0 ? "\r" : Environment.NewLine;
    }

    private static string NormalizeNewLines(string text, string newLine)
    {
        return text
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Replace("\n", newLine, StringComparison.Ordinal);
    }

    private static bool LooksBinary(byte[] bytes)
    {
        var sampleLength = Math.Min(bytes.Length, 8192);
        if (sampleLength == 0)
            return false;

        var controlCount = 0;
        for (var i = 0; i < sampleLength; i++)
        {
            var value = bytes[i];
            if (value == 0)
                return true;

            if (value < 0x08 || value is > 0x0D and < 0x20)
                controlCount++;
        }

        return controlCount > sampleLength / 10;
    }

    private static string CreateTempEditFilePath(string remoteName)
    {
        var root = Path.Combine(Path.GetTempPath(), "CxShell", "RemoteEdit");
        Directory.CreateDirectory(root);
        var name = SanitizeLocalName(remoteName);
        return Path.Combine(root, $"{Guid.NewGuid():N}-{name}");
    }

    private static string GetRemoteParentPath(string remotePath)
    {
        var trimmed = remotePath.Replace('\\', '/').TrimEnd('/');
        if (string.IsNullOrWhiteSpace(trimmed) || trimmed == "/")
            return "/";

        var slashIndex = trimmed.LastIndexOf('/');
        if (slashIndex <= 0)
            return "/";

        return trimmed[..slashIndex];
    }

    private static string BuildConnectionKey(SessionInfo session)
    {
        return $"{session.Id}|{session.Protocol}|{session.Username}@{session.Host}:{session.Port}";
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
        }
    }

    private static string FormatByteSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):F1} GB";
    }

    [RelayCommand]
    private async Task Upload()
    {
        if (!_service.IsConnected || PickUploadFileAsync == null || _currentSession == null)
            return;

        var localPath = await PickUploadFileAsync();
        if (string.IsNullOrEmpty(localPath))
            return;

        var fileName = System.IO.Path.GetFileName(localPath);
        var remotePath = CurrentPath.TrimEnd('/') + "/" + fileName;
        ErrorMessage = null;
        EnqueueTransferTask(CreateUploadTask(localPath, remotePath), CloneTransferSession(_currentSession), _currentPassword);
    }

    public async Task UploadDroppedPathsAsync(IEnumerable<string> localPaths)
    {
        if (!_service.IsConnected || _currentSession == null)
            return;

        var paths = localPaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (paths.Count == 0)
            return;

        UpdateLocalDirectoryFromDroppedPath(paths[0]);

        try
        {
            var targetDirectory = CurrentPath;
            foreach (var path in paths)
                await QueueUploadLocalPathAsync(path, targetDirectory);
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
        });
    }

    private async Task QueueUploadLocalPathAsync(string localPath, string remoteDirectory)
    {
        if (_currentSession == null)
            return;

        if (File.Exists(localPath))
        {
            var fileName = Path.GetFileName(localPath);
            if (string.IsNullOrWhiteSpace(fileName))
                return;

            EnqueueTransferTask(
                CreateUploadTask(localPath, CombineRemotePath(remoteDirectory, fileName)),
                CloneTransferSession(_currentSession),
                _currentPassword);
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
            {
                EnqueueTransferTask(
                    CreateUploadTask(file, CombineRemotePath(remotePath, fileName)),
                    CloneTransferSession(_currentSession),
                    _currentPassword);
            }
        }

        foreach (var directory in Directory.EnumerateDirectories(localPath))
            await QueueUploadLocalPathAsync(directory, remotePath);
    }

    private async Task UploadLocalPathAsync(string localPath, string remoteDirectory)
    {
        if (File.Exists(localPath))
        {
            var fileName = Path.GetFileName(localPath);
            if (string.IsNullOrWhiteSpace(fileName))
                return;

            await RunServiceAsync(service => service.UploadFileAsync(localPath, CombineRemotePath(remoteDirectory, fileName)));
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
                await RunServiceAsync(service => service.UploadFileAsync(file, CombineRemotePath(remotePath, fileName)));
        }

        foreach (var directory in Directory.EnumerateDirectories(localPath))
            await UploadLocalPathAsync(directory, remotePath);
    }

    private async Task EnsureRemoteDirectoryAsync(string remotePath)
    {
        try
        {
            await RunServiceAsync(service => service.CreateDirectoryAsync(remotePath));
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
        return _service is SftpService &&
               _service.IsConnected &&
               PlatformServices.SupportsVirtualFileDragOut &&
               (!PlatformServices.IsMacOS || !item.IsDirectory);
    }

    public async Task<List<VirtualDragFile>> CreateVirtualDragFilesAsync(SftpFileItem item)
    {
        if (_service is not SftpService || _currentSession == null)
            throw new NotSupportedException("Only SFTP supports streaming drag-out.");

        var dragSession = CloneTransferSession(_currentSession);
        var dragPassword = _currentPassword;

        if (!item.IsDirectory)
        {
            var remotePath = item.FullPath;
            return [CreateVirtualDragFile(
                dragSession,
                dragPassword,
                item.Name,
                item.Size,
                item.LastModified,
                remotePath)];
        }

        var files = new List<VirtualDragFile>();
        var rootName = SanitizeLocalName(item.Name);
        await AddVirtualDragDirectoryFilesAsync(
            dragSession,
            dragPassword,
            item.FullPath,
            rootName,
            files);
        return files;
    }

    private async Task AddVirtualDragDirectoryFilesAsync(
        SessionInfo dragSession,
        string? dragPassword,
        string remoteDirectory,
        string relativeDirectory,
        List<VirtualDragFile> files)
    {
        var children = await RunServiceAsync(service => service.ListDirectoryAsync(remoteDirectory));
        foreach (var child in children)
        {
            var relativePath = relativeDirectory + "\\" + SanitizeLocalName(child.Name);
            if (child.IsDirectory)
            {
                await AddVirtualDragDirectoryFilesAsync(
                    dragSession,
                    dragPassword,
                    child.FullPath,
                    relativePath,
                    files);
                continue;
            }

            var remotePath = child.FullPath;
            files.Add(CreateVirtualDragFile(
                dragSession,
                dragPassword,
                relativePath,
                child.Size,
                child.LastModified,
                remotePath));
        }
    }

    private VirtualDragFile CreateVirtualDragFile(
        SessionInfo session,
        string? password,
        string fileName,
        long size,
        DateTime lastModified,
        string remotePath)
    {
        var task = new SftpTransferTaskItem
        {
            Direction = SftpTransferDirection.Download,
            FileName = fileName,
            LocalPath = PlatformServices.IsMacOS
                ? (L.IsEnglish ? "Finder (drag-out)" : "Finder（拖放）")
                : (L.IsEnglish ? "Windows Explorer (drag-out)" : "Windows 资源管理器（拖放）"),
            RemotePath = remotePath,
            TotalBytes = size,
            SupportsRetry = false
        };

        task.PrepareForStart();
        task.IsExecutionActive = true;
        TransferTasks.Add(task);
        IsTransferPanelExpanded = true;

        var cancellation = task.CancellationTokenSource
            ?? throw new InvalidOperationException("The drag transfer cancellation source is unavailable.");
        _ = cancellation.Token.Register(() => QueueVirtualTransferCancelled(task));

        return new VirtualDragFile(
            fileName,
            size,
            lastModified,
            () => OpenVirtualDragStream(session, password, remotePath, cancellation.Token),
            cancellation.Token,
            started: () => QueueVirtualTransferStarted(task),
            progressChanged: transferred => QueueTransferProgress(task, (ulong)Math.Max(0, transferred)),
            completed: () => QueueVirtualTransferCompleted(task),
            failed: message => QueueVirtualTransferFailed(task, message),
            cancelled: () => QueueVirtualTransferCancelled(task),
            cancellationRequested: cancellation.Cancel);
    }

    private static Stream OpenVirtualDragStream(
        SessionInfo session,
        string? password,
        string remotePath,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var service = CreateService(session.Protocol);
        try
        {
            service.ConnectAsync(session, password).GetAwaiter().GetResult();
            cancellationToken.ThrowIfCancellationRequested();
            if (service is not SftpService sftpService)
                throw new NotSupportedException("Only SFTP supports streaming drag-out.");

            return new OwnedFileTransferStream(sftpService.OpenReadStream(remotePath), service);
        }
        catch
        {
            DisposeFileTransferService(service);
            throw;
        }
    }

    private static void QueueVirtualTransferStarted(SftpTransferTaskItem task)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (task.IsExecutionActive && task.Status == SftpTransferStatus.Pending)
                task.MarkRunning();
        });
    }

    private static void QueueVirtualTransferCompleted(SftpTransferTaskItem task)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (!task.IsExecutionActive)
                return;

            if (task.TotalBytes > 0)
                task.UpdateProgress(task.TotalBytes, task.TotalBytes);
            task.MarkCompleted();
            task.ClearRuntimeHandles();
            task.IsExecutionActive = false;
        });
    }

    private static void QueueVirtualTransferFailed(SftpTransferTaskItem task, string message)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (!task.IsExecutionActive)
                return;

            task.MarkFailed(message);
            task.ClearRuntimeHandles();
            task.IsExecutionActive = false;
        });
    }

    private static void QueueVirtualTransferCancelled(SftpTransferTaskItem task)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (!task.IsExecutionActive)
                return;

            task.MarkCancelled();
            task.ClearRuntimeHandles();
            task.IsExecutionActive = false;
        });
    }

    public async Task<string?> ExportItemForDragAsync(SftpFileItem item)
    {
        if (!_service.IsConnected || _currentSession == null)
            return null;

        try
        {
            ErrorMessage = null;
            var cacheRoot = GetDragCacheRoot(_currentSession, item);
            var payloadRoot = Path.Combine(cacheRoot, "payload");
            var localPath = Path.Combine(payloadRoot, SanitizeLocalName(item.Name));
            Directory.CreateDirectory(payloadRoot);

            if (!item.IsDirectory)
            {
                if (File.Exists(localPath) && new FileInfo(localPath).Length == item.Size)
                    return localPath;

                var task = EnqueueTransferTask(
                    CreateDownloadTask(item, localPath),
                    CloneTransferSession(_currentSession),
                    _currentPassword);
                return await WaitForTransferTaskAsync(task) && File.Exists(localPath)
                    ? localPath
                    : null;
            }

            var completeMarker = Path.Combine(cacheRoot, ".complete");
            if (Directory.Exists(localPath) && File.Exists(completeMarker))
                return localPath;

            Directory.CreateDirectory(localPath);
            var tasks = new List<SftpTransferTaskItem>();
            await EnqueueRemoteDirectoryForCacheAsync(
                item.FullPath,
                localPath,
                CloneTransferSession(_currentSession),
                _currentPassword,
                tasks);

            var results = await Task.WhenAll(tasks.Select(WaitForTransferTaskAsync));
            if (results.Any(result => !result))
                return null;

            File.WriteAllText(completeMarker, DateTimeOffset.UtcNow.ToString("O"), Encoding.UTF8);
            return localPath;
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Drag export failed: {ex.Message}";
            return null;
        }
    }

    public bool IsItemCachedForDrag(SftpFileItem item)
    {
        if (_currentSession == null)
            return false;

        try
        {
            var cacheRoot = GetDragCacheRoot(_currentSession, item);
            var localPath = Path.Combine(cacheRoot, "payload", SanitizeLocalName(item.Name));
            if (item.IsDirectory)
                return Directory.Exists(localPath) && File.Exists(Path.Combine(cacheRoot, ".complete"));

            return File.Exists(localPath) && new FileInfo(localPath).Length == item.Size;
        }
        catch
        {
            return false;
        }
    }

    private async Task EnqueueRemoteDirectoryForCacheAsync(
        string remotePath,
        string localPath,
        SessionInfo transferSession,
        string? password,
        ICollection<SftpTransferTaskItem> tasks)
    {
        Directory.CreateDirectory(localPath);
        var children = await RunServiceAsync(service => service.ListDirectoryAsync(remotePath));
        foreach (var child in children)
        {
            var childLocalPath = Path.Combine(localPath, SanitizeLocalName(child.Name));
            if (child.IsDirectory)
            {
                await EnqueueRemoteDirectoryForCacheAsync(
                    child.FullPath,
                    childLocalPath,
                    transferSession,
                    password,
                    tasks);
                continue;
            }

            if (File.Exists(childLocalPath) && new FileInfo(childLocalPath).Length == child.Size)
                continue;

            tasks.Add(EnqueueTransferTask(
                CreateDownloadTask(child, childLocalPath),
                transferSession,
                password));
        }
    }

    private static async Task<bool> WaitForTransferTaskAsync(SftpTransferTaskItem task)
    {
        if (!task.IsExecutionActive)
            return task.Status == SftpTransferStatus.Completed;

        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        PropertyChangedEventHandler? handler = null;
        handler = (_, args) =>
        {
            if (args.PropertyName != nameof(SftpTransferTaskItem.IsExecutionActive) || task.IsExecutionActive)
                return;

            task.PropertyChanged -= handler;
            completion.TrySetResult(task.Status == SftpTransferStatus.Completed);
        };
        task.PropertyChanged += handler;

        if (!task.IsExecutionActive)
        {
            task.PropertyChanged -= handler;
            return task.Status == SftpTransferStatus.Completed;
        }

        return await completion.Task.ConfigureAwait(false);
    }

    private static string GetDragCacheRoot(SessionInfo session, SftpFileItem item)
    {
        var identity = $"{BuildConnectionKey(session)}\0{item.FullPath}\0{item.Size}\0{item.LastModified.ToUniversalTime().Ticks}";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..24];
        return Path.Combine(Path.GetTempPath(), "CxShell", "SftpDragCache", hash);
    }

    private static void CleanupExpiredDragCache()
    {
        try
        {
            var cacheRoot = Path.Combine(Path.GetTempPath(), "CxShell", "SftpDragCache");
            if (!Directory.Exists(cacheRoot))
                return;

            var cutoff = DateTime.UtcNow.AddDays(-7);
            foreach (var directory in Directory.EnumerateDirectories(cacheRoot))
            {
                try
                {
                    if (Directory.GetLastWriteTimeUtc(directory) < cutoff)
                        Directory.Delete(directory, true);
                }
                catch
                {
                }
            }
        }
        catch
        {
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
        if (!_service.IsConnected || _currentSession == null || SelectedFile == null || SelectedFile.IsDirectory)
            return;

        if (PickDownloadPathAsync == null)
            return;

        var selectedFile = SelectedFile;
        var localPath = await PickDownloadPathAsync(SelectedFile.Name);
        if (string.IsNullOrEmpty(localPath))
            return;

        ErrorMessage = null;
        EnqueueTransferTask(CreateDownloadTask(selectedFile, localPath), CloneTransferSession(_currentSession), _currentPassword);
    }

    private static SftpTransferTaskItem CreateUploadTask(string localPath, string remotePath)
    {
        return new SftpTransferTaskItem
        {
            Direction = SftpTransferDirection.Upload,
            FileName = Path.GetFileName(localPath),
            LocalPath = localPath,
            RemotePath = remotePath,
            TotalBytes = File.Exists(localPath) ? new FileInfo(localPath).Length : 0
        };
    }

    private static SftpTransferTaskItem CreateDownloadTask(SftpFileItem item, string localPath)
    {
        return new SftpTransferTaskItem
        {
            Direction = SftpTransferDirection.Download,
            FileName = item.Name,
            LocalPath = localPath,
            RemotePath = item.FullPath,
            TotalBytes = item.Size
        };
    }

    private SftpTransferTaskItem EnqueueTransferTask(SftpTransferTaskItem task, SessionInfo session, string? password)
    {
        var matchingTask = TransferTasks.FirstOrDefault(existing =>
            existing.IsExecutionActive &&
            existing.Direction == task.Direction &&
            string.Equals(existing.LocalPath, task.LocalPath, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(existing.RemotePath, task.RemotePath, StringComparison.Ordinal));
        if (matchingTask != null)
        {
            IsTransferPanelExpanded = true;
            return matchingTask;
        }

        task.PrepareForStart();
        task.IsExecutionActive = true;
        _transferSessions[task.Id] = (session, password);
        TransferTasks.Add(task);
        IsTransferPanelExpanded = true;
        _ = RunTransferTaskAsync(task);
        return task;
    }

    private async Task RunTransferTaskAsync(SftpTransferTaskItem task)
    {
        var cancellation = task.CancellationTokenSource;
        if (cancellation == null || !_transferSessions.TryGetValue(task.Id, out var connection))
            return;

        var enteredGate = false;
        IFileTransferService? transferService = null;

        try
        {
            await _transferGate.WaitAsync(cancellation.Token).ConfigureAwait(false);
            enteredGate = true;
            cancellation.Token.ThrowIfCancellationRequested();

            if (task.Direction == SftpTransferDirection.Upload)
            {
                if (!File.Exists(task.LocalPath))
                    throw new FileNotFoundException("The local file no longer exists.", task.LocalPath);

                var currentLength = new FileInfo(task.LocalPath).Length;
                await Dispatcher.UIThread.InvokeAsync(() => task.TotalBytes = currentLength);
            }

            transferService = CreateService(connection.Session.Protocol);
            task.ActiveService = transferService;
            await transferService.ConnectAsync(connection.Session, connection.Password).ConfigureAwait(false);
            cancellation.Token.ThrowIfCancellationRequested();

            await Dispatcher.UIThread.InvokeAsync(task.MarkRunning);
            Action<ulong> progress = transferred => QueueTransferProgress(task, transferred);
            var canResume = transferService is SftpService;

            if (task.Direction == SftpTransferDirection.Upload)
            {
                await transferService.UploadFileAsync(
                    task.LocalPath,
                    task.RemotePath,
                    progress,
                    cancellation.Token,
                    canResume).ConfigureAwait(false);
            }
            else
            {
                await transferService.DownloadFileAsync(
                    task.RemotePath,
                    task.LocalPath,
                    progress,
                    cancellation.Token,
                    canResume).ConfigureAwait(false);
            }

            cancellation.Token.ThrowIfCancellationRequested();
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (task.TotalBytes > 0)
                    task.UpdateProgress(task.TotalBytes, task.TotalBytes);
                task.MarkCompleted();
            });

            if (task.Direction == SftpTransferDirection.Upload)
                RefreshBrowserAfterUpload(task, connection.Session);
        }
        catch (OperationCanceledException)
        {
            await Dispatcher.UIThread.InvokeAsync(task.MarkCancelled);
        }
        catch (Exception) when (cancellation.IsCancellationRequested)
        {
            await Dispatcher.UIThread.InvokeAsync(task.MarkCancelled);
        }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() => task.MarkFailed(ex.Message));
        }
        finally
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                task.ClearRuntimeHandles();
                task.IsExecutionActive = false;
            });

            if (enteredGate)
                _transferGate.Release();

            if (transferService != null)
                _ = Task.Run(() => DisposeFileTransferService(transferService));
        }
    }

    private static void QueueTransferProgress(SftpTransferTaskItem task, ulong transferred)
    {
        var transferredBytes = transferred > long.MaxValue ? long.MaxValue : (long)transferred;
        var now = DateTimeOffset.UtcNow;
        task.LastUiProgressBytes = transferredBytes;

        if (task.TotalBytes > 0 &&
            transferredBytes < task.TotalBytes &&
            now - task.LastUiProgressAt < TimeSpan.FromMilliseconds(120))
        {
            return;
        }

        task.LastUiProgressAt = now;
        Dispatcher.UIThread.Post(() =>
        {
            if (task.IsExecutionActive && task.Status == SftpTransferStatus.Pending)
                task.MarkRunning();
            if (task.Status == SftpTransferStatus.Running)
                task.UpdateProgress(transferredBytes, task.TotalBytes);
        });
    }

    private void RefreshBrowserAfterUpload(SftpTransferTaskItem task, SessionInfo transferSession)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_currentSession == null ||
                !_service.IsConnected ||
                !string.Equals(BuildConnectionKey(_currentSession), BuildConnectionKey(transferSession), StringComparison.Ordinal) ||
                !string.Equals(CollapseRemotePath(GetRemoteParentPath(task.RemotePath)), CollapseRemotePath(CurrentPath), StringComparison.Ordinal))
            {
                return;
            }

            _ = LoadDirectoryAsync(CurrentPath);
        });
    }

    [RelayCommand]
    private void CancelTransfer(SftpTransferTaskItem? task)
    {
        if (task == null || !task.CanCancel)
            return;

        task.CancellationTokenSource?.Cancel();
        task.MarkCancelling();
    }

    [RelayCommand]
    private void RetryTransfer(SftpTransferTaskItem? task)
    {
        if (task == null || !task.CanRetry || !_transferSessions.ContainsKey(task.Id))
            return;

        task.PrepareForStart();
        task.IsExecutionActive = true;
        _ = RunTransferTaskAsync(task);
    }

    [RelayCommand]
    private void RemoveTransfer(SftpTransferTaskItem? task)
    {
        if (task == null || !task.CanRemove)
            return;

        task.CancellationTokenSource?.Dispose();
        task.CancellationTokenSource = null;
        TransferTasks.Remove(task);
    }

    [RelayCommand]
    private void ClearCompletedTransfers()
    {
        foreach (var task in TransferTasks.Where(item => item.Status == SftpTransferStatus.Completed).ToList())
            RemoveTransfer(task);
    }

    [RelayCommand]
    private void ToggleTransferPanel()
    {
        IsTransferPanelExpanded = !IsTransferPanelExpanded;
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
            var confirmed = await ShowConfirmDialogAsync(BuildDeleteConfirmMessage(targets));
            if (!confirmed)
                return;
        }

        var failures = new List<string>();
        foreach (var item in targets)
        {
            try
            {
                await RunServiceAsync(service => service.DeleteAsync(item.FullPath, item.IsDirectory));
            }
            catch (Exception ex)
            {
                failures.Add($"{item.Name}: {ex.Message}");
            }
        }

        await LoadDirectoryAsync(CurrentPath);

        if (failures.Count > 0)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                ErrorMessage = L.IsEnglish
                    ? $"Failed to delete {failures.Count} item(s): {string.Join("; ", failures.Take(3))}"
                    : $"\u5220\u9664 {failures.Count} \u9879\u5931\u8d25\uff1a{string.Join("; ", failures.Take(3))}";
            });
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
            await RunServiceAsync(service => service.RenameAsync(item.FullPath, newPath));
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
            await RunServiceAsync(service => service.CreateDirectoryAsync(newPath));
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

    private string BuildDeleteConfirmMessage(IReadOnlyList<SftpFileItem> targets)
    {
        if (targets.Count == 1)
        {
            return L.IsEnglish
                ? "Delete the selected item?"
                : "\u786e\u5b9a\u5220\u9664\u9009\u4e2d\u9879\u5417\uff1f";
        }

        return L.IsEnglish
            ? $"Delete {targets.Count} selected items?"
            : $"\u786e\u5b9a\u5220\u9664\u9009\u4e2d\u7684 {targets.Count} \u9879\u5417\uff1f";
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

    private static SessionInfo CloneTransferSession(SessionInfo source)
    {
        var clone = new SessionInfo
        {
            Id = source.Id,
            Name = source.Name,
            GroupId = source.GroupId
        };
        SessionTreeViewModel.CopySessionValues(clone, source);
        return clone;
    }

    private static void DisposeFileTransferService(IFileTransferService service)
    {
        try
        {
            service.Disconnect();
            if (service is IDisposable disposable)
                disposable.Dispose();
        }
        catch
        {
        }
    }

    private async Task RunServiceAsync(Func<IFileTransferService, Task> action, IFileTransferService? service = null)
    {
        await _serviceGate.WaitAsync();
        try
        {
            await action(service ?? _service);
        }
        finally
        {
            _serviceGate.Release();
        }
    }

    private async Task<T> RunServiceAsync<T>(Func<IFileTransferService, Task<T>> action, IFileTransferService? service = null)
    {
        await _serviceGate.WaitAsync();
        try
        {
            return await action(service ?? _service);
        }
        finally
        {
            _serviceGate.Release();
        }
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

    private sealed class OwnedFileTransferStream(Stream inner, IFileTransferService owner) : Stream
    {
        private Stream? _inner = inner;
        private IFileTransferService? _owner = owner;

        private Stream Inner => _inner ?? throw new ObjectDisposedException(nameof(OwnedFileTransferStream));

        public override bool CanRead => _inner?.CanRead ?? false;
        public override bool CanSeek => _inner?.CanSeek ?? false;
        public override bool CanWrite => false;
        public override long Length => Inner.Length;
        public override long Position
        {
            get => Inner.Position;
            set => Inner.Position = value;
        }

        public override void Flush() => Inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => Inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => Inner.Seek(offset, origin);
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Interlocked.Exchange(ref _inner, null)?.Dispose();
                var service = Interlocked.Exchange(ref _owner, null);
                if (service != null)
                    DisposeFileTransferService(service);
            }

            base.Dispose(disposing);
        }
    }
}

public class PathSegment
{
    public string Label { get; set; } = "";
    public string FullPath { get; set; } = "";
}

public sealed class SftpPathSuggestionItem(
    string name,
    string completionPath,
    string icon,
    bool isDirectory)
{
    public string Name { get; } = name;
    public string CompletionPath { get; } = completionPath;
    public string Icon { get; } = icon;
    public bool IsDirectory { get; } = isDirectory;
    public string TypeText => IsDirectory ? "目录" : "文件";
}
