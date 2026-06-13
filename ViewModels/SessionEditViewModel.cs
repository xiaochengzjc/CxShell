using System;
using System.Collections.ObjectModel;
using System.IO.Ports;
using System.Linq;
using System.Threading.Tasks;
using AtomUI.Controls;
using AtomUI.Desktop.Controls;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using ChiXueSsh.Models;
using ChiXueSsh.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ChiXueSsh.ViewModels;

public partial class SessionEditViewModel : ObservableObject
{
    [ObservableProperty] private string _dialogTitle = "New Session Properties";
    [ObservableProperty] private string _sessionName = string.Empty;
    [ObservableProperty] private string _protocol = SessionProtocol.SSH.ToString();
    [ObservableProperty] private string _host = string.Empty;
    [ObservableProperty] private string _port = "22";
    [ObservableProperty] private string _username = string.Empty;
    [ObservableProperty] private ProxyProtocol _proxyProtocol = ProxyProtocol.None;
    [ObservableProperty] private string _proxyHost = string.Empty;
    [ObservableProperty] private string _proxyPort = string.Empty;
    [ObservableProperty] private string _proxyUsername = string.Empty;
    [ObservableProperty] private string _proxyPassword = string.Empty;
    [ObservableProperty] private string _selectedProxyKey = "None";
    [ObservableProperty] private bool _isPasswordAuth = true;
    [ObservableProperty] private bool _isPrivateKeyAuth;
    [ObservableProperty] private string _privateKeyPath = string.Empty;
    [ObservableProperty] private bool _autoReconnect = true;
    [ObservableProperty] private decimal _reconnectIntervalSeconds = 30;
    [ObservableProperty] private decimal _reconnectLimitMinutes;
    [ObservableProperty] private bool _sendSessionKeepAlive = true;
    [ObservableProperty] private decimal _sessionKeepAliveIntervalSeconds = 60;
    [ObservableProperty] private bool _sendIdleString;
    [ObservableProperty] private decimal _idleStringIntervalSeconds;
    [ObservableProperty] private string _idleString = string.Empty;
    [ObservableProperty] private bool _tcpKeepAlive;
    [ObservableProperty] private string _sshRemoteCommand = string.Empty;
    [ObservableProperty] private string _sshVersionPolicy = "Ssh2Only";
    [ObservableProperty] private bool _sshUseXagent;
    [ObservableProperty] private bool _sshForwardAgent;
    [ObservableProperty] private bool _sshUseCompression;
    [ObservableProperty] private bool _sshNoTerminal;
    [ObservableProperty] private bool _sshAcceptAndSaveHostKey;
    [ObservableProperty] private bool _sshDoNotStartFileManager = true;
    [ObservableProperty] private string _sshCipherAlgorithms = string.Empty;
    [ObservableProperty] private string _sshMacAlgorithms = string.Empty;
    [ObservableProperty] private string _sshKeyExchangeAlgorithms = string.Empty;
    [ObservableProperty] private SshTunnelRule? _selectedSshTunnelRule;
    [ObservableProperty] private bool _sshForwardX11 = true;
    [ObservableProperty] private bool _sshX11UseXmanager = true;
    [ObservableProperty] private string _sshX11Display = "localhost:0.0";
    [ObservableProperty] private string? _validationMessage;
    [ObservableProperty] private InputControlStatus _nameStatus = InputControlStatus.Default;
    [ObservableProperty] private InputControlStatus _hostStatus = InputControlStatus.Default;
    [ObservableProperty] private InputControlStatus _portStatus = InputControlStatus.Default;
    [ObservableProperty] private bool _telnetUseXDisplayLocation = true;
    [ObservableProperty] private string _telnetXDisplayLocation = "$PCADDR:0.0";
    [ObservableProperty] private string _telnetOptionMode = "Passive";
    [ObservableProperty] private bool _telnetForceCharacterAtATime;
    [ObservableProperty] private string _telnetUsernamePrompt = "ogin:";
    [ObservableProperty] private string _telnetPasswordPrompt = "assword:";
    [ObservableProperty] private string _rloginPasswordPrompt = "assword:";
    [ObservableProperty] private string _rloginTerminalSpeed = "38400";
    [ObservableProperty] private string _sftpLocalStartDirectory = string.Empty;
    [ObservableProperty] private string _sftpRemoteStartDirectory = string.Empty;
    [ObservableProperty] private bool _sftpUseCustomServer;
    [ObservableProperty] private string _sftpCustomServerCommand = string.Empty;
    [ObservableProperty] private InputControlStatus _serialPortStatus = InputControlStatus.Default;
    [ObservableProperty] private string _serialPortName = "COM1";
    [ObservableProperty] private string _serialBaudRate = "115200";
    [ObservableProperty] private string _serialDataBits = "8";
    [ObservableProperty] private string _serialStopBits = "One";
    [ObservableProperty] private string _serialParity = "None";
    [ObservableProperty] private string _serialFlowControl = "None";
    [ObservableProperty] private string _rdpWindowSize = "WorkSpace";
    [ObservableProperty] private string _rdpDesktopWidth = "1920";
    [ObservableProperty] private string _rdpDesktopHeight = "1080";
    [ObservableProperty] private string _rdpResizeMode = "SmartReconnect";
    [ObservableProperty] private string _rdpScreenScale = "Auto";
    [ObservableProperty] private string _rdpColorQuality = "32";
    [ObservableProperty] private bool _rdpApplyKeyCombinations = true;
    [ObservableProperty] private bool _rdpRedirectDrives;
    [ObservableProperty] private string _rdpAudioMode = "DoNotPlay";
    [ObservableProperty] private bool _rdpAudioCapture = true;

