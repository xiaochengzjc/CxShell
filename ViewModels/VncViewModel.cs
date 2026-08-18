using System;
using System.ComponentModel;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CxShell.Models;
using CxShell.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MarcusW.VncClient;
using MarcusW.VncClient.Protocol.Implementation;
using MarcusW.VncClient.Protocol.Implementation.MessageTypes.Outgoing;
using MarcusW.VncClient.Protocol.Implementation.SecurityTypes;
using MarcusW.VncClient.Protocol.Implementation.Services.Transports;
using MarcusW.VncClient.Protocol.SecurityTypes;
using MarcusW.VncClient.Security;
using Microsoft.Extensions.Logging;
using Renci.SshNet;

namespace CxShell.ViewModels;

public partial class VncViewModel : ObservableObject, IDisposable
{
    private readonly ILoggerFactory _loggerFactory = new VncFileLoggerFactory();
    private readonly VncClient _client;
    private CancellationTokenSource? _connectCts;
    private SshClient? _sshTunnelClient;
    private ForwardedPortLocal? _sshTunnelPort;

    [ObservableProperty] private RfbConnection? _connection;
    [ObservableProperty] private string _statusText = "Disconnected";
    [ObservableProperty] private bool _isConnected;
    [ObservableProperty] private int _remoteWidth;
    [ObservableProperty] private int _remoteHeight;
    [ObservableProperty] private string _remoteClipboardText = string.Empty;
    [ObservableProperty] private bool _isFitToWindow = true;
    [ObservableProperty] private VncDisplayMode _displayMode = VncDisplayMode.Fit;
    [ObservableProperty] private int _scalePercent = SessionInfo.DefaultVncScalePercent;
    [ObservableProperty] private bool _enableKeyboardInput = true;
    [ObservableProperty] private bool _enableMouseInput = true;
    [ObservableProperty] private bool _captureShortcuts = true;
    [ObservableProperty] private string _cursorMode = "Default";
    [ObservableProperty] private string _clipboardMode = "ManualAndRemoteToLocal";
    [ObservableProperty] private string _resizeMode = "None";

    public string ScaleModeText => DisplayMode switch
    {
        VncDisplayMode.Fit => "完整显示",
        VncDisplayMode.Original => "原始大小",
        VncDisplayMode.FixedScale => $"{ScalePercent}%",
        _ => "完整显示"
    };

    public VncViewModel()
    {
        _client = new VncClient(_loggerFactory, CreateProtocolImplementation());
    }

    private static DefaultImplementation CreateProtocolImplementation()
    {
        return new DefaultImplementation(
            context => [new NoneSecurityType(context), new VncAuthenticationSecurityType(context), new TightVncSecurityType(context)],
            DefaultImplementation.GetDefaultMessageTypes,
            DefaultImplementation.GetDefaultEncodingTypes);
    }

    partial void OnIsFitToWindowChanged(bool value)
    {
        OnPropertyChanged(nameof(ScaleModeText));
    }

    partial void OnDisplayModeChanged(VncDisplayMode value)
    {
        IsFitToWindow = value == VncDisplayMode.Fit;
        OnPropertyChanged(nameof(ScaleModeText));
    }

    partial void OnScalePercentChanged(int value)
    {
        OnPropertyChanged(nameof(ScaleModeText));
    }

    private static VncDisplayMode ResolveDisplayMode(string? displayMode)
    {
        return displayMode switch
        {
            "Original" => VncDisplayMode.Original,
            "FixedScale" => VncDisplayMode.FixedScale,
            _ => VncDisplayMode.Fit
        };
    }

    private static JpegSubsamplingLevel ResolveJpegSubsamplingLevel(string? value)
    {
        return value switch
        {
            "None" => JpegSubsamplingLevel.None,
            "ChrominanceSubsampling2X" => JpegSubsamplingLevel.ChrominanceSubsampling2X,
            "Grayscale" => JpegSubsamplingLevel.Grayscale,
            "ChrominanceSubsampling8X" => JpegSubsamplingLevel.ChrominanceSubsampling8X,
            "ChrominanceSubsampling16X" => JpegSubsamplingLevel.ChrominanceSubsampling16X,
            _ => JpegSubsamplingLevel.ChrominanceSubsampling4X
        };
    }

