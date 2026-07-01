using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text.Json.Serialization;

namespace CxShell.Models;

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
    FTP,
    RDP,
    VNC
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

public class ProxySettings : INotifyPropertyChanged
{
    private string _nextProxyDisplay = string.Empty;

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
    public string NextProxyDisplay
    {
        get => _nextProxyDisplay;
        set
        {
            if (_nextProxyDisplay == value)
                return;

            _nextProxyDisplay = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(NextProxyDisplay)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

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

public class LoginScriptRule
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Expect { get; set; } = string.Empty;
    public string Send { get; set; } = string.Empty;
    public bool HideText { get; set; }
    public int SortOrder { get; set; }

    [JsonIgnore]
    public string SendDisplay => HideText
        ? new string('*', Send.Length)
        : Send;
}

public class HighlightRule
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public bool IsEnabled { get; set; } = true;
    public string Keyword { get; set; } = string.Empty;
    public bool IsCaseSensitive { get; set; }
    public bool IsRegex { get; set; }
    public string Description { get; set; } = string.Empty;
    public string ForegroundColor { get; set; } = "#000000";
    public string BackgroundColor { get; set; } = "#FFFF40";
    public bool UseTerminalColor { get; set; }
    public bool Bold { get; set; } = true;
    public bool Italic { get; set; }
    public bool Underline { get; set; }
    public bool Strikethrough { get; set; }
    public int SortOrder { get; set; }

    [JsonIgnore]
    public string Preview => "Highlight";
}

public class HighlightSet
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public ObservableCollection<HighlightRule> Rules { get; set; } = new();

    [JsonIgnore]
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? "Highlight Set" : Name;
}

public class ApplicationSettings
{
    public string UiLanguage { get; set; } = "zh-CN";
    public bool ShowSessionManagerOnStartup { get; set; } = true;
    public bool AutoCheckForUpdates { get; set; } = true;
    public bool IncludePrereleaseUpdates { get; set; }
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

