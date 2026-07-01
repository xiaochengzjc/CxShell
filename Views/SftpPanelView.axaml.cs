using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AtomUI.Theme.Styling;
using CxShell.Services;
using CxShell.ViewModels;
using AtomButton = AtomUI.Desktop.Controls.Button;
using AtomContextMenu = AtomUI.Desktop.Controls.ContextMenu;
using AtomDataGrid = AtomUI.Desktop.Controls.DataGrid;
using AtomLineEdit = AtomUI.Desktop.Controls.LineEdit;
using AtomMenuItem = AtomUI.Desktop.Controls.MenuItem;
using AtomMenuSeparator = AtomUI.Desktop.Controls.MenuSeparator;
using AtomTextBox = AtomUI.Desktop.Controls.TextBox;
using AtomWindow = AtomUI.Desktop.Controls.Window;

namespace CxShell.Views;

public partial class SftpPanelView : UserControl
{
    public static readonly StyledProperty<bool> IsPanelCloseButtonVisibleProperty =
        AvaloniaProperty.Register<SftpPanelView, bool>(nameof(IsPanelCloseButtonVisible));

    public static readonly StyledProperty<ICommand?> PanelCloseCommandProperty =
        AvaloniaProperty.Register<SftpPanelView, ICommand?>(nameof(PanelCloseCommand));

    private SftpViewModel? _attachedViewModel;
    private Models.SftpFileItem? _dragSourceItem;
    private Models.SftpFileItem? _selectionAnchorItem;
    private PointerPressedEventArgs? _dragStartEventArgs;
    private Avalonia.Point _dragStartPoint;
    private bool _isDraggingFileItem;
    private bool _isHandlingDropUpload;
    private bool _doubleTapHandlerAttached;
    private bool _isFileGridColumnUpdateQueued;
    private readonly Dictionary<string, RemoteFileEditorWindow> _remoteFileEditorWindows = new(StringComparer.Ordinal);
    private const double DragStartDistance = 6;
    private const double FileNameColumnMinWidth = 100;
    private const double FileSizeColumnMinWidth = 82;
    private const double FileModifiedColumnMinWidth = 118;
    private const double FileNameColumnWeight = 0.38;
    private const double FileSizeColumnWeight = 0.20;

    public bool IsPanelCloseButtonVisible
    {
        get => GetValue(IsPanelCloseButtonVisibleProperty);
        set => SetValue(IsPanelCloseButtonVisibleProperty, value);
    }

    public ICommand? PanelCloseCommand
    {
        get => GetValue(PanelCloseCommandProperty);
        set => SetValue(PanelCloseCommandProperty, value);
    }