    public bool HasValidationError => !string.IsNullOrWhiteSpace(ValidationMessage);
    public bool IsNameInvalid => NameStatus == InputControlStatus.Error;
    public bool IsHostInvalid => HostStatus == InputControlStatus.Error;
    public bool IsPortInvalid => PortStatus == InputControlStatus.Error;
    public bool IsSerialPortInvalid => SerialPortStatus == InputControlStatus.Error;
    public bool HasSelectedSshTunnelRule => SelectedSshTunnelRule != null;
    public bool IsSftpCustomServerCommandEnabled => SftpUseCustomServer;
    public bool IsSessionKeepAliveIntervalEnabled => SendSessionKeepAlive;
    public bool IsIdleStringSettingsEnabled => SendIdleString;

    public ObservableCollection<ISelectOption> ProtocolOptions { get; } =
    [
        new SelectOption { Header = "SSH", Content = SessionProtocol.SSH.ToString() },
        new SelectOption { Header = "TELNET", Content = SessionProtocol.TELNET.ToString() },
        new SelectOption { Header = "RLOGIN", Content = SessionProtocol.RLOGIN.ToString() },
        new SelectOption { Header = "SFTP", Content = SessionProtocol.SFTP.ToString() },
        new SelectOption { Header = "SERIAL", Content = SessionProtocol.SERIAL.ToString() },
        new SelectOption { Header = "LOCAL", Content = SessionProtocol.LOCAL.ToString() },
        new SelectOption { Header = "FTP", Content = SessionProtocol.FTP.ToString() },
        new SelectOption { Header = "RDP", Content = SessionProtocol.RDP.ToString() }
    ];

    public ObservableCollection<ISelectOption> ProxyOptions { get; } =
    [
        new SelectOption { Header = "<无>", Content = "None" }
    ];

    public ObservableCollection<ISelectOption> ProxyProtocolOptions { get; } =
    [
        new SelectOption { Header = "SOCKS4", Content = ProxyProtocol.Socks4.ToString() },
        new SelectOption { Header = "SOCKS4A", Content = ProxyProtocol.Socks4A.ToString() },
        new SelectOption { Header = "SOCKS5", Content = ProxyProtocol.Socks5.ToString() },
        new SelectOption { Header = "HTTP 1.1", Content = ProxyProtocol.Http.ToString() },
        new SelectOption { Header = "SSH_PASSTHROUGH", Content = ProxyProtocol.SshPassthrough.ToString() },
        new SelectOption { Header = "JUMPHOST", Content = ProxyProtocol.JumpHost.ToString() }
    ];
    public ObservableCollection<ProxySettings> ProxyServers { get; } = new();

    public ObservableCollection<ISelectOption> SerialPortOptions { get; } = new();
    public ObservableCollection<ISelectOption> SshCipherOptions { get; } = CreateAlgorithmOptions("<Cipher List>", SshAlgorithmPreferenceService.DefaultCipherAlgorithms);
    public ObservableCollection<ISelectOption> SshMacOptions { get; } = CreateAlgorithmOptions("<MAC List>", SshAlgorithmPreferenceService.DefaultMacAlgorithms);
    public ObservableCollection<ISelectOption> SshKeyExchangeOptions { get; } = CreateAlgorithmOptions("<Key Exchange List>", SshAlgorithmPreferenceService.DefaultKeyExchangeAlgorithms);
    public ObservableCollection<SshTunnelRule> SshTunnelRules { get; } = new();
    public ObservableCollection<ISelectOption> TerminalSpeedOptions { get; } =
    [
        new SelectOption { Header = "110", Content = "110" },
        new SelectOption { Header = "300", Content = "300" },
        new SelectOption { Header = "600", Content = "600" },
        new SelectOption { Header = "1200", Content = "1200" },
        new SelectOption { Header = "2400", Content = "2400" },
        new SelectOption { Header = "4800", Content = "4800" },
        new SelectOption { Header = "9600", Content = "9600" },
        new SelectOption { Header = "14400", Content = "14400" },
        new SelectOption { Header = "19200", Content = "19200" },
        new SelectOption { Header = "38400", Content = "38400" },
        new SelectOption { Header = "56000", Content = "56000" },
        new SelectOption { Header = "57600", Content = "57600" },
        new SelectOption { Header = "115200", Content = "115200" },
        new SelectOption { Header = "128000", Content = "128000" },
        new SelectOption { Header = "256000", Content = "256000" }
    ];
    public ObservableCollection<ISelectOption> SshVersionOptions { get; } =
    [
        new SelectOption { Header = "SSH2, SSH1 (如果服务器同时支持，则选择SSH2)", Content = "Ssh2ThenSsh1" },
        new SelectOption { Header = "SSH1, SSH2 (如果服务器同时支持，则选择SSH1)", Content = "Ssh1ThenSsh2" },
        new SelectOption { Header = "仅SSH2(不使用SSH1)", Content = "Ssh2Only" },
        new SelectOption { Header = "仅SSH1(不使用SSH2)", Content = "Ssh1Only" }
    ];

