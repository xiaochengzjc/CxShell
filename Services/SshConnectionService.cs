using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CxShell.Models;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace CxShell.Services;

public sealed record SshCommandExecutionResult(
    string Output,
    string Error,
    int? ExitStatus,
    bool Completed)
{
    public bool Succeeded => Completed && (ExitStatus is null or 0);
}

public class SshConnectionService : ITerminalConnectionService
{
    private SshClient? _sshClient;
    private SshConnectionContext? _sshConnectionContext;
    private ShellStream? _shellStream;
    private SshAgentForwardingService? _agentForwarding;
    private readonly Dictionary<Guid, ForwardedPort> _forwardedPorts = new();
    private readonly Dictionary<Guid, DateTimeOffset> _forwardedPortStartedAt = new();
    private readonly Dictionary<Guid, string> _forwardedPortErrors = new();
    private readonly Dictionary<Guid, SshTunnelActivitySnapshot> _forwardedPortActivity = new();
    private readonly object _forwardedPortsLock = new();
    private readonly object _forwardedPortsOperationLock = new();
    private ForwardedPortRemote? _x11ForwardedPort;
    private string? _remoteX11Display;
    private string? _x11StatusMessage;
    private CancellationTokenSource? _readCts;
    private Task? _readTask;
    private readonly object _writeLock = new();
    private readonly object _startupEchoLock = new();
    private readonly SemaphoreSlim _commandGate = new(1, 1);
    private readonly List<string> _startupEchoSuppressions = new();
    private readonly StringBuilder _startupEchoBuffer = new();
    private Encoding _terminalEncoding = Encoding.UTF8;
    private Decoder _terminalDecoder = Encoding.UTF8.GetDecoder();
    private SessionInfo? _session;
    private bool _connectionClosedRaised;
    private DateTimeOffset _startupEchoSuppressUntil = DateTimeOffset.MinValue;
    private const string Utf8LocaleBootstrapCommand =
        "unset LC_ALL; [ \"${LANG:-C}\" = C ] && LANG=en_US.UTF-8; export LANG; export LC_CTYPE=$LANG\r";
    private static readonly TimeSpan StartupEchoSuppressWindow = TimeSpan.FromSeconds(8);
    private const int StartupEchoSuppressMaxBufferLength = 8192;

    public bool SupportsPosixShellFeatures { get; private set; } = true;
    public bool IsConnected => _sshClient?.IsConnected ?? false;
    public bool AutoStartConfiguredTunnels { get; set; } = true;

    public event Action<string>? DataReceived;
    public event Func<byte[], bool>? BinaryDataReceived;
    public event Action<string>? ConnectionClosed;
    public event Action<string>? ErrorOccurred;
    public event Action? TunnelRuntimeChanged;

