using System;
using System.Collections.Generic;
using System.IO;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CxShell.Models;
using Renci.SshNet;

namespace CxShell.Services;

public class SshConnectionService : ITerminalConnectionService
{
    private SshClient? _sshClient;
    private ShellStream? _shellStream;
    private SshAgentForwardingService? _agentForwarding;
    private readonly List<ForwardedPort> _forwardedPorts = new();
    private ForwardedPortRemote? _x11ForwardedPort;
    private string? _remoteX11Display;
    private string? _x11StatusMessage;
    private CancellationTokenSource? _readCts;
    private Task? _readTask;
    private readonly object _writeLock = new();
    private Encoding _terminalEncoding = Encoding.UTF8;
    private Decoder _terminalDecoder = Encoding.UTF8.GetDecoder();
    private SessionInfo? _session;
    private const string Utf8LocaleBootstrapCommand =
        "unset LC_ALL; [ \"${LANG:-C}\" = C ] && LANG=en_US.UTF-8; export LANG; export LC_CTYPE=$LANG; clear; history -d $((HISTCMD-1)) 2>/dev/null\r";

    public bool IsConnected => _sshClient?.IsConnected ?? false;

    public event Action<string>? DataReceived;
    public event Func<byte[], bool>? BinaryDataReceived;
    public event Action<string>? ConnectionClosed;
    public event Action<string>? ErrorOccurred;

