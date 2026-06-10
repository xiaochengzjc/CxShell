using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
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
    private SessionInfo? _session;
    private string? _password;
    private CancellationTokenSource? _connectionCts;
    private readonly SemaphoreSlim _connectGate = new(1, 1);
    private bool _manualDisconnect = true;
    private int _connectionGeneration;
    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(5);
    private readonly object _zmodemLock = new();
    private readonly List<byte[]> _zmodemPendingBytes = new();
    private readonly List<byte> _zmodemProbeBytes = new();
    private readonly Decoder _terminalByteDecoder = Encoding.UTF8.GetDecoder();
    private ZmodemTransfer? _zmodemTransfer;
    private bool _zmodemStarting;
    private ZmodemTransferDirection _zmodemStartingDirection;

    public Func<Task<IReadOnlyList<string>>>? PickZmodemUploadFilesAsync { get; set; }
    public Func<Task<string?>>? PickZmodemDownloadFolderAsync { get; set; }

    public TerminalViewModel()
    {
        Buffer = new TerminalBuffer(Columns, Rows);
        Parser = new AnsiParser(Buffer);
    }

    public event Action? BufferChanged;

    public async Task ConnectAsync(SessionInfo session, string? password)
    {
        Disconnect();
        _session = session;
        _password = password;
        _manualDisconnect = false;
        _connectionCts = new CancellationTokenSource();

        Buffer = new TerminalBuffer(Columns, Rows);
        Parser = new AnsiParser(Buffer);
        _terminalByteDecoder.Reset();
        OnPropertyChanged(nameof(Buffer));

        await ConnectCoreAsync(_connectionCts.Token);
    }

    private async Task ConnectCoreAsync(CancellationToken cancellationToken)
    {
        if (_session == null)
            return;

        await _connectGate.WaitAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            int generation = ++_connectionGeneration;
            var previous = _ssh;
            _ssh = null;
            previous?.Dispose();

            var ssh = new SshConnectionService();
            _ssh = ssh;

            ssh.DataReceived += data =>
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (generation != _connectionGeneration)
                        return;

                    Parser.Process(data);
                    Buffer.MarkAllDirty();
                    BufferChanged?.Invoke();
                });
            };

            ssh.BinaryDataReceived += bytes => HandleBinaryData(generation, bytes);

            ssh.ConnectionClosed += reason =>
            {
                Dispatcher.UIThread.Post(() => HandleConnectionClosed(generation, reason));
            };

            ssh.ErrorOccurred += error =>
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (generation != _connectionGeneration || _manualDisconnect)
                        return;

                    AppendStatusMessage($"[Connection error: {error}]", "31");
                });
            };

            await ssh.ConnectAsync(
                _session.Host, _session.Port, _session.Username,
                _session.AuthMethod, _password, _session.PrivateKeyPath,
                Columns, Rows, cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();
            if (generation != _connectionGeneration || _manualDisconnect)
                return;

            IsConnected = true;
            HostInfo = $"{_session.Username}@{_session.Host}:{_session.Port}";
        }
        finally
        {
            _connectGate.Release();
        }
    }

    private void HandleConnectionClosed(int generation, string reason)
    {
        if (generation != _connectionGeneration || _manualDisconnect)
            return;

        IsConnected = false;
        HostInfo = string.Empty;
        AppendStatusMessage($"[Connection closed: {reason}]", "31");

        var cancellationToken = _connectionCts?.Token ?? CancellationToken.None;
        _ = ReconnectLoopAsync(generation, cancellationToken);
    }

    private async Task ReconnectLoopAsync(int disconnectedGeneration, CancellationToken cancellationToken)
    {
        while (!_manualDisconnect && disconnectedGeneration == _connectionGeneration)
        {
            try
            {
                await Task.Delay(ReconnectDelay, cancellationToken);
                await Dispatcher.UIThread.InvokeAsync(() =>
                    AppendStatusMessage($"[Auto reconnecting; retry interval: {ReconnectDelay.TotalSeconds:0}s...]", "33"));

                await ConnectCoreAsync(cancellationToken);
                await Dispatcher.UIThread.InvokeAsync(() =>
                    AppendStatusMessage("[Auto reconnect succeeded]", "32"));
                return;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                    AppendStatusMessage($"[Auto reconnect failed: {ex.Message}]", "31"));
                disconnectedGeneration = _connectionGeneration;
            }
        }
    }

    private void AppendStatusMessage(string message, string colorCode)
    {
        Parser.Process($"\r\n\x1B[{colorCode}m{message}\x1B[0m\r\n");
        Buffer.MarkAllDirty();
        BufferChanged?.Invoke();
    }

    private void AppendPlainStatusMessage(string message)
    {
        Parser.Process($"\r\n{message}\r\n");
        Buffer.MarkAllDirty();
        BufferChanged?.Invoke();
    }

    private bool HandleBinaryData(int generation, byte[] bytes)
    {
        if (generation != _connectionGeneration)
            return false;

        ZmodemTransfer? transfer = null;
        lock (_zmodemLock)
        {
            if (_zmodemTransfer != null)
            {
                transfer = _zmodemTransfer;
            }
            else if (_zmodemStarting)
            {
                _zmodemPendingBytes.Add(bytes);
                return true;
            }
        }

        if (transfer != null)
        {
            transfer.Feed(bytes);
            return true;
        }

        var probePrefixLength = 0;
        byte[] scanBytes;
        lock (_zmodemLock)
        {
            probePrefixLength = _zmodemProbeBytes.Count;
            if (probePrefixLength > 0)
            {
                scanBytes = _zmodemProbeBytes.Concat(bytes).ToArray();
                _zmodemProbeBytes.Clear();
            }
            else
            {
                scanBytes = bytes;
            }
        }

        if (!ZmodemTransfer.TryFindStartupHeader(scanBytes, out var index, out var direction))
        {
            var keep = GetZmodemStartupPrefixSuffixLength(scanBytes);
            if (keep == 0 && probePrefixLength == 0)
                return false;

            var terminalLength = scanBytes.Length - keep;
            if (terminalLength > 0)
                ProcessTerminalBytes(scanBytes[..terminalLength]);

            lock (_zmodemLock)
            {
                _zmodemProbeBytes.Clear();
                if (keep > 0)
                    _zmodemProbeBytes.AddRange(scanBytes[^keep..]);
            }

            return true;
        }

        if (index > 0 && !ShouldSuppressZmodemPreamble(direction, scanBytes[..index]))
            ProcessTerminalBytes(scanBytes[..index]);

        lock (_zmodemLock)
        {
            _zmodemStarting = true;
            _zmodemStartingDirection = direction;
            _zmodemPendingBytes.Clear();
            _zmodemProbeBytes.Clear();
            _zmodemPendingBytes.Add(scanBytes[index..]);
        }

        _ = BeginZmodemTransferAsync(generation, direction);
        return true;
    }

    private static int GetZmodemStartupPrefixSuffixLength(byte[] bytes)
    {
        ReadOnlySpan<byte> prefix = stackalloc byte[] { 0x2a, 0x2a, 0x18, 0x42, 0x30 };
        var max = Math.Min(prefix.Length, bytes.Length);
        for (var length = max; length > 0; length--)
        {
            var suffix = bytes.AsSpan(bytes.Length - length, length);
            if (suffix.SequenceEqual(prefix[..length]))
                return length;
        }

        return 0;
    }

    private static bool ShouldSuppressZmodemPreamble(ZmodemTransferDirection direction, byte[] bytes)
    {
        if (direction != ZmodemTransferDirection.Download || bytes.Length == 0)
            return false;

        var text = Encoding.ASCII.GetString(bytes).Trim('\r', '\n');
        return text == "rz";
    }

    private async Task BeginZmodemTransferAsync(int generation, ZmodemTransferDirection direction)
    {
        try
        {
            string? downloadFolder = null;
            IReadOnlyList<string>? uploadFiles = null;

            if (direction == ZmodemTransferDirection.Download)
            {
                if (PickZmodemDownloadFolderAsync == null)
                    throw new InvalidOperationException("Download folder picker is not available.");

                downloadFolder = await Dispatcher.UIThread.InvokeAsync(() => PickZmodemDownloadFolderAsync());
                if (string.IsNullOrWhiteSpace(downloadFolder))
                {
                    CancelStartingZmodem("[ZMODEM download cancelled]", generation);
                    return;
                }
            }
            else
            {
                if (PickZmodemUploadFilesAsync == null)
                    throw new InvalidOperationException("Upload file picker is not available.");

                uploadFiles = await Dispatcher.UIThread.InvokeAsync(() => PickZmodemUploadFilesAsync());
                uploadFiles = uploadFiles.Where(path => !string.IsNullOrWhiteSpace(path)).ToList();
                if (uploadFiles.Count == 0)
                {
                    CancelStartingZmodem("[ZMODEM upload cancelled]", generation);
                    return;
                }
            }

            List<byte[]> pending;
            ZmodemTransfer transfer;
            lock (_zmodemLock)
            {
                if (generation != _connectionGeneration || !_zmodemStarting || direction != _zmodemStartingDirection)
                    return;

                transfer = new ZmodemTransfer(
                    direction,
                    SendZmodemBytes,
                    ProcessTerminalBytes,
                    PostStatusMessage,
                    ClearZmodemTransfer,
                    downloadFolder,
                    uploadFiles);

                _zmodemTransfer = transfer;
                _zmodemStarting = false;
                pending = _zmodemPendingBytes.ToList();
                _zmodemPendingBytes.Clear();
            }

            transfer.Start();
            foreach (var chunk in pending)
                transfer.Feed(chunk);
        }
        catch (Exception ex)
        {
            CancelStartingZmodem($"[ZMODEM failed: {ex.Message}]", generation);
        }
    }

    private void CancelStartingZmodem(string message, int generation)
    {
        if (generation != _connectionGeneration)
            return;

        lock (_zmodemLock)
        {
            _zmodemStarting = false;
            _zmodemPendingBytes.Clear();
            _zmodemProbeBytes.Clear();
        }

        _ssh?.SendBytes(new byte[] { 24, 24, 24, 24, 24, 8, 8, 8, 8, 8 });
        PostStatusMessage(message, "33");
    }

    private void ClearZmodemTransfer()
    {
        lock (_zmodemLock)
        {
            _zmodemTransfer?.Dispose();
            _zmodemTransfer = null;
            _zmodemStarting = false;
            _zmodemPendingBytes.Clear();
            _zmodemProbeBytes.Clear();
        }
    }

    private void SendZmodemBytes(byte[] bytes)
    {
        _ssh?.SendBytes(bytes);
    }

    private void ProcessTerminalBytes(byte[] bytes)
    {
        if (bytes.Length == 0)
            return;

        var charCount = _terminalByteDecoder.GetCharCount(bytes, 0, bytes.Length);
        if (charCount == 0)
            return;

        var chars = new char[charCount];
        var charsRead = _terminalByteDecoder.GetChars(bytes, 0, bytes.Length, chars, 0);
        var text = new string(chars, 0, charsRead);
        Dispatcher.UIThread.Post(() =>
        {
            Parser.Process(text);
            Buffer.MarkAllDirty();
            BufferChanged?.Invoke();
        });
    }

    private void PostStatusMessage(string message, string colorCode)
    {
        Dispatcher.UIThread.Post(() => AppendPlainStatusMessage(message));
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

    public void Disconnect(string? statusMessage = null)
    {
        _manualDisconnect = true;
        _connectionCts?.Cancel();
        _connectionCts?.Dispose();
        _connectionCts = null;
        _connectionGeneration++;

        var ssh = _ssh;
        _ssh = null;
        ssh?.Dispose();
        ClearZmodemTransfer();
        IsConnected = false;
        HostInfo = string.Empty;

        if (!string.IsNullOrWhiteSpace(statusMessage))
            AppendStatusMessage(statusMessage, "33");
    }
}
