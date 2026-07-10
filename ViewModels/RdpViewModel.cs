using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using CxShell.Models;
using CxShell.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CxShell.ViewModels;

public partial class RdpViewModel : ObservableObject, IDisposable
{
    private readonly RdpBridgeClient _client = new();
    private bool _started;
    private CancellationTokenSource? _resizeReconnectCts;
    private int? _runtimeDesktopWidth;
    private int? _runtimeDesktopHeight;

    [ObservableProperty] private WriteableBitmap? _framebuffer;
    [ObservableProperty] private string _statusText = "RDP disconnected";
    [ObservableProperty] private bool _isConnected;
    [ObservableProperty] private int _remoteWidth;
    [ObservableProperty] private int _remoteHeight;
    [ObservableProperty] private bool _isFitToWindow = true;
    [ObservableProperty] private int _screenScalePercent = 100;
    [ObservableProperty] private bool _isClipboardChannelReady;
    [ObservableProperty] private string _remoteClipboardText = string.Empty;

    public SessionInfo Session { get; }
    public string? Password { get; }
    public double FixedScaleFactor => Math.Clamp(ScreenScalePercent, 10, 500) / 100.0;
    public string ScaleModeText => IsFitToWindow
        ? "Fit to window"
        : ScreenScalePercent == 100 ? "Original size" : $"{ScreenScalePercent}%";

    public RdpViewModel(SessionInfo session, string? password)
    {
        Session = session;
        Password = password;
        ScreenScalePercent = ResolveScreenScalePercent(session);
        IsFitToWindow = ResolveInitialFitToWindow(session, ScreenScalePercent);
        _client.FramebufferUpdated += OnFramebufferUpdated;
        _client.StatusChanged += message => Dispatcher.UIThread.Post(() => HandleStatus(message));
        _client.ClipboardTextReceived += OnClipboardTextReceived;
        _client.Disconnected += () => Dispatcher.UIThread.Post(() =>
        {
            _started = false;
            IsConnected = false;
            IsClipboardChannelReady = false;
            StatusText = "RDP disconnected";
        });
    }

