using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using ChiXueSsh.ViewModels;

namespace ChiXueSsh.Views;

public partial class VncView : UserControl
{
    private byte _buttonMask;
    private VncViewModel? _boundVm;

    public VncView()
    {
        InitializeComponent();
        AttachedToVisualTree += (_, _) => FramebufferImage?.Focus();
        DetachedFromVisualTree += (_, _) => UnbindViewModel();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        UnbindViewModel();
        if (DataContext is not VncViewModel vm)
            return;

        _boundVm = vm;
        vm.PropertyChanged += OnViewModelPropertyChanged;
        ApplyScaleMode(vm);
    }

    private async void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (_boundVm != null && args.PropertyName == nameof(VncViewModel.IsFitToWindow))
            ApplyScaleMode(_boundVm);

        if (_boundVm == null ||
            args.PropertyName != nameof(VncViewModel.RemoteClipboardText) ||
            string.IsNullOrEmpty(_boundVm.RemoteClipboardText))
        {
            return;
        }

        await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard != null)
                await clipboard.SetTextAsync(_boundVm.RemoteClipboardText);
        });
    }

    private void UnbindViewModel()
    {
        if (_boundVm == null)
            return;

        _boundVm.PropertyChanged -= OnViewModelPropertyChanged;
        _boundVm = null;
    }

    private void ApplyScaleMode(VncViewModel vm)
    {
        if (FramebufferImage == null)
            return;

        FramebufferImage.Stretch = vm.IsFitToWindow ? Stretch.Uniform : Stretch.None;
    }

    private void OnFramebufferPointerMoved(object? sender, PointerEventArgs e)
    {
        SendPointer(e);
    }

    private void OnFramebufferPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        FramebufferImage?.Focus();
        var point = e.GetCurrentPoint(FramebufferImage);
        if (point.Properties.IsLeftButtonPressed)
            _buttonMask |= 1;
        if (point.Properties.IsMiddleButtonPressed)
            _buttonMask |= 2;
        if (point.Properties.IsRightButtonPressed)
            _buttonMask |= 4;

        SendPointer(e);
        e.Handled = true;
    }

    private void OnFramebufferPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        switch (e.InitialPressMouseButton)
        {
            case MouseButton.Left:
                _buttonMask &= unchecked((byte)~1);
                break;
            case MouseButton.Middle:
                _buttonMask &= unchecked((byte)~2);
                break;
            case MouseButton.Right:
                _buttonMask &= unchecked((byte)~4);
                break;
        }

        SendPointer(e);
        e.Handled = true;
    }

    private async void OnFramebufferPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        var wheelMask = e.Delta.Y > 0 ? (byte)8 : (byte)16;
        SendPointer(e, (byte)(_buttonMask | wheelMask));
        await System.Threading.Tasks.Task.Delay(20);
        SendPointer(e, _buttonMask);
        e.Handled = true;
    }

    private void OnFramebufferKeyDown(object? sender, KeyEventArgs e)
    {
        SendKey(e, true);
    }

    private void OnFramebufferKeyUp(object? sender, KeyEventArgs e)
    {
        SendKey(e, false);
    }

    private async void OnSendClipboardClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not VncViewModel vm)
            return;

        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        var text = clipboard == null ? null : await clipboard.TryGetTextAsync();
        if (!string.IsNullOrEmpty(text))
            vm.SendClipboardText(text);
    }

    private void SendPointer(PointerEventArgs e)
    {
        SendPointer(e, _buttonMask);
    }

    private void SendPointer(PointerEventArgs e, byte mask)
    {
        if (DataContext is not VncViewModel vm || FramebufferImage == null)
            return;

        var (x, y) = MapPointer(
            e.GetPosition(FramebufferImage),
            FramebufferImage.Bounds.Size,
            vm.RemoteWidth,
            vm.RemoteHeight,
            vm.IsFitToWindow);
        vm.SendPointer(mask, x, y);
    }

    private static (int X, int Y) MapPointer(Point point, Size imageSize, int remoteWidth, int remoteHeight, bool fitToWindow)
    {
        if (remoteWidth <= 0 || remoteHeight <= 0 || imageSize.Width <= 0 || imageSize.Height <= 0)
            return (0, 0);

        var scale = fitToWindow ? Math.Min(imageSize.Width / remoteWidth, imageSize.Height / remoteHeight) : 1.0;
        var displayedWidth = remoteWidth * scale;
        var displayedHeight = remoteHeight * scale;
        var offsetX = (imageSize.Width - displayedWidth) / 2;
        var offsetY = (imageSize.Height - displayedHeight) / 2;
        var x = (int)Math.Round((point.X - offsetX) / scale);
        var y = (int)Math.Round((point.Y - offsetY) / scale);
        return (Math.Clamp(x, 0, remoteWidth - 1), Math.Clamp(y, 0, remoteHeight - 1));
    }

    private void SendKey(KeyEventArgs e, bool down)
    {
        if (DataContext is not VncViewModel vm)
            return;

        var keySym = ToKeySym(e);
        if (keySym == 0)
            return;

        vm.SendKey(keySym, down);
        e.Handled = true;
    }

    private static uint ToKeySym(KeyEventArgs e)
    {
        if (!string.IsNullOrEmpty(e.KeySymbol))
        {
            var text = e.KeySymbol;
            if (text.Length == 1)
                return text[0];
        }

        return e.Key switch
        {
            Key.Back => 0xff08,
            Key.Tab => 0xff09,
            Key.Enter => 0xff0d,
            Key.Escape => 0xff1b,
            Key.Delete => 0xffff,
            Key.Home => 0xff50,
            Key.Left => 0xff51,
            Key.Up => 0xff52,
            Key.Right => 0xff53,
            Key.Down => 0xff54,
            Key.PageUp => 0xff55,
            Key.PageDown => 0xff56,
            Key.End => 0xff57,
            Key.Insert => 0xff63,
            Key.F1 => 0xffbe,
            Key.F2 => 0xffbf,
            Key.F3 => 0xffc0,
            Key.F4 => 0xffc1,
            Key.F5 => 0xffc2,
            Key.F6 => 0xffc3,
            Key.F7 => 0xffc4,
            Key.F8 => 0xffc5,
            Key.F9 => 0xffc6,
            Key.F10 => 0xffc7,
            Key.F11 => 0xffc8,
            Key.F12 => 0xffc9,
            Key.Space => 0x20,
            _ => 0
        };
    }
}