    public SftpPanelView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, OnFileListDragOver, RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(DragDrop.DropEvent, OnFileListDrop, RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(PointerPressedEvent, OnSftpPanelPointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        DragDrop.SetAllowDrop(FileGrid, true);
        FileGrid.AddHandler(DragDrop.DragOverEvent, OnFileListDragOver, RoutingStrategies.Bubble, handledEventsToo: true);
        FileGrid.AddHandler(DragDrop.DropEvent, OnFileListDrop, RoutingStrategies.Bubble, handledEventsToo: true);
        FileGrid.AddHandler(PointerPressedEvent, OnFileGridPointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        FileGrid.GetObservable(BoundsProperty).Subscribe(_ => QueueFileGridColumnWidthUpdate());
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        AttachViewModel(DataContext as SftpViewModel);
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);

        AttachViewModel(DataContext as SftpViewModel);

        if (!_doubleTapHandlerAttached)
        {
            this.AddHandler(DoubleTappedEvent, OnFileDoubleTapped, RoutingStrategies.Bubble);
            _doubleTapHandlerAttached = true;
        }

        QueueFileGridColumnWidthUpdate();
    }

    private void QueueFileGridColumnWidthUpdate()
    {
        if (_isFileGridColumnUpdateQueued)
            return;

        _isFileGridColumnUpdateQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _isFileGridColumnUpdateQueued = false;
            UpdateFileGridColumnWidths();
        }, DispatcherPriority.Render);
    }

    private void UpdateFileGridColumnWidths()
    {
        if (FileGrid.Columns.Count < 3 || FileGrid.Bounds.Width <= 0)
            return;

        var minimumWidth = FileNameColumnMinWidth + FileSizeColumnMinWidth + FileModifiedColumnMinWidth;
        var availableWidth = Math.Max(minimumWidth, FileGrid.Bounds.Width - 2);
        var extraWidth = availableWidth - minimumWidth;
        var nameWidth = FileNameColumnMinWidth + Math.Round(extraWidth * FileNameColumnWeight);
        var sizeWidth = FileSizeColumnMinWidth + Math.Round(extraWidth * FileSizeColumnWeight);

        FileGrid.Columns[0].Width = new AtomUI.Desktop.Controls.DataGridLength(nameWidth);
        FileGrid.Columns[1].Width = new AtomUI.Desktop.Controls.DataGridLength(sizeWidth);
        FileGrid.Columns[2].Width = new AtomUI.Desktop.Controls.DataGridLength(availableWidth - nameWidth - sizeWidth);
    }

    private void AttachViewModel(SftpViewModel? viewModel)
    {
        if (_attachedViewModel == viewModel)
        {
            if (viewModel != null)
                InjectDelegates(viewModel);
            return;
        }

        if (_attachedViewModel != null)
        {
            _attachedViewModel.PropertyChanged -= OnVmPropertyChanged;
            _attachedViewModel.TryActivateRemoteFileEditorAsync = null;
            _attachedViewModel.ShowRemoteFileEditorAsync = null;
        }

        _attachedViewModel = viewModel;

        if (viewModel != null)
        {
            viewModel.PropertyChanged += OnVmPropertyChanged;
            InjectDelegates(viewModel);
        }
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_attachedViewModel == null)
            return;

        if (e.PropertyName == nameof(SftpViewModel.IsCreatingDirectory) && _attachedViewModel.IsCreatingDirectory)
        {
            Dispatcher.UIThread.Post(() =>
            {
                var input = this.FindControl<AtomTextBox>("NewDirInput");
                if (input != null)
                {
                    input.Foreground = new SolidColorBrush(ThemeTokenColorHelper.GetColor(SharedTokenKind.ColorText, Color.Parse("#000000")));
                    input.Background = new SolidColorBrush(ThemeTokenColorHelper.GetColor(SharedTokenKind.ColorBgContainer, Color.Parse("#FFFFFF")));
                    input.CaretBrush = new SolidColorBrush(ThemeTokenColorHelper.GetColor(SharedTokenKind.ColorText, Color.Parse("#000000")));
                    input.Text = "\u65b0\u76ee\u5f55";
                    input.Focus();
                    input.SelectAll();
                }
            }, DispatcherPriority.Render);
        }

        if (e.PropertyName == nameof(SftpViewModel.RenamingItem) && _attachedViewModel.RenamingItem != null)
        {
            var targetItem = _attachedViewModel.RenamingItem;
            Dispatcher.UIThread.Post(() =>
            {
                var tb = FindRenameTextBox(this, targetItem);
                if (tb != null)
                {
                    tb.Foreground = new SolidColorBrush(ThemeTokenColorHelper.GetColor(SharedTokenKind.ColorText, Color.Parse("#000000")));
                    tb.Background = new SolidColorBrush(ThemeTokenColorHelper.GetColor(SharedTokenKind.ColorBgContainer, Color.Parse("#FFFFFF")));
                    tb.CaretBrush = new SolidColorBrush(ThemeTokenColorHelper.GetColor(SharedTokenKind.ColorText, Color.Parse("#000000")));
                    tb.Text = targetItem.Name;
                    tb.Focus();
                    tb.SelectAll();
                }
            }, DispatcherPriority.Render);
        }
    }

    private static AtomTextBox? FindRenameTextBox(Avalonia.Visual root, Models.SftpFileItem target)
    {
        foreach (var child in root.GetVisualChildren())
        {
            if (child is AtomTextBox tb && tb.Tag == target)
                return tb;

            var found = FindRenameTextBox(child, target);
            if (found != null)
                return found;
        }

        return null;
    }

    public void OnRenameInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (_attachedViewModel == null || sender is not AtomTextBox tb)
            return;

        if (tb.Tag is not Models.SftpFileItem item)
            return;

        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            ConfirmRenameFromTextBox(tb, item);
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            _attachedViewModel.CancelRenameCommand.Execute(item);
        }
    }

    public void OnRenameInputLostFocus(object? sender, RoutedEventArgs e)
    {
        if (_attachedViewModel == null || sender is not AtomTextBox tb)
            return;

        if (tb.Tag is not Models.SftpFileItem item || !item.IsRenaming)
            return;

        ConfirmRenameFromTextBox(tb, item);
    }

    private void ConfirmRenameFromTextBox(AtomTextBox tb, Models.SftpFileItem item)
    {
        if (_attachedViewModel == null || !item.IsRenaming)
            return;

        item.RenamingText = tb.Text ?? item.Name;
        if (_attachedViewModel.ConfirmRenameCommand is CommunityToolkit.Mvvm.Input.IAsyncRelayCommand asyncCmd)
            _ = asyncCmd.ExecuteAsync(item);
    }

    private void ConfirmActiveRename()
    {
        if (_attachedViewModel?.RenamingItem is not { IsRenaming: true } item)
            return;

        var tb = FindRenameTextBox(this, item);
        if (tb != null)
            ConfirmRenameFromTextBox(tb, item);
        else if (_attachedViewModel.ConfirmRenameCommand is CommunityToolkit.Mvvm.Input.IAsyncRelayCommand asyncCmd)
            _ = asyncCmd.ExecuteAsync(item);
    }

    private static bool IsInsideRenameTextBox(Avalonia.Visual? source, Models.SftpFileItem item)
    {
        var visual = source;
        while (visual != null)
        {
            if (visual is AtomTextBox tb && tb.Tag == item)
                return true;

            visual = visual.GetVisualParent() as Avalonia.Visual;
        }

        return false;
    }

    public void OnNewDirInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (_attachedViewModel == null)
            return;

        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            var input = this.FindControl<AtomTextBox>("NewDirInput");
            if (input != null)
                _attachedViewModel.NewDirectoryName = input.Text ?? "\u65b0\u76ee\u5f55";

            _ = _attachedViewModel.ConfirmCreateDirectoryCommand.ExecuteAsync(null);
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            _attachedViewModel.CancelCreateDirectoryCommand.Execute(null);
        }
    }

    public void OnNewDirInputLostFocus(object? sender, RoutedEventArgs e)
    {
        if (_attachedViewModel == null || !_attachedViewModel.IsCreatingDirectory)
            return;

        var input = this.FindControl<AtomTextBox>("NewDirInput");
        if (input != null)
            _attachedViewModel.NewDirectoryName = input.Text ?? "\u65b0\u76ee\u5f55";

        _ = _attachedViewModel.ConfirmCreateDirectoryCommand.ExecuteAsync(null);
    }

    public void OnRemotePathInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (_attachedViewModel == null || e.Key != Key.Enter)
            return;

        e.Handled = true;
        if (sender is AtomLineEdit input)
            _attachedViewModel.PathInput = input.Text ?? _attachedViewModel.CurrentPath;

        _ = _attachedViewModel.NavigateToTypedPathCommand.ExecuteAsync(null);
    }

    public void OnFileListDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = !_isDraggingFileItem &&
            _attachedViewModel is { IsConnected: true } &&
            e.DataTransfer.Contains(DataFormat.File)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    public async void OnFileListDrop(object? sender, DragEventArgs e)
    {
        e.Handled = true;

        if (_isDraggingFileItem || _isHandlingDropUpload || _attachedViewModel is not { IsConnected: true } vm)
            return;

        try
        {
            _isHandlingDropUpload = true;
            var items = e.DataTransfer.TryGetFiles();
            if (items == null)
                return;

            var paths = new List<string>();
            foreach (var item in items)
            {
                var localPath = item.Path.LocalPath;
                if (!string.IsNullOrWhiteSpace(localPath))
                    paths.Add(localPath);
                item.Dispose();
            }

            if (paths.Count > 0)
                await vm.UploadDroppedPathsAsync(paths);
            else
                await vm.ShowDropUploadErrorAsync("No local file or folder path was found in the drop data.");
        }
        catch (Exception ex)
        {
            await vm.ShowDropUploadErrorAsync(ex.Message);
        }
        finally
        {
            _isHandlingDropUpload = false;
        }
    }

    private void OnFileGridSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_attachedViewModel == null || sender is not AtomDataGrid grid)
            return;

        var selected = GetSelectedFiles(grid).ToList();
        if (selected.Count == 0)
            _selectionAnchorItem = null;

        _attachedViewModel.SetSelectedFiles(selected);
    }

    private void OnSftpPanelPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_attachedViewModel?.RenamingItem is not { IsRenaming: true } renamingItem)
            return;

        if (IsInsideRenameTextBox(e.Source as Avalonia.Visual, renamingItem))
            return;

        ConfirmActiveRename();
    }

    private void OnFileGridPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_attachedViewModel == null || sender is not AtomDataGrid grid)
            return;

        if (_attachedViewModel.RenamingItem is { IsRenaming: true } renamingItem &&
            !IsInsideRenameTextBox(e.Source as Avalonia.Visual, renamingItem))
        {
            ConfirmActiveRename();
        }

        var item = TryGetItemFromVisual(e.Source as Avalonia.Visual);
        if (item == null || item.IsRenaming)
            return;

        var point = e.GetCurrentPoint(grid);
        if (point.Properties.IsRightButtonPressed)
        {
            var selectedFiles = GetSelectedFiles(grid).ToList();
            if (selectedFiles.Contains(item))
            {
                _attachedViewModel.SetSelectedFiles(selectedFiles);
            }
            else
            {
                grid.SelectedItem = item;
                _attachedViewModel.SetSelectedFiles([item]);
            }

            e.Handled = true;
            Dispatcher.UIThread.Post(() => ShowFileContextMenu(grid, item, _attachedViewModel), DispatcherPriority.Input);
            ClearFileDragState();
            return;
        }

        if (point.Properties.IsLeftButtonPressed)
        {
            ApplyFileSelection(grid, item, e.KeyModifiers);
            _dragSourceItem = item;
            _dragStartEventArgs = e;
            _dragStartPoint = e.GetPosition(this);
            e.Handled = true;
        }
    }

    private void OnFileGridPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_isDraggingFileItem)
            ClearFileDragState();
    }

    private void ApplyFileSelection(AtomDataGrid grid, Models.SftpFileItem item, KeyModifiers modifiers)
    {
        if (_attachedViewModel == null)
            return;

        var files = _attachedViewModel.Files.ToList();
        var selected = GetSelectedFiles(grid).ToList();
        var hasCtrl = modifiers.HasFlag(KeyModifiers.Control);
        var hasShift = modifiers.HasFlag(KeyModifiers.Shift);

        if (hasShift && _selectionAnchorItem != null)
        {
            var anchorIndex = files.IndexOf(_selectionAnchorItem);
            var itemIndex = files.IndexOf(item);
            if (anchorIndex >= 0 && itemIndex >= 0)
            {
                var start = Math.Min(anchorIndex, itemIndex);
                var count = Math.Abs(anchorIndex - itemIndex) + 1;
                var range = files.Skip(start).Take(count).ToList();
                selected = hasCtrl
                    ? selected.Concat(range).Distinct().OrderBy(files.IndexOf).ToList()
                    : range;
            }
            else
            {
                selected = [item];
                _selectionAnchorItem = item;
            }
        }
        else if (hasCtrl)
        {
            if (selected.Contains(item))
                selected.Remove(item);
            else
                selected.Add(item);

            selected = selected.OrderBy(files.IndexOf).ToList();
            _selectionAnchorItem = item;
        }
        else
        {
            selected = [item];
            _selectionAnchorItem = item;
        }

        SetGridSelectedItems(grid, selected);
        _attachedViewModel.SetSelectedFiles(selected);
    }

    private async void OnFileGridPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_isDraggingFileItem ||
            _dragSourceItem == null ||
            _dragStartEventArgs == null ||
            _attachedViewModel == null)
        {
            return;
        }

        var point = e.GetCurrentPoint(this);
        if (!point.Properties.IsLeftButtonPressed)
        {
            ClearFileDragState();
            return;
        }

        var current = e.GetPosition(this);
        if (Math.Abs(current.X - _dragStartPoint.X) < DragStartDistance &&
            Math.Abs(current.Y - _dragStartPoint.Y) < DragStartDistance)
        {
            return;
        }

        _isDraggingFileItem = true;
        var dragEventArgs = _dragStartEventArgs;
        var item = _dragSourceItem;

        try
        {
            if (_attachedViewModel.CanStreamDragOut(item))
            {
                var dragFiles = await _attachedViewModel.CreateVirtualDragFilesAsync(item);
                if (dragFiles.Count > 0)
                {
                    System.Console.WriteLine($"[SFTP] Starting virtual stream drag-out: {item.FullPath}, files={dragFiles.Count}");
                    if (PlatformServices.TryStartVirtualFileDragOut(dragFiles, out var virtualEffect, out var virtualError))
                    {
                        System.Console.WriteLine($"[SFTP] Virtual stream drag-out finished: {virtualEffect}");
                        return;
                    }

                    if (!string.IsNullOrWhiteSpace(virtualError))
                        System.Console.WriteLine($"[SFTP] Virtual stream drag-out unavailable: {virtualError}");
                }
            }

            var topLevel = TopLevel.GetTopLevel(this);
            var data = new DataTransfer();
            if (data.Items is IList<DataTransferItem> items)
            {
                var dataItem = new DataTransferItem();
                dataItem.Set(DataFormat.File, () =>
                {
                    System.Console.WriteLine($"[SFTP] Exporting remote item for drag: {item.FullPath}");
                    var localPath = _attachedViewModel.ExportItemForDragBlocking(item);
                    if (string.IsNullOrWhiteSpace(localPath))
                        return null;

                    return GetStorageItemForDrag(topLevel, localPath);
                });
                items.Add(dataItem);
            }

            System.Console.WriteLine($"[SFTP] Starting drag-out: {item.FullPath}");
            var effect = await DragDrop.DoDragDropAsync(dragEventArgs, data, DragDropEffects.Copy);
            System.Console.WriteLine($"[SFTP] Drag-out finished: {effect}");
        }
        finally
        {
            ClearFileDragState();
            _isDraggingFileItem = false;
        }
    }

    private void OnFileGridKeyDown(object? sender, KeyEventArgs e)
    {
        if (_attachedViewModel == null)
            return;

        if (e.Key == Key.Delete)
        {
            e.Handled = true;
            if (_attachedViewModel.DeleteCommand is CommunityToolkit.Mvvm.Input.IAsyncRelayCommand asyncCmd)
                _ = asyncCmd.ExecuteAsync(null);
            else
                _attachedViewModel.DeleteCommand.Execute(null);
        }
    }

    private static Models.SftpFileItem? TryGetItemFromVisual(Avalonia.Visual? visual)
    {
        while (visual != null)
        {
            if (visual.DataContext is Models.SftpFileItem item)
                return item;

            visual = visual.GetVisualParent() as Avalonia.Visual;
        }

        return null;
    }

    private static IEnumerable<Models.SftpFileItem> GetSelectedFiles(AtomDataGrid grid)
    {
        var selectedItemsProperty = grid.GetType().GetProperty("SelectedItems");
        if (selectedItemsProperty?.GetValue(grid) is IEnumerable selectedItems)
            return selectedItems.OfType<Models.SftpFileItem>().ToList();

        return [];
    }

    private static void SetGridSelectedItems(AtomDataGrid grid, IReadOnlyList<Models.SftpFileItem> selected)
    {
        var selectedItems = grid.SelectedItems;
        selectedItems.Clear();
        foreach (var item in selected)
        {
            if (!selectedItems.Contains(item))
                selectedItems.Add(item);
        }
    }

    private static Avalonia.Platform.Storage.IStorageItem? GetStorageItemForDrag(TopLevel? topLevel, string localPath)
    {
        if (topLevel == null)
            return null;

        return System.IO.Directory.Exists(localPath)
            ? topLevel.StorageProvider.TryGetFolderFromPathAsync(localPath).GetAwaiter().GetResult()
            : topLevel.StorageProvider.TryGetFileFromPathAsync(localPath).GetAwaiter().GetResult();
    }

    private void ClearFileDragState()
    {
        _dragSourceItem = null;
        _dragStartEventArgs = null;
    }

    private void ShowFileContextMenu(Control anchor, Models.SftpFileItem item, SftpViewModel vm)
    {
        var isBatchSelection = vm.SelectedFiles.Contains(item) && vm.SelectedFileCount > 1;
        var menu = new AtomContextMenu
        {
            PlacementTarget = anchor,
            Placement = PlacementMode.Pointer
        };

        void AddItem(string text, Action action)
        {
            var itemControl = new AtomMenuItem
            {
                Header = text
            };
            itemControl.Click += (_, _) =>
            {
                menu.Close();
                action();
            };
            menu.Items.Add(itemControl);
        }

        if (!isBatchSelection && !item.IsDirectory)
        {
            AddItem(vm.EditText, () => _ = vm.EditRemoteFileCommand.ExecuteAsync(item));
            AddItem(vm.DownloadText, () => _ = vm.DownloadCommand.ExecuteAsync(null));
            menu.Items.Add(new AtomMenuSeparator());
        }

        if (!isBatchSelection)
            AddItem(vm.RenameText, () => vm.RenameCommand.Execute(null));

        AddItem(vm.DeleteText, () => _ = vm.DeleteCommand.ExecuteAsync(null));
        menu.Open(anchor);
    }

    private void OnFileDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (_attachedViewModel == null)
            return;

        var item = TryGetItemFromVisual(e.Source as Avalonia.Visual);
        if (item != null)
            _ = _attachedViewModel.OpenItemCommand.ExecuteAsync(item);
    }

    private static async Task<bool> ShowConfirmWindow(Window owner, string message)
    {
        return await AtomUiDialogService.ShowConfirmAsync(
            owner,
            "\u786e\u8ba4",
            message,
            "\u786e\u8ba4",
            "\u53d6\u6d88");
    }

    private static async Task<string?> ShowInputWindow(Window owner, string title, string defaultValue)
    {
        string? result = null;
        var dialog = new AtomWindow
        {
            Title = title,
            Width = 360,
            Height = 200,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            ShowInTaskbar = false
        };

        var panel = new StackPanel { Spacing = 12, Margin = new Thickness(20) };
        var label = new TextBlock
        {
            Text = title,
            FontSize = 13,
            Foreground = new SolidColorBrush(ThemeTokenColorHelper.GetColor(SharedTokenKind.ColorTextSecondary, Color.Parse("#666666")))
        };
        var input = new AtomTextBox { Text = defaultValue, FontSize = 13 };
        panel.Children.Add(label);

        dialog.Opened += (_, _) =>
        {
            input.Focus();
            input.SelectAll();
        };

        var btnPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right
        };

        var okBtn = new AtomButton { Content = "\u786e\u8ba4", Width = 70 };
        okBtn.Click += (_, _) => { result = input.Text; dialog.Close(); };
        var cancelBtn = new AtomButton { Content = "\u53d6\u6d88", Width = 70 };
        cancelBtn.Click += (_, _) => dialog.Close();

        input.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                result = input.Text;
                dialog.Close();
            }
        };

        btnPanel.Children.Add(okBtn);
        btnPanel.Children.Add(cancelBtn);
        panel.Children.Add(input);
        panel.Children.Add(btnPanel);
        dialog.Content = panel;

        await dialog.ShowDialog(owner);
        return result;
    }

    private void InjectDelegates(SftpViewModel vm)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null)
            return;

        vm.PickUploadFileAsync = async () =>
        {
            var files = await topLevel.StorageProvider.OpenFilePickerAsync(
                new FilePickerOpenOptions
                {
                    Title = "\u9009\u62e9\u8981\u4e0a\u4f20\u7684\u6587\u4ef6",
                    AllowMultiple = false,
                    SuggestedStartLocation = await GetSuggestedLocalFolderAsync(topLevel, vm)
                });
            var path = files.FirstOrDefault()?.Path.LocalPath;
            UpdateLocalStartDirectory(vm, path);
            return path;
        };

        vm.PickDownloadPathAsync = async (fileName) =>
        {
            var file = await topLevel.StorageProvider.SaveFilePickerAsync(
                new FilePickerSaveOptions
                {
                    Title = "\u4fdd\u5b58\u5230\u672c\u5730",
                    SuggestedFileName = fileName,
                    SuggestedStartLocation = await GetSuggestedLocalFolderAsync(topLevel, vm)
                });
            var path = file?.Path.LocalPath;
            UpdateLocalStartDirectory(vm, path);
            return path;
        };

        vm.ShowConfirmDialogAsync = async (message) =>
        {
            var window = topLevel as Window;
            return window != null && await ShowConfirmWindow(window, message);
        };

        vm.ShowInputDialogAsync = async (title, defaultValue) =>
        {
            var window = topLevel as Window;
            return window == null ? null : await ShowInputWindow(window, title, defaultValue);
        };

        vm.TryActivateRemoteFileEditorAsync = remotePath =>
        {
            var key = GetRemoteFileEditorKey(vm, remotePath);
            if (!_remoteFileEditorWindows.TryGetValue(key, out var existingWindow))
                return Task.FromResult(false);

            ActivateRemoteFileEditor(existingWindow);
            return Task.FromResult(true);
        };

        vm.ShowRemoteFileEditorAsync = async editorVm =>
        {
            var key = GetRemoteFileEditorKey(vm, editorVm.RemotePath);
            if (_remoteFileEditorWindows.TryGetValue(key, out var existingWindow))
            {
                ActivateRemoteFileEditor(existingWindow);
                return;
            }

            var dialog = new RemoteFileEditorWindow
            {
                DataContext = editorVm
            };

            _remoteFileEditorWindows[key] = dialog;
            dialog.Closed += (_, _) =>
            {
                if (_remoteFileEditorWindows.TryGetValue(key, out var current) && ReferenceEquals(current, dialog))
                    _remoteFileEditorWindows.Remove(key);
            };

            if (topLevel is Window owner)
                dialog.Show(owner);
            else
                dialog.Show();

            ActivateRemoteFileEditor(dialog);
            await Task.CompletedTask;
        };
    }

    private static string GetRemoteFileEditorKey(SftpViewModel vm, string remotePath)
    {
        return $"{vm.RemoteEditorConnectionKey}|{remotePath}";
    }

    private static void ActivateRemoteFileEditor(Window window)
    {
        if (window.WindowState == WindowState.Minimized)
            window.WindowState = WindowState.Normal;

        window.Activate();
    }

    private static async Task<IStorageFolder?> GetSuggestedLocalFolderAsync(TopLevel topLevel, SftpViewModel vm)
    {
        if (string.IsNullOrWhiteSpace(vm.LocalStartDirectory) || !System.IO.Directory.Exists(vm.LocalStartDirectory))
            return null;

        return await topLevel.StorageProvider.TryGetFolderFromPathAsync(vm.LocalStartDirectory);
    }

    private static void UpdateLocalStartDirectory(SftpViewModel vm, string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        var directory = System.IO.Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
            vm.LocalStartDirectory = directory;
    }
}