    public async Task ConnectAsync(SessionInfo session, string? password)
    {
        Disconnect();
        _connectCts = new CancellationTokenSource();

        var connectHost = session.Host?.Trim() ?? string.Empty;
        var connectPort = session.Port is >= 1 and <= 65535 ? session.Port : 5900;
        if (string.IsNullOrWhiteSpace(connectHost))
            throw new InvalidOperationException("VNC host is required.");

        IsConnected = false;
        StatusText = "Connecting...";
        RefreshSessionOptions(session);

        if (session.VncUseSshTunnel)
        {
            connectPort = await StartSshTunnelAsync(session, _connectCts.Token);
            connectHost = IPAddress.Loopback.ToString();
        }

        var parameters = new ConnectParameters
        {
            TransportParameters = new TcpTransportParameters
            {
                Host = connectHost,
                Port = connectPort
            },
            AuthenticationHandler = new StaticAuthenticationHandler(session.Username ?? string.Empty, password ?? string.Empty),
            AllowSharedConnection = true,
            ConnectTimeout = TimeSpan.FromSeconds(15),
            FramebufferUpdateDelay = TimeSpan.FromMilliseconds(Math.Clamp(session.VncFramebufferUpdateDelayMilliseconds <= 0
                ? SessionInfo.DefaultVncFramebufferUpdateDelayMilliseconds
                : session.VncFramebufferUpdateDelayMilliseconds, 1, 1000)),
            PreferredCompressionLevel = Math.Clamp(session.VncCompressionLevel, 0, 9),
            JpegQualityLevel = Math.Clamp(session.VncJpegQualityLevel <= 0
                ? SessionInfo.DefaultVncJpegQualityLevel
                : session.VncJpegQualityLevel, 1, 100),
            JpegSubsamplingLevel = ResolveJpegSubsamplingLevel(session.VncJpegSubsampling),
            EncodingProfile = string.IsNullOrWhiteSpace(session.VncEncodingProfile)
                ? "Compatibility"
                : session.VncEncodingProfile
        };

        StatusText = $"Connecting to {connectHost}:{connectPort}...";
        var connection = await _client.ConnectAsync(parameters, _connectCts.Token);

        AttachConnection(connection);
        Connection = connection;
        UpdateConnectionInfo(connection);
        IsConnected = connection.ConnectionState == ConnectionState.Connected;
        StatusText = $"Connected: {connection.DesktopName} {RemoteWidth}x{RemoteHeight}";
    }

    public void RefreshSessionOptions(SessionInfo session)
    {
        DisplayMode = ResolveDisplayMode(session.VncDisplayMode);
        ScalePercent = Math.Clamp(session.VncScalePercent <= 0
            ? SessionInfo.DefaultVncScalePercent
            : session.VncScalePercent, 25, 300);
        EnableKeyboardInput = !session.VncReadOnlyMode && session.VncEnableKeyboardInput;
        EnableMouseInput = !session.VncReadOnlyMode && session.VncEnableMouseInput;
        CaptureShortcuts = session.VncCaptureShortcuts;
        CursorMode = string.IsNullOrWhiteSpace(session.VncCursorMode) ? "Default" : session.VncCursorMode;
        ClipboardMode = string.IsNullOrWhiteSpace(session.VncClipboardMode)
            ? "ManualAndRemoteToLocal"
            : session.VncClipboardMode;
        ResizeMode = string.IsNullOrWhiteSpace(session.VncResizeMode)
            ? "None"
            : session.VncResizeMode;
    }

    public bool SendClipboardText(string text)
    {
        if (Connection == null || string.IsNullOrEmpty(text))
            return false;

        return Connection.SendClipboardText(text, CancellationToken.None);
    }

    public async Task SendClipboardTextAndPasteAsync(string text)
    {
        if (!IsConnected || Connection == null || string.IsNullOrEmpty(text))
            return;

        await ReleaseCommonModifiersAsync();
        await Task.Delay(30);

        var needsDirectUnicodeInput = NeedsDirectUnicodeInput(text);
        if (needsDirectUnicodeInput && Connection.ServerSupportsExtendedClipboard)
        {
            if (SendClipboardText(text))
            {
                await Task.Delay(120);
                await SendCtrlVAsync();
                return;
            }
        }

        if (needsDirectUnicodeInput)
        {
            if (SendClipboardText(text))
            {
                await Task.Delay(120);
                await SendCtrlVAsync();
                return;
            }

            await TypeTextAsync(text);
            return;
        }

        SendClipboardText(text);
        await Task.Delay(80);
        await SendCtrlVAsync();
    }

