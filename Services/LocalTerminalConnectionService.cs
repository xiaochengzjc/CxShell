using System;
using System.Diagnostics;
using System.Text;
using ChiXueSsh.Models;

namespace ChiXueSsh.Services;

public sealed class LocalTerminalConnectionService : ITerminalConnectionService
{
    private readonly object _writeLock = new();
    private Encoding _terminalEncoding = Encoding.UTF8;
    private Decoder _stdoutDecoder = Encoding.UTF8.GetDecoder();
    private Decoder _stderrDecoder = Encoding.UTF8.GetDecoder();
    private readonly StringBuilder _pendingInput = new();
    private Process? _process;
    private CancellationTokenSource? _readCts;
    private Task? _stdoutTask;
    private Task? _stderrTask;
    private bool _disconnecting;

    public bool IsConnected => _process is { HasExited: false };

    public event Action<string>? DataReceived;
    public event Func<byte[], bool>? BinaryDataReceived;
    public event Action<string>? ConnectionClosed;
    public event Action<string>? ErrorOccurred;

    public Task ConnectAsync(
        SessionInfo session,
        string? password,
        int columns = 80,
        int rows = 24,
        CancellationToken cancellationToken = default)
    {
        Disconnect();

        _terminalEncoding = TerminalSessionOptions.GetEncoding(session);
        _stdoutDecoder = _terminalEncoding.GetDecoder();
        _stderrDecoder = _terminalEncoding.GetDecoder();
        _disconnecting = false;
        _pendingInput.Clear();
        _stdoutDecoder.Reset();
        _stderrDecoder.Reset();

        var shellPath = GetShellPath();
        var startInfo = new ProcessStartInfo
        {
            FileName = shellPath,
            Arguments = IsWindowsCommandShell(shellPath)
                ? $"/Q /K \"chcp {_terminalEncoding.CodePage} >nul\""
                : string.Empty,
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = _terminalEncoding,
            StandardOutputEncoding = _terminalEncoding,
            StandardErrorEncoding = _terminalEncoding,
            CreateNoWindow = true
        };

        try
        {
            _process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            _process.Exited += OnProcessExited;
            if (!_process.Start())
                throw new InvalidOperationException("Failed to start local shell.");

            _readCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _stdoutTask = Task.Run(() => ReadStreamLoop(_process.StandardOutput.BaseStream, _stdoutDecoder, _readCts.Token), _readCts.Token);
            _stderrTask = Task.Run(() => ReadStreamLoop(_process.StandardError.BaseStream, _stderrDecoder, _readCts.Token), _readCts.Token);
            DataReceived?.Invoke($"[LOCAL shell started: {shellPath}]\r\n");
            return Task.CompletedTask;
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
        if (string.IsNullOrEmpty(data))
            return;

        lock (_writeLock)
        {
            if (_process is not { HasExited: false })
                return;

            foreach (var ch in data)
            {
                switch (ch)
                {
                    case '\r':
                    case '\n':
                        SubmitPendingLine();
                        break;
                    case '\x7F':
                    case '\b':
                        BackspacePendingInput();
                        break;
                    case '\x03':
                        CancelPendingInput();
                        break;
                    case '\x1B':
                        break;
                    default:
                        if (ch >= ' ')
                        {
                            _pendingInput.Append(ch);
                            DataReceived?.Invoke(ch.ToString());
                        }
                        break;
                }
            }
        }
    }

    public void SendBytes(byte[] data)
    {
        SendData(_terminalEncoding.GetString(data));
    }

    public void SendKeepAlive()
    {
        // Local redirected shells do not need network keepalive.
    }

    public void ResizeTerminal(int columns, int rows)
    {
        // Redirected local processes do not receive terminal size changes without ConPTY.
    }

    public void Disconnect()
    {
        _disconnecting = true;
        _readCts?.Cancel();

        try
        {
            if (_process is { HasExited: false })
                _process.Kill(entireProcessTree: true);
        }
        catch
        {
        }

        try
        {
            Task.WaitAll(
                new[] { _stdoutTask, _stderrTask }.Where(task => task != null).Cast<Task>().ToArray(),
                TimeSpan.FromSeconds(2));
        }
        catch
        {
        }

        _process?.Dispose();
        _process = null;

        _readCts?.Dispose();
        _readCts = null;
        _stdoutTask = null;
        _stderrTask = null;
        _pendingInput.Clear();
    }

    private void SubmitPendingLine()
    {
        if (_process is not { HasExited: false })
            return;

        var line = _pendingInput.ToString();
        _pendingInput.Clear();
        DataReceived?.Invoke("\r\n");
        _process.StandardInput.WriteLine(line);
        _process.StandardInput.Flush();
    }

    private void BackspacePendingInput()
    {
        if (_pendingInput.Length == 0)
            return;

        _pendingInput.Remove(_pendingInput.Length - 1, 1);
        DataReceived?.Invoke("\b \b");
    }

    private void CancelPendingInput()
    {
        _pendingInput.Clear();
        DataReceived?.Invoke("^C\r\n");
    }

    private async Task ReadStreamLoop(Stream stream, Decoder decoder, CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var bytesRead = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
                if (bytesRead <= 0)
                    break;

                var chunk = buffer[..bytesRead].ToArray();
                if (BinaryDataReceived?.Invoke(chunk) == true)
                    continue;

                var charCount = decoder.GetCharCount(chunk, 0, chunk.Length);
                if (charCount == 0)
                    continue;

                var chars = new char[charCount];
                var charsRead = decoder.GetChars(chunk, 0, chunk.Length, chars, 0);
                DataReceived?.Invoke(new string(chars, 0, charsRead));
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
    }

    private void OnProcessExited(object? sender, EventArgs e)
    {
        if (!_disconnecting)
            ConnectionClosed?.Invoke("Local shell exited.");
    }

    private static string GetShellPath()
    {
        var comSpec = Environment.GetEnvironmentVariable("COMSPEC");
        return string.IsNullOrWhiteSpace(comSpec) ? "cmd.exe" : comSpec;
    }

    private static bool IsWindowsCommandShell(string shellPath)
    {
        return shellPath.EndsWith("cmd.exe", StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        Disconnect();
    }
}
