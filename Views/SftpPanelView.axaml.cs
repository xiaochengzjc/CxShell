using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ChiXueSsh.ViewModels;

namespace ChiXueSsh.Views;

public partial class SftpPanelView : UserControl
{
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

        // 双击打开条目（挂在整个控件上）
        this.AddHandler(DoubleTappedEvent, OnFileDoubleTapped, RoutingStrategies.Bubble);
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (DataContext is not SftpViewModel vm) return;

        if (e.PropertyName == nameof(SftpViewModel.IsCreatingDirectory) && vm.IsCreatingDirectory)
        {
            Dispatcher.UIThread.Post(() =>
            {
                var input = this.FindControl<TextBox>("NewDirInput");
                if (input != null)
                {
                    input.Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#FFD066"));
                    input.Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#1A2A3A"));
                    input.CaretBrush = Avalonia.Media.Brushes.White;
                    input.Text = "新目录";
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
                // 遍历可视树找到 Tag == targetItem 的 TextBox
                var tb = FindRenameTextBox(this, targetItem);
                if (tb != null)
                {
                    tb.Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#FFD066"));
                    tb.Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#1A2A3A"));
                    tb.CaretBrush = Avalonia.Media.Brushes.White;
                    tb.Text = targetItem.Name;
                    tb.Focus();
                    tb.SelectAll();
                }
            }, DispatcherPriority.Render);
        }
    }

    private static TextBox? FindRenameTextBox(Avalonia.Visual root, Models.SftpFileItem target)
    {
        foreach (var child in root.GetVisualChildren())
        {
            if (child is TextBox tb && tb.Tag == target)
                return tb;
            var found = FindRenameTextBox(child, target);
            if (found != null) return found;
        }
        return null;
    }