    [RelayCommand]
    private void ToggleScaleMode()
    {
        DisplayMode = DisplayMode switch
        {
            VncDisplayMode.Fit => VncDisplayMode.Original,
            VncDisplayMode.Original => VncDisplayMode.FixedScale,
            _ => VncDisplayMode.Fit
        };
    }

    [RelayCommand]
    private Task SendCtrlAltDelete()
    {
        return SendCtrlAltDeleteAsync();
    }

    public async Task SendCtrlAltDeleteAsync()
    {
        if (!IsConnected || Connection == null)
            return;

        try
        {
            await SendKeyAsync(KeySymbol.Control_L, true);
            await Task.Delay(20);
            await SendKeyAsync(KeySymbol.Alt_L, true);
            await Task.Delay(20);
            await SendKeyAsync(KeySymbol.Delete, true);
            await Task.Delay(60);
        }
        finally
        {
            await SendKeyAsync(KeySymbol.Delete, false);
            await SendKeyAsync(KeySymbol.Alt_L, false);
            await SendKeyAsync(KeySymbol.Control_L, false);
        }
    }

    private async Task SendCtrlVAsync()
    {
        if (!IsConnected || Connection == null)
            return;

        try
        {
            await SendKeyAsync(KeySymbol.Control_L, true);
            await Task.Delay(20);
            await SendKeyAsync(KeySymbol.v, true);
            await Task.Delay(40);
        }
        finally
        {
            await SendKeyAsync(KeySymbol.v, false);
            await SendKeyAsync(KeySymbol.Control_L, false);
        }
    }

    private async Task ReleaseCommonModifiersAsync()
    {
        var modifiers = new[]
        {
            KeySymbol.Shift_L,
            KeySymbol.Shift_R,
            KeySymbol.Control_L,
            KeySymbol.Control_R,
            KeySymbol.Alt_L,
            KeySymbol.Alt_R,
            KeySymbol.Super_L,
            KeySymbol.Super_R
        };

        foreach (var modifier in modifiers)
            await SendKeyAsync(modifier, false);
    }

    private async Task TypeTextAsync(string text)
    {
        var normalizedText = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        foreach (var rune in normalizedText.EnumerateRunes())
        {
            var keySymbol = rune.Value switch
            {
                '\n' => KeySymbol.Return,
                '\t' => KeySymbol.Tab,
                >= 0x20 and <= 0x7e => (KeySymbol)rune.Value,
                _ => (KeySymbol)(0x01000000 | rune.Value)
            };

            await SendKeyTapAsync(keySymbol);
            await Task.Delay(5);
        }
    }

    private async Task SendKeyTapAsync(KeySymbol keySymbol)
    {
        await SendKeyAsync(keySymbol, true);
        await Task.Delay(5);
        await SendKeyAsync(keySymbol, false);
    }

    private static bool NeedsDirectUnicodeInput(string text)
    {
        foreach (var rune in text.EnumerateRunes())
        {
            if (rune.Value > 0xff)
                return true;
        }

        return false;
    }

    private Task SendKeyAsync(KeySymbol keySymbol, bool down)
    {
        var connection = Connection;
        return connection == null
            ? Task.CompletedTask
            : connection.SendMessageAsync<KeyEventMessageType>(
                new KeyEventMessage(down, keySymbol),
                CancellationToken.None);
    }

    private void AttachConnection(RfbConnection connection)
    {
        connection.PropertyChanged += OnConnectionPropertyChanged;
        connection.ConnectionStateChanged += OnConnectionStateChanged;
    }

    private void DetachConnection(RfbConnection? connection)
    {
        if (connection == null)
            return;

        connection.PropertyChanged -= OnConnectionPropertyChanged;
        connection.ConnectionStateChanged -= OnConnectionStateChanged;
    }

