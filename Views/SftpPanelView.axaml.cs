using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AtomUI.Theme.Styling;
using ChiXueSsh.Services;
using ChiXueSsh.ViewModels;
using AtomButton = AtomUI.Desktop.Controls.Button;
using AtomLineEdit = AtomUI.Desktop.Controls.LineEdit;
using AtomPopup = AtomUI.Desktop.Controls.Popup;
using AtomTextBox = AtomUI.Desktop.Controls.TextBox;
using AtomWindow = AtomUI.Desktop.Controls.Window;

namespace ChiXueSsh.Views;

public partial class SftpPanelView : UserControl
{
    private Border? _dragSourceBorder;
    private Models.SftpFileItem? _dragSourceItem;
    private PointerPressedEventArgs? _dragStartEventArgs;
    private Avalonia.Point _dragStartPoint;
    private bool _isDraggingFileItem;
    private const double DragStartDistance = 6;

    public SftpPanelView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (DataContext is SftpViewModel vm)
        {
            InjectDelegates(vm);
            vm.PropertyChanged += OnVmPropertyChanged;
        }
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);

        if (DataContext is SftpViewModel vm)
        {
            InjectDelegates(vm);
            vm.PropertyChanged += OnVmPropertyChanged;
        }

        this.AddHandler(DoubleTappedEvent, OnFileDoubleTapped, RoutingStrategies.Bubble);
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (DataContext is not SftpViewModel vm)
            return;

        if (e.PropertyName == nameof(SftpViewModel.IsCreatingDirectory) && vm.IsCreatingDirectory)
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

        if (e.PropertyName == nameof(SftpViewModel.RenamingItem) && vm.RenamingItem != null)
        {
            var targetItem = vm.RenamingItem;
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
        if (DataContext is not SftpViewModel vm || sender is not AtomTextBox tb)
            return;

        if (tb.Tag is not Models.SftpFileItem item)
            return;

        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            item.RenamingText = tb.Text ?? item.Name;
            if (vm.ConfirmRenameCommand is CommunityToolkit.Mvvm.Input.IAsyncRelayCommand asyncCmd)
                _ = asyncCmd.ExecuteAsync(item);
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            vm.CancelRenameCommand.Execute(item);
        }
    }

    public void OnRenameInputLostFocus(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SftpViewModel vm || sender is not AtomTextBox tb)
            return;

        if (tb.Tag is not Models.SftpFileItem item || !item.IsRenaming)
            return;

        item.RenamingText = tb.Text ?? item.Name;
        if (vm.ConfirmRenameCommand is CommunityToolkit.Mvvm.Input.IAsyncRelayCommand asyncCmd)
            _ = asyncCmd.ExecuteAsync(item);
    }

    public void OnNewDirInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not SftpViewModel vm)
            return;

        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            var input = this.FindControl<AtomTextBox>("NewDirInput");
            if (input != null)
                vm.NewDirectoryName = input.Text ?? "\u65b0\u76ee\u5f55";

            _ = vm.ConfirmCreateDirectoryCommand.ExecuteAsync(null);
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            vm.CancelCreateDirectoryCommand.Execute(null);
        }
    }

    public void OnNewDirInputLostFocus(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SftpViewModel vm || !vm.IsCreatingDirectory)
            return;

        var input = this.FindControl<AtomTextBox>("NewDirInput");
        if (input != null)
            vm.NewDirectoryName = input.Text ?? "\u65b0\u76ee\u5f55";

        _ = vm.ConfirmCreateDirectoryCommand.ExecuteAsync(null);
    }

    public void OnRemotePathInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not SftpViewModel vm || e.Key != Key.Enter)
            return;

        e.Handled = true;
        if (sender is AtomLineEdit input)
            vm.PathInput = input.Text ?? vm.CurrentPath;

        _ = vm.NavigateToTypedPathCommand.ExecuteAsync(null);
    }

    public void OnFileListDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = DataContext is SftpViewModel { IsConnected: true }
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    public async void OnFileListDrop(object? sender, DragEventArgs e)
    {
        e.Handled = true;

        if (DataContext is not SftpViewModel { IsConnected: true } vm)
            return;

        try
        {
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
        }
        catch (Exception ex)
        {
            await vm.ShowDropUploadErrorAsync(ex.Message);
        }
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

    private void OnFileItemPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is not SftpViewModel vm || sender is not Border border)
            return;

        if (border.DataContext is not Models.SftpFileItem item)
            return;

        var point = e.GetCurrentPoint(border);
        System.Console.WriteLine($"[SFTP] PointerPressed: Left={point.Properties.IsLeftButtonPressed}, Right={point.Properties.IsRightButtonPressed}, item={item.Name}");
        vm.SelectFileCommand.Execute(item);

        if (point.Properties.IsRightButtonPressed)
        {
            e.Handled = true;
            Dispatcher.UIThread.Post(() => ShowFileContextMenu(border, item, vm), DispatcherPriority.Input);
            ClearFileDragState();
            return;
        }

        if (point.Properties.IsLeftButtonPressed)
        {
            _dragSourceBorder = border;
            _dragSourceItem = item;
            _dragStartEventArgs = e;
            _dragStartPoint = e.GetPosition(this);
        }
    }

    private void OnFileItemReleased(object? sender, PointerReleasedEventArgs e)
    {
        System.Console.WriteLine($"[SFTP] PointerReleased: InitialPressMouseButton={e.InitialPressMouseButton}");
        if (!_isDraggingFileItem)
            ClearFileDragState();
    }

    private async void OnFileItemPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_isDraggingFileItem ||
            _dragSourceBorder == null ||
            _dragSourceItem == null ||
            _dragStartEventArgs == null ||
            DataContext is not SftpViewModel vm)
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
            if (vm.CanStreamDragOut(item))
            {
                var dragFiles = await vm.CreateVirtualDragFilesAsync(item);
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
                    var localPath = vm.ExportItemForDragBlocking(item);
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
        _dragSourceBorder = null;
        _dragSourceItem = null;
        _dragStartEventArgs = null;
    }

    private void ShowFileContextMenu(Border anchor, Models.SftpFileItem item, SftpViewModel vm)
    {
        var popup = new AtomPopup
        {
            PlacementTarget = anchor,
            Placement = Avalonia.Controls.PlacementMode.Pointer,
            IsLightDismissEnabled = true,
            WindowManagerAddShadowHint = false,
        };

        var panel = new StackPanel
        {
            Background = new SolidColorBrush(ThemeTokenColorHelper.GetColor(SharedTokenKind.ColorBgElevated, Color.Parse("#FFFFFF"))),
            MinWidth = 120,
        };

        void AddItem(string text, Action action)
        {
            var btn = new AtomButton
            {
                Content = text,
                Background = Brushes.Transparent,
                BorderThickness = new Avalonia.Thickness(0),
                Padding = new Avalonia.Thickness(12, 6),
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
                HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Left,
                Foreground = new SolidColorBrush(ThemeTokenColorHelper.GetColor(SharedTokenKind.ColorText, Color.Parse("#000000"))),
                FontSize = 12,
            };
            btn.Click += (_, _) =>
            {
                popup.IsOpen = false;
                action();
            };
            panel.Children.Add(btn);
        }

        if (!item.IsDirectory)
        {
            AddItem("\u2193 \u4e0b\u8f7d", () => _ = vm.DownloadCommand.ExecuteAsync(null));
            panel.Children.Add(new Border
            {
                Height = 1,
                Background = new SolidColorBrush(ThemeTokenColorHelper.GetColor(SharedTokenKind.ColorSplit, Color.Parse("#F0F0F0"))),
                Margin = new Avalonia.Thickness(4, 2),
            });
        }

        AddItem("\u270e \u91cd\u547d\u540d", () => vm.RenameCommand.Execute(null));
        AddItem("\u5220\u9664", () => _ = vm.DeleteCommand.ExecuteAsync(null));

        popup.Child = new Border
        {
            Child = panel,
            Background = new SolidColorBrush(ThemeTokenColorHelper.GetColor(SharedTokenKind.ColorBgElevated, Color.Parse("#FFFFFF"))),
            BorderBrush = new SolidColorBrush(ThemeTokenColorHelper.GetColor(SharedTokenKind.ColorBorder, Color.Parse("#D9D9D9"))),
            BorderThickness = new Avalonia.Thickness(1),
            CornerRadius = new Avalonia.CornerRadius(4),
            BoxShadow = BoxShadows.Parse("0 4 12 0 #80000000"),
        };

        popup.PlacementTarget = anchor;
        popup.Open();
    }

    private void OnFileDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is not SftpViewModel vm)
            return;

        var source = e.Source as Avalonia.Visual;
        while (source != null)
        {
            if (source.DataContext is Models.SftpFileItem item)
            {
                _ = vm.OpenItemCommand.ExecuteAsync(item);
                return;
            }
            source = source.GetVisualParent() as Avalonia.Visual;
        }
    }

    private static async Task<bool> ShowConfirmWindow(Window owner, string message)
    {
        var result = false;
        var dialog = new AtomWindow
        {
            Title = "\u786e\u8ba4",
            Width = 360,
            Height = 140,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            ShowInTaskbar = false
        };

        var panel = new StackPanel { Spacing = 12, Margin = new Avalonia.Thickness(20) };
        panel.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap });

        var btnPanel = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right
        };

        var okBtn = new AtomButton { Content = "\u786e\u8ba4", Width = 70 };
        okBtn.Click += (_, _) => { result = true; dialog.Close(); };
        var cancelBtn = new AtomButton { Content = "\u53d6\u6d88", Width = 70 };
        cancelBtn.Click += (_, _) => dialog.Close();

        btnPanel.Children.Add(okBtn);
        btnPanel.Children.Add(cancelBtn);
        panel.Children.Add(btnPanel);
        dialog.Content = panel;

        await dialog.ShowDialog(owner);
        return result;
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

        var panel = new StackPanel { Spacing = 12, Margin = new Avalonia.Thickness(20) };
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
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right
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
}
