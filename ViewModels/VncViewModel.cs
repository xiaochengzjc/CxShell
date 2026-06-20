using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using ChiXueSsh.Models;
using ChiXueSsh.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ChiXueSsh.ViewModels;

public partial class VncViewModel : ObservableObject, IDisposable
{
    private readonly VncClientService _client = new();
    private CancellationTokenSource? _connectCts;

    [ObservableProperty] private WriteableBitmap? _framebuffer;
    [ObservableProperty] private string _statusText = "Disconnected";
    [ObservableProperty] private bool _isConnected;
    [ObservableProperty] private int _remoteWidth;
    [ObservableProperty] private int _remoteHeight;
    [ObservableProperty] private string _remoteClipboardText = string.Empty;
    [ObservableProperty] private bool _isFitToWindow = true;

    public string ScaleModeText => IsFitToWindow ? "适应窗口" : "原始大小";

    public VncViewModel()
    {
        _client.FramebufferUpdated += (_, e) =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                Framebuffer = null;
                Framebuffer = e.Bitmap;
                OnPropertyChanged(nameof(Framebuffer));
                RemoteWidth = e.Width;
                RemoteHeight = e.Height;
            });
        };
        _client.StatusChanged += message => Dispatcher.UIThread.Post(() => StatusText = message);
        _client.ErrorOccurred += message => Dispatcher.UIThread.Post(() => StatusText = $"VNC error: {message}");
        _client.ClipboardTextReceived += text => Dispatcher.UIThread.Post(() => RemoteClipboardText = text);
        _client.Disconnected += () => Dispatcher.UIThread.Post(() => IsConnected = false);
    }

    partial void OnIsFitToWindowChanged(bool value)
    {
        OnPropertyChanged(nameof(ScaleModeText));
    }

    public async Task ConnectAsync(SessionInfo session, string? password)
    {
        _connectCts?.Cancel();
        _connectCts?.Dispose();
        _connectCts = new CancellationTokenSource();

        IsConnected = false;
        StatusText = "Connecting...";
        await _client.ConnectAsync(session, password, _connectCts.Token);
        IsConnected = true;
    }

    public void SendPointer(byte buttonMask, int x, int y)
    {
        _client.SendPointer(buttonMask, x, y);
    }

    public void SendKey(uint keySym, bool down)
    {
        _client.SendKey(keySym, down);
    }

    public void SendClipboardText(string text)
    {
        _client.SendClipboardText(text);
    }

    [RelayCommand]
    private void ToggleScaleMode()
    {
        IsFitToWindow = !IsFitToWindow;
    }

    public void Disconnect()
    {
        _connectCts?.Cancel();
        _connectCts?.Dispose();
        _connectCts = null;
        _client.Disconnect();
        IsConnected = false;
        StatusText = "Disconnected";
    }

    public void Dispose()
    {
        Disconnect();
        _client.Dispose();
    }
}