    private void OnConnectionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not RfbConnection connection)
            return;

        if (e.PropertyName is nameof(RfbConnection.RemoteFramebufferSize) or nameof(RfbConnection.DesktopName) or nameof(RfbConnection.InterruptionCause))
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (!ReferenceEquals(connection, Connection))
                    return;

                if (e.PropertyName == nameof(RfbConnection.InterruptionCause) && connection.InterruptionCause != null)
                {
                    StatusText = $"VNC interrupted: {connection.InterruptionCause.Message}";
                    return;
                }

                UpdateConnectionInfo(connection);
            });
        }
    }

    private void OnConnectionStateChanged(object? sender, ConnectionStateChangedEventArgs e)
    {
        if (sender is not RfbConnection connection)
            return;

        Dispatcher.UIThread.Post(() =>
        {
            if (!ReferenceEquals(connection, Connection))
                return;

            IsConnected = e.CurrentState == ConnectionState.Connected;
            StatusText = e.CurrentState switch
            {
                ConnectionState.Connected => $"Connected: {connection.DesktopName} {RemoteWidth}x{RemoteHeight}",
                ConnectionState.Reconnecting => "VNC reconnecting...",
                ConnectionState.Interrupted => e.Exception == null ? "VNC interrupted" : $"VNC interrupted: {e.Exception.Message}",
                ConnectionState.ReconnectFailed => "VNC reconnect failed",
                ConnectionState.Closed => "Disconnected",
                _ => $"VNC {e.CurrentState}"
            };
        });
    }

    private void UpdateConnectionInfo(RfbConnection connection)
    {
        RemoteWidth = connection.RemoteFramebufferSize.Width;
        RemoteHeight = connection.RemoteFramebufferSize.Height;
        if (connection.ConnectionState == ConnectionState.Connected)
            StatusText = $"Connected: {connection.DesktopName} {RemoteWidth}x{RemoteHeight}";
    }

    private async Task<int> StartSshTunnelAsync(SessionInfo session, CancellationToken cancellationToken)
    {
        var sshHost = string.IsNullOrWhiteSpace(session.VncSshHost) ? session.Host : session.VncSshHost.Trim();
        var sshPort = session.VncSshPort is >= 1 and <= 65535 ? session.VncSshPort : 22;
        var sshUser = session.VncSshUsername?.Trim() ?? string.Empty;
        var remoteHost = string.IsNullOrWhiteSpace(session.VncSshRemoteHost)
            ? (string.IsNullOrWhiteSpace(session.Host) ? "127.0.0.1" : session.Host.Trim())
            : session.VncSshRemoteHost.Trim();
        var remotePort = session.VncSshRemotePort is >= 1 and <= 65535
            ? session.VncSshRemotePort
            : session.Port is >= 1 and <= 65535 ? session.Port : 5901;
        var localPort = GetFreeLoopbackPort();

        if (string.IsNullOrWhiteSpace(sshHost))
            throw new InvalidOperationException("VNC SSH tunnel host is required.");
        if (string.IsNullOrWhiteSpace(sshUser))
            throw new InvalidOperationException("VNC SSH tunnel username is required.");

        var authMethods = CreateVncSshAuthMethods(session, sshUser);
        var connectionInfo = new ConnectionInfo(sshHost, sshPort, sshUser, authMethods);
        _sshTunnelClient = new SshClient(connectionInfo);
        SshHostKeyTrustService.Shared.Attach(
            _sshTunnelClient,
            sshHost,
            sshPort,
            session.SshAcceptAndSaveHostKey);

        StatusText = $"Opening SSH tunnel to {sshHost}:{sshPort}...";
        await Task.Run(() => _sshTunnelClient.Connect(), cancellationToken);

        _sshTunnelPort = new ForwardedPortLocal("127.0.0.1", (uint)localPort, remoteHost, (uint)remotePort);
        _sshTunnelClient.AddForwardedPort(_sshTunnelPort);
        _sshTunnelPort.Start();
        return localPort;
    }

    private static AuthenticationMethod[] CreateVncSshAuthMethods(SessionInfo session, string username)
    {
        if (session.VncSshUsePrivateKey)
        {
            if (string.IsNullOrWhiteSpace(session.VncSshPrivateKeyPath))
                throw new InvalidOperationException("VNC SSH private key path is required.");
            return [new PrivateKeyAuthenticationMethod(username, new PrivateKeyFile(ExpandPath(session.VncSshPrivateKeyPath)))];
        }

        var password = PasswordEncryptionService.Decrypt(session.VncSshPassword);
        return [new PasswordAuthenticationMethod(username, password)];
    }

    private static int GetFreeLoopbackPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static string ExpandPath(string path)
    {
        if (path.StartsWith("~", StringComparison.Ordinal))
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                path[2..]);
        }

        return Path.GetFullPath(path);
    }

    private void StopSshTunnel()
    {
        try
        {
            if (_sshTunnelPort?.IsStarted == true)
                _sshTunnelPort.Stop();
        }
        catch
        {
            // Ignore tunnel shutdown failures.
        }

        try
        {
            if (_sshTunnelClient?.IsConnected == true)
                _sshTunnelClient.Disconnect();
        }
        catch
        {
            // Ignore tunnel shutdown failures.
        }

        _sshTunnelPort?.Dispose();
        _sshTunnelClient?.Dispose();
        _sshTunnelPort = null;
        _sshTunnelClient = null;
    }

    public void Disconnect()
    {
        _connectCts?.Cancel();
        _connectCts?.Dispose();
        _connectCts = null;

        var connection = Connection;
        DetachConnection(connection);
        Connection = null;
        IsConnected = false;
        RemoteWidth = 0;
        RemoteHeight = 0;
        StatusText = "Disconnected";

        if (connection != null)
        {
            try
            {
                connection.CloseAsync().Wait(TimeSpan.FromSeconds(2));
            }
            catch
            {
                // Ignore disconnect failures.
            }

            connection.Dispose();
        }

        StopSshTunnel();
    }

    public void Dispose()
    {
        Disconnect();
        _loggerFactory.Dispose();
    }

    private sealed class StaticAuthenticationHandler : IAuthenticationHandler
    {
        private readonly string _username;
        private readonly string _password;

        public StaticAuthenticationHandler(string username, string password)
        {
            _username = username;
            _password = password;
        }

        public Task<TInput> ProvideAuthenticationInputAsync<TInput>(
            RfbConnection connection,
            ISecurityType securityType,
            IAuthenticationInputRequest<TInput> request)
            where TInput : class, IAuthenticationInput
        {
            if (typeof(TInput) == typeof(PasswordAuthenticationInput))
                return Task.FromResult((TInput)(object)new PasswordAuthenticationInput(_password));

            if (typeof(TInput) == typeof(CredentialsAuthenticationInput))
                return Task.FromResult((TInput)(object)new CredentialsAuthenticationInput(_username, _password));

            throw new NotSupportedException($"Unsupported VNC authentication input: {typeof(TInput).Name}");
        }
    }

    private sealed class TightVncSecurityType : ISecurityType
    {
        private const uint NoTunneling = 0;
        private const uint NoneAuthentication = 1;
        private const uint VncAuthentication = 2;
        private const uint TightAuthentication = 16;
        private const uint UnixLoginAuthentication = 129;

        private readonly MarcusW.VncClient.Protocol.RfbConnectionContext _context;

        public TightVncSecurityType(MarcusW.VncClient.Protocol.RfbConnectionContext context)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
        }

        public byte Id => 16;
        public string Name => "Tight";
        public int Priority => 5;

        public async Task<MarcusW.VncClient.Protocol.SecurityTypes.AuthenticationResult> AuthenticateAsync(
            IAuthenticationHandler authenticationHandler,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(authenticationHandler);
            var transport = _context.Transport ?? throw new InvalidOperationException("Cannot access transport for authentication.");
            var stream = transport.Stream;

            var tunnelTypes = await ReadTightCapabilitiesAsync(stream, cancellationToken);
            if (tunnelTypes.Length > 0)
            {
                if (!ContainsCapability(tunnelTypes, NoTunneling))
                    throw new NotSupportedException("The VNC server did not offer Tight no-tunneling mode.");

                await WriteUInt32BigEndianAsync(stream, NoTunneling, cancellationToken);
            }

            var authTypes = await ReadTightCapabilitiesAsync(stream, cancellationToken);
            var selectedAuthType = SelectAuthenticationType(authTypes);
            if (selectedAuthType == 0)
            {
                throw new NotSupportedException(
                    $"No supported Tight authentication type found. Offered: {string.Join(",", authTypes)}");
            }

            if (authTypes.Length > 0)
                await WriteUInt32BigEndianAsync(stream, selectedAuthType, cancellationToken);

            await AuthenticateSelectedTypeAsync(stream, selectedAuthType, authenticationHandler, cancellationToken);
            return new MarcusW.VncClient.Protocol.SecurityTypes.AuthenticationResult();
        }

        public Task ReadServerInitExtensionAsync(CancellationToken cancellationToken = default)
        {
            return ReadTightServerInitExtensionAsync(cancellationToken);
        }

        private async Task AuthenticateSelectedTypeAsync(
            Stream stream,
            uint authType,
            IAuthenticationHandler authenticationHandler,
            CancellationToken cancellationToken)
        {
            switch (authType)
            {
                case NoneAuthentication:
                    return;
                case VncAuthentication:
                    await PerformVncAuthenticationAsync(stream, authenticationHandler, cancellationToken);
                    return;
                case TightAuthentication:
                    await PerformCredentialsAuthenticationAsync(stream, authenticationHandler, cancellationToken);
                    return;
                case UnixLoginAuthentication:
                    await PerformCredentialsAuthenticationAsync(stream, authenticationHandler, cancellationToken);
                    return;
                default:
                    throw new NotSupportedException($"Tight authentication type {authType} is not supported.");
            }
        }

        private async Task PerformVncAuthenticationAsync(
            Stream stream,
            IAuthenticationHandler authenticationHandler,
            CancellationToken cancellationToken)
        {
            var challenge = new byte[16];
            await stream.ReadExactlyAsync(challenge, cancellationToken);
            var input = await authenticationHandler.ProvideAuthenticationInputAsync(
                _context.Connection,
                this,
                new PasswordAuthenticationInputRequest());
            await stream.WriteAsync(CreateVncPasswordResponse(input.Password, challenge), cancellationToken);
        }

        private async Task PerformCredentialsAuthenticationAsync(
            Stream stream,
            IAuthenticationHandler authenticationHandler,
            CancellationToken cancellationToken)
        {
            var input = await authenticationHandler.ProvideAuthenticationInputAsync(
                _context.Connection,
                this,
                new CredentialsAuthenticationInputRequest());
            await WriteLengthPrefixedUtf8Async(stream, input.Username ?? string.Empty, cancellationToken);
            await WriteLengthPrefixedUtf8Async(stream, input.Password ?? string.Empty, cancellationToken);
        }

        private static async Task<uint[]> ReadTightCapabilitiesAsync(Stream stream, CancellationToken cancellationToken)
        {
            var count = await ReadUInt32BigEndianAsync(stream, cancellationToken);
            if (count == 0)
                return [];

            if (count > 256)
                throw new InvalidOperationException($"The VNC server returned an invalid Tight capability count: {count}.");

            var capabilities = new uint[count];
            var capability = new byte[16];
            for (var i = 0; i < count; i++)
            {
                await stream.ReadExactlyAsync(capability, cancellationToken);
                capabilities[i] = ReadUInt32BigEndian(capability);
            }

            return capabilities;
        }

        private async Task ReadTightServerInitExtensionAsync(CancellationToken cancellationToken)
        {
            var transport = _context.Transport ?? throw new InvalidOperationException("Cannot access transport for server initialization.");
            var header = new byte[8];
            await transport.Stream.ReadExactlyAsync(header, cancellationToken);

            var serverMessageTypeCount = ReadUInt16BigEndian(header, 0);
            var clientMessageTypeCount = ReadUInt16BigEndian(header, 2);
            var encodingTypeCount = ReadUInt16BigEndian(header, 4);
            var capabilityCount = serverMessageTypeCount + clientMessageTypeCount + encodingTypeCount;
            if (capabilityCount == 0)
                return;
            if (capabilityCount > 1024)
                throw new InvalidOperationException($"The VNC server returned an invalid Tight server capability count: {capabilityCount}.");

            var capabilities = new byte[capabilityCount * 16];
            await transport.Stream.ReadExactlyAsync(capabilities, cancellationToken);
        }

        private static uint SelectAuthenticationType(uint[] authTypes)
        {
            if (authTypes.Length == 0)
                return NoneAuthentication;
            if (ContainsCapability(authTypes, VncAuthentication))
                return VncAuthentication;
            if (ContainsCapability(authTypes, NoneAuthentication))
                return NoneAuthentication;
            if (ContainsCapability(authTypes, TightAuthentication))
                return TightAuthentication;
            if (ContainsCapability(authTypes, UnixLoginAuthentication))
                return UnixLoginAuthentication;
            return 0;
        }

        private static bool ContainsCapability(uint[] capabilities, uint capability)
        {
            foreach (var item in capabilities)
            {
                if (item == capability)
                    return true;
            }

            return false;
        }

        private static async Task<uint> ReadUInt32BigEndianAsync(Stream stream, CancellationToken cancellationToken)
        {
            var buffer = new byte[4];
            await stream.ReadExactlyAsync(buffer, cancellationToken);
            return ReadUInt32BigEndian(buffer);
        }

        private static uint ReadUInt32BigEndian(byte[] buffer)
        {
            return ((uint)buffer[0] << 24) |
                   ((uint)buffer[1] << 16) |
                   ((uint)buffer[2] << 8) |
                   buffer[3];
        }

        private static int ReadUInt16BigEndian(byte[] buffer, int offset)
        {
            return (buffer[offset] << 8) | buffer[offset + 1];
        }

        private static Task WriteUInt32BigEndianAsync(Stream stream, uint value, CancellationToken cancellationToken)
        {
            var buffer = new[]
            {
                (byte)(value >> 24),
                (byte)(value >> 16),
                (byte)(value >> 8),
                (byte)value
            };
            return stream.WriteAsync(buffer, cancellationToken).AsTask();
        }

        private static async Task WriteLengthPrefixedUtf8Async(
            Stream stream,
            string value,
            CancellationToken cancellationToken)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            if (bytes.Length > byte.MaxValue)
                throw new InvalidOperationException("TightVNC username/password is too long.");

            await stream.WriteAsync(new[] { (byte)bytes.Length }, cancellationToken);
            await stream.WriteAsync(bytes, cancellationToken);
        }

        private static byte[] CreateVncPasswordResponse(string password, byte[] challenge)
        {
            var key = new byte[8];
            var passwordBytes = Encoding.UTF8.GetBytes(password ?? string.Empty);
            Array.Copy(passwordBytes, key, Math.Min(key.Length, passwordBytes.Length));

            for (var i = 0; i < key.Length; i++)
                key[i] = ReverseBits(key[i]);

            using var des = DES.Create();
            des.Mode = CipherMode.ECB;
            des.Padding = PaddingMode.None;
            des.Key = key;

            using var encryptor = des.CreateEncryptor();
            var response = encryptor.TransformFinalBlock(challenge, 0, challenge.Length);
            Array.Clear(key);
            Array.Clear(passwordBytes);
            return response;
        }

        private static byte ReverseBits(byte value)
        {
            var result = 0;
            for (var i = 0; i < 8; i++)
                result = (result << 1) | ((value >> i) & 1);

            return (byte)result;
        }
    }

    private sealed class VncFileLoggerFactory : ILoggerFactory
    {
        private readonly string _logPath = Path.Combine(GetLogDirectory(), "vnc-marcusw.log");

        public ILogger CreateLogger(string categoryName)
        {
            return new VncFileLogger(_logPath, categoryName);
        }

        public void AddProvider(ILoggerProvider provider)
        {
        }

        public void Dispose()
        {
        }

        private static string GetLogDirectory()
        {
            var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(root))
                root = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (string.IsNullOrWhiteSpace(root))
                root = AppContext.BaseDirectory;

            var directory = Path.Combine(root, "CxShell", "Logs");
            Directory.CreateDirectory(directory);
            return directory;
        }
    }

    private sealed class VncFileLogger : ILogger
    {
        private static readonly object FileLock = new();
        private readonly string _logPath;
        private readonly string _categoryName;

        public VncFileLogger(string logPath, string categoryName)
        {
            _logPath = logPath;
            _categoryName = categoryName;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull
        {
            return null;
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return logLevel >= LogLevel.Warning;
        }

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
                return;

            try
            {
                var message = formatter(state, exception);
                var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} [{logLevel}] {_categoryName}: {message}";
                if (exception != null)
                    line += $"{Environment.NewLine}{exception}";

                lock (FileLock)
                    File.AppendAllText(_logPath, line + Environment.NewLine, Encoding.UTF8);
            }
            catch
            {
                // Logging must never break the VNC connection path.
            }
        }
    }
}
