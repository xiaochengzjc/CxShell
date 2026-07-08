using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.ComponentModel;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using CxShell.Models;
using MarcusW.VncClient;
using MarcusW.VncClient.Output;
using MarcusW.VncClient.Protocol.Implementation.MessageTypes.Outgoing;
using AvPixelFormat = Avalonia.Platform.PixelFormat;
using RfbFramebufferReference = MarcusW.VncClient.Rendering.IFramebufferReference;
using RfbPixelFormat = MarcusW.VncClient.PixelFormat;
using RfbPosition = MarcusW.VncClient.Position;
using RfbRectangle = MarcusW.VncClient.Rectangle;
using RfbRenderTarget = MarcusW.VncClient.Rendering.IRenderTarget;
using RfbScreen = MarcusW.VncClient.Screen;
using RfbSize = MarcusW.VncClient.Size;

namespace CxShell.Controls;

public sealed class VncClientView : Control, RfbRenderTarget, IOutputHandler, IDisposable
{
    public static readonly DirectProperty<VncClientView, RfbConnection?> ConnectionProperty =
        AvaloniaProperty.RegisterDirect<VncClientView, RfbConnection?>(
            nameof(Connection),
            control => control.Connection,
            (control, value) => control.Connection = value);

    public static readonly DirectProperty<VncClientView, bool> AutoResizeRemoteProperty =
        AvaloniaProperty.RegisterDirect<VncClientView, bool>(
            nameof(AutoResizeRemote),
            control => control.AutoResizeRemote,
            (control, value) => control.AutoResizeRemote = value);

    public static readonly DirectProperty<VncClientView, VncDisplayMode> DisplayModeProperty =
        AvaloniaProperty.RegisterDirect<VncClientView, VncDisplayMode>(
            nameof(DisplayMode),
            control => control.DisplayMode,
            (control, value) => control.DisplayMode = value);

    public static readonly DirectProperty<VncClientView, int> ScalePercentProperty =
        AvaloniaProperty.RegisterDirect<VncClientView, int>(
            nameof(ScalePercent),
            control => control.ScalePercent,
            (control, value) => control.ScalePercent = value);

    public static readonly DirectProperty<VncClientView, bool> IsKeyboardInputEnabledProperty =
        AvaloniaProperty.RegisterDirect<VncClientView, bool>(
            nameof(IsKeyboardInputEnabled),
            control => control.IsKeyboardInputEnabled,
            (control, value) => control.IsKeyboardInputEnabled = value);

    public static readonly DirectProperty<VncClientView, bool> IsMouseInputEnabledProperty =
        AvaloniaProperty.RegisterDirect<VncClientView, bool>(
            nameof(IsMouseInputEnabled),
            control => control.IsMouseInputEnabled,
            (control, value) => control.IsMouseInputEnabled = value);

    public static readonly DirectProperty<VncClientView, bool> CaptureShortcutsProperty =
        AvaloniaProperty.RegisterDirect<VncClientView, bool>(
            nameof(CaptureShortcuts),
            control => control.CaptureShortcuts,
            (control, value) => control.CaptureShortcuts = value);

    public static readonly DirectProperty<VncClientView, string> CursorModeProperty =
        AvaloniaProperty.RegisterDirect<VncClientView, string>(
            nameof(CursorMode),
            control => control.CursorMode,
            (control, value) => control.CursorMode = value);

    public static readonly DirectProperty<VncClientView, string> ClipboardModeProperty =
        AvaloniaProperty.RegisterDirect<VncClientView, string>(
            nameof(ClipboardMode),
            control => control.ClipboardMode,
            (control, value) => control.ClipboardMode = value);

    public static readonly DirectProperty<VncClientView, string> ResizeModeProperty =
        AvaloniaProperty.RegisterDirect<VncClientView, string>(
            nameof(ResizeMode),
            control => control.ResizeMode,
            (control, value) => control.ResizeMode = value);

    private readonly HashSet<KeySymbol> _pressedKeys = [];
    private readonly object _bitmapReplacementLock = new();
    private WriteableBitmap? _bitmap;
    private bool _autoResizeRemote;
    private RfbConnection? _connection;
    private VncDisplayMode _displayMode = VncDisplayMode.Fit;
    private int _scalePercent = 100;
    private bool _isKeyboardInputEnabled = true;
    private bool _isMouseInputEnabled = true;
    private bool _captureShortcuts = true;
    private string _cursorMode = "Default";
    private string _clipboardMode = "ManualAndRemoteToLocal";
    private string _resizeMode = "None";
    private string? _lastSentClipboardText;
    private CancellationTokenSource? _resizeRequestCts;
    private RfbSize? _lastRequestedRemoteSize;
    private bool _sentConnectResize;
    private bool _disposed;

