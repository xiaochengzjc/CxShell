using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ChiXueSsh.Models;

namespace ChiXueSsh.Services;

public sealed class TelnetConnectionService : ITerminalConnectionService
{
    private const byte Iac = 255;
    private const byte Dont = 254;
    private const byte Do = 253;
    private const byte Wont = 252;
    private const byte Will = 251;
    private const byte Sb = 250;
    private const byte Se = 240;
    private const byte TerminalType = 24;
    private const byte Naws = 31;
    private const byte Linemode = 34;
    private const byte XDisplayLocation = 35;
    private const byte SuppressGoAhead = 3;
    private const byte Echo = 1;
    private const byte Binary = 0;
    private const byte SubOptionIs = 0;
    private const byte SubOptionSend = 1;

    private readonly object _writeLock = new();
    private Encoding _terminalEncoding = Encoding.UTF8;
    private Decoder _terminalDecoder = Encoding.UTF8.GetDecoder();
    private SessionInfo? _session;
    private TcpClient? _client;
    private NetworkStream? _stream;
    private CancellationTokenSource? _readCts;
    private Task? _readTask;
    private int _columns = 80;
    private int _rows = 24;
    private bool _useXDisplayLocation = true;
    private bool _forceCharacterAtATime;
    private string _xDisplayLocation = "$PCADDR:0.0";
    private string _username = string.Empty;
    private string? _password;
    private string _usernamePrompt = "ogin:";
    private string _passwordPrompt = "assword:";
    private string _terminalType = "xterm";
    private readonly StringBuilder _loginProbeBuffer = new();
    private bool _usernameSent;
    private bool _passwordSent;
    private TelnetParseState _parseState;
    private byte _pendingCommand;
    private readonly List<byte> _subNegotiation = new();

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
        _session = session;
        _terminalEncoding = TerminalSessionOptions.GetEncoding(session);
        _terminalDecoder = _terminalEncoding.GetDecoder();
        _terminalType = TerminalSessionOptions.GetTerminalType(session);

        _columns = columns;
        _rows = rows;
        _useXDisplayLocation = session.TelnetUseXDisplayLocation;
        _forceCharacterAtATime = session.TelnetForceCharacterAtATime;
        _xDisplayLocation = string.IsNullOrWhiteSpace(session.TelnetXDisplayLocation)
            ? "$PCADDR:0.0"
            : session.TelnetXDisplayLocation;
        _username = session.Username;
        _password = password;
        _usernamePrompt = string.IsNullOrWhiteSpace(session.TelnetUsernamePrompt)
            ? "ogin:"
            : session.TelnetUsernamePrompt;
        _passwordPrompt = string.IsNullOrWhiteSpace(session.TelnetPasswordPrompt)
            ? "assword:"
            : session.TelnetPasswordPrompt;
        _loginProbeBuffer.Clear();
        _usernameSent = false;
        _passwordSent = false;
        _parseState = TelnetParseState.Data;
        _subNegotiation.Clear();
        _terminalDecoder.Reset();

