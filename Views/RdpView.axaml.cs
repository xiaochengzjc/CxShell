using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using CxShell.ViewModels;

namespace CxShell.Views;

public partial class RdpView : UserControl
{
    private ushort _buttonFlags;
    private RdpViewModel? _boundVm;
    private bool _isAttached;
    private bool _syntheticShiftDown;

    public RdpView()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, OnFramebufferKeyDown, RoutingStrategies.Tunnel);
        AddHandler(KeyUpEvent, OnFramebufferKeyUp, RoutingStrategies.Tunnel);
        AttachedToVisualTree += (_, _) =>
        {
            _isAttached = true;
            Focus();
            if (DataContext is RdpViewModel vm)
                vm.Start();
        };
        DetachedFromVisualTree += (_, _) =>
        {
            _isAttached = false;
            UnbindViewModel();
        };
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        UnbindViewModel();
        if (DataContext is not RdpViewModel vm)
            return;

        _boundVm = vm;
        vm.PropertyChanged += OnViewModelPropertyChanged;
        ApplyScaleMode(vm);
        if (_isAttached)
            vm.Start();
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (_boundVm != null && args.PropertyName == nameof(RdpViewModel.IsFitToWindow))
            ApplyScaleMode(_boundVm);
    }

    private void UnbindViewModel()
    {
        if (_boundVm == null)
            return;

        _boundVm.PropertyChanged -= OnViewModelPropertyChanged;
        _boundVm = null;
    }

    private void ApplyScaleMode(RdpViewModel vm)
    {
        if (FramebufferImage == null)
            return;

        FramebufferImage.Stretch = vm.IsFitToWindow ? Stretch.Uniform : Stretch.None;
    }

    private void OnFramebufferPointerMoved(object? sender, PointerEventArgs e)
    {
        SendPointer(e, 0x0800);
    }

    private void OnFramebufferPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        Focus();
        var point = e.GetCurrentPoint(FramebufferImage);
        if (point.Properties.IsLeftButtonPressed)
            _buttonFlags |= 0x1000;
        if (point.Properties.IsMiddleButtonPressed)
            _buttonFlags |= 0x4000;
        if (point.Properties.IsRightButtonPressed)
            _buttonFlags |= 0x2000;

        SendPointer(e, (ushort)(0x8000 | _buttonFlags));
        e.Handled = true;
    }

    private void OnFramebufferPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        var releaseFlag = e.InitialPressMouseButton switch
        {
            MouseButton.Left => (ushort)0x1000,
            MouseButton.Right => (ushort)0x2000,
            MouseButton.Middle => (ushort)0x4000,
            _ => (ushort)0
        };

        if (releaseFlag != 0)
        {
            SendPointer(e, releaseFlag);
            _buttonFlags &= (ushort)~releaseFlag;
        }

        e.Handled = true;
    }

    private void OnFramebufferPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        var wheelFlag = e.Delta.Y > 0 ? (ushort)0x0200 : (ushort)(0x0200 | 0x0100);
        SendPointer(e, wheelFlag);
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

    private void SendPointer(PointerEventArgs e, ushort flags)
    {
        if (DataContext is not RdpViewModel vm || FramebufferImage == null)
            return;

        var (x, y) = MapPointer(
            e.GetPosition(FramebufferImage),
            FramebufferImage.Bounds.Size,
            vm.RemoteWidth,
            vm.RemoteHeight,
            vm.IsFitToWindow);
        vm.SendPointer(flags, (ushort)x, (ushort)y);
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
        if (DataContext is not RdpViewModel vm)
            return;

        if (TryGetPrintableScancode(e, out var printableScancode, out var needsShift))
        {
            if (!needsShift && down && _syntheticShiftDown)
            {
                vm.SendKey(0x2A, false);
                _syntheticShiftDown = false;
            }

            if (needsShift && down && !_syntheticShiftDown)
            {
                vm.SendKey(0x2A, true);
                _syntheticShiftDown = true;
            }

            vm.SendKey(printableScancode, down);

            if (!down && _syntheticShiftDown)
            {
                vm.SendKey(0x2A, false);
                _syntheticShiftDown = false;
            }

            e.Handled = true;
            return;
        }

        var key = ToRdpScancode(e);
        if (key == 0)
            return;

        vm.SendKey(key, down);
        e.Handled = true;
    }

    private static bool TryGetPrintableScancode(KeyEventArgs e, out uint scancode, out bool needsShift)
    {
        scancode = 0;
        needsShift = false;

        if (e.Key >= Key.A && e.Key <= Key.Z)
        {
            needsShift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
            scancode = e.Key switch
            {
                Key.A => 0x1E,
                Key.B => 0x30,
                Key.C => 0x2E,
                Key.D => 0x20,
                Key.E => 0x12,
                Key.F => 0x21,
                Key.G => 0x22,
                Key.H => 0x23,
                Key.I => 0x17,
                Key.J => 0x24,
                Key.K => 0x25,
                Key.L => 0x26,
                Key.M => 0x32,
                Key.N => 0x31,
                Key.O => 0x18,
                Key.P => 0x19,
                Key.Q => 0x10,
                Key.R => 0x13,
                Key.S => 0x1F,
                Key.T => 0x14,
                Key.U => 0x16,
                Key.V => 0x2F,
                Key.W => 0x11,
                Key.X => 0x2D,
                Key.Y => 0x15,
                Key.Z => 0x2C,
                _ => 0
            };
            return scancode != 0;
        }

        if (e.Key >= Key.D0 && e.Key <= Key.D9)
        {
            needsShift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
            var index = e.Key - Key.D0;
            scancode = index == 0 ? 0x0B : 0x01 + (uint)index;
            return true;
        }

        if (e.Key >= Key.NumPad0 && e.Key <= Key.NumPad9)
        {
            scancode = 0x52 + (uint)(e.Key - Key.NumPad0);
            return true;
        }

        needsShift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        scancode = e.Key switch
        {
            Key.Space => 0x39,
            Key.OemMinus => 0x0C,
            Key.OemPlus => 0x0D,
            Key.OemOpenBrackets => 0x1A,
            Key.OemCloseBrackets => 0x1B,
            Key.OemPipe => 0x2B,
            Key.OemSemicolon => 0x27,
            Key.OemQuotes => 0x28,
            Key.OemComma => 0x33,
            Key.OemPeriod => 0x34,
            Key.OemQuestion => 0x35,
            Key.OemTilde => 0x29,
            Key.Decimal => 0x53,
            Key.Add => 0x4E,
            Key.Subtract => 0x4A,
            Key.Multiply => 0x37,
            Key.Divide => 0x0100 | 0x35,
            _ => 0
        };

        return scancode != 0;
    }

    private static uint ToRdpScancode(KeyEventArgs e)
    {
        return e.Key switch
        {
            Key.Escape => 0x01,
            Key.Back => 0x0E,
            Key.Tab => 0x0F,
            Key.Enter => 0x1C,
            Key.F1 => 0x3B,
            Key.F2 => 0x3C,
            Key.F3 => 0x3D,
            Key.F4 => 0x3E,
            Key.F5 => 0x3F,
            Key.F6 => 0x40,
            Key.F7 => 0x41,
            Key.F8 => 0x42,
            Key.F9 => 0x43,
            Key.F10 => 0x44,
            Key.F11 => 0x57,
            Key.F12 => 0x58,
            Key.Home => 0x0100 | 0x47,
            Key.Up => 0x0100 | 0x48,
            Key.PageUp => 0x0100 | 0x49,
            Key.Left => 0x0100 | 0x4B,
            Key.Right => 0x0100 | 0x4D,
            Key.End => 0x0100 | 0x4F,
            Key.Down => 0x0100 | 0x50,
            Key.PageDown => 0x0100 | 0x51,
            Key.Insert => 0x0100 | 0x52,
            Key.Delete => 0x0100 | 0x53,
            _ => 0
        };
    }
}