    public ObservableCollection<ISelectOption> SerialBaudRateOptions { get; } =
    [
        new SelectOption { Header = "1200", Content = "1200" },
        new SelectOption { Header = "2400", Content = "2400" },
        new SelectOption { Header = "4800", Content = "4800" },
        new SelectOption { Header = "9600", Content = "9600" },
        new SelectOption { Header = "19200", Content = "19200" },
        new SelectOption { Header = "38400", Content = "38400" },
        new SelectOption { Header = "57600", Content = "57600" },
        new SelectOption { Header = "115200", Content = "115200" }
    ];
    public ObservableCollection<ISelectOption> SerialDataBitsOptions { get; } =
    [
        new SelectOption { Header = "5", Content = "5" },
        new SelectOption { Header = "6", Content = "6" },
        new SelectOption { Header = "7", Content = "7" },
        new SelectOption { Header = "8", Content = "8" }
    ];
    public ObservableCollection<ISelectOption> SerialStopBitsOptions { get; } =
    [
        new SelectOption { Header = "1", Content = "One" },
        new SelectOption { Header = "1.5", Content = "OnePointFive" },
        new SelectOption { Header = "2", Content = "Two" }
    ];
    public ObservableCollection<ISelectOption> SerialParityOptions { get; } =
    [
        new SelectOption { Header = "None", Content = "None" },
        new SelectOption { Header = "Odd", Content = "Odd" },
        new SelectOption { Header = "Even", Content = "Even" },
        new SelectOption { Header = "Mark", Content = "Mark" },
        new SelectOption { Header = "Space", Content = "Space" }
    ];
    public ObservableCollection<ISelectOption> SerialFlowControlOptions { get; } =
    [
        new SelectOption { Header = "None", Content = "None" },
        new SelectOption { Header = "RTS/CTS", Content = "RTS/CTS" },
        new SelectOption { Header = "XON/XOFF", Content = "XON/XOFF" },
        new SelectOption { Header = "Both", Content = "Both" }
    ];
    public ObservableCollection<ISelectOption> RdpWindowSizeOptions { get; } =
    [
        new SelectOption { Header = "Full Screen", Content = "FullScreen" },
        new SelectOption { Header = "Work Space", Content = "WorkSpace" },
        new SelectOption { Header = "Custom", Content = "Custom" },
        new SelectOption { Header = "800x600", Content = "800x600" },
        new SelectOption { Header = "1024x768", Content = "1024x768" },
        new SelectOption { Header = "1280x720", Content = "1280x720" },
        new SelectOption { Header = "1280x1024", Content = "1280x1024" },
        new SelectOption { Header = "1366x768", Content = "1366x768" },
        new SelectOption { Header = "1600x900", Content = "1600x900" },
        new SelectOption { Header = "1600x1200", Content = "1600x1200" },
        new SelectOption { Header = "1680x1050", Content = "1680x1050" },
        new SelectOption { Header = "1920x1080", Content = "1920x1080" },
        new SelectOption { Header = "1920x1200", Content = "1920x1200" },
        new SelectOption { Header = "2560x1440", Content = "2560x1440" }
    ];
    public ObservableCollection<ISelectOption> RdpResizeModeOptions { get; } =
    [
        new SelectOption { Header = "Not Used", Content = "NotUsed" },
        new SelectOption { Header = "Smart sizing", Content = "SmartSizing" },
        new SelectOption { Header = "Smart reconnect", Content = "SmartReconnect" },
        new SelectOption { Header = "Legacy reconnect", Content = "LegacyReconnect" }
    ];
    public ObservableCollection<ISelectOption> RdpScreenScaleOptions { get; } =
    [
        new SelectOption { Header = "Auto", Content = "Auto" },
        new SelectOption { Header = "100%", Content = "100" },
        new SelectOption { Header = "125%", Content = "125" },
        new SelectOption { Header = "150%", Content = "150" },
        new SelectOption { Header = "175%", Content = "175" },
        new SelectOption { Header = "200%", Content = "200" },
        new SelectOption { Header = "225%", Content = "225" },
        new SelectOption { Header = "250%", Content = "250" },
        new SelectOption { Header = "275%", Content = "275" },
        new SelectOption { Header = "300%", Content = "300" },
        new SelectOption { Header = "325%", Content = "325" },
        new SelectOption { Header = "350%", Content = "350" },
        new SelectOption { Header = "375%", Content = "375" },
        new SelectOption { Header = "400%", Content = "400" },
        new SelectOption { Header = "425%", Content = "425" },
        new SelectOption { Header = "450%", Content = "450" },
        new SelectOption { Header = "475%", Content = "475" },
        new SelectOption { Header = "500%", Content = "500" }
    ];
    public ObservableCollection<ISelectOption> RdpColorQualityOptions { get; } =
    [
        new SelectOption { Header = "High Color (15 bit)", Content = "15" },
        new SelectOption { Header = "High Color (16 bit)", Content = "16" },
        new SelectOption { Header = "True Color (24 bit)", Content = "24" },
        new SelectOption { Header = "Highest Quality (32 bit)", Content = "32" }
    ];
    public ObservableCollection<ISelectOption> RdpAudioModeOptions { get; } =
    [
        new SelectOption { Header = "Play on this computer", Content = "PlayLocal" },
        new SelectOption { Header = "Do not play", Content = "DoNotPlay" },
        new SelectOption { Header = "Play on remote computer", Content = "PlayRemote" }
    ];