        try
        {
            TraceTelnet($"connecting to {session.Host}:{session.Port}");
            _client = await ProxyConnectionFactory.ConnectTcpAsync(
                session.Host,
                session.Port,
                session.Proxy,
                cancellationToken,
                session.AdvancedIpVersion);
            ApplyTcpKeepAlive(_client, session.TcpKeepAlive);
            _client.NoDelay = !session.AdvancedUseNagle;
            _stream = _client.GetStream();
            TraceTelnet($"connected; option mode={session.TelnetOptionMode}, terminal={_terminalType}");
            if (string.Equals(session.TelnetOptionMode, "Active", StringComparison.OrdinalIgnoreCase))
                SendActiveNegotiation();

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
        var normalized = TerminalSessionOptions.NormalizeSendLineEndings(data, _session);
        SendBytes(_terminalEncoding.GetBytes(normalized));
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
        SendNaws();
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

                var terminalBytes = FilterTelnetCommands(chunk);
                if (terminalBytes.Length == 0)
                    continue;

                var charCount = _terminalDecoder.GetCharCount(terminalBytes, 0, terminalBytes.Length);
                if (charCount == 0)
                    continue;

                var chars = new char[charCount];
                var charsRead = _terminalDecoder.GetChars(terminalBytes, 0, terminalBytes.Length, chars, 0);
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

    private byte[] FilterTelnetCommands(byte[] bytes)
    {
        var output = new List<byte>(bytes.Length);

        foreach (var value in bytes)
        {
            switch (_parseState)
            {
                case TelnetParseState.Data:
                    if (value == Iac)
                        _parseState = TelnetParseState.Iac;
                    else
                        output.Add(value);
                    break;

                case TelnetParseState.Iac:
                    HandleIac(value, output);
                    break;

                case TelnetParseState.Option:
                    HandleOption(value);
                    _parseState = TelnetParseState.Data;
                    break;

                case TelnetParseState.SubNegotiation:
                    if (value == Iac)
                        _parseState = TelnetParseState.SubNegotiationIac;
                    else
                        _subNegotiation.Add(value);
                    break;

                case TelnetParseState.SubNegotiationIac:
                    if (value == Se)
                    {
                        HandleSubNegotiation();
                        _subNegotiation.Clear();
                        _parseState = TelnetParseState.Data;
                    }
                    else
                    {
                        _subNegotiation.Add(Iac);
                        _subNegotiation.Add(value);
                        _parseState = TelnetParseState.SubNegotiation;
                    }
                    break;
            }
        }

        return output.ToArray();
    }

    private void HandleIac(byte value, List<byte> output)
    {
        switch (value)
        {
            case Iac:
                output.Add(Iac);
                _parseState = TelnetParseState.Data;
                break;
            case Do:
            case Dont:
            case Will:
            case Wont:
                _pendingCommand = value;
                _parseState = TelnetParseState.Option;
                break;
            case Sb:
                _subNegotiation.Clear();
                _parseState = TelnetParseState.SubNegotiation;
                break;
            default:
                _parseState = TelnetParseState.Data;
                break;
        }
    }

    private void HandleOption(byte option)
    {
        TraceTelnet($"received {TelnetCommandName(_pendingCommand)} {TelnetOptionName(option)}");
        switch (_pendingCommand)
        {
            case Do:
                if (option is SuppressGoAhead or Binary or Naws or TerminalType ||
                    option == XDisplayLocation && _useXDisplayLocation)
                {
                    SendCommand(Will, option);
                    if (option == Naws)
                        SendNaws();
                }
                else
                {
                    SendCommand(Wont, option);
                }
                break;

            case Will:
                if (option is SuppressGoAhead or Echo or Binary)
                    SendCommand(Do, option);
                else if (option == Linemode && _forceCharacterAtATime)
                    SendCommand(Dont, option);
                else
                    SendCommand(Dont, option);
                break;

            case Dont:
                SendCommand(Wont, option);
                break;

            case Wont:
                SendCommand(Dont, option);
                break;
        }
    }

    private void HandleSubNegotiation()
    {
        if (_subNegotiation.Count > 0)
            TraceTelnet($"received SB {TelnetOptionName(_subNegotiation[0])}");

        if (_subNegotiation.Count >= 2 &&
            _subNegotiation[0] == TerminalType &&
            _subNegotiation[1] == SubOptionSend)
        {
            var terminalName = Encoding.ASCII.GetBytes(_terminalType);
            var response = new byte[terminalName.Length + 6];
            response[0] = Iac;
            response[1] = Sb;
            response[2] = TerminalType;
            response[3] = SubOptionIs;
            Buffer.BlockCopy(terminalName, 0, response, 4, terminalName.Length);
            response[^2] = Iac;
            response[^1] = Se;
            SendBytes(response);
            return;
        }

        if (_useXDisplayLocation &&
            _subNegotiation.Count >= 2 &&
            _subNegotiation[0] == XDisplayLocation &&
            _subNegotiation[1] == SubOptionSend)
        {
            SendXDisplayLocation();
        }
    }

    private void SendActiveNegotiation()
    {
        SendCommand(Will, SuppressGoAhead);
        SendCommand(Will, Binary);
        SendCommand(Will, Naws);
        SendCommand(Will, TerminalType);
        SendCommand(Do, SuppressGoAhead);
        SendCommand(Do, Echo);
        SendCommand(Do, Binary);

        if (_useXDisplayLocation)
            SendCommand(Will, XDisplayLocation);

        if (_forceCharacterAtATime)
            SendCommand(Wont, Linemode);

        SendNaws();
    }

    private void SendCommand(byte command, byte option)
    {
        TraceTelnet($"sent {TelnetCommandName(command)} {TelnetOptionName(option)}");
        SendBytes(new[] { Iac, command, option });
    }

    private void SendNaws()
    {
        TraceTelnet($"sent SB NAWS {_columns}x{_rows}");
        var width = Math.Clamp(_columns, 1, ushort.MaxValue);
        var height = Math.Clamp(_rows, 1, ushort.MaxValue);
        SendBytes(new[]
        {
            Iac, Sb, Naws,
            (byte)(width >> 8), (byte)(width & 0xff),
            (byte)(height >> 8), (byte)(height & 0xff),
            Iac, Se
        });
    }

    private void SendXDisplayLocation()
    {
        var display = ResolveXDisplayLocation();
        TraceTelnet($"sent SB XDISPLOC {display}");
        var displayBytes = Encoding.ASCII.GetBytes(display);
        var response = new List<byte>(displayBytes.Length + 6) { Iac, Sb, XDisplayLocation, SubOptionIs };

        foreach (var value in displayBytes)
        {
            response.Add(value);
            if (value == Iac)
                response.Add(Iac);
        }

        response.Add(Iac);
        response.Add(Se);
        SendBytes(response.ToArray());
    }

    private string ResolveXDisplayLocation()
    {
        var localAddress = "127.0.0.1";
        if (_client?.Client.LocalEndPoint is IPEndPoint endpoint)
            localAddress = endpoint.Address.ToString();

        return _xDisplayLocation.Replace("$PCADDR", localAddress, StringComparison.OrdinalIgnoreCase);
    }

    private void TraceTelnet(string message)
    {
        if (_session?.AdvancedTraceTelnetOptions == true)
            DataReceived?.Invoke($"\r\n[TRACE TELNET] {message}\r\n");
    }

    private static string TelnetCommandName(byte command)
    {
        return command switch
        {
            Do => "DO",
            Dont => "DONT",
            Will => "WILL",
            Wont => "WONT",
            Sb => "SB",
            Se => "SE",
            _ => $"0x{command:x2}"
        };
    }

    private static string TelnetOptionName(byte option)
    {
        return option switch
        {
            Binary => "BINARY",
            Echo => "ECHO",
            SuppressGoAhead => "SUPPRESS-GO-AHEAD",
            TerminalType => "TERMINAL-TYPE",
            Naws => "NAWS",
            Linemode => "LINEMODE",
            XDisplayLocation => "XDISPLOC",
            _ => $"OPTION-{option}"
        };
    }

    private void HandleLoginPrompt(string text)
    {
        if (string.IsNullOrEmpty(text))
            return;

        _loginProbeBuffer.Append(text);
        if (_loginProbeBuffer.Length > 512)
            _loginProbeBuffer.Remove(0, _loginProbeBuffer.Length - 512);

        var probe = _loginProbeBuffer.ToString();
        if (!_usernameSent &&
            !string.IsNullOrWhiteSpace(_username) &&
            PromptSeen(probe, _usernamePrompt))
        {
            _usernameSent = true;
            SendLine(_username);
            _loginProbeBuffer.Clear();
            return;
        }

        if (!_passwordSent &&
            !string.IsNullOrEmpty(_password) &&
            PromptSeen(probe, _passwordPrompt))
        {
            _passwordSent = true;
            SendLine(_password);
            _loginProbeBuffer.Clear();
        }
    }

    private static bool PromptSeen(string text, string prompt)
    {
        return !string.IsNullOrWhiteSpace(prompt) &&
               text.Contains(prompt, StringComparison.OrdinalIgnoreCase);
    }

    private void SendLine(string value)
    {
        var lineEnding = TerminalSessionOptions.ResolveLineEnding(_session?.TerminalSendLineEnding, "CRLF");
        SendBytes(_terminalEncoding.GetBytes(value + lineEnding));
    }

    private static void ApplyTcpKeepAlive(TcpClient client, bool enabled)
    {
        try
        {
            client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, enabled);
        }
        catch
        {
            // Some platforms/proxies do not expose TCP keepalive tuning; the session remains usable.
        }
    }

    public void Dispose()
    {
        Disconnect();
    }

    private enum TelnetParseState
    {
        Data,
        Iac,
        Option,
        SubNegotiation,
        SubNegotiationIac
    }
}
