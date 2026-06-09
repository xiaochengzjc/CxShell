using System;
using System.Threading.Tasks;
using Avalonia.Threading;
using ChiXueSsh.Models;
using ChiXueSsh.Services;
using ChiXueSsh.Terminal;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ChiXueSsh.ViewModels;

public partial class TerminalViewModel : ObservableObject
{
    [ObservableProperty] private bool _isConnected;
    [ObservableProperty] private string _hostInfo = string.Empty;
    [ObservableProperty] private int _columns = 80;
    [ObservableProperty] private int _rows = 24;

    public TerminalBuffer Buffer { get; private set; }
    public AnsiParser Parser { get; private set; }

    private SshConnectionService? _ssh;

    public TerminalViewModel()
    {
        Buffer = new TerminalBuffer(Columns, Rows);
        Parser = new AnsiParser(Buffer);
    }

    // 当 Buffer 数据变化时触发，供 View 层订阅以刷新渲染
    public event Action? BufferChanged;

    public async Task ConnectAsync(SessionInfo session, string? password)
    {
        Disconnect();

        Buffer = new TerminalBuffer(Columns, Rows);
        Parser = new AnsiParser(Buffer);
        OnPropertyChanged(nameof(Buffer));

        _ssh = new SshConnectionService();

        _ssh.DataReceived += data =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                Parser.Process(data);
                Buffer.MarkAllDirty();
                BufferChanged?.Invoke();
            });
        };

        _ssh.ConnectionClosed += reason =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                IsConnected = false;
                HostInfo = string.Empty;
                Parser.Process($"\r\n\x1B[31m[连接已断开: {reason}]\x1B[0m\r\n");
                Buffer.MarkAllDirty();
                BufferChanged?.Invoke();
            });
        };

        _ssh.ErrorOccurred += error =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                Parser.Process($"\r\n\x1B[31m[错误: {error}]\x1B[0m\r\n");
                Buffer.MarkAllDirty();
                BufferChanged?.Invoke();
            });
        };

        await _ssh.ConnectAsync(
            session.Host, session.Port, session.Username,
            session.AuthMethod, password, session.PrivateKeyPath,
            Columns, Rows);

        IsConnected = true;
        HostInfo = $"{session.Username}@{session.Host}:{session.Port}";
    }

    public void SendInput(string data)
    {
        _ssh?.SendData(data);
    }

    public void Resize(int columns, int rows)
    {
        if (columns == Columns && rows == Rows)
            return;

        Columns = columns;
        Rows = rows;
        Buffer.Resize(columns, rows);
        _ssh?.ResizeTerminal(columns, rows);
    }

    public void Disconnect()
    {
        _ssh?.Disconnect();
        _ssh?.Dispose();
        _ssh = null;
        IsConnected = false;
        HostInfo = string.Empty;
    }
}