    static VncClientView()
    {
        FocusableProperty.OverrideDefaultValue(typeof(VncClientView), true);
    }

    public VncClientView()
    {
        LostFocus += (_, _) => ResetKeyPresses();
        GotFocus += (_, _) => _ = SyncLocalClipboardToRemoteAsync();
    }

    public RfbConnection? Connection
    {
        get => _connection;
        set
        {
            if (ReferenceEquals(_connection, value))
                return;

            if (_connection != null)
            {
                _connection.PropertyChanged -= OnConnectionPropertyChanged;
                if (ReferenceEquals(_connection.RenderTarget, this))
                    _connection.RenderTarget = null;
                if (ReferenceEquals(_connection.OutputHandler, this))
                    _connection.OutputHandler = null;
            }

            ResetKeyPresses();
            _lastRequestedRemoteSize = null;
            _sentConnectResize = false;

            if (value != null)
            {
                value.PropertyChanged += OnConnectionPropertyChanged;
                value.RenderTarget = this;
                value.OutputHandler = this;
            }

            SetAndRaise(ConnectionProperty, ref _connection, value);
            QueueRemoteResizeIfNeeded();
        }
    }

    public bool AutoResizeRemote
    {
        get => _autoResizeRemote;
        set => SetAndRaise(AutoResizeRemoteProperty, ref _autoResizeRemote, value);
    }

    public VncDisplayMode DisplayMode
    {
        get => _displayMode;
        set
        {
            if (SetAndRaise(DisplayModeProperty, ref _displayMode, value))
            {
                InvalidateMeasure();
                InvalidateVisual();
            }
        }
    }

    public int ScalePercent
    {
        get => _scalePercent;
        set
        {
            var clamped = Math.Clamp(value, 25, 300);
            if (SetAndRaise(ScalePercentProperty, ref _scalePercent, clamped))
            {
                InvalidateMeasure();
                InvalidateVisual();
            }
        }
    }

    public bool IsKeyboardInputEnabled
    {
        get => _isKeyboardInputEnabled;
        set => SetAndRaise(IsKeyboardInputEnabledProperty, ref _isKeyboardInputEnabled, value);
    }

    public bool IsMouseInputEnabled
    {
        get => _isMouseInputEnabled;
        set => SetAndRaise(IsMouseInputEnabledProperty, ref _isMouseInputEnabled, value);
    }

    public bool CaptureShortcuts
    {
        get => _captureShortcuts;
        set => SetAndRaise(CaptureShortcutsProperty, ref _captureShortcuts, value);
    }

    public string CursorMode
    {
        get => _cursorMode;
        set
        {
            var normalized = string.IsNullOrWhiteSpace(value) ? "Default" : value;
            if (SetAndRaise(CursorModeProperty, ref _cursorMode, normalized))
                ApplyCursorMode();
        }
    }

    public string ClipboardMode
    {
        get => _clipboardMode;
        set
        {
            var normalized = string.IsNullOrWhiteSpace(value) ? "ManualAndRemoteToLocal" : value;
            if (SetAndRaise(ClipboardModeProperty, ref _clipboardMode, normalized))
                _ = SyncLocalClipboardToRemoteAsync();
        }
    }

    public string ResizeMode
    {
        get => _resizeMode;
        set
        {
            var normalized = string.IsNullOrWhiteSpace(value) ? "None" : value;
            if (SetAndRaise(ResizeModeProperty, ref _resizeMode, normalized))
            {
                _lastRequestedRemoteSize = null;
                _sentConnectResize = false;
                QueueRemoteResizeIfNeeded();
            }
        }
    }

    public RfbFramebufferReference GrabFramebufferReference(RfbSize size, IImmutableSet<RfbScreen> layout)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var requiredPixelSize = new PixelSize(size.Width, size.Height);
        var sizeChanged = _bitmap == null || _bitmap.PixelSize != requiredPixelSize;
        WriteableBitmap bitmap;

        if (sizeChanged)
        {
            bitmap = new WriteableBitmap(requiredPixelSize, new Vector(96, 96), null);

            lock (_bitmapReplacementLock)
            {
                _bitmap?.Dispose();
                _bitmap = bitmap;
            }
        }
        else
        {
            bitmap = _bitmap!;
        }

