using System;
using System.IO;
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

    public bool IsConnected => _sshClient?.IsConnected ?? false;

    public event Action<string>? DataReceived;
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

    private void ReadLoop(CancellationToken ct)
    {
        var buffer = new byte[4096];
        var lastActivity = DateTime.UtcNow;
        const int keepAliveIntervalSeconds = 30;

        try
        {
            while (!ct.IsCancellationRequested && _shellStream != null)
            {
                var bytesRead = _shellStream.Read(buffer, 0, buffer.Length);
                if (bytesRead > 0)
                {
                    lastActivity = DateTime.UtcNow;
                    var text = System.Text.Encoding.UTF8.GetString(buffer, 0, bytesRead);
                    DataReceived?.Invoke(text);
                }
                else
                {
                    // KeepAliveInterval on SshClient handles protocol-level keepalive automatically
                    if ((DateTime.UtcNow - lastActivity).TotalSeconds >= keepAliveIntervalSeconds)
                    {
                        lastActivity = DateTime.UtcNow;
                    }
                    Thread.Sleep(10);
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
            _shellStream?.Write(data);
            _shellStream?.Flush();
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