    public SessionInfo? SavedSession { get; private set; }

    private readonly SessionInfo? _editingSession;

    public SessionEditViewModel()
    {
        RefreshSerialPortOptions();
    }

    public SessionEditViewModel(SessionInfo session)
    {
        _editingSession = session;
        DialogTitle = "Session Properties";
        SessionName = session.Name;
        Protocol = session.Protocol.ToString();
        Host = session.Host;
        Port = session.Port.ToString();
        Username = session.Username;
        foreach (var proxy in session.ProxyServers)
            ProxyServers.Add(CloneProxy(proxy));
        ApplyProxySettings(session.Proxy);
        if (session.Proxy.IsEnabled && ProxyServers.All(proxy => proxy.Id != session.Proxy.Id))
            ProxyServers.Add(CloneProxy(session.Proxy));
        if (session.SelectedProxyId.HasValue && ProxyServers.Any(proxy => proxy.Id == session.SelectedProxyId.Value))
            SelectProxy(session.SelectedProxyId.Value);
        IsPasswordAuth = session.AuthMethod == AuthMethod.Password;
        IsPrivateKeyAuth = session.AuthMethod == AuthMethod.PrivateKey;
        PrivateKeyPath = session.PrivateKeyPath ?? string.Empty;
        AutoReconnect = session.AutoReconnect;
        ReconnectIntervalSeconds = Math.Max(1, session.ReconnectIntervalSeconds);
        ReconnectLimitMinutes = Math.Max(0, session.ReconnectLimitMinutes);
        SendSessionKeepAlive = session.SendSessionKeepAlive;
        SessionKeepAliveIntervalSeconds = Math.Max(1, session.SessionKeepAliveIntervalSeconds);
        SendIdleString = session.SendIdleString;
        IdleStringIntervalSeconds = Math.Max(0, session.IdleStringIntervalSeconds);
        IdleString = session.IdleString ?? string.Empty;
        TcpKeepAlive = session.TcpKeepAlive;
        SshRemoteCommand = session.SshRemoteCommand ?? string.Empty;
        SshVersionPolicy = string.IsNullOrWhiteSpace(session.SshVersionPolicy) ? "Ssh2Only" : session.SshVersionPolicy;
        SshUseXagent = session.SshUseXagent;
        SshForwardAgent = session.SshForwardAgent;
        SshUseCompression = session.SshUseCompression;
        SshNoTerminal = session.SshNoTerminal;
        SshAcceptAndSaveHostKey = session.SshAcceptAndSaveHostKey;
        SshDoNotStartFileManager = session.SshDoNotStartFileManager;
        SshCipherAlgorithms = session.SshCipherAlgorithms ?? string.Empty;
        SshMacAlgorithms = session.SshMacAlgorithms ?? string.Empty;
        SshKeyExchangeAlgorithms = session.SshKeyExchangeAlgorithms ?? string.Empty;
        foreach (var rule in session.SshTunnelRules)
            SshTunnelRules.Add(CloneTunnelRule(rule));
        SshForwardX11 = session.SshForwardX11;
        SshX11UseXmanager = session.SshX11UseXmanager;
        SshX11Display = string.IsNullOrWhiteSpace(session.SshX11Display) ? "localhost:0.0" : session.SshX11Display;
        TelnetUseXDisplayLocation = session.TelnetUseXDisplayLocation;
        TelnetXDisplayLocation = string.IsNullOrWhiteSpace(session.TelnetXDisplayLocation) ? "$PCADDR:0.0" : session.TelnetXDisplayLocation;
        TelnetOptionMode = string.IsNullOrWhiteSpace(session.TelnetOptionMode) ? "Passive" : session.TelnetOptionMode;
        TelnetForceCharacterAtATime = session.TelnetForceCharacterAtATime;
        TelnetUsernamePrompt = string.IsNullOrWhiteSpace(session.TelnetUsernamePrompt) ? "ogin:" : session.TelnetUsernamePrompt;
        TelnetPasswordPrompt = string.IsNullOrWhiteSpace(session.TelnetPasswordPrompt) ? "assword:" : session.TelnetPasswordPrompt;
        RloginPasswordPrompt = string.IsNullOrWhiteSpace(session.RloginPasswordPrompt) ? "assword:" : session.RloginPasswordPrompt;
        RloginTerminalSpeed = Math.Max(1, session.RloginTerminalSpeed).ToString();
        SftpLocalStartDirectory = session.SftpLocalStartDirectory ?? string.Empty;
        SftpRemoteStartDirectory = session.SftpRemoteStartDirectory ?? string.Empty;
        SftpUseCustomServer = session.SftpUseCustomServer;
        SftpCustomServerCommand = session.SftpCustomServerCommand ?? string.Empty;
        SerialPortName = string.IsNullOrWhiteSpace(session.SerialPortName) ? "COM1" : session.SerialPortName;
        SerialBaudRate = Math.Max(1, session.SerialBaudRate).ToString();
        SerialDataBits = Math.Clamp(session.SerialDataBits, 5, 8).ToString();
        SerialStopBits = string.IsNullOrWhiteSpace(session.SerialStopBits) ? "One" : session.SerialStopBits;
        SerialParity = string.IsNullOrWhiteSpace(session.SerialParity) ? "None" : session.SerialParity;
        SerialFlowControl = string.IsNullOrWhiteSpace(session.SerialFlowControl) ? "None" : session.SerialFlowControl;
        RdpWindowSize = string.IsNullOrWhiteSpace(session.RdpWindowSize) ? "WorkSpace" : session.RdpWindowSize;
        RdpDesktopWidth = Math.Max(1, session.RdpDesktopWidth).ToString();
        RdpDesktopHeight = Math.Max(1, session.RdpDesktopHeight).ToString();
        RdpResizeMode = string.IsNullOrWhiteSpace(session.RdpResizeMode) ? "SmartReconnect" : session.RdpResizeMode;
        RdpScreenScale = string.IsNullOrWhiteSpace(session.RdpScreenScale) ? "Auto" : session.RdpScreenScale;
        RdpColorQuality = string.IsNullOrWhiteSpace(session.RdpColorQuality) ? "32" : session.RdpColorQuality;
        RdpApplyKeyCombinations = session.RdpApplyKeyCombinations;
        RdpRedirectDrives = session.RdpRedirectDrives;
        RdpAudioMode = string.IsNullOrWhiteSpace(session.RdpAudioMode) ? "DoNotPlay" : session.RdpAudioMode;
        RdpAudioCapture = session.RdpAudioCapture;
        RefreshSerialPortOptions();
    }