    public string Password { get; set; } = string.Empty;
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
    public string TerminalType { get; set; } = "xterm";
    public int TerminalColumns { get; set; } = 80;
    public int TerminalRows { get; set; } = 24;
    public bool TerminalFixedSize { get; set; }
    public bool TerminalResetSizeOnConnect { get; set; }
    public int TerminalScrollbackSize { get; set; } = 1024;
    public bool TerminalPushClearedScreenToScrollback { get; set; } = true;
    public string TerminalEncoding { get; set; } = "utf-8";
    public bool TerminalTreatAmbiguousAsWide { get; set; }
    public string TerminalSendLineEnding { get; set; } = "CR";
    public string TerminalReceiveLineEnding { get; set; } = "AUTO";
    public string TerminalKeyboardFunctionKeyMode { get; set; } = "Default";
    public string TerminalKeyboardMappingFile { get; set; } = string.Empty;
    public string TerminalDeleteKeySequence { get; set; } = "VT220";
    public string TerminalBackspaceKeySequence { get; set; } = "Backspace";
    public bool TerminalLeftAltAsMeta { get; set; }
    public bool TerminalRightAltAsMeta { get; set; }
    public bool TerminalCtrlAltAsAltGr { get; set; } = true;
    public bool TerminalVtAutoWrapMode { get; set; } = true;
    public bool TerminalVtOriginMode { get; set; }
    public bool TerminalVtReverseVideoMode { get; set; }
    public bool TerminalVtNewLineMode { get; set; }
    public bool TerminalVtInsertMode { get; set; }
    public bool TerminalVtEchoMode { get; set; }
    public string TerminalVtCursorKeyMode { get; set; } = "Normal";
    public string TerminalVtNumericKeypadMode { get; set; } = "Normal";
    public bool TerminalAdvancedUseApplicationCursorMode { get; set; } = true;
    public bool TerminalAdvancedShiftLimitsApplicationCursorMode { get; set; } = true;
    public bool TerminalAdvancedClearScreenBackground { get; set; } = true;
    public bool TerminalAdvancedScrollToBottomOnInputOutput { get; set; } = true;
    public bool TerminalAdvancedSuspendScrollToBottomOnScrollLock { get; set; }
    public bool TerminalAdvancedScrollToBottomByKey { get; set; }
    public bool TerminalAdvancedDestructiveBackspace { get; set; }
    public bool TerminalAdvancedDuplicateSessionCd { get; set; } = true;
    public string TerminalAdvancedPreinputString { get; set; } = string.Empty;
    public bool TerminalAdvancedUseRxvtHomeEnd { get; set; }
    public bool TerminalAdvancedDisableBlinkingText { get; set; }
    public bool TerminalAdvancedDisableTitleChange { get; set; }
    public bool TerminalAdvancedDisableTerminalPrint { get; set; }
    public bool TerminalAdvancedDisableAlternateScreen { get; set; }
    public bool TerminalAdvancedIgnoreResizeRequest { get; set; } = true;
    public string TerminalAdvancedAnswerback { get; set; } = "CxShell";
    public bool TerminalAdvancedUseBuiltinLineDrawing { get; set; } = true;
    public bool TerminalAdvancedUseBuiltinPowerline { get; set; } = true;
    public string AppearanceColorScheme { get; set; } = "XTerm";
    public string AppearanceForegroundColor { get; set; } = "#CCCCCC";
    public string AppearanceBoldForegroundColor { get; set; } = "#33FF33";
    public string AppearanceBackgroundColor { get; set; } = "#000000";
    public string AppearanceAnsiColors { get; set; } = "#000000;#CC0000;#4E9A06;#C4A000;#3465A4;#75507B;#06989A;#D3D7CF;#555753;#EF2929;#8AE234;#FCE94F;#729FCF;#AD7FA8;#34E2E2;#EEEEEC";
    public string AppearanceFontFamily { get; set; } = "DejaVu Sans Mono";
    public string AppearanceFontStyle { get; set; } = "Normal";
    public int AppearanceFontSize { get; set; } = 14;
    public string AppearanceCjkFontFamily { get; set; } = "DejaVu Sans Mono";
    public string AppearanceCjkFontStyle { get; set; } = "Normal";
    public int AppearanceCjkFontSize { get; set; } = 14;
    public bool AppearanceUseVariablePitchFont { get; set; }
    public string AppearanceFontQuality { get; set; } = "Default";
    public string AppearanceBoldTextMode { get; set; } = "ColorAndFont";
    public string AppearanceCursorColor { get; set; } = "#00FF00";
    public string AppearanceCursorTextColor { get; set; } = "#000000";
    public bool AppearanceUseBlinkingCursor { get; set; }
    public int AppearanceCursorBlinkSpeedMilliseconds { get; set; } = 500;
    public string AppearanceCursorShape { get; set; } = "Block";
    public int AppearanceWindowPaddingTop { get; set; } = 5;
    public int AppearanceWindowPaddingBottom { get; set; } = 5;
    public int AppearanceWindowPaddingLeft { get; set; } = 5;
    public int AppearanceWindowPaddingRight { get; set; } = 5;
    public int AppearanceLineSpacing { get; set; }
    public int AppearanceCharacterSpacing { get; set; }
    public string AppearanceTabColorMode { get; set; } = "Default";
    public string AppearanceTabCustomColor { get; set; } = "#000000";
    public string AppearanceBackgroundImagePath { get; set; } = string.Empty;
    public string AppearanceBackgroundImagePosition { get; set; } = "Center";
    public string AppearanceHighlightSetId { get; set; } = "None";
    public ObservableCollection<HighlightSet> AppearanceHighlightSets { get; set; } = new();
    public string AdvancedQuickCommandSet { get; set; } = "<<所有命令>>";
    public bool AdvancedDisableQuickCommandShortcuts { get; set; }
    public int AdvancedFtpPort { get; set; } = 21;
    public int AdvancedCharacterDelayMilliseconds { get; set; }
    public bool AdvancedUseLineDelay { get; set; } = true;
    public int AdvancedLineDelayMilliseconds { get; set; }
    public bool AdvancedUsePromptDelay { get; set; }
    public string AdvancedPromptText { get; set; } = string.Empty;
    public int AdvancedPromptMaxWaitMilliseconds { get; set; }
    public bool AdvancedUseNagle { get; set; }
    public string AdvancedIpVersion { get; set; } = "Auto";
    public bool AdvancedTraceSshProtocol { get; set; }
    public bool AdvancedTraceSshTunneling { get; set; }
    public bool AdvancedTraceSshPackets { get; set; }
    public bool AdvancedTraceTelnetOptions { get; set; }
    public string AdvancedBellMode { get; set; } = "Default";
    public string AdvancedBellSoundPath { get; set; } = string.Empty;
    public bool AdvancedBellFlashInactiveWindow { get; set; }
    public int AdvancedBellIgnoreRepeatedSeconds { get; set; } = 3;
    public int AdvancedBellReactivateAfterSeconds { get; set; } = 3;
    public string AdvancedLogFilePath { get; set; } = "%n_%Y-%m-%d_%t.log";
    public bool AdvancedLogOverwriteExisting { get; set; } = true;
    public bool AdvancedLogStartOnConnect { get; set; }
    public bool AdvancedLogPromptFileOnStart { get; set; }
    public bool AdvancedLogUseRtf { get; set; }
    public bool AdvancedLogIncludeTerminalCodes { get; set; }
    public string AdvancedLogEncoding { get; set; } = "Utf16Le";
    public bool AdvancedLogWriteTimestamp { get; set; }
    public string AdvancedLogTimestampFormat { get; set; } = "[%a]";
    public bool EnableLoginScriptRules { get; set; } = true;
    public List<LoginScriptRule> LoginScriptRules { get; set; } = new();
    public bool RunLoginScriptFile { get; set; }
    public string LoginScriptFilePath { get; set; } = string.Empty;
    public string LoginScriptParameters { get; set; } = string.Empty;
    public string SshRemoteCommand { get; set; } = string.Empty;
    public string SshVersionPolicy { get; set; } = "Ssh2Only";
    public bool SshUseXagent { get; set; }
    public bool SshForwardAgent { get; set; }
    public bool SshUseCompression { get; set; }
    public bool SshNoTerminal { get; set; }
    public bool SshAcceptAndSaveHostKey { get; set; }
    public bool SshAutoOpenSftpPanel { get; set; } = true;
    public bool SshAutoOpenMonitorPanel { get; set; } = true;
    // Kept for older config files and older app versions.
    public bool SshDoNotStartFileManager { get; set; }
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
    public bool SftpFollowTerminalDirectory { get; set; } = true;
    public bool SftpUseCustomServer { get; set; }
    public string SftpCustomServerCommand { get; set; } = string.Empty;
    public bool FileTransferAlwaysAskDownloadFolder { get; set; } = true;
    public string FileTransferDownloadDirectory { get; set; } = string.Empty;
    public string FileTransferUploadDirectory { get; set; } = string.Empty;
    public string FileTransferDuplicateAction { get; set; } = "AutoRename";
    public string FileTransferUploadProtocol { get; set; } = "Zmodem";
    public int FileTransferXymodemBlockSize { get; set; } = 128;
    public string FileTransferXmodemUploadCommand { get; set; } = "rx";
    public string FileTransferYmodemUploadCommand { get; set; } = "rb -E";
    public bool FileTransferZmodemAutoActivate { get; set; } = true;
    public string FileTransferZmodemUploadCommand { get; set; } = "rz -E";
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
    public bool RdpUseSshTunnel { get; set; }
    public string RdpSshHost { get; set; } = string.Empty;
    public int RdpSshPort { get; set; } = 22;
    public string RdpSshUsername { get; set; } = string.Empty;
    public string RdpSshPassword { get; set; } = string.Empty;
    public bool RdpSshUsePrivateKey { get; set; }
    public string RdpSshPrivateKeyPath { get; set; } = string.Empty;
    public bool VncUseSshTunnel { get; set; }
    public string VncSshHost { get; set; } = string.Empty;
    public int VncSshPort { get; set; } = 22;
    public string VncSshUsername { get; set; } = string.Empty;
    public string VncSshPassword { get; set; } = string.Empty;
    public bool VncSshUsePrivateKey { get; set; }
    public string VncSshPrivateKeyPath { get; set; } = string.Empty;
    public string VncSshRemoteHost { get; set; } = "127.0.0.1";
    public int VncSshRemotePort { get; set; } = 5901;
    public int SortOrder { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
}
