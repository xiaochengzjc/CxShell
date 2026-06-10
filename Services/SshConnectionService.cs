using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Renci.SshNet;

namespace ChiXueSsh.Services;

public class SshConnectionService : IDisposable
{
    private SshClient? _sshClient;
    private ShellStream? _shellStream;
    private CancellationTokenSource? _readCts;
    private Task? _readTask;
    private readonly object _writeLock = new();
    private readonly Decoder _utf8Decoder = Encoding.UTF8.GetDecoder();
    private const string Utf8LocaleBootstrapCommand =
        "unset LC_ALL; [ \"${LANG:-C}\" = C ] && LANG=en_US.UTF-8; export LANG; export LC_CTYPE=$LANG; clear\r";

    public bool IsConnected => _sshClient?.IsConnected ?? false;

    public event Action<string>? DataReceived;
    public event Func<byte[], bool>? BinaryDataReceived;
    public event Action<string>? ConnectionClosed;
    public event Action<string>? ErrorOccurred;

    public async Task ConnectAsync(
        string host, int port, string username,
        Models.AuthMethod authMethod, string? password, string? privateKeyPath,
        int columns = 80, int rows = 24,
        CancellationToken cancellationToken = default)
    {
        Disconnect();

        AuthenticationMethod auth;
        if (authMethod == Models.AuthMethod.PrivateKey && !string.IsNullOrEmpty(privateKeyPath))
        {
            var expandedPath = ExpandPath(privateKeyPath);
            var keyFile = new PrivateKeyFile(expandedPath);
            auth = new PrivateKeyAuthenticationMethod(username, keyFile);
        }
        else
        {
            auth = new PasswordAuthenticationMethod(username, password ?? string.Empty);
        }

        var connectionInfo = new ConnectionInfo(host, port, username, auth);
        _sshClient = new SshClient(connectionInfo)
        {
            KeepAliveInterval = TimeSpan.FromSeconds(30)
        };

        try
        {
            await Task.Run(() => _sshClient.Connect(), cancellationToken);

            _shellStream = _sshClient.CreateShellStream(
                "xterm-256color",
                (uint)columns, (uint)rows,
                800, 600, 65536);

            SendUtf8LocaleBootstrap();
            _utf8Decoder.Reset();
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

    private void SendUtf8LocaleBootstrap()
    {
        try
        {
            var bytes = Encoding.UTF8.GetBytes(Utf8LocaleBootstrapCommand);
            _shellStream?.Write(bytes, 0, bytes.Length);
            _shellStream?.Flush();
        }
        catch
        {
            // Locale bootstrap is best-effort; the shell remains usable if it fails.
        }
    }

    private void ReadLoop(CancellationToken ct)
    {
        var buffer = new byte[4096];

        try
        {
            while (!ct.IsCancellationRequested && _shellStream != null)
            {
                var bytesRead = _shellStream.Read(buffer, 0, buffer.Length);
                if (bytesRead > 0)
                {
                    var data = new byte[bytesRead];
                    Buffer.BlockCopy(buffer, 0, data, 0, bytesRead);
                    if (BinaryDataReceived?.Invoke(data) == true)
                        continue;

                    var charCount = _utf8Decoder.GetCharCount(data, 0, data.Length);
                    if (charCount > 0)
                    {
                        var chars = new char[charCount];
                        var charsRead = _utf8Decoder.GetChars(data, 0, data.Length, chars, 0);
                        DataReceived?.Invoke(new string(chars, 0, charsRead));
                    }
                }
                else
                {
                    // A blocking stream read returning 0 indicates remote-shell EOF.
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal cancellation
        }
        catch (Exception ex)
        {
            if (!ct.IsCancellationRequested)
            {
                ErrorOccurred?.Invoke(ex.Message);
            }
        }
        finally
        {
            ConnectionClosed?.Invoke("Connection closed.");
        }
    }

    public void SendData(string data)
    {
        try
        {
            lock (_writeLock)
            {
                if (_shellStream != null)
                {
                    var bytes = Encoding.UTF8.GetBytes(data);
                    _shellStream.Write(bytes, 0, bytes.Length);
                }
                _shellStream?.Flush();
            }
        }
        catch (ObjectDisposedException)
        {
            // Shell stream already disposed, connection is dead
        }
        catch (System.IO.IOException)
        {
            // Connection lost
        }
    }

    public void SendBytes(byte[] data)
    {
        try
        {
            lock (_writeLock)
            {
                _shellStream?.Write(data, 0, data.Length);
                _shellStream?.Flush();
            }
        }
        catch (ObjectDisposedException)
        {
            // Shell stream already disposed, connection is dead
        }
        catch (System.IO.IOException)
        {
            // Connection lost
        }
    }

    public void ResizeTerminal(int columns, int rows)
    {
        // ShellStream window resize - SSH.NET 2024.2.0 may not expose this publicly
        // Reconnect with new size if needed
        try
        {
            var method = _shellStream?.GetType().GetMethod("SendWindowChangeRequest",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
            method?.Invoke(_shellStream, new object[] { (uint)columns, (uint)rows, 800u, 600u });
        }
        catch
        {
            // Ignore resize failures
        }
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
            // Ignore
        }

        _shellStream?.Dispose();
        _shellStream = null;

        if (_sshClient?.IsConnected == true)
        {
            try { _sshClient.Disconnect(); } catch { }
        }
        _sshClient?.Dispose();
        _sshClient = null;

        _readCts?.Dispose();
        _readCts = null;
        _readTask = null;
    }

    private static string ExpandPath(string path)
    {
        if (path.StartsWith("~"))
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                path[2..]);
        }
        return Path.GetFullPath(path);
    }

    public void Dispose()
    {
        Disconnect();
    }
}
