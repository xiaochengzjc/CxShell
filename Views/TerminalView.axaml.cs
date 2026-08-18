using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CxShell.ViewModels;
using AtomContextMenu = AtomUI.Desktop.Controls.ContextMenu;
using AtomMenuItem = AtomUI.Desktop.Controls.MenuItem;
using AtomMenuSeparator = AtomUI.Desktop.Controls.MenuSeparator;

namespace CxShell.Views;

public partial class TerminalView : UserControl
{
    private TerminalViewModel? _boundVm;
    private Controls.TerminalControl? _terminal;
    private int _suppressRemoteResizeDepth;
    private int _suppressNextRemoteResizeEvents;
    private int _remoteResizeVersion;

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
        _suppressNextRemoteResizeEvents = 2;

        _terminal.InputReceived += OnInputReceived;
        _terminal.SizeChanged2 += OnSizeChanged;
        _terminal.PointerPressed += OnPointerPressed;
        _terminal.KeyDown += OnTerminalKeyDown;
        _terminal.SearchChanged += OnTerminalSearchChanged;
        vm.BufferChanged += OnBufferChanged;
        vm.BellRequested += OnBellRequested;
        vm.PropertyChanged += OnVmPropertyChanged;
        InjectZmodemDelegates(vm);
        SyncTerminalSize(notifyRemote: false);
        Dispatcher.UIThread.Post(() => SyncTerminalSize(notifyRemote: false), DispatcherPriority.Loaded);
        Dispatcher.UIThread.Post(() => SyncTerminalSize(notifyRemote: true), DispatcherPriority.Background);
        SearchBox.TextChanged += OnSearchBoxTextChanged;

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
            _terminal.KeyDown -= OnTerminalKeyDown;
            _terminal.SearchChanged -= OnTerminalSearchChanged;
            _terminal.ClearSearch();
        }

        SearchBox.TextChanged -= OnSearchBoxTextChanged;
        if (_boundVm != null)
        {
            _boundVm.BufferChanged -= OnBufferChanged;
            _boundVm.BellRequested -= OnBellRequested;
            _boundVm.PropertyChanged -= OnVmPropertyChanged;
            _boundVm.PickZmodemUploadFilesAsync = null;
            _boundVm.PickZmodemDownloadFolderAsync = null;
            _boundVm.PickSessionLogFileAsync = null;
            _boundVm.SetClipboardTextAsync = null;
        }

        _boundVm = null;
        SearchBar.IsVisible = false;
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

        vm.SetClipboardTextAsync = async text =>
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard != null)
                await clipboard.SetTextAsync(text);
        };
    }

    private void OnInputReceived(string data)
    {
        var mainVm = GetMainWindowViewModel();
        if (mainVm != null && _boundVm != null)
        {
            mainVm.SendTerminalInput(_boundVm, data);
            return;
        }

        _boundVm?.SendInput(data);
    }
    private void OnSizeChanged(int cols, int rows)
    {
        if (!IsActuallyVisible())
            return;

        var notifyRemote = _suppressRemoteResizeDepth == 0 && _suppressNextRemoteResizeEvents <= 0;
        if (_suppressNextRemoteResizeEvents > 0)
            _suppressNextRemoteResizeEvents--;

        _boundVm?.Resize(cols, rows, notifyRemote: false);
        if (notifyRemote)
            ScheduleRemoteResize(cols, rows);
    }

    private void SyncTerminalSize(bool notifyRemote)
    {
        if (_terminal == null || _boundVm == null)
            return;

        if (!IsActuallyVisible())
            return;

        if (_boundVm.IsTerminalSizeFixed)
        {
            _boundVm.ApplyConfiguredTerminalSize();
            return;
        }

        if (!notifyRemote)
            _suppressRemoteResizeDepth++;

        try
        {
            _terminal.SyncSizeToBounds();
            _boundVm.Resize(_terminal.Columns, _terminal.Rows, notifyRemote);
        }
        finally
        {
            if (!notifyRemote)
                _suppressRemoteResizeDepth--;
        }
    }

    private void ScheduleRemoteResize(int cols, int rows)
    {
        var version = ++_remoteResizeVersion;
        _ = Task.Run(async () =>
        {
            await Task.Delay(150);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (version != _remoteResizeVersion || _boundVm == null || !IsActuallyVisible())
                    return;

                _boundVm.Resize(cols, rows, notifyRemote: true);
            }, DispatcherPriority.Background);
        });
    }

    private bool IsActuallyVisible()
    {
        if (!IsLoaded || _terminal == null)
            return false;

        Avalonia.Controls.Control? current = this;
        while (current != null)
        {
            if (!current.IsVisible)
                return false;

            current = current.Parent as Avalonia.Controls.Control;
        }

        return true;
    }

    private void OnPointerPressed(object? s, Avalonia.Input.PointerPressedEventArgs e)
    {
        _terminal?.Focus();
        if (_terminal == null || !e.GetCurrentPoint(_terminal).Properties.IsRightButtonPressed)
            return;

        ShowTerminalContextMenu(_terminal);
        e.Handled = true;
    }

    private void OnTerminalKeyDown(object? sender, KeyEventArgs e)
    {
        var commandModifier = e.KeyModifiers.HasFlag(KeyModifiers.Control) ||
                              e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        if (commandModifier && e.Key == Key.F)
        {
            OpenSearchBar();
            e.Handled = true;
            return;
        }

        if (SearchBar.IsVisible && e.Key == Key.Escape)
        {
            CloseSearchBar();
            e.Handled = true;
        }
    }

    private void OpenSearchBar()
    {
        SearchBar.IsVisible = true;
        SearchBox.Focus();
        SearchBox.SelectAll();
        UpdateSearchQuery();
    }

    private void CloseSearchBar()
    {
        SearchBar.IsVisible = false;
        _terminal?.ClearSearch();
        _terminal?.Focus();
    }

    private void OnSearchBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            _terminal?.MoveToSearchMatch(e.KeyModifiers.HasFlag(KeyModifiers.Shift));
            UpdateSearchStatus();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            CloseSearchBar();
            e.Handled = true;
        }
    }

    private void OnSearchBoxTextChanged(object? sender, EventArgs e)
    {
        UpdateSearchQuery();
    }

    private void OnSearchPreviousClick(object? sender, RoutedEventArgs e)
    {
        _terminal?.MoveToSearchMatch(backwards: true);
        UpdateSearchStatus();
        SearchBox.Focus();
    }

    private void OnSearchNextClick(object? sender, RoutedEventArgs e)
    {
        _terminal?.MoveToSearchMatch(backwards: false);
        UpdateSearchStatus();
        SearchBox.Focus();
    }

    private void OnSearchCloseClick(object? sender, RoutedEventArgs e)
    {
        CloseSearchBar();
    }

    private void UpdateSearchQuery()
    {
        if (!SearchBar.IsVisible || _terminal == null)
            return;

        _terminal.SetSearchQuery(SearchBox.Text);
        if (!string.IsNullOrEmpty(SearchBox.Text))
            _terminal.MoveToSearchMatch(backwards: false);

        UpdateSearchStatus();
    }

    private void OnTerminalSearchChanged() => UpdateSearchStatus();

    private void UpdateSearchStatus()
    {
        if (_terminal == null)
        {
            SearchStatus.Text = "0/0";
            return;
        }

        var count = _terminal.SearchMatchCount;
        var current = _terminal.SearchMatchIndex;
        SearchStatus.Text = count == 0
            ? "0/0"
            : $"{(current < 0 ? 0 : current + 1)}/{count}";
    }

    private void ShowTerminalContextMenu(Controls.TerminalControl terminal)
    {
        var menu = new AtomContextMenu
        {
            Placement = PlacementMode.Pointer,
            PlacementTarget = terminal
        };

        var copyItem = new AtomMenuItem
        {
            Header = _boundVm?.CopyText ?? "Copy",
            IsEnabled = terminal.HasSelection
        };
        copyItem.Click += async (_, _) =>
        {
            menu.Close();
            await CopyTerminalSelectionAsync();
        };

        var pasteItem = new AtomMenuItem
        {
            Header = _boundVm?.PasteText ?? "Paste"
        };
        pasteItem.Click += async (_, _) =>
        {
            menu.Close();
            await PasteTerminalTextAsync();
        };

        menu.Items.Add(copyItem);
        menu.Items.Add(pasteItem);
        var exportItem = new AtomMenuItem
        {
            Header = _boundVm?.ExportText ?? "Export terminal output",
            IsEnabled = terminal.TerminalBuffer != null
        };
        exportItem.Click += async (_, _) =>
        {
            menu.Close();
            await ExportTerminalOutputAsync();
        };

        menu.Items.Add(exportItem);
        menu.Items.Add(new AtomMenuSeparator());
        AddKeyboardBroadcastMenuItems(menu);
        menu.Items.Add(new AtomMenuSeparator());
        AddQuickCommandMenuItems(menu);
        menu.Open(terminal);
    }

    private void AddKeyboardBroadcastMenuItems(AtomContextMenu menu)
    {
        var mainVm = GetMainWindowViewModel();
        if (mainVm == null)
            return;

        var broadcastMenu = new AtomMenuItem
        {
            Header = mainVm.KeyboardBroadcastMenuText
        };

        AddKeyboardBroadcastTargetItem(
            menu,
            broadcastMenu,
            mainVm,
            KeyboardBroadcastTarget.CurrentSession,
            mainVm.KeyboardBroadcastCurrentText);
        AddKeyboardBroadcastTargetItem(
            menu,
            broadcastMenu,
            mainVm,
            KeyboardBroadcastTarget.AllSessions,
            mainVm.KeyboardBroadcastAllText);
        AddKeyboardBroadcastTargetItem(
            menu,
            broadcastMenu,
            mainVm,
            KeyboardBroadcastTarget.ConnectedSessions,
            mainVm.KeyboardBroadcastConnectedText);
        AddKeyboardBroadcastTargetItem(
            menu,
            broadcastMenu,
            mainVm,
            KeyboardBroadcastTarget.CurrentTabGroup,
            mainVm.KeyboardBroadcastCurrentGroupText);

        menu.Items.Add(broadcastMenu);
    }

    private void AddKeyboardBroadcastTargetItem(
        AtomContextMenu menu,
        AtomMenuItem parent,
        MainWindowViewModel mainVm,
        KeyboardBroadcastTarget target,
        string text)
    {
        var item = new AtomMenuItem
        {
            Header = $"{(mainVm.KeyboardBroadcastTarget == target ? "[x]" : "[ ]")} {text}"
        };
        item.Click += (_, _) =>
        {
            menu.Close();
            mainVm.SetKeyboardBroadcastTargetCommand.Execute(target);
            _terminal?.Focus();
        };
        parent.Items.Add(item);
    }

    private void AddQuickCommandMenuItems(AtomContextMenu menu)
    {
        if (_boundVm == null)
            return;

        var mainVm = GetMainWindowViewModel();
        var sourceTab = mainVm?.Tabs.FirstOrDefault(tab => ReferenceEquals(tab.Terminal, _boundVm));
        var commands = mainVm != null && sourceTab != null
            ? mainVm.GetQuickCommands(sourceTab)
            : _boundVm.GetQuickCommands();
        var quickMenu = new AtomMenuItem
        {
            Header = _boundVm.QuickCommandsText,
            IsEnabled = commands.Count > 0
        };

        if (commands.Count == 0)
        {
            quickMenu.Items.Add(new AtomMenuItem
            {
                Header = _boundVm.QuickCommandsEmptyText,
                IsEnabled = false
            });
        }
        else
        {
            foreach (var command in commands)
            {
                var item = new AtomMenuItem
                {
                    Header = command.Name
                };
                item.Click += (_, _) =>
                {
                    menu.Close();
                    if (mainVm != null && sourceTab != null)
                        mainVm.ExecuteQuickCommand(sourceTab, command);
                    else
                        _boundVm.ExecuteQuickCommand(command);
                    _terminal?.Focus();
                };
                quickMenu.Items.Add(item);
            }
        }

        menu.Items.Add(quickMenu);
    }

    private MainWindowViewModel? GetMainWindowViewModel()
    {
        return TopLevel.GetTopLevel(this) is Window { DataContext: MainWindowViewModel vm }
            ? vm
            : null;
    }

    private async Task CopyTerminalSelectionAsync()
    {
        if (_terminal != null)
        {
            await _terminal.CopySelectionAsync();
            _terminal.Focus();
        }
    }

    private async Task PasteTerminalTextAsync()
    {
        if (_terminal != null)
        {
            await _terminal.PasteAsync();
            _terminal.Focus();
        }
    }

    private async Task ExportTerminalOutputAsync()
    {
        if (_terminal == null)
            return;

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null)
            return;

        var text = _terminal.GetTextForExport();
        var file = await topLevel.StorageProvider.SaveFilePickerAsync(
            new FilePickerSaveOptions
            {
                Title = _boundVm?.ExportText ?? "Export terminal output",
                SuggestedFileName = $"terminal-{DateTime.Now:yyyyMMdd-HHmmss}.txt",
                FileTypeChoices =
                [
                    new FilePickerFileType("Text files") { Patterns = ["*.txt", "*.log"] },
                    FilePickerFileTypes.All
                ]
            });

        if (file == null || string.IsNullOrWhiteSpace(file.Path.LocalPath))
            return;

        try
        {
            await File.WriteAllTextAsync(file.Path.LocalPath, text, new UTF8Encoding(false));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error exporting terminal output: {ex.Message}");
        }
        finally
        {
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
        else if (e.PropertyName == nameof(TerminalViewModel.IsConnected) &&
                 _boundVm?.IsConnected == true)
        {
            Dispatcher.UIThread.Post(
                () => SyncTerminalSize(notifyRemote: true),
                DispatcherPriority.Background);
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
