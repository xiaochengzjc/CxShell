using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ChiXueSsh.Models;

namespace ChiXueSsh.Services;

public sealed class RloginConnectionService : ITerminalConnectionService
{
    private readonly object _writeLock = new();
    private readonly Decoder _utf8Decoder = Encoding.UTF8.GetDecoder();
    private TcpClient? _client;
    private NetworkStream? _stream;
    private CancellationTokenSource? _readCts;
    private Task? _readTask;
    private int _columns = 80;
    private int _rows = 24;
    private int _terminalSpeed = 38400;
    private string? _password;
    private string _passwordPrompt = "assword:";
    private readonly StringBuilder _loginProbeBuffer = new();
    private bool _passwordSent;

    public bool IsConnected => _client?.Connected ?? false;

    public event Action<string>? DataReceived;
    public event Func<byte[], bool>? BinaryDataReceived;
    public event Action<string>? ConnectionClosed;
    public event Action<string>? ErrorOccurred;

    public async Task ConnectAsync(
        SessionInfo session,
        string? password,
        int columns = 80,
        int rows = 24,
        CancellationToken cancellationToken = default)
    {
        Disconnect();

        _columns = Math.Max(1, columns);
        _rows = Math.Max(1, rows);
        _terminalSpeed = session.RloginTerminalSpeed > 0 ? session.RloginTerminalSpeed : 38400;
        _password = password;
        _passwordPrompt = string.IsNullOrWhiteSpace(session.RloginPasswordPrompt)
            ? "assword:"
            : session.RloginPasswordPrompt;
        _loginProbeBuffer.Clear();
        _passwordSent = false;
        _utf8Decoder.Reset();

        try
        {
            _client = await ProxyConnectionFactory.ConnectTcpAsync(session.Host, session.Port, session.Proxy, cancellationToken);
            ApplyTcpKeepAlive(_client, session.TcpKeepAlive);
            _stream = _client.GetStream();
            await SendHandshakeAsync(session.Username, cancellationToken);
            await ReadLoginResponseAsync(cancellationToken);
            SendWindowSize();

            _readCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _readTask = Task.Run(() => ReadLoop(_readCts.Token), _readCts.Token);
        }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke(ex.Message);
            Disconnect();
            throw;
        }
    }

    public void SendData(string data)
    {
        SendBytes(Encoding.UTF8.GetBytes(data));
    }

    public void SendBytes(byte[] data)
    {
        try
        {
            lock (_writeLock)
            {
                if (_stream == null)
                    return;

                _stream.Write(data, 0, data.Length);
                _stream.Flush();
            }
        }
        catch (Exception ex) when (ex is ObjectDisposedException or IOException or SocketException)
        {
            ErrorOccurred?.Invoke(ex.Message);
        }
    }

    public void SendKeepAlive()
    {
        SendBytes(new byte[] { 0 });
    }

    public void ResizeTerminal(int columns, int rows)
    {
        _columns = Math.Max(1, columns);
        _rows = Math.Max(1, rows);
        SendWindowSize();
    }

    public void Disconnect()
    {
        _readCts?.Cancel();

        try
        {
            _readTask?.Wait(TimeSpan.FromSeconds(2));
        }
        catch
        {
        }

        _stream?.Dispose();
        _stream = null;
        _client?.Dispose();
        _client = null;

        _readCts?.Dispose();
        _readCts = null;
        _readTask = null;
    }

    private async Task SendHandshakeAsync(string username, CancellationToken cancellationToken)
    {
        if (_stream == null)
            throw new InvalidOperationException("RLOGIN stream is not connected.");

        var safeUsername = string.IsNullOrWhiteSpace(username)
            ? Environment.UserName
            : username.Trim();
        var terminal = $"xterm-256color/{_terminalSpeed}";

        using var payload = new MemoryStream();
        payload.WriteByte(0);
        WriteNullTerminated(payload, safeUsername);
        WriteNullTerminated(payload, safeUsername);
        WriteNullTerminated(payload, terminal);

        var bytes = payload.ToArray();
        await _stream.WriteAsync(bytes, cancellationToken);
        await _stream.FlushAsync(cancellationToken);
    }

    private async Task ReadLoginResponseAsync(CancellationToken cancellationToken)
    {
        if (_stream == null)
            throw new InvalidOperationException("RLOGIN stream is not connected.");

        var response = new byte[1];
        var read = await _stream.ReadAsync(response, cancellationToken);
        if (read == 0)
            throw new IOException("RLOGIN server closed the connection during login.");

        if (response[0] == 0)
            return;

        var message = await ReadErrorMessageAsync(cancellationToken);
        throw new IOException(string.IsNullOrWhiteSpace(message)
            ? "RLOGIN login failed."
            : message.Trim());
    }

    private async Task<string> ReadErrorMessageAsync(CancellationToken cancellationToken)
    {
        if (_stream == null)
            return string.Empty;

        var buffer = new byte[1024];
        var builder = new StringBuilder();

        while (!cancellationToken.IsCancellationRequested)
        {
            var read = await _stream.ReadAsync(buffer, cancellationToken);
            if (read <= 0)
                break;

            builder.Append(Encoding.UTF8.GetString(buffer, 0, read));
            if (builder.ToString().Contains('\n'))
                break;
        }

        return builder.ToString();
    }

    private void ReadLoop(CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];

        try
        {
            while (!cancellationToken.IsCancellationRequested && _stream != null)
            {
                var bytesRead = _stream.Read(buffer, 0, buffer.Length);
                if (bytesRead <= 0)
                    break;

                var chunk = new byte[bytesRead];
                Buffer.BlockCopy(buffer, 0, chunk, 0, bytesRead);
                if (BinaryDataReceived?.Invoke(chunk) == true)
                    continue;

                var charCount = _utf8Decoder.GetCharCount(chunk, 0, chunk.Length);
                if (charCount == 0)
                    continue;

                var chars = new char[charCount];
                var charsRead = _utf8Decoder.GetChars(chunk, 0, chunk.Length, chars, 0);
                var text = new string(chars, 0, charsRead);
                HandleLoginPrompt(text);
                DataReceived?.Invoke(text);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (!cancellationToken.IsCancellationRequested)
                ErrorOccurred?.Invoke(ex.Message);
        }
        finally
        {
            ConnectionClosed?.Invoke("Connection closed.");
        }
    }

    private void SendWindowSize()
    {
        var width = Math.Clamp(_columns, 1, ushort.MaxValue);
        var height = Math.Clamp(_rows, 1, ushort.MaxValue);
        Span<byte> bytes = stackalloc byte[12]
        {
            0xff, 0xff, (byte)'s', (byte)'s',
            (byte)(height >> 8), (byte)(height & 0xff),
            (byte)(width >> 8), (byte)(width & 0xff),
            0, 0, 0, 0
        };

        SendBytes(bytes.ToArray());
    }

    private static void WriteNullTerminated(Stream stream, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        stream.Write(bytes, 0, bytes.Length);
        stream.WriteByte(0);
    }

    private void HandleLoginPrompt(string text)
    {
        if (_passwordSent || string.IsNullOrEmpty(_password) || string.IsNullOrEmpty(text))
            return;

        _loginProbeBuffer.Append(text);
        if (_loginProbeBuffer.Length > 512)
            _loginProbeBuffer.Remove(0, _loginProbeBuffer.Length - 512);

        if (!_loginProbeBuffer.ToString().Contains(_passwordPrompt, StringComparison.OrdinalIgnoreCase))
            return;

        _passwordSent = true;
        SendBytes(Encoding.UTF8.GetBytes(_password + "\n"));
        _loginProbeBuffer.Clear();
    }

    private static void ApplyTcpKeepAlive(TcpClient client, bool enabled)
    {
        try
        {
            client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, enabled);
        }
        catch
        {
            // TCP keepalive is best-effort and depends on the socket implementation.
        }
    }

    public void Dispose()
    {
        Disconnect();
    }
}