        var lockedFramebuffer = bitmap.Lock();
        return new AvaloniaFramebufferReference(lockedFramebuffer, () =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (sizeChanged)
                    InvalidateMeasure();
                InvalidateVisual();
            });
        });
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        lock (_bitmapReplacementLock)
        {
            if (_bitmap == null)
                return;

            var destination = GetBitmapDestinationRect(_bitmap);
            context.DrawImage(_bitmap, new Rect(GetBitmapSize(_bitmap)), destination);
        }
    }

    protected override Avalonia.Size MeasureOverride(Avalonia.Size availableSize)
    {
        lock (_bitmapReplacementLock)
        {
            var bitmapSize = _bitmap == null ? new Avalonia.Size(0, 0) : GetBitmapSize(_bitmap);
            if (DisplayMode == VncDisplayMode.Original)
                return bitmapSize;
            if (DisplayMode == VncDisplayMode.FixedScale)
            {
                var scale = GetFixedScaleFactor();
                return new Avalonia.Size(bitmapSize.Width * scale, bitmapSize.Height * scale);
            }

            if (!double.IsInfinity(availableSize.Width) && !double.IsInfinity(availableSize.Height))
                return availableSize;

            if (bitmapSize.Width <= 0 || bitmapSize.Height <= 0)
            {
                return new Avalonia.Size(
                    double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width,
                    double.IsInfinity(availableSize.Height) ? 0 : availableSize.Height);
            }

            if (!double.IsInfinity(availableSize.Width))
                return new Avalonia.Size(availableSize.Width, availableSize.Width * bitmapSize.Height / bitmapSize.Width);

            if (!double.IsInfinity(availableSize.Height))
                return new Avalonia.Size(availableSize.Height * bitmapSize.Width / bitmapSize.Height, availableSize.Height);

            return bitmapSize;
        }
    }

    protected override Avalonia.Size ArrangeOverride(Avalonia.Size finalSize)
    {
        InvalidateVisual();
        QueueRemoteResizeIfNeeded();
        return finalSize;
    }

    public void RingBell()
    {
    }

    public void HandleServerClipboardUpdate(string text)
    {
        if (!ShouldSyncRemoteClipboardToLocal() || string.IsNullOrEmpty(text))
            return;

        Dispatcher.UIThread.Post(async () =>
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard != null)
                await clipboard.SetTextAsync(text);
        });
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!e.Handled && HandlePointerEvent(e.GetCurrentPoint(this), Vector.Zero))
            e.Handled = true;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        if (!e.Handled && HandlePointerEvent(e.GetCurrentPoint(this), Vector.Zero))
            e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!e.Handled && HandlePointerEvent(e.GetCurrentPoint(this), Vector.Zero))
            e.Handled = true;
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (!e.Handled && HandlePointerEvent(e.GetCurrentPoint(this), e.Delta))
            e.Handled = true;
    }

    protected override void OnTextInput(TextInputEventArgs e)
    {
        base.OnTextInput(e);
        if (e.Handled || Connection == null || !IsKeyboardInputEnabled || string.IsNullOrEmpty(e.Text))
            return;

        foreach (var c in e.Text)
        {
            if (c == '\b')
            {
                SendKeyTap(KeySymbol.BackSpace);
                continue;
            }

            var keySymbol = GetSymbolFromChar(c);
            if (!Connection.EnqueueMessage(new KeyEventMessage(true, keySymbol)))
                break;
            Connection.EnqueueMessage(new KeyEventMessage(false, keySymbol));
        }

        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (!e.Handled && TryStartLocalClipboardPaste(e))
        {
            e.Handled = true;
            return;
        }

        if (!e.Handled &&
            CaptureShortcuts &&
            e.Key != Key.None &&
            HandleKeyEvent(true, e.Key, e.KeyModifiers))
        {
            e.Handled = true;
            return;
        }

        if (!e.Handled &&
            e.Key != Key.None &&
            ShouldHandleKeyBeforeBase(e.Key) &&
            HandleKeyEvent(true, e.Key, e.KeyModifiers))
        {
            e.Handled = true;
            return;
        }

        base.OnKeyDown(e);
        if (!e.Handled && e.Key != Key.None && HandleKeyEvent(true, e.Key, e.KeyModifiers))
            e.Handled = true;
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        if (!e.Handled &&
            CaptureShortcuts &&
            e.Key != Key.None &&
            HandleKeyEvent(false, e.Key, e.KeyModifiers))
        {
            e.Handled = true;
            return;
        }

        if (!e.Handled &&
            e.Key != Key.None &&
            ShouldHandleKeyBeforeBase(e.Key) &&
            HandleKeyEvent(false, e.Key, e.KeyModifiers))
        {
            e.Handled = true;
            return;
        }

        base.OnKeyUp(e);
        if (!e.Handled && e.Key != Key.None && HandleKeyEvent(false, e.Key, e.KeyModifiers))
            e.Handled = true;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        Connection = null;
        _resizeRequestCts?.Cancel();
        _resizeRequestCts?.Dispose();
        _resizeRequestCts = null;
        lock (_bitmapReplacementLock)
        {
            _bitmap?.Dispose();
            _bitmap = null;
        }

        _disposed = true;
    }

    private bool HandlePointerEvent(PointerPoint pointerPoint, Vector wheelDelta)
    {
        var connection = Connection;
        if (connection == null || !IsMouseInputEnabled)
            return false;

        var position = GetRemotePosition(pointerPoint.Position);
        var buttonsMask = GetButtonsMask(pointerPoint.Properties);
        var wheelMask = GetWheelMask(wheelDelta);

        if (wheelMask != MouseButtons.None)
            connection.EnqueueMessage(new PointerEventMessage(position, buttonsMask | wheelMask));
        connection.EnqueueMessage(new PointerEventMessage(position, buttonsMask));

        return true;
    }

    private void OnConnectionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(RfbConnection.DesktopIsResizable) or nameof(RfbConnection.RemoteFramebufferSize))
            Dispatcher.UIThread.Post(QueueRemoteResizeIfNeeded);
    }

    private void QueueRemoteResizeIfNeeded()
    {
        if (!ShouldConsiderRemoteResize())
            return;

        _resizeRequestCts?.Cancel();
        _resizeRequestCts?.Dispose();
        _resizeRequestCts = new CancellationTokenSource();
        _ = ResizeRemoteAfterDelayAsync(_resizeRequestCts.Token);
    }

    private async Task ResizeRemoteAfterDelayAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(150, cancellationToken);
            Dispatcher.UIThread.Post(() =>
            {
                if (!cancellationToken.IsCancellationRequested)
                    TryResizeRemoteDesktop();
            });
        }
        catch (OperationCanceledException)
        {
        }
    }

    private bool ShouldConsiderRemoteResize()
    {
        var connection = Connection;
        if (connection == null ||
            connection.ConnectionState != ConnectionState.Connected ||
            !connection.DesktopIsResizable)
        {
            return false;
        }

        if (string.Equals(ResizeMode, "Dynamic", StringComparison.OrdinalIgnoreCase))
            return true;

        return string.Equals(ResizeMode, "Connect", StringComparison.OrdinalIgnoreCase) && !_sentConnectResize;
    }

    private void TryResizeRemoteDesktop()
    {
        var connection = Connection;
        if (connection == null || !ShouldConsiderRemoteResize() || !TryGetDesiredRemoteSize(out var desiredSize))
            return;

        if (SameSize(connection.RemoteFramebufferSize, desiredSize))
        {
            MarkConnectResizeSent();
            return;
        }

        if (_lastRequestedRemoteSize.HasValue && SameSize(_lastRequestedRemoteSize.Value, desiredSize))
        {
            MarkConnectResizeSent();
            return;
        }

        var layout = CreateSingleScreenLayout(desiredSize, connection.RemoteFramebufferLayout);
        if (connection.EnqueueMessage(new SetDesktopSizeMessage((_, _) => (desiredSize, layout))))
        {
            _lastRequestedRemoteSize = desiredSize;
            MarkConnectResizeSent();
        }
    }

    private bool TryGetDesiredRemoteSize(out RfbSize desiredSize)
    {
        desiredSize = default;

        var width = (int)Math.Round(Bounds.Width);
        var height = (int)Math.Round(Bounds.Height);
        if (width < 64 || height < 64)
            return false;

        desiredSize = new RfbSize(Math.Clamp(width, 64, 8192), Math.Clamp(height, 64, 8192));
        return true;
    }

    private void MarkConnectResizeSent()
    {
        if (string.Equals(ResizeMode, "Connect", StringComparison.OrdinalIgnoreCase))
            _sentConnectResize = true;
    }

    private static bool SameSize(RfbSize left, RfbSize right)
    {
        return left.Width == right.Width && left.Height == right.Height;
    }

    private static IImmutableSet<RfbScreen> CreateSingleScreenLayout(RfbSize size, IImmutableSet<RfbScreen> currentLayout)
    {
        var id = 0u;
        var flags = 0u;
        if (currentLayout.Count > 0)
        {
            var first = currentLayout.First();
            id = first.Id;
            flags = first.Flags;
        }

        return ImmutableHashSet.Create(new RfbScreen(
            id,
            new RfbRectangle(0, 0, size.Width, size.Height),
            flags));
    }

    private RfbPosition GetRemotePosition(Point point)
    {
        lock (_bitmapReplacementLock)
        {
            if (_bitmap == null)
                return new RfbPosition((int)point.X, (int)point.Y);

            var destination = GetBitmapDestinationRect(_bitmap);
            var bitmapSize = GetBitmapSize(_bitmap);
            var scaleX = destination.Width <= 0 ? 1d : bitmapSize.Width / destination.Width;
            var scaleY = destination.Height <= 0 ? 1d : bitmapSize.Height / destination.Height;
            var x = (point.X - destination.X) * scaleX;
            var y = (point.Y - destination.Y) * scaleY;
            x = Math.Clamp(x, 0, Math.Max(0, bitmapSize.Width - 1));
            y = Math.Clamp(y, 0, Math.Max(0, bitmapSize.Height - 1));
            return new RfbPosition((int)x, (int)y);
        }
    }

    private Rect GetBitmapDestinationRect(WriteableBitmap bitmap)
    {
        var bounds = Bounds;
        var bitmapSize = GetBitmapSize(bitmap);
        if (DisplayMode == VncDisplayMode.Original)
            return new Rect(0, 0, bitmapSize.Width, bitmapSize.Height);

        if (DisplayMode == VncDisplayMode.FixedScale)
        {
            var fixedScale = GetFixedScaleFactor();
            var fixedWidth = bitmapSize.Width * fixedScale;
            var fixedHeight = bitmapSize.Height * fixedScale;
            var fixedX = Math.Max(0, (bounds.Width - fixedWidth) / 2d);
            var fixedY = Math.Max(0, (bounds.Height - fixedHeight) / 2d);
            return new Rect(fixedX, fixedY, fixedWidth, fixedHeight);
        }

        if (bounds.Width <= 0 || bounds.Height <= 0)
            return new Rect(bitmapSize);

        var scale = Math.Min(bounds.Width / bitmapSize.Width, bounds.Height / bitmapSize.Height);
        if (double.IsNaN(scale) || double.IsInfinity(scale) || scale <= 0)
            scale = 1d;

        var width = bitmapSize.Width * scale;
        var height = bitmapSize.Height * scale;
        var x = (bounds.Width - width) / 2d;
        var y = (bounds.Height - height) / 2d;
        return new Rect(x, y, width, height);
    }

    private double GetFixedScaleFactor()
    {
        return Math.Clamp(ScalePercent, 25, 300) / 100d;
    }

    private void ApplyCursorMode()
    {
        Cursor = CursorMode switch
        {
            "Hidden" => new Cursor(StandardCursorType.None),
            "Crosshair" => new Cursor(StandardCursorType.Cross),
            _ => null
        };
    }

    private static Avalonia.Size GetBitmapSize(WriteableBitmap bitmap)
    {
        return new Avalonia.Size(bitmap.PixelSize.Width, bitmap.PixelSize.Height);
    }

    private static MouseButtons GetButtonsMask(PointerPointProperties pointProperties)
    {
        var mask = MouseButtons.None;
        if (pointProperties.IsLeftButtonPressed)
            mask |= MouseButtons.Left;
        if (pointProperties.IsMiddleButtonPressed)
            mask |= MouseButtons.Middle;
        if (pointProperties.IsRightButtonPressed)
            mask |= MouseButtons.Right;
        return mask;
    }

    private static MouseButtons GetWheelMask(Vector wheelDelta)
    {
        var mask = MouseButtons.None;
        if (wheelDelta.X > 0)
            mask |= MouseButtons.WheelRight;
        else if (wheelDelta.X < 0)
            mask |= MouseButtons.WheelLeft;
        if (wheelDelta.Y > 0)
            mask |= MouseButtons.WheelUp;
        else if (wheelDelta.Y < 0)
            mask |= MouseButtons.WheelDown;
        return mask;
    }

    private bool HandleKeyEvent(bool downFlag, Key key, KeyModifiers keyModifiers)
    {
        var connection = Connection;
        if (connection == null || !IsKeyboardInputEnabled)
            return false;

        var includePrintable = (keyModifiers & KeyModifiers.Control) != 0;
        var keySymbol = GetSymbolFromKey(key, includePrintable);
        if (keySymbol == KeySymbol.Null)
            return false;

        var queued = connection.EnqueueMessage(new KeyEventMessage(downFlag, keySymbol));
        if (downFlag && queued)
            _pressedKeys.Add(keySymbol);
        else if (!downFlag)
            _pressedKeys.Remove(keySymbol);

        return queued;
    }

    private void SendKeyTap(KeySymbol keySymbol)
    {
        var connection = Connection;
        if (connection == null || !IsKeyboardInputEnabled)
            return;

        connection.EnqueueMessage(new KeyEventMessage(true, keySymbol));
        connection.EnqueueMessage(new KeyEventMessage(false, keySymbol));
    }

    private static bool ShouldHandleKeyBeforeBase(Key key)
    {
        return key switch
        {
            Key.Back or
            Key.Delete or
            Key.Tab or
            Key.Return or
            Key.Escape or
            Key.Left or
            Key.Up or
            Key.Right or
            Key.Down or
            Key.Home or
            Key.End or
            Key.Prior or
            Key.PageDown or
            Key.Insert => true,
            _ => false
        };
    }

    private async Task SyncLocalClipboardToRemoteAsync()
    {
        if (!ShouldSyncLocalClipboardToRemote() || Connection == null)
            return;

        try
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            var text = clipboard == null ? null : await clipboard.TryGetTextAsync();
            if (string.IsNullOrEmpty(text) || string.Equals(text, _lastSentClipboardText, StringComparison.Ordinal))
                return;
            if (Connection.SendClipboardText(text, CancellationToken.None))
                _lastSentClipboardText = text;
        }
        catch
        {
            // Clipboard access can fail on some desktop backends.
        }
    }

    private bool ShouldSyncLocalClipboardToRemote()
    {
        return string.Equals(ClipboardMode, "LocalToRemote", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(ClipboardMode, "Bidirectional", StringComparison.OrdinalIgnoreCase);
    }

    private bool ShouldSyncRemoteClipboardToLocal()
    {
        return string.Equals(ClipboardMode, "ManualAndRemoteToLocal", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(ClipboardMode, "RemoteToLocal", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(ClipboardMode, "Bidirectional", StringComparison.OrdinalIgnoreCase);
    }

    private bool TryStartLocalClipboardPaste(KeyEventArgs e)
    {
        if (Connection == null ||
            e.Key != Key.V ||
            (e.KeyModifiers & KeyModifiers.Control) == 0 ||
            (e.KeyModifiers & KeyModifiers.Alt) != 0)
        {
            return false;
        }

        _ = PasteLocalClipboardAsync();
        return true;
    }

    private async Task PasteLocalClipboardAsync()
    {
        try
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            var text = clipboard == null ? null : await clipboard.TryGetTextAsync();
            if (string.IsNullOrEmpty(text) || Connection == null)
                return;

            ResetKeyPresses();
            await ReleaseCommonModifiersAsync();
            await Task.Delay(30);

            var needsDirectUnicodeInput = NeedsDirectUnicodeInput(text);
            if (needsDirectUnicodeInput && Connection.ServerSupportsExtendedClipboard)
            {
                if (Connection.SendClipboardText(text, CancellationToken.None))
                {
                    _lastSentClipboardText = text;
                    await Task.Delay(120);
                    await SendCtrlVAsync();
                    return;
                }
            }

            if (needsDirectUnicodeInput)
            {
                if (Connection.SendClipboardText(text, CancellationToken.None))
                {
                    _lastSentClipboardText = text;
                    await Task.Delay(120);
                    await SendCtrlVAsync();
                    return;
                }

                await TypeTextAsync(text);
                return;
            }

            if (Connection.SendClipboardText(text, CancellationToken.None))
                _lastSentClipboardText = text;
            await Task.Delay(80);
            await SendCtrlVAsync();
        }
        catch
        {
            // Clipboard access can fail on some desktop backends.
        }
    }

    private async Task ReleaseCommonModifiersAsync()
    {
        var connection = Connection;
        if (connection == null)
            return;

        var modifiers = new[]
        {
            KeySymbol.Shift_L,
            KeySymbol.Shift_R,
            KeySymbol.Control_L,
            KeySymbol.Control_R,
            KeySymbol.Alt_L,
            KeySymbol.Alt_R,
            KeySymbol.Super_L,
            KeySymbol.Super_R
        };

        foreach (var modifier in modifiers)
        {
            if (!connection.EnqueueMessage(new KeyEventMessage(false, modifier)))
                break;
            await Task.Delay(1);
        }
    }

    private async Task TypeTextAsync(string text)
    {
        var normalizedText = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        foreach (var rune in normalizedText.EnumerateRunes())
        {
            var keySymbol = GetSymbolFromRune(rune);
            await SendKeyTapAsync(keySymbol);
            await Task.Delay(5);
        }
    }

    private async Task SendCtrlVAsync()
    {
        var connection = Connection;
        if (connection == null)
            return;

        try
        {
            connection.EnqueueMessage(new KeyEventMessage(true, KeySymbol.Control_L));
            await Task.Delay(20);
            connection.EnqueueMessage(new KeyEventMessage(true, KeySymbol.v));
            await Task.Delay(40);
        }
        finally
        {
            Connection?.EnqueueMessage(new KeyEventMessage(false, KeySymbol.v));
            Connection?.EnqueueMessage(new KeyEventMessage(false, KeySymbol.Control_L));
        }
    }

    private async Task SendKeyTapAsync(KeySymbol keySymbol)
    {
        var connection = Connection;
        if (connection == null)
            return;

        connection.EnqueueMessage(new KeyEventMessage(true, keySymbol));
        await Task.Delay(5);
        Connection?.EnqueueMessage(new KeyEventMessage(false, keySymbol));
    }

    private static KeySymbol GetSymbolFromRune(Rune rune)
    {
        return rune.Value switch
        {
            '\n' => KeySymbol.Return,
            '\t' => KeySymbol.Tab,
            >= 0x20 and <= 0x7e => (KeySymbol)rune.Value,
            _ => (KeySymbol)(0x01000000 | rune.Value)
        };
    }

    private static bool NeedsDirectUnicodeInput(string text)
    {
        foreach (var rune in text.EnumerateRunes())
        {
            if (rune.Value > 0xff)
                return true;
        }

        return false;
    }

    private void ResetKeyPresses()
    {
        var connection = Connection;
        if (connection != null)
        {
            foreach (var keySymbol in _pressedKeys)
            {
                if (!connection.EnqueueMessage(new KeyEventMessage(false, keySymbol)))
                    break;
            }
        }

        _pressedKeys.Clear();
    }

    private static KeySymbol GetSymbolFromKey(Key key, bool includePrintable = true)
    {
        var keySymbol = key switch
        {
            Key.Cancel => KeySymbol.Cancel,
            Key.Back => KeySymbol.BackSpace,
            Key.Tab => KeySymbol.Tab,
            Key.LineFeed => KeySymbol.Linefeed,
            Key.Clear => KeySymbol.Clear,
            Key.Return => KeySymbol.Return,
            Key.Pause => KeySymbol.Pause,
            Key.CapsLock => KeySymbol.Caps_Lock,
            Key.Escape => KeySymbol.Escape,
            Key.Prior => KeySymbol.Prior,
            Key.PageDown => KeySymbol.Page_Down,
            Key.End => KeySymbol.End,
            Key.Home => KeySymbol.Home,
            Key.Left => KeySymbol.Left,
            Key.Up => KeySymbol.Up,
            Key.Right => KeySymbol.Right,
            Key.Down => KeySymbol.Down,
            Key.Select => KeySymbol.Select,
            Key.Print => KeySymbol.Print,
            Key.Execute => KeySymbol.Execute,
            Key.Insert => KeySymbol.Insert,
            Key.Delete => KeySymbol.Delete,
            Key.Help => KeySymbol.Help,
            Key.LWin => KeySymbol.Super_L,
            Key.RWin => KeySymbol.Super_R,
            Key.Apps => KeySymbol.Menu,
            Key.F1 => KeySymbol.F1,
            Key.F2 => KeySymbol.F2,
            Key.F3 => KeySymbol.F3,
            Key.F4 => KeySymbol.F4,
            Key.F5 => KeySymbol.F5,
            Key.F6 => KeySymbol.F6,
            Key.F7 => KeySymbol.F7,
            Key.F8 => KeySymbol.F8,
            Key.F9 => KeySymbol.F9,
            Key.F10 => KeySymbol.F10,
            Key.F11 => KeySymbol.F11,
            Key.F12 => KeySymbol.F12,
            Key.F13 => KeySymbol.F13,
            Key.F14 => KeySymbol.F14,
            Key.F15 => KeySymbol.F15,
            Key.F16 => KeySymbol.F16,
            Key.F17 => KeySymbol.F17,
            Key.F18 => KeySymbol.F18,
            Key.F19 => KeySymbol.F19,
            Key.F20 => KeySymbol.F20,
            Key.F21 => KeySymbol.F21,
            Key.F22 => KeySymbol.F22,
            Key.F23 => KeySymbol.F23,
            Key.F24 => KeySymbol.F24,
            Key.NumLock => KeySymbol.Num_Lock,
            Key.Scroll => KeySymbol.Scroll_Lock,
            Key.LeftShift => KeySymbol.Shift_L,
            Key.RightShift => KeySymbol.Shift_R,
            Key.LeftCtrl => KeySymbol.Control_L,
            Key.RightCtrl => KeySymbol.Control_R,
            Key.LeftAlt => KeySymbol.Alt_L,
            Key.RightAlt => KeySymbol.Alt_R,
            _ => KeySymbol.Null
        };

        if (keySymbol != KeySymbol.Null || !includePrintable)
            return keySymbol;

        return key switch
        {
            Key.Space => KeySymbol.space,
            Key.A => KeySymbol.a,
            Key.B => KeySymbol.b,
            Key.C => KeySymbol.c,
            Key.D => KeySymbol.d,
            Key.E => KeySymbol.e,
            Key.F => KeySymbol.f,
            Key.G => KeySymbol.g,
            Key.H => KeySymbol.h,
            Key.I => KeySymbol.i,
            Key.J => KeySymbol.j,
            Key.K => KeySymbol.k,
            Key.L => KeySymbol.l,
            Key.M => KeySymbol.m,
            Key.N => KeySymbol.n,
            Key.O => KeySymbol.o,
            Key.P => KeySymbol.p,
            Key.Q => KeySymbol.q,
            Key.R => KeySymbol.r,
            Key.S => KeySymbol.s,
            Key.T => KeySymbol.t,
            Key.U => KeySymbol.u,
            Key.V => KeySymbol.v,
            Key.W => KeySymbol.w,
            Key.X => KeySymbol.x,
            Key.Y => KeySymbol.y,
            Key.Z => KeySymbol.z,
            Key.NumPad0 => KeySymbol.KP_0,
            Key.NumPad1 => KeySymbol.KP_1,
            Key.NumPad2 => KeySymbol.KP_2,
            Key.NumPad3 => KeySymbol.KP_3,
            Key.NumPad4 => KeySymbol.KP_4,
            Key.NumPad5 => KeySymbol.KP_5,
            Key.NumPad6 => KeySymbol.KP_6,
            Key.NumPad7 => KeySymbol.KP_7,
            Key.NumPad8 => KeySymbol.KP_8,
            Key.NumPad9 => KeySymbol.KP_9,
            Key.Multiply => KeySymbol.KP_Multiply,
            Key.Add => KeySymbol.KP_Add,
            Key.Subtract => KeySymbol.KP_Subtract,
            Key.Decimal => KeySymbol.KP_Decimal,
            Key.Divide => KeySymbol.KP_Divide,
            Key.D1 => KeySymbol.XK_1,
            Key.D2 => KeySymbol.XK_2,
            Key.D3 => KeySymbol.XK_3,
            Key.D4 => KeySymbol.XK_4,
            Key.D5 => KeySymbol.XK_5,
            Key.D6 => KeySymbol.XK_6,
            Key.D7 => KeySymbol.XK_7,
            Key.D8 => KeySymbol.XK_8,
            Key.D9 => KeySymbol.XK_9,
            Key.D0 => KeySymbol.XK_0,
            _ => KeySymbol.Null
        };
    }

    private static KeySymbol GetSymbolFromChar(char c)
    {
        if (c is >= ' ' and <= '~')
            return KeySymbol.space + (c - ' ');
        return (KeySymbol)(0x1000000 | c);
    }

    private sealed class AvaloniaFramebufferReference : RfbFramebufferReference
    {
        private ILockedFramebuffer? _lockedFramebuffer;
        private readonly Action _invalidateVisual;

        public AvaloniaFramebufferReference(ILockedFramebuffer lockedFramebuffer, Action invalidateVisual)
        {
            _lockedFramebuffer = lockedFramebuffer;
            _invalidateVisual = invalidateVisual;
        }

        public IntPtr Address => _lockedFramebuffer?.Address ?? throw new ObjectDisposedException(nameof(AvaloniaFramebufferReference));
        public RfbSize Size => GetRfbSize(_lockedFramebuffer?.Size ?? throw new ObjectDisposedException(nameof(AvaloniaFramebufferReference)));
        public RfbPixelFormat Format => GetRfbPixelFormat(_lockedFramebuffer?.Format ?? throw new ObjectDisposedException(nameof(AvaloniaFramebufferReference)));
        public double HorizontalDpi => _lockedFramebuffer?.Dpi.X ?? throw new ObjectDisposedException(nameof(AvaloniaFramebufferReference));
        public double VerticalDpi => _lockedFramebuffer?.Dpi.Y ?? throw new ObjectDisposedException(nameof(AvaloniaFramebufferReference));

        public void Dispose()
        {
            var lockedFramebuffer = _lockedFramebuffer;
            _lockedFramebuffer = null;
            if (lockedFramebuffer == null)
                return;

            lockedFramebuffer.Dispose();
            _invalidateVisual();
        }

        private static RfbSize GetRfbSize(PixelSize pixelSize)
        {
            return new RfbSize(pixelSize.Width, pixelSize.Height);
        }

        private static RfbPixelFormat GetRfbPixelFormat(AvPixelFormat pixelFormat)
        {
            if (pixelFormat == AvPixelFormat.Rgb565)
                return new RfbPixelFormat("Avalonia RGB565", 16, 16, false, true, false, 31, 63, 31, 0, 11, 5, 0, 0);
            if (pixelFormat == AvPixelFormat.Rgba8888)
                return new RfbPixelFormat("Avalonia RGBA8888", 32, 32, false, true, true, 0xFF, 0xFF, 0xFF, 0xFF, 0, 8, 16, 24);
            if (pixelFormat == AvPixelFormat.Bgra8888)
                return new RfbPixelFormat("Avalonia BGRA8888", 32, 32, false, true, true, 0xFF, 0xFF, 0xFF, 0xFF, 16, 8, 0, 24);
            throw new ArgumentException($"Unsupported Avalonia pixel format: {pixelFormat}", nameof(pixelFormat));
        }
    }
}