    [RelayCommand]
    private async Task BrowseKey()
    {
        var lifetime = Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime;
        var topLevel = TopLevel.GetTopLevel(lifetime?.MainWindow);
        if (topLevel == null) return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select private key file",
            AllowMultiple = false
        });

        if (files.Count > 0)
        {
            PrivateKeyPath = files[0].Path.LocalPath;
        }
    }

    [RelayCommand]
    private void Save()
    {
        var protocol = ParseProtocol(Protocol);
        if (!Validate(protocol, out var port))
        {
            SavedSession = null;
            return;
        }

        var session = _editingSession ?? new SessionInfo();
        session.Name = SessionName.Trim();
        session.Protocol = protocol;
        session.Host = Host.Trim();
        session.Port = port;
        session.Username = Username.Trim();
        session.Proxy = CreateProxySettings();
        session.ProxyServers = ProxyServers.Select(CloneProxy).ToList();
        session.SelectedProxyId = session.Proxy.IsEnabled ? session.Proxy.Id : null;
        session.AuthMethod = IsPrivateKeyAuth ? AuthMethod.PrivateKey : AuthMethod.Password;
        session.PrivateKeyPath = IsPrivateKeyAuth ? PrivateKeyPath : null;
        session.AutoReconnect = AutoReconnect;
        session.ReconnectIntervalSeconds = Math.Max(1, (int)ReconnectIntervalSeconds);
        session.ReconnectLimitMinutes = Math.Max(0, (int)ReconnectLimitMinutes);
        session.SendSessionKeepAlive = SendSessionKeepAlive;
        session.SessionKeepAliveIntervalSeconds = Math.Max(1, (int)SessionKeepAliveIntervalSeconds);
        session.SendIdleString = SendIdleString;
        session.IdleStringIntervalSeconds = Math.Max(0, (int)IdleStringIntervalSeconds);
        session.IdleString = IdleString;
        session.TcpKeepAlive = TcpKeepAlive;
        session.SshRemoteCommand = SshRemoteCommand.Trim();
        session.SshVersionPolicy = string.IsNullOrWhiteSpace(SshVersionPolicy) ? "Ssh2Only" : SshVersionPolicy;
        session.SshUseXagent = SshUseXagent;
        session.SshForwardAgent = SshForwardAgent;
        session.SshUseCompression = SshUseCompression;
        session.SshNoTerminal = SshNoTerminal;
        session.SshAcceptAndSaveHostKey = SshAcceptAndSaveHostKey;
        session.SshDoNotStartFileManager = SshDoNotStartFileManager;
        session.SshCipherAlgorithms = NormalizeAlgorithmList(SshCipherAlgorithms);
        session.SshMacAlgorithms = NormalizeAlgorithmList(SshMacAlgorithms);
        session.SshKeyExchangeAlgorithms = NormalizeAlgorithmList(SshKeyExchangeAlgorithms);
        session.SshTunnelRules = SshTunnelRules.Select(CloneTunnelRule).ToList();
        session.SshForwardX11 = SshForwardX11;
        session.SshX11UseXmanager = SshX11UseXmanager;
        session.SshX11Display = SshX11Display.Trim();
        session.TelnetUseXDisplayLocation = TelnetUseXDisplayLocation;
        session.TelnetXDisplayLocation = TelnetXDisplayLocation.Trim();
        session.TelnetOptionMode = TelnetOptionMode;
        session.TelnetForceCharacterAtATime = TelnetForceCharacterAtATime;
        session.TelnetUsernamePrompt = TelnetUsernamePrompt.Trim();
        session.TelnetPasswordPrompt = TelnetPasswordPrompt.Trim();
        session.RloginPasswordPrompt = RloginPasswordPrompt.Trim();
        session.RloginTerminalSpeed = int.TryParse(RloginTerminalSpeed, out var rloginTerminalSpeed) && rloginTerminalSpeed > 0
            ? rloginTerminalSpeed
            : 38400;
        session.SftpLocalStartDirectory = SftpLocalStartDirectory.Trim();
        session.SftpRemoteStartDirectory = SftpRemoteStartDirectory.Trim();
        session.SftpUseCustomServer = SftpUseCustomServer;
        session.SftpCustomServerCommand = SftpCustomServerCommand.Trim();
        session.SerialPortName = SerialPortName.Trim();
        session.SerialBaudRate = int.TryParse(SerialBaudRate, out var serialBaudRate) ? serialBaudRate : 115200;
        session.SerialDataBits = int.TryParse(SerialDataBits, out var serialDataBits) ? serialDataBits : 8;
        session.SerialStopBits = SerialStopBits;
        session.SerialParity = SerialParity;
        session.SerialFlowControl = SerialFlowControl;
        session.RdpWindowSize = RdpWindowSize;
        session.RdpDesktopWidth = int.TryParse(RdpDesktopWidth, out var rdpWidth) ? Math.Max(1, rdpWidth) : 1920;
        session.RdpDesktopHeight = int.TryParse(RdpDesktopHeight, out var rdpHeight) ? Math.Max(1, rdpHeight) : 1080;
        session.RdpResizeMode = RdpResizeMode;
        session.RdpScreenScale = RdpScreenScale;
        session.RdpColorQuality = RdpColorQuality;
        session.RdpApplyKeyCombinations = RdpApplyKeyCombinations;
        session.RdpRedirectDrives = RdpRedirectDrives;
        session.RdpAudioMode = RdpAudioMode;
        session.RdpAudioCapture = RdpAudioCapture;

        SavedSession = session;
    }

    partial void OnSessionNameChanged(string value)
    {
        if (!string.IsNullOrWhiteSpace(value) && IsNameInvalid)
            NameStatus = InputControlStatus.Default;
        ClearValidationMessageIfResolved();
    }

    partial void OnHostChanged(string value)
    {
        if (!string.IsNullOrWhiteSpace(value) && IsHostInvalid)
            HostStatus = InputControlStatus.Default;
        ClearValidationMessageIfResolved();
    }

    partial void OnPortChanged(string value)
    {
        if (IsValidPortText(value) && IsPortInvalid)
            PortStatus = InputControlStatus.Default;
        ClearValidationMessageIfResolved();
    }

    partial void OnSerialPortNameChanged(string value)
    {
        if (!string.IsNullOrWhiteSpace(value) && IsSerialPortInvalid)
            SerialPortStatus = InputControlStatus.Default;
        ClearValidationMessageIfResolved();
    }

    partial void OnProxyProtocolChanged(ProxyProtocol value)
    {
        RefreshProxyOptions();
    }

    partial void OnProxyHostChanged(string value)
    {
        RefreshProxyOptions();
    }

    partial void OnProxyPortChanged(string value)
    {
        RefreshProxyOptions();
    }

    partial void OnSelectedSshTunnelRuleChanged(SshTunnelRule? value)
    {
        OnPropertyChanged(nameof(HasSelectedSshTunnelRule));
    }

    partial void OnSftpUseCustomServerChanged(bool value)
    {
        OnPropertyChanged(nameof(IsSftpCustomServerCommandEnabled));
    }

    partial void OnSendSessionKeepAliveChanged(bool value)
    {
        OnPropertyChanged(nameof(IsSessionKeepAliveIntervalEnabled));
    }

    partial void OnSendIdleStringChanged(bool value)
    {
        OnPropertyChanged(nameof(IsIdleStringSettingsEnabled));
    }

    partial void OnValidationMessageChanged(string? value)
    {
        OnPropertyChanged(nameof(HasValidationError));
    }

    partial void OnNameStatusChanged(InputControlStatus value)
    {
        OnPropertyChanged(nameof(IsNameInvalid));
    }

    partial void OnHostStatusChanged(InputControlStatus value)
    {
        OnPropertyChanged(nameof(IsHostInvalid));
    }

    partial void OnPortStatusChanged(InputControlStatus value)
    {
        OnPropertyChanged(nameof(IsPortInvalid));
    }

    partial void OnSerialPortStatusChanged(InputControlStatus value)
    {
        OnPropertyChanged(nameof(IsSerialPortInvalid));
    }

    [RelayCommand]
    private void Cancel()
    {
        SavedSession = null;
    }

    private static SessionProtocol ParseProtocol(string value)
    {
        return Enum.TryParse<SessionProtocol>(value, ignoreCase: true, out var protocol)
            ? protocol
            : SessionProtocol.SSH;
    }

    private static bool RequiresHost(SessionProtocol protocol)
    {
        return protocol is not SessionProtocol.SERIAL and not SessionProtocol.LOCAL;
    }

    private static bool RequiresPort(SessionProtocol protocol)
    {
        return protocol is not SessionProtocol.SERIAL and not SessionProtocol.LOCAL;
    }

    private static bool RequiresUsername(SessionProtocol protocol)
    {
        return protocol is SessionProtocol.SSH or SessionProtocol.SFTP or SessionProtocol.TELNET or SessionProtocol.RLOGIN;
    }

    public static int GetDefaultPort(SessionProtocol protocol)
    {
        return protocol switch
        {
            SessionProtocol.TELNET => 23,
            SessionProtocol.RLOGIN => 513,
            SessionProtocol.FTP => 21,
            SessionProtocol.RDP => 3389,
            SessionProtocol.SERIAL or SessionProtocol.LOCAL => 0,
            _ => 22
        };
    }

    private bool Validate(SessionProtocol protocol, out int port)
    {
        port = 0;
        ResetValidation();

        if (string.IsNullOrWhiteSpace(SessionName))
        {
            NameStatus = InputControlStatus.Error;
            ValidationMessage = "Name is required.";
            return false;
        }

        if (RequiresHost(protocol) && string.IsNullOrWhiteSpace(Host))
        {
            HostStatus = InputControlStatus.Error;
            ValidationMessage = "Host is required.";
            return false;
        }

        if (RequiresPort(protocol))
        {
            if (string.IsNullOrWhiteSpace(Port))
            {
                PortStatus = InputControlStatus.Error;
                ValidationMessage = "Port is required.";
                return false;
            }

            if (!int.TryParse(Port.Trim(), out port) || port < 1 || port > 65535)
            {
                PortStatus = InputControlStatus.Error;
                ValidationMessage = "Port must be an integer between 1 and 65535.";
                return false;
            }
        }

        if (protocol == SessionProtocol.SERIAL && string.IsNullOrWhiteSpace(SerialPortName))
        {
            SerialPortStatus = InputControlStatus.Error;
            ValidationMessage = "Serial port name is required.";
            return false;
        }

        return true;
    }

    private void ResetValidation()
    {
        ValidationMessage = null;
        NameStatus = InputControlStatus.Default;
        HostStatus = InputControlStatus.Default;
        PortStatus = InputControlStatus.Default;
        SerialPortStatus = InputControlStatus.Default;
    }

    private void ClearValidationMessageIfResolved()
    {
        if (NameStatus != InputControlStatus.Error &&
            HostStatus != InputControlStatus.Error &&
            PortStatus != InputControlStatus.Error &&
            SerialPortStatus != InputControlStatus.Error)
        {
            ValidationMessage = null;
        }
    }

    private static bool IsValidPortText(string value)
    {
        return int.TryParse(value.Trim(), out var port) && port is >= 1 and <= 65535;
    }

    private void RefreshSerialPortOptions()
    {
        SerialPortOptions.Clear();
        var portNames = SerialPort.GetPortNames();
        if (portNames.Length == 0)
            portNames = ["COM1", "COM2", "COM3", "COM4"];

        foreach (var portName in portNames.Order(StringComparer.OrdinalIgnoreCase))
            SerialPortOptions.Add(new SelectOption { Header = portName, Content = portName });

        if (!SerialPortOptions.Any(option =>
                string.Equals(option.Content?.ToString(), SerialPortName, StringComparison.OrdinalIgnoreCase)))
        {
            SerialPortOptions.Add(new SelectOption { Header = SerialPortName, Content = SerialPortName });
        }
    }

    private void ApplyProxySettings(ProxySettings? proxy)
    {
        proxy ??= new ProxySettings();
        SelectedProxyKey = proxy.IsEnabled ? proxy.Id.ToString() : "None";
        ProxyProtocol = proxy.Protocol;
        ProxyHost = proxy.Host ?? string.Empty;
        ProxyPort = proxy.Port > 0 ? proxy.Port.ToString() : string.Empty;
        ProxyUsername = proxy.Username ?? string.Empty;
        ProxyPassword = proxy.Password ?? string.Empty;
        RefreshProxyOptions();
    }

    public ProxySettings CreateProxySettings()
    {
        var port = int.TryParse(ProxyPort.Trim(), out var parsedPort) ? parsedPort : 0;
        var existing = Guid.TryParse(SelectedProxyKey, out var selectedId)
            ? ProxyServers.FirstOrDefault(proxy => proxy.Id == selectedId)
            : null;

        return new ProxySettings
        {
            Id = existing?.Id ?? Guid.NewGuid(),
            Name = existing?.Name ?? string.Empty,
            Protocol = ProxyProtocol,
            Host = ProxyHost.Trim(),
            Port = port,
            Username = ProxyUsername.Trim(),
            Password = ProxyPassword,
            UseSessionFile = existing?.UseSessionFile ?? false,
            SessionFilePath = existing?.SessionFilePath ?? string.Empty,
            NextProxyId = existing?.NextProxyId
        };
    }

    public void UpdateProxySettings(ProxySettings proxy)
    {
        if (proxy.IsEnabled)
        {
            var existing = ProxyServers.FirstOrDefault(item => item.Id == proxy.Id);
            if (existing == null)
                ProxyServers.Add(CloneProxy(proxy));
            else
                CopyProxy(proxy, existing);
        }

        ApplyProxySettings(proxy);
    }

    private void RefreshProxyOptions()
    {
        ProxyOptions.Clear();
        ProxyOptions.Add(new SelectOption { Header = "<无>", Content = "None" });
        foreach (var proxy in ProxyServers.Where(proxy => proxy.IsEnabled))
            ProxyOptions.Add(new SelectOption { Header = proxy.DisplayName, Content = proxy.Id.ToString() });
    }

    public void SelectProxy(Guid proxyId)
    {
        var proxy = ProxyServers.FirstOrDefault(item => item.Id == proxyId);
        if (proxy != null)
            ApplyProxySettings(proxy);
    }

    public void ClearProxy()
    {
        ApplyProxySettings(new ProxySettings());
    }

    public void ReplaceProxyServers(IEnumerable<ProxySettings> proxies, Guid? selectedProxyId)
    {
        ProxyServers.Clear();
        foreach (var proxy in proxies.Select(CloneProxy))
            ProxyServers.Add(proxy);

        if (selectedProxyId.HasValue && ProxyServers.Any(proxy => proxy.Id == selectedProxyId.Value))
        {
            SelectProxy(selectedProxyId.Value);
            return;
        }

        ClearProxy();
    }

    public static ProxySettings CloneProxy(ProxySettings source)
    {
        return new ProxySettings
        {
            Id = source.Id,
            Name = source.Name,
            Protocol = source.Protocol,
            Host = source.Host,
            Port = source.Port,
            Username = source.Username,
            Password = source.Password,
            UseSessionFile = source.UseSessionFile,
            SessionFilePath = source.SessionFilePath,
            NextProxyId = source.NextProxyId
        };
    }

    private static void CopyProxy(ProxySettings source, ProxySettings target)
    {
        target.Name = source.Name;
        target.Protocol = source.Protocol;
        target.Host = source.Host;
        target.Port = source.Port;
        target.Username = source.Username;
        target.Password = source.Password;
        target.UseSessionFile = source.UseSessionFile;
        target.SessionFilePath = source.SessionFilePath;
        target.NextProxyId = source.NextProxyId;
    }

    private static ObservableCollection<ISelectOption> CreateAlgorithmOptions(string listHeader, IReadOnlyList<string> algorithms)
    {
        var options = new ObservableCollection<ISelectOption>
        {
            new SelectOption { Header = listHeader, Content = string.Empty }
        };

        foreach (var algorithm in algorithms)
            options.Add(new SelectOption { Header = algorithm, Content = algorithm });

        return options;
    }

    private static string NormalizeAlgorithmList(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        return string.Join(';', value
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.Ordinal));
    }

    public static SshTunnelRule CloneTunnelRule(SshTunnelRule source)
    {
        return new SshTunnelRule
        {
            Id = source.Id,
            Type = source.Type,
            SourceHost = source.SourceHost,
            ListenPort = source.ListenPort,
            AcceptLocalConnectionsOnly = source.AcceptLocalConnectionsOnly,
            DestinationHost = source.DestinationHost,
            DestinationPort = source.DestinationPort,
            Description = source.Description
        };
    }
}
