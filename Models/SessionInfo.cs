using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace ChiXueSsh.Models;

public enum AuthMethod
{
    Password,
    PrivateKey
}

public enum SessionProtocol
{
    SSH,
    TELNET,
    RLOGIN,
    SFTP,
    SERIAL,
    LOCAL,
    FTP,
    RDP
}

public enum SshTunnelRuleType
{
    Local,
    Remote,
    Dynamic
}

public enum ProxyProtocol
{
    None,
    Http,
    Socks4,
    Socks4A,
    Socks5,
    SshPassthrough,
    JumpHost
}

public class ProxySettings
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ProxyProtocol Protocol { get; set; } = ProxyProtocol.None;

    public string Host { get; set; } = string.Empty;
    public int Port { get; set; }
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public bool UseSessionFile { get; set; }
    public string SessionFilePath { get; set; } = string.Empty;
    public Guid? NextProxyId { get; set; }

    [JsonIgnore]
    public bool IsEnabled => Protocol != ProxyProtocol.None && !string.IsNullOrWhiteSpace(Host) && Port is >= 1 and <= 65535;

    [JsonIgnore]
    public string DisplayName => IsEnabled
        ? string.IsNullOrWhiteSpace(Name) ? $"{TypeDisplay} {Host}:{Port}" : Name
        : "<无>";

    [JsonIgnore]
    public string TypeDisplay => Protocol switch
    {
        _ => GetTypeDisplay(Protocol)
    };

    [JsonIgnore]
    public string PortDisplay => Port > 0 ? Port.ToString() : string.Empty;

    [JsonIgnore]
    public string NextProxyDisplay { get; set; } = string.Empty;

    public static string GetTypeDisplay(ProxyProtocol protocol)
    {
        return protocol switch
        {
            ProxyProtocol.Http => "HTTP 1.1",
            ProxyProtocol.Socks4 => "SOCKS4",
            ProxyProtocol.Socks4A => "SOCKS4A",
            ProxyProtocol.Socks5 => "SOCKS5",
            ProxyProtocol.SshPassthrough => "SSH_PASSTHROUGH",
            ProxyProtocol.JumpHost => "JUMPHOST",
            _ => string.Empty
        };
    }
}

public class SshTunnelRule
{
    public Guid Id { get; set; } = Guid.NewGuid();

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public SshTunnelRuleType Type { get; set; } = SshTunnelRuleType.Local;

    public string SourceHost { get; set; } = "localhost";
    public int ListenPort { get; set; }
    public bool AcceptLocalConnectionsOnly { get; set; } = true;
    public string DestinationHost { get; set; } = "localhost";
    public int DestinationPort { get; set; }
    public string Description { get; set; } = string.Empty;

    [JsonIgnore]
    public string TypeDisplay => Type switch
    {
        SshTunnelRuleType.Remote => "远程（传入）",
        SshTunnelRuleType.Dynamic => "Dynamic (SOCKS4/5)",
        _ => "本地（拨出）"
    };

    [JsonIgnore]
    public string ListenPortDisplay => ListenPort > 0 ? ListenPort.ToString() : string.Empty;

    [JsonIgnore]
    public string TargetDisplay => Type == SshTunnelRuleType.Dynamic
        ? "SOCKS4/5"
        : $"{DestinationHost}:{DestinationPort}";
}

public class SessionInfo
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public Guid? GroupId { get; set; }
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 22;
    public string Username { get; set; } = string.Empty;
    public ProxySettings Proxy { get; set; } = new();
    public List<ProxySettings> ProxyServers { get; set; } = new();
    public Guid? SelectedProxyId { get; set; }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public SessionProtocol Protocol { get; set; } = SessionProtocol.SSH;

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public AuthMethod AuthMethod { get; set; } = AuthMethod.Password;

    public string? PrivateKeyPath { get; set; }
    public bool AutoReconnect { get; set; } = true;
    public int ReconnectIntervalSeconds { get; set; } = 30;
    public int ReconnectLimitMinutes { get; set; }
    public bool SendSessionKeepAlive { get; set; } = true;
    public int SessionKeepAliveIntervalSeconds { get; set; } = 60;
    public bool SendIdleString { get; set; }
    public int IdleStringIntervalSeconds { get; set; }
    public string IdleString { get; set; } = string.Empty;
    public bool TcpKeepAlive { get; set; }
    public string SshRemoteCommand { get; set; } = string.Empty;
    public string SshVersionPolicy { get; set; } = "Ssh2Only";
    public bool SshUseXagent { get; set; }
    public bool SshForwardAgent { get; set; }
    public bool SshUseCompression { get; set; }
    public bool SshNoTerminal { get; set; }
    public bool SshAcceptAndSaveHostKey { get; set; }
    public bool SshDoNotStartFileManager { get; set; } = true;
    public string SshCipherAlgorithms { get; set; } = string.Empty;
    public string SshMacAlgorithms { get; set; } = string.Empty;
    public string SshKeyExchangeAlgorithms { get; set; } = string.Empty;
    public List<SshTunnelRule> SshTunnelRules { get; set; } = new();
    public bool SshForwardX11 { get; set; } = true;
    public bool SshX11UseXmanager { get; set; } = true;
    public string SshX11Display { get; set; } = "localhost:0.0";
    public bool TelnetUseXDisplayLocation { get; set; } = true;
    public string TelnetXDisplayLocation { get; set; } = "$PCADDR:0.0";
    public string TelnetOptionMode { get; set; } = "Passive";
    public bool TelnetForceCharacterAtATime { get; set; }
    public string TelnetUsernamePrompt { get; set; } = "ogin:";
    public string TelnetPasswordPrompt { get; set; } = "assword:";
    public string RloginPasswordPrompt { get; set; } = "assword:";
    public int RloginTerminalSpeed { get; set; } = 38400;
    public string SftpLocalStartDirectory { get; set; } = string.Empty;
    public string SftpRemoteStartDirectory { get; set; } = string.Empty;
    public bool SftpUseCustomServer { get; set; }
    public string SftpCustomServerCommand { get; set; } = string.Empty;
    public string SerialPortName { get; set; } = "COM1";
    public int SerialBaudRate { get; set; } = 9600;
    public int SerialDataBits { get; set; } = 8;
    public string SerialParity { get; set; } = "None";
    public string SerialStopBits { get; set; } = "One";
    public string SerialFlowControl { get; set; } = "None";
    public string RdpWindowSize { get; set; } = "WorkSpace";
    public int RdpDesktopWidth { get; set; } = 1920;
    public int RdpDesktopHeight { get; set; } = 1080;
    public string RdpResizeMode { get; set; } = "SmartReconnect";
    public string RdpScreenScale { get; set; } = "Auto";
    public string RdpColorQuality { get; set; } = "32";
    public bool RdpApplyKeyCombinations { get; set; } = true;
    public bool RdpRedirectDrives { get; set; }
    public string RdpAudioMode { get; set; } = "DoNotPlay";
    public bool RdpAudioCapture { get; set; } = true;
    public int SortOrder { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
}