    public void Start()
    {
        if (_started || IsConnected)
            return;

        _started = true;
        StatusText = "Starting RDP bridge...";

        _ = Task.Run(() =>
        {
            try
            {
                _client.Connect(Session, Password, _runtimeDesktopWidth, _runtimeDesktopHeight);
                Dispatcher.UIThread.Post(() => IsConnected = false);
            }
            catch (DllNotFoundException ex)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    _started = false;
                    StatusText = $"RDP native library load failed: {RdpBridgeClient.GetNativeLibraryLoadErrorMessage(ex)}";
                });
            }
            catch (EntryPointNotFoundException ex)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    _started = false;
                    StatusText = $"CxRdpBridge API mismatch: {ex.Message}";
                });
            }
            catch (Exception ex)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    _started = false;
                    StatusText = $"RDP failed: {ex.Message}";
                });
            }
        });
    }

    private void HandleStatus(string message)
    {
        StatusText = GetDisplayStatusText(message);

        if (message.StartsWith("RDP clipboard channel ready", StringComparison.OrdinalIgnoreCase))
        {
            IsClipboardChannelReady = true;
            return;
        }

        if (string.Equals(message, "RDP connected.", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(message, "RDP connected", StringComparison.OrdinalIgnoreCase))
        {
            IsConnected = true;
            _started = true;
            return;
        }

        if (message.Contains("failed", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("disconnected", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("closed", StringComparison.OrdinalIgnoreCase))
        {
            IsConnected = false;
            _started = false;
            IsClipboardChannelReady = false;
        }
    }

    private static string GetDisplayStatusText(string message)
    {
        if (string.Equals(message, "RDP clipboard waiting for server MonitorReady.", StringComparison.OrdinalIgnoreCase))
            return "Remote clipboard service is not responding. Restart rdpclip.exe on the remote Windows session.";

        if (message.StartsWith("RDP clipboard channel ready", StringComparison.OrdinalIgnoreCase))
            return "RDP clipboard ready";

        return message;
    }

    public void Reconnect()
    {
        _resizeReconnectCts?.Cancel();
        Disconnect();
        _started = false;
        Start();
    }

    public void Disconnect()
    {
        _resizeReconnectCts?.Cancel();
        _client.Disconnect();
        _started = false;
        IsConnected = false;
        IsClipboardChannelReady = false;
        StatusText = "RDP disconnected";
    }

    public void ConfigureInitialDisplay(Size viewportSize, PixelSize? monitorPixelSize, double renderScaling)
    {
        if (_started || IsConnected)
            return;

        var size = RdpDisplaySizeResolver.ResolveInitial(
            Session.RdpWindowSize,
            viewportSize,
            monitorPixelSize,
            renderScaling);
        if (size == null)
            return;

        _runtimeDesktopWidth = size.Value.Width;
        _runtimeDesktopHeight = size.Value.Height;
    }

    public void RequestViewportResize(Size viewportSize, double renderScaling = 1)
    {
        if (!IsConnected ||
            !UsesReconnectResizeMode(Session) ||
            viewportSize.Width < 320 ||
            viewportSize.Height < 240)
        {
            return;
        }

        var size = RdpDisplaySizeResolver.ResolveViewport(viewportSize, renderScaling);
        if (size == null)
            return;

        var width = size.Value.Width;
        var height = size.Value.Height;
        var currentWidth = _runtimeDesktopWidth ?? Math.Max(1, Session.RdpDesktopWidth);
        var currentHeight = _runtimeDesktopHeight ?? Math.Max(1, Session.RdpDesktopHeight);
        if (Math.Abs(width - currentWidth) < 32 && Math.Abs(height - currentHeight) < 32)
            return;

        _resizeReconnectCts?.Cancel();
        var cts = new CancellationTokenSource();
        _resizeReconnectCts = cts;
        _ = ReconnectAfterResizeDelayAsync(width, height, cts.Token);
    }

    private async Task ReconnectAfterResizeDelayAsync(int width, int height, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(900), cancellationToken).ConfigureAwait(false);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (cancellationToken.IsCancellationRequested || !IsConnected)
                    return;

                _runtimeDesktopWidth = width;
                _runtimeDesktopHeight = height;
                StatusText = $"RDP resizing to {width}x{height}...";
                Reconnect();
            });
        }
        catch (OperationCanceledException)
        {
        }
    }

    public void SendPointer(ushort flags, ushort x, ushort y)
    {
        if (IsConnected)
            _client.SendPointer(flags, x, y);
    }

    public void SendKey(uint key, bool down)
    {
        if (IsConnected)
            _client.SendKey(key, down);
    }

    public void SendUnicodeKey(char key, bool down)
    {
        if (IsConnected)
            _client.SendUnicodeKey(key, down);
    }

    public Task SendCtrlAltDeleteAsync()
    {
        return SendKeyCombinationAsync(RdpKeyboardShortcutSequences.CtrlAltDelete);
    }

    public Task SendAltTabAsync()
    {
        return SendKeyCombinationAsync(0x38, 0x0F);
    }

    public Task SendWindowsKeyAsync()
    {
        return SendKeyCombinationAsync(0x0100 | 0x5B);
    }

    public Task SendCtrlEscapeAsync()
    {
        return SendKeyCombinationAsync(0x1D, 0x01);
    }

    public Task SendAltF4Async()
    {
        return SendKeyCombinationAsync(0x38, 0x3E);
    }

    public Task SendTaskManagerAsync()
    {
        return SendKeyCombinationAsync(0x1D, 0x2A, 0x01);
    }

    public Task SendPrintScreenAsync()
    {
        return SendKeyCombinationAsync(RdpKeyboardShortcutSequences.SaveRemoteScreenshot);
    }

    private async Task SendKeyCombinationAsync(params uint[] scancodes)
    {
        await SendKeyCombinationAsync((IReadOnlyList<uint>)scancodes);
    }

    private async Task SendKeyCombinationAsync(IReadOnlyList<uint> scancodes)
    {
        if (!IsConnected || scancodes.Count == 0)
            return;

        try
        {
            foreach (var scancode in scancodes)
            {
                _client.SendKey(scancode, true);
                await Task.Delay(20);
            }

            await Task.Delay(40);
        }
        finally
        {
            for (var i = scancodes.Count - 1; i >= 0; i--)
            {
                _client.SendKey(scancodes[i], false);
                await Task.Delay(10);
            }
        }
    }

    public void SetClipboardText(string text)
    {
        if (IsConnected)
            _client.SetClipboardText(text);
    }

    [RelayCommand]
    private void ToggleScaleMode()
    {
        IsFitToWindow = !IsFitToWindow;
    }

    partial void OnIsFitToWindowChanged(bool value)
    {
        OnPropertyChanged(nameof(ScaleModeText));
        OnPropertyChanged(nameof(FixedScaleFactor));
    }

    partial void OnScreenScalePercentChanged(int value)
    {
        OnPropertyChanged(nameof(ScaleModeText));
        OnPropertyChanged(nameof(FixedScaleFactor));
    }

    private static bool ResolveInitialFitToWindow(SessionInfo session, int screenScalePercent)
    {
        if (!string.Equals(session.RdpScreenScale, "Auto", StringComparison.OrdinalIgnoreCase) &&
            screenScalePercent > 0)
        {
            return false;
        }

        return !string.Equals(session.RdpResizeMode, "NotUsed", StringComparison.OrdinalIgnoreCase);
    }

    private static bool UsesReconnectResizeMode(SessionInfo session)
    {
        return string.Equals(session.RdpResizeMode, "SmartReconnect", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(session.RdpResizeMode, "LegacyReconnect", StringComparison.OrdinalIgnoreCase);
    }

    private static int ResolveScreenScalePercent(SessionInfo session)
    {
        return int.TryParse(session.RdpScreenScale, out var parsed) && parsed is >= 10 and <= 500
            ? parsed
            : 100;
    }

    private void OnFramebufferUpdated(object? sender, RdpFramebufferEventArgs e)
    {
        var pixels = new byte[e.Pixels.Length];
        Buffer.BlockCopy(e.Pixels, 0, pixels, 0, pixels.Length);

        Dispatcher.UIThread.Post(() =>
        {
            var bitmap = new WriteableBitmap(
                new PixelSize(e.Width, e.Height),
                new Vector(96, 96),
                PixelFormat.Bgra8888,
                AlphaFormat.Opaque);

            using (var locked = bitmap.Lock())
            {
                var copyBytes = Math.Min(pixels.Length, locked.RowBytes * locked.Size.Height);
                Marshal.Copy(pixels, 0, locked.Address, copyBytes);
            }

            Framebuffer = bitmap;
            RemoteWidth = e.Width;
            RemoteHeight = e.Height;
        });
    }

    private void OnClipboardTextReceived(string text)
    {
        Dispatcher.UIThread.Post(() => RemoteClipboardText = text);
    }

    public void Dispose()
    {
        _resizeReconnectCts?.Cancel();
        _resizeReconnectCts?.Dispose();
        _client.FramebufferUpdated -= OnFramebufferUpdated;
        _client.ClipboardTextReceived -= OnClipboardTextReceived;
        _client.Dispose();
    }
}
