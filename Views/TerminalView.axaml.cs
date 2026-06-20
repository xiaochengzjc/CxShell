using System;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using ChiXueSsh.ViewModels;

namespace ChiXueSsh.Views;

public partial class TerminalView : UserControl
{
    private TerminalViewModel? _boundVm;
    private Controls.TerminalControl? _terminal;

    public TerminalView()
    {
        InitializeComponent();
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        _terminal = this.FindControl<Controls.TerminalControl>("Terminal");
        TryBind();
    }

    protected override void OnUnloaded(RoutedEventArgs e)
    {
        TerminalContextMenuPopup.Close();
        Unbind();
        _terminal = null;
        base.OnUnloaded(e);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        // Only re-bind if we're already loaded (terminal control exists)
        if (_terminal != null)
        {
            Unbind();
            TryBind();
        }
    }

    private void TryBind()
    {
        if (_terminal == null) return;
        if (DataContext is not TerminalViewModel vm) return;

        _boundVm = vm;

        _terminal.InputReceived += OnInputReceived;
        _terminal.SizeChanged2 += OnSizeChanged;
        _terminal.PointerPressed += OnPointerPressed;
        MenuCopyBtn.Click += OnCopyClick;
        MenuPasteBtn.Click += OnPasteClick;
        vm.BufferChanged += OnBufferChanged;
        vm.BellRequested += OnBellRequested;
        vm.PropertyChanged += OnVmPropertyChanged;
        InjectZmodemDelegates(vm);
        SyncTerminalSize();
        Dispatcher.UIThread.Post(SyncTerminalSize, DispatcherPriority.Loaded);

        // 立即刷新一次
        _terminal.InvalidateVisual();
    }

    private void Unbind()
    {
        if (_terminal != null)
        {
            _terminal.InputReceived -= OnInputReceived;
            _terminal.SizeChanged2 -= OnSizeChanged;
            _terminal.PointerPressed -= OnPointerPressed;
        }
        MenuCopyBtn.Click -= OnCopyClick;
        MenuPasteBtn.Click -= OnPasteClick;

        if (_boundVm != null)
        {
            _boundVm.BufferChanged -= OnBufferChanged;
            _boundVm.BellRequested -= OnBellRequested;
            _boundVm.PropertyChanged -= OnVmPropertyChanged;
            _boundVm.PickZmodemUploadFilesAsync = null;
            _boundVm.PickZmodemDownloadFolderAsync = null;
            _boundVm.PickSessionLogFileAsync = null;
        }

        _boundVm = null;
    }

    private void InjectZmodemDelegates(TerminalViewModel vm)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        vm.PickZmodemUploadFilesAsync = async () =>
        {
            var options = new FilePickerOpenOptions
            {
                Title = "选择要通过 rz 上传的文件",
                AllowMultiple = true
            };
            var startDirectory = vm.ZmodemUploadStartDirectory;
            if (!string.IsNullOrWhiteSpace(startDirectory) && System.IO.Directory.Exists(startDirectory))
                options.SuggestedStartLocation = await topLevel.StorageProvider.TryGetFolderFromPathAsync(startDirectory);

            var files = await topLevel.StorageProvider.OpenFilePickerAsync(options);

            return files
                .Select(file => file.Path.LocalPath)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .ToList();
        };

        vm.PickZmodemDownloadFolderAsync = async () =>
        {
            var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(
                new FolderPickerOpenOptions
                {
                    Title = "选择 sz 下载保存目录",
                    AllowMultiple = false
                });

            return folders.FirstOrDefault()?.Path.LocalPath;
        };

        vm.PickSessionLogFileAsync = async () =>
        {
            var file = await topLevel.StorageProvider.SaveFilePickerAsync(
                new FilePickerSaveOptions
                {
                    Title = "选择日志文件",
                    SuggestedFileName = "session.log",
                    FileTypeChoices =
                    [
                        new FilePickerFileType("Log files")
                        {
                            Patterns = ["*.log", "*.txt", "*.rtf"]
                        },
                        FilePickerFileTypes.All
                    ]
                });

            return file?.Path.LocalPath;
        };
    }

    private void OnInputReceived(string data) => _boundVm?.SendInput(data);
    private void OnSizeChanged(int cols, int rows) => _boundVm?.Resize(cols, rows);

    private void SyncTerminalSize()
    {
        if (_terminal == null || _boundVm == null)
            return;

        if (_boundVm.IsTerminalSizeFixed)
        {
            _boundVm.ApplyConfiguredTerminalSize();
            return;
        }

        _terminal.SyncSizeToBounds();
        _boundVm.Resize(_terminal.Columns, _terminal.Rows);
    }

    private void OnPointerPressed(object? s, Avalonia.Input.PointerPressedEventArgs e)
    {
        _terminal?.Focus();
        if (_terminal == null || !e.GetCurrentPoint(_terminal).Properties.IsRightButtonPressed)
            return;

        TerminalContextMenuPopup.PlacementTarget = _terminal;
        MenuCopyBtn.IsEnabled = _terminal.HasSelection;
        TerminalContextMenuPopup.IsOpen = true;
        e.Handled = true;
    }

    private async void OnCopyClick(object? sender, RoutedEventArgs e)
    {
        TerminalContextMenuPopup.Close();
        if (_terminal != null)
        {
            await _terminal.CopySelectionAsync();
            _terminal.Focus();
        }
    }

    private async void OnPasteClick(object? sender, RoutedEventArgs e)
    {
        TerminalContextMenuPopup.Close();
        if (_terminal != null)
        {
            await _terminal.PasteAsync();
            _terminal.Focus();
        }
    }
    private void OnBufferChanged() => _terminal?.InvalidateVisual();

    private void OnBellRequested()
    {
        if (_boundVm?.FlashInactiveWindowOnBell != true)
            return;

        if (TopLevel.GetTopLevel(this) is not Window window || window.IsActive)
            return;

        FlashWindow(window);
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TerminalViewModel.Buffer))
        {
            _terminal?.InvalidateVisual();
        }
    }

    private static void FlashWindow(Window window)
    {
        if (!OperatingSystem.IsWindows())
            return;

        var handle = window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (handle == IntPtr.Zero)
            return;

        var info = new FlashWindowInfo
        {
            cbSize = Convert.ToUInt32(Marshal.SizeOf<FlashWindowInfo>()),
            hwnd = handle,
            dwFlags = 0x00000003 | 0x0000000C,
            uCount = 3,
            dwTimeout = 0
        };
        FlashWindowEx(ref info);
    }

    [DllImport("user32.dll")]
    private static extern bool FlashWindowEx(ref FlashWindowInfo pwfi);

    [StructLayout(LayoutKind.Sequential)]
    private struct FlashWindowInfo
    {
        public uint cbSize;
        public IntPtr hwnd;
        public uint dwFlags;
        public uint uCount;
        public uint dwTimeout;
    }
}