    public void OnRenameInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not SftpViewModel vm) return;
        if (sender is not TextBox tb) return;
        var item = tb.Tag as Models.SftpFileItem;
        if (item == null) return;

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
        if (DataContext is not SftpViewModel vm) return;
        if (sender is not TextBox tb) return;
        var item = tb.Tag as Models.SftpFileItem;
        if (item == null || !item.IsRenaming) return;
        item.RenamingText = tb.Text ?? item.Name;
        if (vm.ConfirmRenameCommand is CommunityToolkit.Mvvm.Input.IAsyncRelayCommand asyncCmd)
            _ = asyncCmd.ExecuteAsync(item);
    }

    public void OnNewDirInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not SftpViewModel vm) return;
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            var input = this.FindControl<TextBox>("NewDirInput");
            if (input != null) vm.NewDirectoryName = input.Text ?? "新目录";
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
        if (DataContext is not SftpViewModel vm) return;
        if (vm.IsCreatingDirectory)
        {
            var input = this.FindControl<TextBox>("NewDirInput");
            if (input != null) vm.NewDirectoryName = input.Text ?? "新目录";
            _ = vm.ConfirmCreateDirectoryCommand.ExecuteAsync(null);
        }
    }

    private void InjectDelegates(SftpViewModel vm)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        vm.PickUploadFileAsync = async () =>
        {
            var files = await topLevel.StorageProvider.OpenFilePickerAsync(
                new FilePickerOpenOptions
                {
                    Title = "选择要上传的文件",
                    AllowMultiple = false
                });
            return files.FirstOrDefault()?.Path.LocalPath;
        };

        vm.PickDownloadPathAsync = async (fileName) =>
        {
            var file = await topLevel.StorageProvider.SaveFilePickerAsync(
                new FilePickerSaveOptions
                {
                    Title = "保存到本地",
                    SuggestedFileName = fileName
                });
            return file?.Path.LocalPath;
        };

        vm.ShowConfirmDialogAsync = async (message) =>
        {
            var window = topLevel as Window;
            if (window == null) return false;
            return await ShowConfirmWindow(window, message);
        };

        vm.ShowInputDialogAsync = async (title, defaultValue) =>
        {
            var window = topLevel as Window;
            if (window == null) return null;
            return await ShowInputWindow(window, title, defaultValue);
        };
    }

    private void OnFileItemPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is not SftpViewModel vm) return;
        if (sender is not Border border) return;
        if (border.DataContext is not Models.SftpFileItem item) return;

        var point = e.GetCurrentPoint(border);
        System.Console.WriteLine($"[SFTP] PointerPressed: Left={point.Properties.IsLeftButtonPressed}, Right={point.Properties.IsRightButtonPressed}, item={item.Name}");

        // 左键和右键都先选中
        vm.SelectFileCommand.Execute(item);

        if (point.Properties.IsRightButtonPressed)
        {
            e.Handled = true;
            // 延迟一帧再打开菜单，确保选中状态更新完毕
            Dispatcher.UIThread.Post(() => ShowFileContextMenu(border, item, vm), DispatcherPriority.Input);
        }
    }

    private void OnFileItemReleased(object? sender, PointerReleasedEventArgs e)
    {
        // 仅保留日志，菜单已在 Pressed 中处理
        System.Console.WriteLine($"[SFTP] PointerReleased: InitialPressMouseButton={e.InitialPressMouseButton}");
    }

    private void ShowFileContextMenu(Border anchor, Models.SftpFileItem item, SftpViewModel vm)
    {
        // 用 Popup + StackPanel 完全绕开 AtomUI ContextMenu
        var popup = new Avalonia.Controls.Primitives.Popup
        {
            PlacementTarget = anchor,
            Placement = Avalonia.Controls.PlacementMode.Pointer,
            IsLightDismissEnabled = true,
            WindowManagerAddShadowHint = false,
        };

        var panel = new StackPanel
        {
            Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#2A2A3E")),
            MinWidth = 120,
        };

        void AddItem(string text, Action action)
        {
            var btn = new Button
            {
                Content = text,
                Background = Avalonia.Media.Brushes.Transparent,
                BorderThickness = new Avalonia.Thickness(0),
                Padding = new Avalonia.Thickness(12, 6),
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
                HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Left,
                Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#D0D0E8")),
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
            AddItem("↓ 下载", () => _ = vm.DownloadCommand.ExecuteAsync(null));
            panel.Children.Add(new Border
            {
                Height = 1,
                Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#404060")),
                Margin = new Avalonia.Thickness(4, 2),
            });
        }

        AddItem("✏ 重命名", () => vm.RenameCommand.Execute(null));
        AddItem("🗑 删除", () => _ = vm.DeleteCommand.ExecuteAsync(null));

        // 加边框
        popup.Child = new Border
        {
            Child = panel,
            Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#2A2A3E")),
            BorderBrush = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#404060")),
            BorderThickness = new Avalonia.Thickness(1),
            CornerRadius = new Avalonia.CornerRadius(4),
            BoxShadow = Avalonia.Media.BoxShadows.Parse("0 4 12 0 #80000000"),
        };

        // 附加到当前 UserControl 的可视树
        popup.PlacementTarget = anchor;
        popup.Open();
    }

    private void OnFileDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is not SftpViewModel vm) return;
        // 从事件源找到 SftpFileItem
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
        var dialog = new Window
        {
            Title = "确认",
            Width = 360,
            Height = 140,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            ShowInTaskbar = false
        };

        var panel = new StackPanel { Spacing = 12, Margin = new Avalonia.Thickness(20) };
        panel.Children.Add(new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap });

        var btnPanel = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right
        };

        var okBtn = new Button { Content = "确认", Width = 70 };
        okBtn.Click += (_, _) => { result = true; dialog.Close(); };
        var cancelBtn = new Button { Content = "取消", Width = 70 };
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
        var dialog = new Window
        {
            Title = title,
            Width = 360,
            Height = 200,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            ShowInTaskbar = false
        };

        var panel = new StackPanel { Spacing = 12, Margin = new Avalonia.Thickness(20) };
        var label = new TextBlock { Text = title, FontSize = 13, Foreground = Avalonia.Media.Brushes.Gray };
        var input = new TextBox { Text = defaultValue, FontSize = 13 };
        panel.Children.Add(label);

        // 窗口显示后聚焦并全选，方便用户直接输入
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

        var okBtn = new Button { Content = "确认", Width = 70 };
        okBtn.Click += (_, _) => { result = input.Text; dialog.Close(); };
        var cancelBtn = new Button { Content = "取消", Width = 70 };
        cancelBtn.Click += (_, _) => dialog.Close();

        // 回车确认
        input.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { result = input.Text; dialog.Close(); }
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
