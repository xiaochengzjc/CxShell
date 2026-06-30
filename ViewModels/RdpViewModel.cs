using System;
using System.Runtime.InteropServices;
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

    [ObservableProperty] private WriteableBitmap? _framebuffer;
    [ObservableProperty] private string _statusText = "RDP disconnected";
    [ObservableProperty] private bool _isConnected;
    [ObservableProperty] private int _remoteWidth;
    [ObservableProperty] private int _remoteHeight;
    [ObservableProperty] private bool _isFitToWindow = true;
    [ObservableProperty] private bool _isClipboardChannelReady;
    [ObservableProperty] private string _remoteClipboardText = string.Empty;

    public SessionInfo Session { get; }
    public string? Password { get; }
    public string ScaleModeText => IsFitToWindow ? "Fit to window" : "Original size";

    public RdpViewModel(SessionInfo session, string? password)
    {
        Session = session;
        Password = password;
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
                _client.Connect(Session, Password);
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
        Disconnect();
        _started = false;
        Start();
    }

    public void Disconnect()
    {
        _client.Disconnect();
        _started = false;
        IsConnected = false;
        IsClipboardChannelReady = false;
        StatusText = "RDP disconnected";
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
        _client.FramebufferUpdated -= OnFramebufferUpdated;
        _client.ClipboardTextReceived -= OnClipboardTextReceived;
        _client.Dispose();
    }
}