    public async Task ConnectAsync(
        SessionInfo session,
        string? password,
        int columns = 80, int rows = 24,
        CancellationToken cancellationToken = default)
    {
        Disconnect();
        _x11StatusMessage = null;
        _session = session;
        _terminalEncoding = TerminalSessionOptions.GetEncoding(session);
        _terminalDecoder = _terminalEncoding.GetDecoder();

        if (string.Equals(session.SshVersionPolicy, "Ssh1Only", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException("SSH1 is not supported. Please select SSH2 or a mixed SSH policy.");

        var authMethods = SshAgentAuthService.CreateAuthenticationMethods(session, password);
        var connectionInfo = ProxyConnectionFactory.CreateSshConnectionInfo(session, authMethods);
        SshAlgorithmPreferenceService.Apply(connectionInfo, session);
        if (session.SshUseCompression)
            PreferCompression(connectionInfo);

        _sshClient = new SshClient(connectionInfo)
        {
            KeepAliveInterval = session.SendSessionKeepAlive
                ? TimeSpan.FromSeconds(Math.Max(1, session.SessionKeepAliveIntervalSeconds))
                : Timeout.InfiniteTimeSpan
        };
        if (session.SshAcceptAndSaveHostKey)
            _sshClient.HostKeyReceived += (_, e) => e.CanTrust = true;

        try
        {
            TraceSshProtocol($"connecting to {session.Username}@{session.Host}:{session.Port}");
            await Task.Run(() => _sshClient.Connect(), cancellationToken);
            TraceSshProtocol($"connected; SSH version policy={session.SshVersionPolicy}, auth={session.AuthMethod}");
            StartForwardedPorts(session);
            StartX11Forwarding(session);

            TraceSshProtocol(session.SshNoTerminal
                ? "opening shell channel without PTY"
                : $"requesting PTY terminal={TerminalSessionOptions.GetTerminalType(session)}, size={columns}x{rows}");
            _shellStream = session.SshNoTerminal
                ? _sshClient.CreateShellStreamNoTerminal(65536)
                : _sshClient.CreateShellStream(
                    TerminalSessionOptions.GetTerminalType(session),
                    (uint)columns, (uint)rows,
                    GetPixelWidth(columns), GetPixelHeight(rows), 65536);

            if (session.SshForwardAgent)
            {
                TraceSshProtocol("starting SSH agent forwarding");
                _agentForwarding = new SshAgentForwardingService();
                _agentForwarding.Start(_shellStream);
            }

            SendX11DisplayExport();
            if (!session.SshNoTerminal && _terminalEncoding.CodePage == Encoding.UTF8.CodePage)
                SendUtf8LocaleBootstrap();
            SendRemoteCommand(session.SshRemoteCommand);
            _terminalDecoder.Reset();
            _readCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _readTask = Task.Run(() => ReadLoop(_readCts.Token), _readCts.Token);
            EmitStartupStatus();
        }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke(ex.Message);
            Disconnect();
            throw;
        }
    }

    private void StartForwardedPorts(SessionInfo session)
    {
        if (_sshClient == null || session.SshTunnelRules.Count == 0)
            return;

        foreach (var rule in session.SshTunnelRules)
        {
            if (rule.ListenPort is < 1 or > 65535)
                continue;

            ForwardedPort forwardedPort = rule.Type switch
            {
                SshTunnelRuleType.Remote => new ForwardedPortRemote(
                    NormalizeBindHost(rule.SourceHost, fallback: "0.0.0.0"),
                    (uint)rule.ListenPort,
                    NormalizeHost(rule.DestinationHost),
                    (uint)rule.DestinationPort),
                SshTunnelRuleType.Dynamic => new ForwardedPortDynamic(
                    GetLocalBindHost(rule),
                    (uint)rule.ListenPort),
                _ => new ForwardedPortLocal(
                    GetLocalBindHost(rule),
                    (uint)rule.ListenPort,
                    NormalizeHost(rule.DestinationHost),
                    (uint)rule.DestinationPort)
            };

            forwardedPort.Exception += (_, e) =>
            {
                ErrorOccurred?.Invoke($"SSH tunnel {rule.TypeDisplay} {rule.ListenPort} failed: {e.Exception.Message}");
            };

            _sshClient.AddForwardedPort(forwardedPort);
            forwardedPort.Start();
            TraceSshTunneling($"started {rule.TypeDisplay} tunnel on {rule.ListenPort} -> {rule.DestinationHost}:{rule.DestinationPort}");
            _forwardedPorts.Add(forwardedPort);
        }
    }

    private static string GetLocalBindHost(SshTunnelRule rule)
    {
        return rule.AcceptLocalConnectionsOnly
            ? "127.0.0.1"
            : NormalizeBindHost(rule.SourceHost, fallback: "0.0.0.0");
    }

    private static string NormalizeBindHost(string? host, string fallback)
    {
        return string.IsNullOrWhiteSpace(host)
            ? fallback
            : host.Trim();
    }

    private static string NormalizeHost(string? host)
    {
        return string.IsNullOrWhiteSpace(host)
            ? "localhost"
            : host.Trim();
    }

    private void StartX11Forwarding(SessionInfo session)
    {
        if (_sshClient == null || !session.SshForwardX11)
            return;

        var localDisplay = ResolveLocalX11Display(session);
        Exception? lastError = null;

        for (uint remoteDisplayNumber = 10; remoteDisplayNumber <= 19; remoteDisplayNumber++)
        {
            var remotePort = 6000u + remoteDisplayNumber;

            try
            {
                _x11ForwardedPort = new ForwardedPortRemote(
                    "127.0.0.1",
                    remotePort,
                    localDisplay.Host,
                    localDisplay.Port);
                _x11ForwardedPort.Exception += (_, e) =>
                {
                    ErrorOccurred?.Invoke($"SSH X11 forwarding failed: {e.Exception.Message}");
                };

                _sshClient.AddForwardedPort(_x11ForwardedPort);
                _x11ForwardedPort.Start();
                _remoteX11Display = $"localhost:{remoteDisplayNumber}.0";
                _x11StatusMessage =
                    $"[SSH X11 forwarding enabled: DISPLAY={_remoteX11Display}, local target={localDisplay.Host}:{localDisplay.Port}]";
                TraceSshTunneling($"started X11 remote display {_remoteX11Display} -> {localDisplay.Host}:{localDisplay.Port}");
                return;
            }
            catch (Exception ex)
            {
                lastError = ex;
                try
                {
                    if (_x11ForwardedPort != null)
                        _sshClient.RemoveForwardedPort(_x11ForwardedPort);
                }
                catch
                {
                    // Ignore cleanup failure; the next display number will be tried.
                }

                _x11ForwardedPort?.Dispose();
                _x11ForwardedPort = null;
                _remoteX11Display = null;
            }
        }

        ErrorOccurred?.Invoke($"SSH X11 forwarding is disabled: {lastError?.Message ?? "no remote display port is available"}");
    }

    private static (string Host, uint Port) ResolveLocalX11Display(SessionInfo session)
    {
        var display = session.SshX11UseXmanager || string.IsNullOrWhiteSpace(session.SshX11Display)
            ? "localhost:0.0"
            : session.SshX11Display.Trim();

        var host = "localhost";
        var displayPart = display;
        var separatorIndex = display.LastIndexOf(':');
        if (separatorIndex >= 0)
        {
            host = string.IsNullOrWhiteSpace(display[..separatorIndex])
                ? "localhost"
                : display[..separatorIndex];
            displayPart = display[(separatorIndex + 1)..];
        }

        var screenSeparator = displayPart.IndexOf('.');
        var displayNumberText = screenSeparator >= 0
            ? displayPart[..screenSeparator]
            : displayPart;
        var displayNumber = uint.TryParse(displayNumberText, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 0;

        return (host, 6000 + displayNumber);
    }

    private void SendX11DisplayExport()
    {
        if (string.IsNullOrWhiteSpace(_remoteX11Display))
            return;

        try
        {
            var command = $"export DISPLAY={_remoteX11Display}\r";
            var bytes = _terminalEncoding.GetBytes(command);
            _shellStream?.Write(bytes, 0, bytes.Length);
            _shellStream?.Flush();
        }
        catch
        {
            // X11 DISPLAY export is best-effort; the shell remains usable.
        }
    }

    private void EmitStartupStatus()
    {
        if (!string.IsNullOrWhiteSpace(_x11StatusMessage))
            DataReceived?.Invoke($"\r\n{_x11StatusMessage}\r\n");
    }

    private void TraceSshProtocol(string message)
    {
        if (_session?.AdvancedTraceSshProtocol == true)
            DataReceived?.Invoke($"\r\n[TRACE SSH] {message}\r\n");
    }

    private void TraceSshTunneling(string message)
    {
        if (_session?.AdvancedTraceSshTunneling == true)
            DataReceived?.Invoke($"\r\n[TRACE SSH TUNNEL] {message}\r\n");
    }

    private void TraceSshPacket(string message)
    {
        if (_session?.AdvancedTraceSshPackets == true)
            DataReceived?.Invoke($"\r\n[TRACE SSH PACKET] {message}\r\n");
    }

    private static void PreferCompression(ConnectionInfo connectionInfo)
    {
        // SSH.NET includes "none" by default. Removing it asks the server for zlib
        // when available and fails clearly if the server has compression disabled.
        if (connectionInfo.CompressionAlgorithms.Count > 1)
            connectionInfo.CompressionAlgorithms.Remove("none");
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

    private void SendRemoteCommand(string? remoteCommand)
    {
        if (string.IsNullOrWhiteSpace(remoteCommand))
            return;

        try
        {
            var command = TerminalSessionOptions.NormalizeSendLineEndings(remoteCommand.TrimEnd('\r', '\n') + "\r", _session);
            var bytes = _terminalEncoding.GetBytes(command);
            _shellStream?.Write(bytes, 0, bytes.Length);
            _shellStream?.Flush();
        }
        catch
        {
            // Remote command startup is best-effort; normal input remains available.
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

                    TraceSshPacket($"received {bytesRead} byte(s)");
                    var charCount = _terminalDecoder.GetCharCount(data, 0, data.Length);
                    if (charCount > 0)
                    {
                        var chars = new char[charCount];
                        var charsRead = _terminalDecoder.GetChars(data, 0, data.Length, chars, 0);
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
                    var normalized = TerminalSessionOptions.NormalizeSendLineEndings(data, _session);
                    var bytes = _terminalEncoding.GetBytes(normalized);
                    TraceSshPacket($"sent {bytes.Length} byte(s)");
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
                TraceSshPacket($"sent {data.Length} raw byte(s)");
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

    public void SendKeepAlive()
    {
        // SSH.NET sends SSH keepalive automatically through KeepAliveInterval.
    }

    public void ResizeTerminal(int columns, int rows)
    {
        try
        {
            if (_shellStream == null)
                return;

            // SSH.NET keeps the session channel private; send the PTY window-change
            // request through that channel so remote readline/bash wrap at our width.
            var channelField = typeof(ShellStream).GetField(
                "_channel",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var channel = channelField?.GetValue(_shellStream);
            if (channel == null || channelField == null)
                return;

            var method = channelField.FieldType.GetMethod(
                "SendWindowChangeRequest",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public,
                binder: null,
                types: new[] { typeof(uint), typeof(uint), typeof(uint), typeof(uint) },
                modifiers: null)
                ?? channel.GetType().GetMethod(
                    "SendWindowChangeRequest",
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public,
                    binder: null,
                    types: new[] { typeof(uint), typeof(uint), typeof(uint), typeof(uint) },
                    modifiers: null);
            if (method == null)
            {
                ErrorOccurred?.Invoke("SSH terminal resize failed: SendWindowChangeRequest was not found.");
                return;
            }

            method.Invoke(channel, new object[]
            {
                (uint)Math.Max(1, columns),
                (uint)Math.Max(1, rows),
                GetPixelWidth(columns),
                GetPixelHeight(rows)
            });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"SSH terminal resize failed: {ex.Message}");
            ErrorOccurred?.Invoke($"SSH terminal resize failed: {ex.Message}");
        }
    }

    private static uint GetPixelWidth(int columns) => (uint)Math.Max(1, columns * 8);

    private static uint GetPixelHeight(int rows) => (uint)Math.Max(1, rows * 16);

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
        _agentForwarding?.Dispose();
        _agentForwarding = null;
        StopX11Forwarding();
        StopForwardedPorts();

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

    private void StopForwardedPorts()
    {
        foreach (var forwardedPort in _forwardedPorts.ToArray())
        {
            try
            {
                if (forwardedPort.IsStarted)
                    forwardedPort.Stop();
            }
            catch
            {
                // Ignore tunnel shutdown failures during disconnect.
            }

            try
            {
                _sshClient?.RemoveForwardedPort(forwardedPort);
            }
            catch
            {
                // Ignore removal failures during disconnect.
            }

            forwardedPort.Dispose();
        }

        _forwardedPorts.Clear();
    }

    private void StopX11Forwarding()
    {
        if (_x11ForwardedPort == null)
        {
            _remoteX11Display = null;
            _x11StatusMessage = null;
            return;
        }

        try
        {
            if (_x11ForwardedPort.IsStarted)
                _x11ForwardedPort.Stop();
        }
        catch
        {
            // Ignore X11 shutdown failures during disconnect.
        }

        try
        {
            _sshClient?.RemoveForwardedPort(_x11ForwardedPort);
        }
        catch
        {
            // Ignore removal failures during disconnect.
        }

        _x11ForwardedPort.Dispose();
        _x11ForwardedPort = null;
        _remoteX11Display = null;
        _x11StatusMessage = null;
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