    public async Task ConnectAsync(
        SessionInfo session,
        string? password,
        int columns = 80, int rows = 24,
        CancellationToken cancellationToken = default)
    {
        Disconnect();
        _x11StatusMessage = null;
        _connectionClosedRaised = false;
        _session = session;
        SupportsPosixShellFeatures = true;
        _terminalEncoding = TerminalSessionOptions.GetEncoding(session);
        _terminalDecoder = _terminalEncoding.GetDecoder();
        ResetStartupEchoSuppression();

        if (string.Equals(session.SshVersionPolicy, "Ssh1Only", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException("SSH1 is not supported. Please select SSH2 or a mixed SSH policy.");

        var authMethods = SshAgentAuthService.CreateAuthenticationMethods(session, password);
        _sshConnectionContext = await Task.Run(
            () => ProxyConnectionFactory.CreateSshConnectionContext(session, authMethods),
            cancellationToken);
        try
        {
            var connectionInfo = _sshConnectionContext.ConnectionInfo;
            SshAlgorithmPreferenceService.Apply(connectionInfo, session);
            if (session.SshUseCompression)
                PreferCompression(connectionInfo);

            _sshClient = new SshClient(connectionInfo)
            {
                KeepAliveInterval = session.SendSessionKeepAlive
                    ? TimeSpan.FromSeconds(Math.Max(1, session.SessionKeepAliveIntervalSeconds))
                    : Timeout.InfiniteTimeSpan
            };
            SshHostKeyTrustService.Shared.Attach(
                _sshClient,
                session.Host,
                session.Port,
                session.SshAcceptAndSaveHostKey);

            TraceSshProtocol($"connecting to {session.Username}@{session.Host}:{session.Port}");
            await Task.Run(() => _sshClient.Connect(), cancellationToken);
            SupportsPosixShellFeatures = !SshServerInfo.IsWindowsOpenSshServer(connectionInfo.ServerVersion);
            TraceSshProtocol($"connected; SSH version policy={session.SshVersionPolicy}, auth={session.AuthMethod}");
            if (AutoStartConfiguredTunnels)
                StartForwardedPorts(session);
            if (SupportsPosixShellFeatures)
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

            if (SupportsPosixShellFeatures)
                SendX11DisplayExport();
            SendRemoteCommand(session.SshRemoteCommand);
            _terminalDecoder.Reset();
            _readCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _readTask = Task.Run(() => ReadLoop(_readCts.Token), _readCts.Token);
            EmitStartupStatus();
        }
        catch (Exception ex)
        {
            var displayMessage = SshServerInfo.BuildConnectionErrorMessage(ex);
            ErrorOccurred?.Invoke(displayMessage);
            Disconnect();
            if (!string.Equals(displayMessage, ex.Message, StringComparison.Ordinal))
                throw new InvalidOperationException(displayMessage, ex);

            throw;
        }
    }

    private void StartForwardedPorts(SessionInfo session)
    {
        if (_sshClient == null || session.SshTunnelRules.Count == 0)
            return;

        foreach (var rule in session.SshTunnelRules)
        {
            StartTunnel(rule.Id);
        }
    }

    public IReadOnlyList<SshTunnelRuntimeSnapshot> GetTunnelRuntimeSnapshot()
    {
        var rules = _session?.SshTunnelRules.ToArray() ?? [];
        lock (_forwardedPortsLock)
        {
            return rules.Select(rule =>
            {
                _forwardedPorts.TryGetValue(rule.Id, out var forwardedPort);
                _forwardedPortErrors.TryGetValue(rule.Id, out var lastError);
                var activity = _forwardedPortActivity.GetValueOrDefault(
                    rule.Id,
                    SshTunnelActivitySnapshot.Empty);
                var hasStartedAt = _forwardedPortStartedAt.TryGetValue(rule.Id, out var startedAt);
                var isRunning = forwardedPort?.IsStarted == true;
                var status = isRunning
                    ? SshTunnelRuntimeStatus.Running
                    : string.IsNullOrWhiteSpace(lastError)
                        ? SshTunnelRuntimeStatus.Stopped
                        : SshTunnelRuntimeStatus.Error;

                return new SshTunnelRuntimeSnapshot(
                    rule.Id,
                    rule.Type,
                    rule.Description,
                    GetTunnelListenHost(rule),
                    rule.ListenPort,
                    rule.Type == SshTunnelRuleType.Dynamic ? string.Empty : NormalizeHost(rule.DestinationHost),
                    rule.Type == SshTunnelRuleType.Dynamic ? 0 : rule.DestinationPort,
                    status,
                    isRunning && hasStartedAt ? startedAt : null,
                    lastError,
                    activity);
            }).ToArray();
        }
    }

    public SshTunnelOperationResult StartTunnel(Guid ruleId)
    {
        SshTunnelOperationResult result;
        SshTunnelRule? rule;
        lock (_forwardedPortsOperationLock)
        {
            if (_sshClient?.IsConnected != true || _session == null)
                return SshTunnelOperationResult.Failed("The SSH connection is not active.");

            rule = _session.SshTunnelRules.FirstOrDefault(item => item.Id == ruleId);
            if (rule == null)
                return SshTunnelOperationResult.Failed("The tunnel rule no longer exists.");

            ForwardedPort? current;
            lock (_forwardedPortsLock)
            {
                if (_forwardedPorts.TryGetValue(rule.Id, out current) && current.IsStarted)
                    return SshTunnelOperationResult.Completed();

                _forwardedPorts.Remove(rule.Id);
            }

            if (current != null)
                DisposeForwardedPort(current);

            var validationError = ValidateTunnelRule(rule);
            if (validationError != null)
            {
                lock (_forwardedPortsLock)
                {
                    _forwardedPortStartedAt.Remove(rule.Id);
                    _forwardedPortErrors[rule.Id] = validationError;
                }
                result = SshTunnelOperationResult.Failed(validationError);
            }
            else
            {
                ForwardedPort? forwardedPort = null;
                try
                {
                    forwardedPort = CreateForwardedPort(rule);
                    var observedPort = forwardedPort;
                    forwardedPort.RequestReceived += (_, e) =>
                        HandleTunnelRequest(rule.Id, observedPort, e);
                    forwardedPort.Exception += (_, e) =>
                        HandleTunnelException(rule.Id, observedPort, e.Exception);

                    _sshClient.AddForwardedPort(forwardedPort);
                    lock (_forwardedPortsLock)
                    {
                        _forwardedPorts[rule.Id] = forwardedPort;
                        _forwardedPortActivity[rule.Id] = SshTunnelActivitySnapshot.Empty;
                    }
                    forwardedPort.Start();
                    lock (_forwardedPortsLock)
                    {
                        _forwardedPortStartedAt[rule.Id] = DateTimeOffset.UtcNow;
                        _forwardedPortErrors.Remove(rule.Id);
                    }
                    TraceSshTunneling(
                        $"started {rule.TypeDisplay} tunnel on {rule.ListenPort} -> {rule.DestinationHost}:{rule.DestinationPort}");
                    result = SshTunnelOperationResult.Completed();
                }
                catch (Exception ex)
                {
                    if (forwardedPort != null)
                    {
                        lock (_forwardedPortsLock)
                        {
                            _forwardedPorts.Remove(rule.Id);
                            _forwardedPortActivity.Remove(rule.Id);
                        }
                        DisposeForwardedPort(forwardedPort);
                    }

                    lock (_forwardedPortsLock)
                    {
                        _forwardedPortStartedAt.Remove(rule.Id);
                        _forwardedPortErrors[rule.Id] = ex.Message;
                    }
                    result = SshTunnelOperationResult.Failed(ex.Message);
                }
            }
        }

        if (!result.Success)
            ErrorOccurred?.Invoke($"SSH tunnel {rule.TypeDisplay} {rule.ListenPort} failed: {result.ErrorMessage}");
        TunnelRuntimeChanged?.Invoke();
        return result;
    }

    public SshTunnelOperationResult StopTunnel(Guid ruleId)
    {
        lock (_forwardedPortsOperationLock)
        {
            ForwardedPort? forwardedPort;
            lock (_forwardedPortsLock)
            {
                _forwardedPorts.Remove(ruleId, out forwardedPort);
                _forwardedPortStartedAt.Remove(ruleId);
                _forwardedPortErrors.Remove(ruleId);
                _forwardedPortActivity.Remove(ruleId);
            }

            if (forwardedPort != null)
                DisposeForwardedPort(forwardedPort);
        }

        TunnelRuntimeChanged?.Invoke();
        return SshTunnelOperationResult.Completed();
    }

    public SshTunnelOperationResult RestartTunnel(Guid ruleId)
    {
        StopTunnel(ruleId);
        return StartTunnel(ruleId);
    }

    private ForwardedPort CreateForwardedPort(SshTunnelRule rule)
    {
        return rule.Type switch
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
    }

    private static string? ValidateTunnelRule(SshTunnelRule rule)
    {
        if (rule.ListenPort is < 1 or > 65535)
            return "The listen port must be between 1 and 65535.";
        if (rule.Type != SshTunnelRuleType.Dynamic && rule.DestinationPort is < 1 or > 65535)
            return "The destination port must be between 1 and 65535.";
        return null;
    }

    private static string GetTunnelListenHost(SshTunnelRule rule)
    {
        return rule.Type == SshTunnelRuleType.Remote
            ? NormalizeBindHost(rule.SourceHost, fallback: "0.0.0.0")
            : GetLocalBindHost(rule);
    }

    private void HandleTunnelException(Guid ruleId, ForwardedPort forwardedPort, Exception exception)
    {
        SshTunnelRule? rule;
        lock (_forwardedPortsLock)
        {
            if (!_forwardedPorts.TryGetValue(ruleId, out var current) ||
                !ReferenceEquals(current, forwardedPort))
            {
                return;
            }

            _forwardedPortErrors[ruleId] = exception.Message;
            rule = _session?.SshTunnelRules.FirstOrDefault(item => item.Id == ruleId);
        }

        ErrorOccurred?.Invoke(
            $"SSH tunnel {rule?.TypeDisplay ?? "unknown"} {rule?.ListenPort ?? 0} failed: {exception.Message}");
        TunnelRuntimeChanged?.Invoke();
    }

    private void HandleTunnelRequest(
        Guid ruleId,
        ForwardedPort forwardedPort,
        PortForwardEventArgs request)
    {
        lock (_forwardedPortsLock)
        {
            if (!_forwardedPorts.TryGetValue(ruleId, out var current) ||
                !ReferenceEquals(current, forwardedPort))
            {
                return;
            }

            var activity = _forwardedPortActivity.GetValueOrDefault(
                ruleId,
                SshTunnelActivitySnapshot.Empty);
            var originator = $"{request.OriginatorHost}:{request.OriginatorPort}";
            _forwardedPortActivity[ruleId] = activity.RecordConnection(
                DateTimeOffset.UtcNow,
                originator);
        }

        TunnelRuntimeChanged?.Invoke();
    }

    private void DisposeForwardedPort(ForwardedPort forwardedPort)
    {
        try
        {
            if (forwardedPort.IsStarted)
                forwardedPort.Stop();
        }
        catch
        {
            // Tunnel shutdown is best-effort.
        }

        try
        {
            _sshClient?.RemoveForwardedPort(forwardedPort);
        }
        catch
        {
            // The SSH client may already be disconnecting.
        }

        try
        {
            forwardedPort.Dispose();
        }
        catch
        {
            // A failed or partially started tunnel may already be disposed.
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
            RegisterStartupEchoSuppression(command);
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
            RegisterStartupEchoSuppression(Utf8LocaleBootstrapCommand);
            _shellStream?.Write(bytes, 0, bytes.Length);
            _shellStream?.Flush();
        }
        catch
        {
            // Locale bootstrap is best-effort; the shell remains usable if it fails.
        }
    }

    private void RegisterStartupEchoSuppression(string command)
    {
        var normalizedCommand = command.TrimEnd('\r', '\n');
        if (string.IsNullOrWhiteSpace(normalizedCommand))
            return;

        lock (_startupEchoLock)
        {
            _startupEchoSuppressions.Add(normalizedCommand);
            _startupEchoSuppressUntil = DateTimeOffset.UtcNow.Add(StartupEchoSuppressWindow);
        }
    }

    private void ResetStartupEchoSuppression()
    {
        lock (_startupEchoLock)
        {
            _startupEchoSuppressions.Clear();
            _startupEchoBuffer.Clear();
            _startupEchoSuppressUntil = DateTimeOffset.MinValue;
        }
    }

    private string SuppressStartupCommandEchoes(string text)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;

        lock (_startupEchoLock)
        {
            if (_startupEchoSuppressions.Count == 0 && _startupEchoBuffer.Length == 0)
                return text;

            if (DateTimeOffset.UtcNow > _startupEchoSuppressUntil)
            {
                var flushed = _startupEchoBuffer.ToString() + text;
                _startupEchoBuffer.Clear();
                _startupEchoSuppressions.Clear();
                return flushed;
            }

            _startupEchoBuffer.Append(text);
            var output = new StringBuilder();

            while (TryReadBufferedLine(_startupEchoBuffer, out var line))
            {
                if (!IsStartupCommandEchoLine(line))
                    output.Append(line);
            }

            while (TrySuppressBufferedStartupCommandEcho(_startupEchoBuffer))
            {
            }

            if (_startupEchoSuppressions.Count == 0)
            {
                output.Append(_startupEchoBuffer);
                _startupEchoBuffer.Clear();
            }
            else if (_startupEchoBuffer.Length > StartupEchoSuppressMaxBufferLength)
            {
                output.Append(_startupEchoBuffer);
                _startupEchoBuffer.Clear();
                _startupEchoSuppressions.Clear();
            }

            return output.ToString();
        }
    }

    private bool IsStartupCommandEchoLine(string line)
    {
        if (_startupEchoSuppressions.Count == 0)
            return false;

        var normalizedLine = SanitizeStartupEchoText(line).TrimEnd('\r', '\n');
        var shouldSuppress = false;
        for (var i = _startupEchoSuppressions.Count - 1; i >= 0; i--)
        {
            if (MatchesStartupSuppression(normalizedLine, _startupEchoSuppressions[i]))
            {
                _startupEchoSuppressions.RemoveAt(i);
                shouldSuppress = true;
            }
        }

        return shouldSuppress;
    }

    private bool TrySuppressBufferedStartupCommandEcho(StringBuilder buffer)
    {
        if (_startupEchoSuppressions.Count == 0 || buffer.Length == 0)
            return false;

        var text = buffer.ToString();
        for (var i = _startupEchoSuppressions.Count - 1; i >= 0; i--)
        {
            if (!TryFindStartupCommandEchoRange(text, _startupEchoSuppressions[i], out var start, out var length))
                continue;

            buffer.Remove(start, length);
            _startupEchoSuppressions.RemoveAt(i);
            return true;
        }

        return false;
    }

    private static bool TryFindStartupCommandEchoRange(string text, string suppression, out int start, out int length)
    {
        start = 0;
        length = 0;

        var commandIndex = text.IndexOf(suppression, StringComparison.Ordinal);
        if (commandIndex >= 0)
        {
            start = FindStartupEchoLineStart(text, commandIndex);
            length = commandIndex + suppression.Length - start;
            return true;
        }

        if (IsLocaleBootstrapSuppression(suppression) &&
            TryFindFragmentCommandEchoRange(text, "unset LC_ALL", "LC_CTYPE=$LANG", out start, out length))
        {
            return true;
        }

        if (IsX11DisplaySuppression(suppression) &&
            TryFindFragmentCommandEchoRange(text, "export DISPLAY=", null, out start, out length))
        {
            return true;
        }

        return false;
    }

    private static bool TryFindFragmentCommandEchoRange(
        string text,
        string firstFragment,
        string? lastFragment,
        out int start,
        out int length)
    {
        start = 0;
        length = 0;

        var firstIndex = text.IndexOf(firstFragment, StringComparison.Ordinal);
        if (firstIndex < 0)
            return false;

        var end = firstIndex + firstFragment.Length;
        if (!string.IsNullOrEmpty(lastFragment))
        {
            var lastIndex = text.IndexOf(lastFragment, firstIndex, StringComparison.Ordinal);
            if (lastIndex < 0)
                return false;

            end = lastIndex + lastFragment.Length;
        }
        else
        {
            var lineEnd = FindStartupEchoLineEnd(text, firstIndex);
            if (lineEnd >= 0)
                end = lineEnd;
            else if (text.EndsWith('\r') || text.EndsWith('\n'))
                end = text.Length;
        }

        start = FindStartupEchoLineStart(text, firstIndex);
        length = end - start;
        return length > 0;
    }

    private static int FindStartupEchoLineStart(string text, int index)
    {
        for (var i = index - 1; i >= 0; i--)
        {
            if (text[i] is '\r' or '\n')
                return i + 1;
        }

        return 0;
    }

    private static int FindStartupEchoLineEnd(string text, int index)
    {
        for (var i = index; i < text.Length; i++)
        {
            if (text[i] is '\r' or '\n')
                return i;
        }

        return -1;
    }

    private static bool MatchesStartupSuppression(string line, string suppression)
    {
        return line.Contains(suppression, StringComparison.Ordinal) ||
               (IsLocaleBootstrapSuppression(suppression) && IsLocaleBootstrapEcho(line)) ||
               (IsX11DisplaySuppression(suppression) && IsX11DisplayEcho(line));
    }

    private static bool IsLocaleBootstrapSuppression(string command)
    {
        return command.Contains("unset LC_ALL", StringComparison.Ordinal) &&
               command.Contains("LC_CTYPE=$LANG", StringComparison.Ordinal);
    }

    private static bool IsLocaleBootstrapEcho(string line)
    {
        return line.Contains("unset LC_ALL", StringComparison.Ordinal) &&
               line.Contains("LC_CTYPE=$LANG", StringComparison.Ordinal);
    }

    private static bool IsX11DisplaySuppression(string command)
    {
        return command.Contains("export DISPLAY=", StringComparison.Ordinal);
    }

    private static bool IsX11DisplayEcho(string line)
    {
        return line.Contains("export DISPLAY=", StringComparison.Ordinal);
    }

    private static string SanitizeStartupEchoText(string text)
    {
        if (string.IsNullOrEmpty(text) || text.IndexOf('\u001b') < 0)
            return text;

        var sanitized = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (ch != '\u001b')
            {
                sanitized.Append(ch);
                continue;
            }

            if (i + 1 >= text.Length)
                break;

            var next = text[++i];
            if (next == '[')
            {
                while (i + 1 < text.Length)
                {
                    var terminator = text[++i];
                    if (terminator >= '@' && terminator <= '~')
                        break;
                }
            }
            else if (next == ']')
            {
                while (i + 1 < text.Length)
                {
                    var terminator = text[++i];
                    if (terminator == '\a')
                        break;

                    if (terminator == '\u001b' && i + 1 < text.Length && text[i + 1] == '\\')
                    {
                        i++;
                        break;
                    }
                }
            }
        }

        return sanitized.ToString();
    }

