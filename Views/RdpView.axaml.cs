using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using CxShell.ViewModels;

namespace CxShell.Views;

public partial class RdpView : UserControl
{
    private ushort _buttonFlags;
    private RdpViewModel? _boundVm;
    private bool _isAttached;
    private bool _controlDown;
    private bool _shiftDown;
    private bool _altDown;
    private bool _suppressNextPasteKeyUp;
    private bool _applyingRemoteClipboardText;
    private string? _lastSyncedClipboardText;

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
            ReleaseTrackedModifiers();
            UnbindViewModel();
        };
        GotFocus += (_, _) => _ = SyncLocalClipboardToRemoteAsync();
        LostFocus += (_, _) => ReleaseTrackedModifiers();
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
        {
            vm.Start();
            _ = SyncLocalClipboardToRemoteAsync();
        }
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (_boundVm != null && args.PropertyName == nameof(RdpViewModel.IsFitToWindow))
            ApplyScaleMode(_boundVm);
        if (_boundVm != null && args.PropertyName == nameof(RdpViewModel.IsClipboardChannelReady) && _boundVm.IsClipboardChannelReady)
            _ = SyncLocalClipboardToRemoteAsync();
        if (_boundVm != null && args.PropertyName == nameof(RdpViewModel.RemoteClipboardText))
            _ = ApplyRemoteClipboardTextAsync(_boundVm.RemoteClipboardText);
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
        _ = SyncLocalClipboardToRemoteAsync();
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
        if (TryStartLocalClipboardPaste(e))
            return;

        SendKey(e, true);
    }

    private void OnFramebufferKeyUp(object? sender, KeyEventArgs e)
    {
        if (_suppressNextPasteKeyUp && e.Key == Key.V)
        {
            _suppressNextPasteKeyUp = false;
            e.Handled = true;
            return;
        }

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

        if (TrySendModifierKey(vm, e.Key, down))
        {
            e.Handled = true;
            return;
        }

        SyncModifierState(vm, e.KeyModifiers);

        if (TryGetPrintableScancode(e, out var printableScancode, out _))
        {
            vm.SendKey(printableScancode, down);
            e.Handled = true;
            return;
        }

        var key = ToRdpScancode(e);
        if (key == 0)
            return;

        vm.SendKey(key, down);
        e.Handled = true;
    }

    private bool TryStartLocalClipboardPaste(KeyEventArgs e)
    {
        if (e.Key != Key.V || !e.KeyModifiers.HasFlag(KeyModifiers.Control))
            return false;

        _suppressNextPasteKeyUp = true;
        e.Handled = true;
        _ = PasteClipboardViaRdpAsync();
        return true;
    }

    private async Task PasteClipboardViaRdpAsync()
    {
        if (DataContext is not RdpViewModel vm)
            return;

        var text = await TryReadLocalClipboardTextAsync();
        if (string.IsNullOrEmpty(text))
            return;

        if (!vm.IsClipboardChannelReady)
        {
            vm.SetClipboardText(text);
            _lastSyncedClipboardText = text;
            await PasteLocalClipboardTextAsync(text);
            return;
        }

        vm.SetClipboardText(text);
        _lastSyncedClipboardText = text;
        await Task.Delay(300);
        SendClipboardPasteShortcut(vm);
    }

    private async Task PasteLocalClipboardTextAsync(string text)
    {
        if (DataContext is not RdpViewModel vm)
            return;

        if (string.IsNullOrEmpty(text))
            return;

        ReleaseTrackedModifiers(vm);
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (ch == '\r')
            {
                if (i + 1 < text.Length && text[i + 1] == '\n')
                    continue;

                SendScancodeTap(vm, 0x1C);
                continue;
            }

            if (ch == '\n')
            {
                SendScancodeTap(vm, 0x1C);
                continue;
            }

            if (ch == '\t')
            {
                SendScancodeTap(vm, 0x0F);
                continue;
            }

            vm.SendUnicodeKey(ch, true);
            vm.SendUnicodeKey(ch, false);

            if (i % 128 == 127)
                await Task.Yield();
        }
    }

    private async Task<string?> TryReadLocalClipboardTextAsync()
    {
        try
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            return clipboard == null ? null : await clipboard.TryGetTextAsync();
        }
        catch
        {
            return null;
        }
    }

    private async Task SyncLocalClipboardToRemoteAsync()
    {
        if (_applyingRemoteClipboardText || DataContext is not RdpViewModel vm || !vm.IsClipboardChannelReady)
            return;

        var text = await TryReadLocalClipboardTextAsync();
        if (text == null || string.Equals(text, _lastSyncedClipboardText, StringComparison.Ordinal))
            return;

        vm.SetClipboardText(text);
        _lastSyncedClipboardText = text;
    }

    private async Task ApplyRemoteClipboardTextAsync(string text)
    {
        _applyingRemoteClipboardText = true;
        try
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard != null)
            {
                await clipboard.SetTextAsync(text);
                _lastSyncedClipboardText = text;
            }
        }
        catch
        {
            // Clipboard access may fail on some desktop backends.
        }
        finally
        {
            _applyingRemoteClipboardText = false;
        }
    }

    private void SendClipboardPasteShortcut(RdpViewModel vm)
    {
        ReleaseTrackedModifiers(vm);
        vm.SendKey(0x1D, true);
        SendScancodeTap(vm, 0x2F);
        vm.SendKey(0x1D, false);
    }

    private static void SendScancodeTap(RdpViewModel vm, uint scancode)
    {
        vm.SendKey(scancode, true);
        vm.SendKey(scancode, false);
    }

    private void ReleaseTrackedModifiers()
    {
        if (DataContext is RdpViewModel vm)
            ReleaseTrackedModifiers(vm);
        else
            ClearTrackedModifiers();
    }

    private void ReleaseTrackedModifiers(RdpViewModel vm)
    {
        SetModifierState(vm, ref _controlDown, false, 0x1D);
        SetModifierState(vm, ref _shiftDown, false, 0x2A);
        SetModifierState(vm, ref _altDown, false, 0x38);
    }

    private void ClearTrackedModifiers()
    {
        _controlDown = false;
        _shiftDown = false;
        _altDown = false;
    }

    private void SyncModifierState(RdpViewModel vm, KeyModifiers modifiers)
    {
        SetModifierState(vm, ref _controlDown, modifiers.HasFlag(KeyModifiers.Control), 0x1D);
        SetModifierState(vm, ref _shiftDown, modifiers.HasFlag(KeyModifiers.Shift), 0x2A);
        SetModifierState(vm, ref _altDown, modifiers.HasFlag(KeyModifiers.Alt), 0x38);
    }

    private bool TrySendModifierKey(RdpViewModel vm, Key key, bool down)
    {
        switch (key)
        {
            case Key.LeftCtrl:
            case Key.RightCtrl:
                SetModifierState(vm, ref _controlDown, down, key == Key.RightCtrl ? 0x0100u | 0x1D : 0x1D);
                return true;
            case Key.LeftShift:
                SetModifierState(vm, ref _shiftDown, down, 0x2A);
                return true;
            case Key.RightShift:
                SetModifierState(vm, ref _shiftDown, down, 0x36);
                return true;
            case Key.LeftAlt:
            case Key.RightAlt:
                SetModifierState(vm, ref _altDown, down, key == Key.RightAlt ? 0x0100u | 0x38 : 0x38);
                return true;
            default:
                return false;
        }
    }

    private static void SetModifierState(RdpViewModel vm, ref bool current, bool desired, uint scancode)
    {
        if (current == desired)
            return;

        vm.SendKey(scancode, desired);
        current = desired;
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