    private static bool TryReadBufferedLine(StringBuilder buffer, out string line)
    {
        line = string.Empty;
        for (var i = 0; i < buffer.Length; i++)
        {
            if (buffer[i] is not ('\r' or '\n'))
                continue;

            var end = i + 1;
            while (end < buffer.Length && buffer[end] is '\r' or '\n')
                end++;

            line = buffer.ToString(0, end);
            buffer.Remove(0, end);
            return true;
        }

        return false;
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
        var buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);

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
                        var text = SuppressStartupCommandEchoes(new string(chars, 0, charsRead));
                        if (!string.IsNullOrEmpty(text))
                            DataReceived?.Invoke(text);
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
            ArrayPool<byte>.Shared.Return(buffer);
            RaiseConnectionClosedOnce("Connection closed.");
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
            RaiseConnectionClosedOnce("Connection closed.");
        }
        catch (System.IO.IOException)
        {
            RaiseConnectionClosedOnce("Connection lost.");
        }
        catch (SshConnectionException)
        {
            RaiseConnectionClosedOnce("Connection lost.");
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
            RaiseConnectionClosedOnce("Connection closed.");
        }
        catch (System.IO.IOException)
        {
            RaiseConnectionClosedOnce("Connection lost.");
        }
        catch (SshConnectionException)
        {
            RaiseConnectionClosedOnce("Connection lost.");
        }
    }

    private void RaiseConnectionClosedOnce(string reason)
    {
        if (_connectionClosedRaised)
            return;

        _connectionClosedRaised = true;
        ConnectionClosed?.Invoke(reason);
    }

    public void SendKeepAlive()
    {
        // SSH.NET sends SSH keepalive automatically through KeepAliveInterval.
    }

    public Task<string> RunCommandAsync(
        string commandText,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        return RunCommandAndThrowOnFailureAsync(
            commandText,
            timeout,
            cancellationToken: cancellationToken);
    }

    public Task<string> RunCommandStreamingAsync(
        string commandText,
        TimeSpan timeout,
        Action<string> outputReceived,
        CancellationToken cancellationToken = default,
        Encoding? outputEncoding = null,
        string? inputText = null)
    {
        ArgumentNullException.ThrowIfNull(outputReceived);
        return RunCommandAndThrowOnFailureAsync(
            commandText,
            timeout,
            outputReceived,
            cancellationToken,
            outputEncoding,
            inputText);
    }

    public Task<SshCommandExecutionResult> RunCommandStreamingResultAsync(
        string commandText,
        TimeSpan timeout,
        Action<string>? outputReceived = null,
        Action<string>? errorReceived = null,
        CancellationToken cancellationToken = default,
        Encoding? outputEncoding = null,
        string? inputText = null)
    {
        return RunCommandCoreAsync(
            commandText,
            timeout,
            outputReceived,
            errorReceived,
            cancellationToken,
            outputEncoding,
            inputText);
    }

    private async Task<string> RunCommandAndThrowOnFailureAsync(
        string commandText,
        TimeSpan timeout,
        Action<string>? outputReceived = null,
        CancellationToken cancellationToken = default,
        Encoding? outputEncoding = null,
        string? inputText = null)
    {
        var result = await RunCommandCoreAsync(
                commandText,
                timeout,
                outputReceived,
                errorReceived: null,
                cancellationToken: cancellationToken,
                outputEncoding: outputEncoding,
                inputText: inputText)
            .ConfigureAwait(false);
        ThrowIfCommandFailed(result);
        return result.Output;
    }

    private async Task<SshCommandExecutionResult> RunCommandCoreAsync(
        string commandText,
        TimeSpan timeout,
        Action<string>? outputReceived,
        Action<string>? errorReceived,
        CancellationToken cancellationToken,
        Encoding? outputEncoding = null,
        string? inputText = null)
    {
        if (_sshClient == null || !_sshClient.IsConnected)
            throw new InvalidOperationException("SSH connection is not connected.");

        await _commandGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(async () =>
            {
                if (_sshClient == null || !_sshClient.IsConnected)
                    throw new InvalidOperationException("SSH connection is not connected.");

                var commandEncoding = outputEncoding ?? _terminalEncoding;
                using var command = _sshClient.CreateCommand(commandText, commandEncoding);
                command.CommandTimeout = timeout;
                var executeTask = command.ExecuteAsync(cancellationToken);
                var streamedOutputTask = ReadCommandOutputAsync(
                    command.OutputStream,
                    commandEncoding,
                    outputReceived ?? IgnoreOutput,
                    cancellationToken);
                var streamedErrorTask = ReadCommandOutputAsync(
                    command.ExtendedOutputStream,
                    commandEncoding,
                    errorReceived ?? IgnoreOutput,
                    cancellationToken);

                if (!string.IsNullOrEmpty(inputText))
                {
                    // SSH.NET documents creating the input stream after
                    // ExecuteAsync, writing the payload, then disposing it to
                    // signal EOF to the remote command.
                    using var commandInput = command.CreateInputStream();
                    var inputBytes = commandEncoding.GetBytes(inputText + "\n");
                    commandInput.Write(inputBytes, 0, inputBytes.Length);
                    commandInput.Flush();
                }

                await executeTask.ConfigureAwait(false);
                var output = await streamedOutputTask.ConfigureAwait(false);
                var error = await streamedErrorTask.ConfigureAwait(false);
                return new SshCommandExecutionResult(
                    output,
                    error,
                    command.ExitStatus,
                    Completed: true);
            }, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _commandGate.Release();
        }
    }

    private static void ThrowIfCommandFailed(SshCommandExecutionResult result)
    {
        if (result.Succeeded)
            return;

        var detail = string.IsNullOrWhiteSpace(result.Error)
            ? $"Remote command exited with code {result.ExitStatus?.ToString() ?? "unknown"}."
            : result.Error.Trim();
        throw new InvalidOperationException(detail);
    }

    private static void IgnoreOutput(string _)
    {
    }

    private static async Task<string> ReadCommandOutputAsync(
        Stream outputStream,
        Encoding encoding,
        Action<string> outputReceived,
        CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(
            outputStream,
            encoding,
            detectEncodingFromByteOrderMarks: false,
            bufferSize: 4096,
            leaveOpen: true);
        var output = new StringBuilder();
        var buffer = new char[4096];
        while (true)
        {
            var count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (count == 0)
                break;

            var chunk = new string(buffer, 0, count);
            output.Append(chunk);
            outputReceived(chunk);
        }

        return output.ToString();
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
        _sshConnectionContext?.Dispose();
        _sshConnectionContext = null;

        _readCts?.Dispose();
        _readCts = null;
        _readTask = null;
    }

    private void StopForwardedPorts()
    {
        lock (_forwardedPortsOperationLock)
        {
            ForwardedPort[] forwardedPorts;
            lock (_forwardedPortsLock)
            {
                forwardedPorts = _forwardedPorts.Values.ToArray();
                _forwardedPorts.Clear();
                _forwardedPortStartedAt.Clear();
                _forwardedPortErrors.Clear();
                _forwardedPortActivity.Clear();
            }

            foreach (var forwardedPort in forwardedPorts)
                DisposeForwardedPort(forwardedPort);
        }

        TunnelRuntimeChanged?.Invoke();
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
        _commandGate.Dispose();
    }
}
