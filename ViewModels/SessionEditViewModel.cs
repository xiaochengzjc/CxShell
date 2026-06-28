using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using AtomUI.Controls;
using AtomUI.Controls.Primitives;
using AtomUI.Desktop.Controls;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using CxShell.Models;
using CxShell.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CxShell.ViewModels;

public partial class SessionEditViewModel : ObservableObject
{
    private LocalizationService L => LocalizationService.Shared;
    public string NavTitleText => L.Text("SessionEdit.NavTitle");
    public string NavSubtitleText => L.Text("SessionEdit.NavSubtitle");
    public string ConnectionText => L.Text("SessionEdit.Connection");
    public string LoginPromptText => L.Text("SessionEdit.LoginPrompt");
    public string LoginScriptText => L.Text("SessionEdit.LoginScript");
    public string ProxyText => L.Text("SessionEdit.Proxy");
    public string KeepAliveText => L.Text("SessionEdit.KeepAlive");
    public string SerialText => L.Text("SessionEdit.Serial");
    public string SecurityText => L.Text("SessionEdit.Security");
    public string TunnelText => L.Text("SessionEdit.Tunnel");
    public string TerminalText => L.Text("SessionEdit.Terminal");
    public string KeyboardText => L.Text("SessionEdit.Keyboard");
    public string VtModeText => L.Text("SessionEdit.VtMode");
    public string AdvancedText => L.Text("SessionEdit.Advanced");
    public string AppearanceText => L.Text("SessionEdit.Appearance");
    public string WindowText => L.Text("SessionEdit.Window");
    public string HighlightText => L.Text("SessionEdit.Highlight");
    public string TransferText => L.Text("SessionEdit.Transfer");
    public string XymodemText => L.Text("SessionEdit.Xymodem");
    public string ZmodemText => L.Text("SessionEdit.Zmodem");
    public string LoggingText => L.Text("SessionEdit.Logging");
    public string BellText => L.Text("SessionEdit.Bell");
    public string TracingText => L.Text("SessionEdit.Tracing");
    public string GeneralText => L.Text("SessionEdit.General");
    public string NameText => L.Text("SessionEdit.Name");
    public string NamePlaceholderText => L.Text("SessionEdit.NamePlaceholder");
    public string ProtocolText => L.Text("SessionEdit.Protocol");
    public string ProtocolPlaceholderText => L.Text("SessionEdit.ProtocolPlaceholder");
    public string HostText => L.Text("SessionEdit.Host");
    public string HostPlaceholderText => L.Text("SessionEdit.HostPlaceholder");
    public string PortText => L.Text("SessionEdit.Port");
    public string UserAuthText => L.Text("SessionEdit.UserAuth");
    public string UsernameText => L.Text("SessionEdit.Username");
    public string AuthMethodText => L.Text("SessionEdit.AuthMethod");
    public string PasswordText => L.Text("SessionEdit.Password");
    public string PrivateKeyText => L.Text("SessionEdit.PrivateKey");
    public string PromptPasswordText => L.Text("SessionEdit.PromptPassword");
    public string PasswordNotSavedText => L.Text("SessionEdit.PasswordNotSaved");
    public string PasswordSavedEncryptedText => L.Text("SessionEdit.PasswordSavedEncrypted");
    public string PrivateKeyPathText => L.Text("SessionEdit.PrivateKeyPath");
    public string SshTunnelTitleText => L.Text("SessionEdit.SshTunnelTitle");
    public string VncSshTunnelTitleText => L.Text("SessionEdit.VncSshTunnelTitle");
    public string UseVncSshTunnelText => L.Text("SessionEdit.UseVncSshTunnel");
    public string VncSshTunnelDescriptionText => L.Text("SessionEdit.VncSshTunnelDescription");
    public string UseRdpSshTunnelText => L.Text("SessionEdit.UseRdpSshTunnel");
    public string RdpSshTunnelDescriptionText => L.Text("SessionEdit.RdpSshTunnelDescription");
    public string SshHostText => L.Text("SessionEdit.SshHost");
    public string SshPortText => L.Text("SessionEdit.SshPort");
    public string SshUsernameText => L.Text("SessionEdit.SshUsername");
    public string SshPasswordText => L.Text("SessionEdit.SshPassword");
    public string SshPrivateKeyText => L.Text("SessionEdit.SshPrivateKey");
    public string BrowseText => L.Text("SessionEdit.Browse");
    public string ReconnectText => L.Text("SessionEdit.Reconnect");
    public string AutoReconnectText => L.Text("SessionEdit.AutoReconnect");
    public string IntervalText => L.Text("SessionEdit.Interval");
    public string LimitText => L.Text("SessionEdit.Limit");
    public string SecondsText => L.Text("SessionEdit.Seconds");
    public string MinutesText => L.Text("SessionEdit.Minutes");
    public string SaveText => L.Text("SessionEdit.Save");
    public string ConnectText => L.Text("Toolbar.Connect");
    public string CancelText => L.Text("SessionEdit.Cancel");
    public string PlaceholderText => L.Text("SessionEdit.Placeholder");
    public readonly record struct AppearanceColorPalette(
        string Foreground,
        string BoldForeground,
        string Background,
        string Cursor,
        string CursorText,
        string AnsiColors);

    [ObservableProperty] private string _dialogTitle = "新建会话";
    [ObservableProperty] private string _selectedPage = "Connection";
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
    [ObservableProperty] private string _password = string.Empty;
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
    [ObservableProperty] private string _terminalType = "xterm";
    [ObservableProperty] private decimal _terminalColumns = 80;
    [ObservableProperty] private decimal _terminalRows = 24;
    [ObservableProperty] private bool _terminalFixedSize;
    [ObservableProperty] private bool _terminalResetSizeOnConnect;
    [ObservableProperty] private decimal _terminalScrollbackSize = 1024;
    [ObservableProperty] private bool _terminalPushClearedScreenToScrollback = true;
    [ObservableProperty] private string _terminalEncoding = "utf-8";
    [ObservableProperty] private bool _terminalTreatAmbiguousAsWide;
    [ObservableProperty] private string _terminalSendLineEnding = "CR";
    [ObservableProperty] private string _terminalReceiveLineEnding = "AUTO";
    [ObservableProperty] private string _terminalKeyboardFunctionKeyMode = "Default";
    [ObservableProperty] private string _terminalKeyboardMappingFile = string.Empty;
    [ObservableProperty] private string _terminalDeleteKeySequence = "VT220";
    [ObservableProperty] private string _terminalBackspaceKeySequence = "Backspace";
    [ObservableProperty] private bool _terminalLeftAltAsMeta;
    [ObservableProperty] private bool _terminalRightAltAsMeta;
    [ObservableProperty] private bool _terminalCtrlAltAsAltGr = true;
    [ObservableProperty] private bool _terminalVtAutoWrapMode = true;
    [ObservableProperty] private bool _terminalVtOriginMode;
    [ObservableProperty] private bool _terminalVtReverseVideoMode;
    [ObservableProperty] private bool _terminalVtNewLineMode;
    [ObservableProperty] private bool _terminalVtInsertMode;
    [ObservableProperty] private bool _terminalVtEchoMode;
    [ObservableProperty] private string _terminalVtCursorKeyMode = "Normal";
    [ObservableProperty] private string _terminalVtNumericKeypadMode = "Normal";
    [ObservableProperty] private bool _terminalAdvancedUseApplicationCursorMode = true;
    [ObservableProperty] private bool _terminalAdvancedShiftLimitsApplicationCursorMode = true;
    [ObservableProperty] private bool _terminalAdvancedClearScreenBackground = true;
    [ObservableProperty] private bool _terminalAdvancedScrollToBottomOnInputOutput = true;
    [ObservableProperty] private bool _terminalAdvancedSuspendScrollToBottomOnScrollLock;
    [ObservableProperty] private bool _terminalAdvancedScrollToBottomByKey;
    [ObservableProperty] private bool _terminalAdvancedDestructiveBackspace;
    [ObservableProperty] private bool _terminalAdvancedDuplicateSessionCd = true;
    [ObservableProperty] private string _terminalAdvancedPreinputString = string.Empty;
    [ObservableProperty] private bool _terminalAdvancedUseRxvtHomeEnd;
    [ObservableProperty] private bool _terminalAdvancedDisableBlinkingText;
    [ObservableProperty] private bool _terminalAdvancedDisableTitleChange;
    [ObservableProperty] private bool _terminalAdvancedDisableTerminalPrint;
    [ObservableProperty] private bool _terminalAdvancedDisableAlternateScreen;
    [ObservableProperty] private bool _terminalAdvancedIgnoreResizeRequest = true;
    [ObservableProperty] private string _terminalAdvancedAnswerback = "CxShell";
    [ObservableProperty] private bool _terminalAdvancedUseBuiltinLineDrawing = true;
    [ObservableProperty] private bool _terminalAdvancedUseBuiltinPowerline = true;
    [ObservableProperty] private string _appearanceColorScheme = "XTerm";
    [ObservableProperty] private Color _appearanceForegroundColor = Color.Parse("#CCCCCC");
    [ObservableProperty] private Color _appearanceBoldForegroundColor = Color.Parse("#33FF33");
    [ObservableProperty] private Color _appearanceBackgroundColor = Color.Parse("#000000");
    [ObservableProperty] private string _appearanceAnsiColors = "#000000;#CC0000;#4E9A06;#C4A000;#3465A4;#75507B;#06989A;#D3D7CF;#555753;#EF2929;#8AE234;#FCE94F;#729FCF;#AD7FA8;#34E2E2;#EEEEEC";
    [ObservableProperty] private string _appearanceFontFamily = "DejaVu Sans Mono";
    [ObservableProperty] private string _appearanceFontStyle = "Normal";
    [ObservableProperty] private decimal _appearanceFontSize = 14;
    [ObservableProperty] private string _appearanceCjkFontFamily = "DejaVu Sans Mono";
    [ObservableProperty] private string _appearanceCjkFontStyle = "Normal";
    [ObservableProperty] private decimal _appearanceCjkFontSize = 14;
    [ObservableProperty] private bool _appearanceUseVariablePitchFont;
    [ObservableProperty] private string _appearanceFontQuality = "Default";
    [ObservableProperty] private string _appearanceBoldTextMode = "ColorAndFont";
    [ObservableProperty] private Color _appearanceCursorColor = Color.Parse("#00FF00");
    [ObservableProperty] private Color _appearanceCursorTextColor = Color.Parse("#000000");
    [ObservableProperty] private bool _appearanceUseBlinkingCursor;
    [ObservableProperty] private decimal _appearanceCursorBlinkSpeedMilliseconds = 500;
    [ObservableProperty] private string _appearanceCursorShape = "Block";
    [ObservableProperty] private bool _appearancePreviewCursorVisible = true;
    [ObservableProperty] private decimal _appearanceWindowPaddingTop = 5;
    [ObservableProperty] private decimal _appearanceWindowPaddingBottom = 5;
    [ObservableProperty] private decimal _appearanceWindowPaddingLeft = 5;
    [ObservableProperty] private decimal _appearanceWindowPaddingRight = 5;
    [ObservableProperty] private decimal _appearanceLineSpacing;
    [ObservableProperty] private decimal _appearanceCharacterSpacing;
    [ObservableProperty] private string _appearanceTabColorMode = "Default";
    [ObservableProperty] private Color _appearanceTabCustomColor = Color.Parse("#000000");
    [ObservableProperty] private string _appearanceBackgroundImagePath = string.Empty;
    [ObservableProperty] private string _appearanceBackgroundImagePosition = "Center";
    [ObservableProperty] private string _appearanceHighlightSetId = "None";
    [ObservableProperty] private HighlightSet? _selectedHighlightSet;
    [ObservableProperty] private HighlightRule? _selectedHighlightRule;
    [ObservableProperty] private string _advancedQuickCommandSet = "<<所有命令>>";
    [ObservableProperty] private bool _advancedDisableQuickCommandShortcuts;
    [ObservableProperty] private decimal _advancedFtpPort = 21;
    [ObservableProperty] private decimal _advancedCharacterDelayMilliseconds;
    [ObservableProperty] private bool _advancedUseLineDelay = true;
    [ObservableProperty] private decimal _advancedLineDelayMilliseconds;
    [ObservableProperty] private bool _advancedUsePromptDelay;
    [ObservableProperty] private string _advancedPromptText = string.Empty;
    [ObservableProperty] private decimal _advancedPromptMaxWaitMilliseconds;
    [ObservableProperty] private bool _advancedUseNagle;
    [ObservableProperty] private string _advancedIpVersion = "Auto";
    [ObservableProperty] private bool _advancedTraceSshProtocol;
    [ObservableProperty] private bool _advancedTraceSshTunneling;
    [ObservableProperty] private bool _advancedTraceSshPackets;
    [ObservableProperty] private bool _advancedTraceTelnetOptions;
    [ObservableProperty] private string _advancedBellMode = "Default";
    [ObservableProperty] private string _advancedBellSoundPath = string.Empty;
    [ObservableProperty] private bool _advancedBellFlashInactiveWindow;
    [ObservableProperty] private decimal _advancedBellIgnoreRepeatedSeconds = 3;
    [ObservableProperty] private decimal _advancedBellReactivateAfterSeconds = 3;
    [ObservableProperty] private string _advancedLogFilePath = "%n_%Y-%m-%d_%t.log";
    [ObservableProperty] private bool _advancedLogOverwriteExisting = true;
    [ObservableProperty] private bool _advancedLogStartOnConnect;
    [ObservableProperty] private bool _advancedLogPromptFileOnStart;
    [ObservableProperty] private bool _advancedLogUseRtf;
    [ObservableProperty] private bool _advancedLogIncludeTerminalCodes;
    [ObservableProperty] private string _advancedLogEncoding = "Utf16Le";
    [ObservableProperty] private string _advancedLogFilePathPreview = string.Empty;
    [ObservableProperty] private bool _advancedLogWriteTimestamp;
    [ObservableProperty] private string _advancedLogTimestampFormat = "[%a]";
    [ObservableProperty] private string _advancedLogTimestampPreview = string.Empty;
    [ObservableProperty] private bool _enableLoginScriptRules = true;
    [ObservableProperty] private LoginScriptRule? _selectedLoginScriptRule;
    [ObservableProperty] private bool _runLoginScriptFile;
    [ObservableProperty] private string _loginScriptFilePath = string.Empty;
    [ObservableProperty] private string _loginScriptParameters = string.Empty;
    [ObservableProperty] private string _sshRemoteCommand = string.Empty;
    [ObservableProperty] private string _sshVersionPolicy = "Ssh2Only";
    [ObservableProperty] private bool _sshUseXagent;
    [ObservableProperty] private bool _sshForwardAgent;
    [ObservableProperty] private bool _sshUseCompression;
    [ObservableProperty] private bool _sshNoTerminal;
    [ObservableProperty] private bool _sshAcceptAndSaveHostKey;
    [ObservableProperty] private bool _sshAutoOpenSftpPanel = true;
    [ObservableProperty] private bool _sshAutoOpenMonitorPanel = true;
    [ObservableProperty] private bool _sshDoNotStartFileManager;
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
    [ObservableProperty] private bool _sftpFollowTerminalDirectory = true;
    [ObservableProperty] private bool _sftpUseCustomServer;
    [ObservableProperty] private string _sftpCustomServerCommand = string.Empty;
    [ObservableProperty] private bool _fileTransferAlwaysAskDownloadFolder = true;
    [ObservableProperty] private string _fileTransferDownloadDirectory = string.Empty;
    [ObservableProperty] private string _fileTransferUploadDirectory = string.Empty;
    [ObservableProperty] private string _fileTransferDuplicateAction = "AutoRename";
    [ObservableProperty] private string _fileTransferUploadProtocol = "Zmodem";
    [ObservableProperty] private int _fileTransferXymodemBlockSize = 128;
    [ObservableProperty] private string _fileTransferXmodemUploadCommand = "rx";
    [ObservableProperty] private string _fileTransferYmodemUploadCommand = "rb -E";
    [ObservableProperty] private bool _fileTransferZmodemAutoActivate = true;
    [ObservableProperty] private string _fileTransferZmodemUploadCommand = "rz -E";
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
    [ObservableProperty] private bool _rdpUseSshTunnel;
    [ObservableProperty] private string _rdpSshHost = string.Empty;
    [ObservableProperty] private string _rdpSshPort = "22";
    [ObservableProperty] private string _rdpSshUsername = string.Empty;
    [ObservableProperty] private string _rdpSshPassword = string.Empty;
    [ObservableProperty] private bool _rdpSshUsePrivateKey;
    [ObservableProperty] private string _rdpSshPrivateKeyPath = string.Empty;
    [ObservableProperty] private bool _vncUseSshTunnel;
    [ObservableProperty] private string _vncSshHost = string.Empty;
    [ObservableProperty] private string _vncSshPort = "22";
    [ObservableProperty] private string _vncSshUsername = string.Empty;
    [ObservableProperty] private string _vncSshPassword = string.Empty;
    [ObservableProperty] private bool _vncSshUsePrivateKey;
    [ObservableProperty] private string _vncSshPrivateKeyPath = string.Empty;

    public bool HasValidationError => !string.IsNullOrWhiteSpace(ValidationMessage);
    public bool IsNameInvalid => NameStatus == InputControlStatus.Error;
    public bool IsHostInvalid => HostStatus == InputControlStatus.Error;
    public bool IsPortInvalid => PortStatus == InputControlStatus.Error;
    public bool IsSerialPortInvalid => SerialPortStatus == InputControlStatus.Error;
    public bool HasSelectedSshTunnelRule => SelectedSshTunnelRule != null;
    public bool HasSelectedLoginScriptRule => SelectedLoginScriptRule != null;
    public bool HasSelectedHighlightSet => SelectedHighlightSet != null;
    public bool HasSelectedHighlightRule => SelectedHighlightRule != null;
    public bool IsTerminalPage => SelectedPage == "Terminal";
    public bool IsAppearancePage => SelectedPage == "Appearance";
    public bool IsAppearanceWindowPage => SelectedPage == "AppearanceWindow";
    public bool IsAppearanceHighlightPage => SelectedPage == "AppearanceHighlight";
    public bool IsKeyboardPage => SelectedPage == "Keyboard";
    public bool IsVtModePage => SelectedPage == "VtMode";
    public bool IsTerminalAdvancedPage => SelectedPage == "TerminalAdvanced";
    public bool IsTransferPage => SelectedPage == "Transfer";
    public bool IsFileTransferXymodemPage => SelectedPage == "FileTransferXymodem";
    public bool IsFileTransferZmodemPage => SelectedPage == "FileTransferZmodem";
    public bool IsLoggingPage => SelectedPage == "Logging";
    public bool IsBellPage => SelectedPage == "Bell";
    public bool IsAdvancedPage => SelectedPage == "Advanced";
    public bool IsTracingPage => SelectedPage == "Tracing";
    public bool IsSessionScope => true;
    public bool IsVncProtocol => string.Equals(Protocol, SessionProtocol.VNC.ToString(), StringComparison.OrdinalIgnoreCase);
    public bool IsRdpProtocol => string.Equals(Protocol, SessionProtocol.RDP.ToString(), StringComparison.OrdinalIgnoreCase);
    public bool IsRdpSshPasswordAuth
    {
        get => !RdpSshUsePrivateKey;
        set { if (value) RdpSshUsePrivateKey = false; }
    }
    public bool IsRdpSshPrivateKeyAuth
    {
        get => RdpSshUsePrivateKey;
        set { if (value) RdpSshUsePrivateKey = true; }
    }
    public bool IsVncSshPasswordAuth
    {
        get => !VncSshUsePrivateKey;
        set { if (value) VncSshUsePrivateKey = false; }
    }
    public bool IsVncSshPrivateKeyAuth
    {
        get => VncSshUsePrivateKey;
        set { if (value) VncSshUsePrivateKey = true; }
    }
    public bool IsSftpCustomServerCommandEnabled => SftpUseCustomServer;
    public bool IsSessionKeepAliveIntervalEnabled => SendSessionKeepAlive;
    public bool IsIdleStringSettingsEnabled => SendIdleString;
    public bool IsLoginScriptFileEnabled => RunLoginScriptFile;
    public bool IsKeyboardMappingFileEnabled => string.Equals(TerminalKeyboardFunctionKeyMode, "UserCustom", StringComparison.OrdinalIgnoreCase);
    public bool IsCtrlAltAsAltGrEnabled => TerminalLeftAltAsMeta;
    public bool IsFileTransferPathSettingsEnabled => !FileTransferAlwaysAskDownloadFolder;
    public bool IsFileTransferUseConfiguredFolders
    {
        get => !FileTransferAlwaysAskDownloadFolder;
        set { if (value) FileTransferAlwaysAskDownloadFolder = false; }
    }
    public bool IsFileTransferDuplicateAutoRename
    {
        get => string.Equals(FileTransferDuplicateAction, "AutoRename", StringComparison.OrdinalIgnoreCase);
        set { if (value) FileTransferDuplicateAction = "AutoRename"; }
    }
    public bool IsFileTransferDuplicateOverwrite
    {
        get => string.Equals(FileTransferDuplicateAction, "Overwrite", StringComparison.OrdinalIgnoreCase);
        set { if (value) FileTransferDuplicateAction = "Overwrite"; }
    }
    public bool IsFileTransferUploadProtocolXmodem
    {
        get => string.Equals(FileTransferUploadProtocol, "Xmodem", StringComparison.OrdinalIgnoreCase);
        set { if (value) FileTransferUploadProtocol = "Xmodem"; }
    }
    public bool IsFileTransferUploadProtocolYmodem
    {
        get => string.Equals(FileTransferUploadProtocol, "Ymodem", StringComparison.OrdinalIgnoreCase);
        set { if (value) FileTransferUploadProtocol = "Ymodem"; }
    }
    public bool IsFileTransferUploadProtocolZmodem
    {
        get => string.Equals(FileTransferUploadProtocol, "Zmodem", StringComparison.OrdinalIgnoreCase);
        set { if (value) FileTransferUploadProtocol = "Zmodem"; }
    }
    public bool IsFileTransferUploadProtocolFtp
    {
        get => string.Equals(FileTransferUploadProtocol, "Ftp", StringComparison.OrdinalIgnoreCase);
        set { if (value) FileTransferUploadProtocol = "Ftp"; }
    }
    public bool IsFileTransferXymodemBlockSize128
    {
        get => FileTransferXymodemBlockSize != 1024;
        set { if (value) FileTransferXymodemBlockSize = 128; }
    }
    public bool IsFileTransferXymodemBlockSize1024
    {
        get => FileTransferXymodemBlockSize == 1024;
        set { if (value) FileTransferXymodemBlockSize = 1024; }
    }
    public bool IsAdvancedLineDelay => AdvancedUseLineDelay;
    public bool IsAdvancedPromptDelay => AdvancedUsePromptDelay;
    public bool IsAdvancedBellModeNone
    {
        get => string.Equals(AdvancedBellMode, "None", StringComparison.OrdinalIgnoreCase);
        set { if (value) AdvancedBellMode = "None"; }
    }
    public bool IsAdvancedBellModeDefault
    {
        get => string.Equals(AdvancedBellMode, "Default", StringComparison.OrdinalIgnoreCase);
        set { if (value) AdvancedBellMode = "Default"; }
    }
    public bool IsAdvancedBellModeBuiltin
    {
        get => string.Equals(AdvancedBellMode, "Builtin", StringComparison.OrdinalIgnoreCase);
        set { if (value) AdvancedBellMode = "Builtin"; }
    }
    public bool IsAdvancedBellModeSound
    {
        get => string.Equals(AdvancedBellMode, "Sound", StringComparison.OrdinalIgnoreCase);
        set { if (value) AdvancedBellMode = "Sound"; }
    }
    public bool IsAdvancedBellSoundPathEnabled => IsAdvancedBellModeSound;
    public bool IsAdvancedBellFlashInactiveWindowEnabled => !IsAdvancedBellModeNone;
    public bool IsAdvancedLogPromptFileOnStartEnabled => AdvancedLogStartOnConnect;
    public bool IsAdvancedLogTimestampSettingsEnabled => AdvancedLogWriteTimestamp;
    public FontFamily AppearancePreviewFontFamily => new(NormalizeFontFamilyName(AppearanceFontFamily));
    public FontFamily AppearancePreviewCjkFontFamily => new(NormalizeFontFamilyName(AppearanceCjkFontFamily));
    public bool IsAppearanceCjkHorizontalPreviewVisible => !IsVerticalFontName(AppearanceCjkFontFamily);
    public bool IsAppearanceCjkVerticalPreviewVisible => IsVerticalFontName(AppearanceCjkFontFamily);
    public bool IsAdvancedIpVersionAuto
    {
        get => string.Equals(AdvancedIpVersion, "Auto", StringComparison.OrdinalIgnoreCase);
        set { if (value) AdvancedIpVersion = "Auto"; }
    }
    public bool IsAdvancedIpVersion4
    {
        get => string.Equals(AdvancedIpVersion, "IPv4", StringComparison.OrdinalIgnoreCase);
        set { if (value) AdvancedIpVersion = "IPv4"; }
    }
    public bool IsAdvancedIpVersion6
    {
        get => string.Equals(AdvancedIpVersion, "IPv6", StringComparison.OrdinalIgnoreCase);
        set { if (value) AdvancedIpVersion = "IPv6"; }
    }
    public FontStyle AppearancePreviewFontStyle => AppearanceFontStyle.Contains("Italic", StringComparison.OrdinalIgnoreCase)
        ? FontStyle.Italic
        : FontStyle.Normal;
    public FontWeight AppearancePreviewFontWeight => AppearanceFontStyle.Contains("Bold", StringComparison.OrdinalIgnoreCase)
        ? FontWeight.Bold
        : FontWeight.Normal;
    public FontWeight AppearancePreviewBoldWeight => string.Equals(AppearanceBoldTextMode, "Font", StringComparison.OrdinalIgnoreCase) ||
                                                     string.Equals(AppearanceBoldTextMode, "ColorAndFont", StringComparison.OrdinalIgnoreCase)
        ? FontWeight.Bold
        : FontWeight.Normal;
    public Color AppearancePreviewBoldColor => string.Equals(AppearanceBoldTextMode, "Color", StringComparison.OrdinalIgnoreCase) ||
                                               string.Equals(AppearanceBoldTextMode, "ColorAndFont", StringComparison.OrdinalIgnoreCase)
        ? AppearanceBoldForegroundColor
        : AppearanceForegroundColor;
    public IBrush AppearanceForegroundBrush => new SolidColorBrush(AppearanceForegroundColor);
    public IBrush AppearanceBoldForegroundBrush => new SolidColorBrush(AppearanceBoldForegroundColor);
    public IBrush AppearanceBackgroundBrush => new SolidColorBrush(AppearanceBackgroundColor);
    public IBrush AppearancePreviewBoldBrush => new SolidColorBrush(AppearancePreviewBoldColor);
    public IBrush AppearanceCursorBrush => new SolidColorBrush(AppearanceCursorColor);
    public IBrush AppearanceCursorTextBrush => new SolidColorBrush(AppearanceCursorTextColor);
    public IBrush AppearanceAnsiBlackBrush => new SolidColorBrush(AppearanceAnsiBlack);
    public IBrush AppearanceAnsiRedBrush => new SolidColorBrush(AppearanceAnsiRed);
    public IBrush AppearanceAnsiGreenBrush => new SolidColorBrush(AppearanceAnsiGreen);
    public IBrush AppearanceAnsiYellowBrush => new SolidColorBrush(AppearanceAnsiYellow);
    public IBrush AppearanceAnsiBlueBrush => new SolidColorBrush(AppearanceAnsiBlue);
    public IBrush AppearanceAnsiMagentaBrush => new SolidColorBrush(AppearanceAnsiMagenta);
    public IBrush AppearanceAnsiCyanBrush => new SolidColorBrush(AppearanceAnsiCyan);
    public IBrush AppearanceAnsiWhiteBrush => new SolidColorBrush(AppearanceAnsiWhite);
    public TextRenderingMode AppearancePreviewTextRenderingMode => GetTextRenderingMode(AppearanceFontQuality);
    public TextHintingMode AppearancePreviewTextHintingMode => GetTextHintingMode(AppearanceFontQuality);
    public BaselinePixelAlignment AppearancePreviewBaselinePixelAlignment => GetBaselinePixelAlignment(AppearanceFontQuality);
    public Color AppearanceAnsiBlack => GetAnsiPreviewColor(0, "#000000");
    public Color AppearanceAnsiRed => GetAnsiPreviewColor(1, "#CC0000");
    public Color AppearanceAnsiGreen => GetAnsiPreviewColor(2, "#4E9A06");
    public Color AppearanceAnsiYellow => GetAnsiPreviewColor(3, "#C4A000");
    public Color AppearanceAnsiBlue => GetAnsiPreviewColor(4, "#3465A4");
    public Color AppearanceAnsiMagenta => GetAnsiPreviewColor(5, "#75507B");
    public Color AppearanceAnsiCyan => GetAnsiPreviewColor(6, "#06989A");
    public Color AppearanceAnsiWhite => GetAnsiPreviewColor(7, "#D3D7CF");
    public bool IsAppearanceCursorShapeBlock
    {
        get => string.Equals(AppearanceCursorShape, "Block", StringComparison.OrdinalIgnoreCase);
        set { if (value) AppearanceCursorShape = "Block"; }
    }
    public bool IsAppearanceCursorShapeVertical
    {
        get => string.Equals(AppearanceCursorShape, "Vertical", StringComparison.OrdinalIgnoreCase);
        set { if (value) AppearanceCursorShape = "Vertical"; }
    }
    public bool IsAppearanceCursorShapeUnderline
    {
        get => string.Equals(AppearanceCursorShape, "Underline", StringComparison.OrdinalIgnoreCase);
        set { if (value) AppearanceCursorShape = "Underline"; }
    }
    public bool IsAppearanceCursorBlockVisible => IsAppearanceCursorShapeBlock && AppearancePreviewCursorVisible;
    public bool IsAppearanceCursorVerticalVisible => IsAppearanceCursorShapeVertical && AppearancePreviewCursorVisible;
    public bool IsAppearanceCursorUnderlineVisible => IsAppearanceCursorShapeUnderline && AppearancePreviewCursorVisible;
    public bool IsAppearanceTabColorDefault
    {
        get => string.Equals(AppearanceTabColorMode, "Default", StringComparison.OrdinalIgnoreCase);
        set { if (value) AppearanceTabColorMode = "Default"; }
    }
    public bool IsAppearanceTabColorRed
    {
        get => string.Equals(AppearanceTabColorMode, "Red", StringComparison.OrdinalIgnoreCase);
        set { if (value) AppearanceTabColorMode = "Red"; }
    }
    public bool IsAppearanceTabColorPurple
    {
        get => string.Equals(AppearanceTabColorMode, "Purple", StringComparison.OrdinalIgnoreCase);
        set { if (value) AppearanceTabColorMode = "Purple"; }
    }
    public bool IsAppearanceTabColorYellow
    {
        get => string.Equals(AppearanceTabColorMode, "Yellow", StringComparison.OrdinalIgnoreCase);
        set { if (value) AppearanceTabColorMode = "Yellow"; }
    }
    public bool IsAppearanceTabColorCustom
    {
        get => string.Equals(AppearanceTabColorMode, "Custom", StringComparison.OrdinalIgnoreCase);
        set { if (value) AppearanceTabColorMode = "Custom"; }
    }
    public bool IsDeleteKeyVt220
    {
        get => string.Equals(TerminalDeleteKeySequence, "VT220", StringComparison.OrdinalIgnoreCase);
        set { if (value) TerminalDeleteKeySequence = "VT220"; }
    }
    public bool IsDeleteKeyAscii127
    {
        get => string.Equals(TerminalDeleteKeySequence, "ASCII127", StringComparison.OrdinalIgnoreCase);
        set { if (value) TerminalDeleteKeySequence = "ASCII127"; }
    }
    public bool IsDeleteKeyBackspace
    {
        get => string.Equals(TerminalDeleteKeySequence, "Backspace", StringComparison.OrdinalIgnoreCase);
        set { if (value) TerminalDeleteKeySequence = "Backspace"; }
    }
    public bool IsBackspaceKeyVt220
    {
        get => string.Equals(TerminalBackspaceKeySequence, "VT220", StringComparison.OrdinalIgnoreCase);
        set { if (value) TerminalBackspaceKeySequence = "VT220"; }
    }
    public bool IsBackspaceKeyAscii127
    {
        get => string.Equals(TerminalBackspaceKeySequence, "ASCII127", StringComparison.OrdinalIgnoreCase);
        set { if (value) TerminalBackspaceKeySequence = "ASCII127"; }
    }
    public bool IsBackspaceKeyBackspace
    {
        get => string.Equals(TerminalBackspaceKeySequence, "Backspace", StringComparison.OrdinalIgnoreCase);
        set { if (value) TerminalBackspaceKeySequence = "Backspace"; }
    }
    public bool IsCursorKeyModeNormal
    {
        get => string.Equals(TerminalVtCursorKeyMode, "Normal", StringComparison.OrdinalIgnoreCase);
        set { if (value) TerminalVtCursorKeyMode = "Normal"; }
    }
    public bool IsCursorKeyModeApplication
    {
        get => string.Equals(TerminalVtCursorKeyMode, "Application", StringComparison.OrdinalIgnoreCase);
        set { if (value) TerminalVtCursorKeyMode = "Application"; }
    }
    public bool IsNumericKeypadModeNormal
    {
        get => string.Equals(TerminalVtNumericKeypadMode, "Normal", StringComparison.OrdinalIgnoreCase);
        set { if (value) TerminalVtNumericKeypadMode = "Normal"; }
    }
    public bool IsNumericKeypadModeApplication
    {
        get => string.Equals(TerminalVtNumericKeypadMode, "Application", StringComparison.OrdinalIgnoreCase);
        set { if (value) TerminalVtNumericKeypadMode = "Application"; }
    }
    public bool IsNumericKeypadModeForceNormal
    {
        get => string.Equals(TerminalVtNumericKeypadMode, "ForceNormal", StringComparison.OrdinalIgnoreCase);
        set { if (value) TerminalVtNumericKeypadMode = "ForceNormal"; }
    }

    public IList<TreeNodePath> DefaultOpenNavPaths { get; } =
    [
        new TreeNodePath("Connection"),
        new TreeNodePath("Ssh"),
        new TreeNodePath("Terminal"),
        new TreeNodePath("Appearance"),
        new TreeNodePath("Advanced")
    ];

    public TreeNodePath DefaultSelectedNavPath { get; } = new("Connection");

    public ObservableCollection<ISelectOption> ProtocolOptions { get; } =
    [
        new SelectOption { Header = "SSH", Content = SessionProtocol.SSH.ToString() },
        new SelectOption { Header = "TELNET", Content = SessionProtocol.TELNET.ToString() },
        new SelectOption { Header = "RLOGIN", Content = SessionProtocol.RLOGIN.ToString() },
        new SelectOption { Header = "SFTP", Content = SessionProtocol.SFTP.ToString() },
        new SelectOption { Header = "SERIAL", Content = SessionProtocol.SERIAL.ToString() },
        new SelectOption { Header = "FTP", Content = SessionProtocol.FTP.ToString() },
        new SelectOption { Header = "RDP", Content = SessionProtocol.RDP.ToString() },
        new SelectOption { Header = "VNC", Content = SessionProtocol.VNC.ToString() }
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

    public ObservableCollection<ISelectOption> TerminalTypeOptions { get; } =
    [
        new SelectOption { Header = "vt100", Content = "vt100" },
        new SelectOption { Header = "vt102", Content = "vt102" },
        new SelectOption { Header = "vt220", Content = "vt220" },
        new SelectOption { Header = "vt320", Content = "vt320" },
        new SelectOption { Header = "xterm", Content = "xterm" },
        new SelectOption { Header = "linux", Content = "linux" },
        new SelectOption { Header = "scoansi", Content = "scoansi" },
        new SelectOption { Header = "ansi", Content = "ansi" }
    ];

    public ObservableCollection<ISelectOption> TerminalEncodingOptions { get; } =
    [
        new SelectOption { Header = "默认语言", Content = "default" },
        new SelectOption { Header = "Unicode (UTF-8)", Content = "utf-8" },
        new SelectOption { Header = "Arabic (ASMO 708)", Content = "asmo-708" },
        new SelectOption { Header = "Arabic (DOS)", Content = "ibm864" },
        new SelectOption { Header = "Arabic (ISO)", Content = "iso-8859-6" },
        new SelectOption { Header = "Arabic (Windows)", Content = "windows-1256" },
        new SelectOption { Header = "Baltic (ISO)", Content = "iso-8859-4" },
        new SelectOption { Header = "Baltic (Windows)", Content = "windows-1257" },
        new SelectOption { Header = "Central European (ISO)", Content = "iso-8859-2" },
        new SelectOption { Header = "Central European (Windows)", Content = "windows-1250" },
        new SelectOption { Header = "Chinese Simplified (GBK)", Content = "gbk" },
        new SelectOption { Header = "Chinese Simplified (GB18030)", Content = "gb18030" },
        new SelectOption { Header = "Chinese Simplified (GB2312)", Content = "gb2312" },
        new SelectOption { Header = "Chinese Traditional (Big5)", Content = "big5" },
        new SelectOption { Header = "Cyrillic (ISO)", Content = "iso-8859-5" },
        new SelectOption { Header = "Cyrillic (KOI8-R)", Content = "koi8-r" },
        new SelectOption { Header = "Cyrillic (KOI8-U)", Content = "koi8-u" },
        new SelectOption { Header = "Cyrillic (Windows)", Content = "windows-1251" },
        new SelectOption { Header = "Cyrillic (IBM-866)", Content = "ibm866" },
        new SelectOption { Header = "Greek (ISO)", Content = "iso-8859-7" },
        new SelectOption { Header = "Greek (Windows)", Content = "windows-1253" },
        new SelectOption { Header = "Hebrew (DOS)", Content = "dos-862" },
        new SelectOption { Header = "Hebrew (ISO-Visual)", Content = "iso-8859-8" },
        new SelectOption { Header = "Hebrew (ISO-Logical)", Content = "iso-8859-8-i" },
        new SelectOption { Header = "Hebrew (Windows)", Content = "windows-1255" },
        new SelectOption { Header = "Japanese (EUC)", Content = "euc-jp" },
        new SelectOption { Header = "Japanese (Shift-JIS)", Content = "shift_jis" },
        new SelectOption { Header = "Korean", Content = "ks_c_5601-1987" },
        new SelectOption { Header = "Korean (EUC)", Content = "euc-kr" },
        new SelectOption { Header = "Thai (Windows)", Content = "windows-874" },
        new SelectOption { Header = "Turkish (ISO)", Content = "iso-8859-9" },
        new SelectOption { Header = "Turkish (Windows)", Content = "windows-1254" },
        new SelectOption { Header = "Vietnamese (Windows)", Content = "windows-1258" },
        new SelectOption { Header = "Western European (ISO)", Content = "iso-8859-1" },
        new SelectOption { Header = "Western European (Windows)", Content = "windows-1252" }
    ];

    public ObservableCollection<ISelectOption> TerminalLineEndingOptions { get; } =
    [
        new SelectOption { Header = "CR", Content = "CR" },
        new SelectOption { Header = "LF", Content = "LF" },
        new SelectOption { Header = "CRLF", Content = "CRLF" }
    ];

    public ObservableCollection<ISelectOption> TerminalReceiveLineEndingOptions { get; } =
    [
        new SelectOption { Header = "AUTO", Content = "AUTO" },
        new SelectOption { Header = "CR", Content = "CR" },
        new SelectOption { Header = "CRLF", Content = "CRLF" },
        new SelectOption { Header = "LF", Content = "LF" }
    ];

    public ObservableCollection<ISelectOption> TerminalKeyboardFunctionKeyOptions { get; } =
    [
        new SelectOption { Header = "默认", Content = "Default" },
        new SelectOption { Header = "用户自定义", Content = "UserCustom" },
        new SelectOption { Header = "ESC[n~", Content = "EscN" },
        new SelectOption { Header = "Linux", Content = "Linux" },
        new SelectOption { Header = "Xterm R6", Content = "XtermR6" },
        new SelectOption { Header = "VT400", Content = "VT400" },
        new SelectOption { Header = "VT100+", Content = "VT100Plus" },
        new SelectOption { Header = "SCO", Content = "SCO" }
    ];

    public ObservableCollection<ISelectOption> TerminalVtCursorKeyModeOptions { get; } =
    [
        new SelectOption { Header = "普通", Content = "Normal" },
        new SelectOption { Header = "应用程序", Content = "Application" }
    ];

    public ObservableCollection<ISelectOption> TerminalVtNumericKeypadModeOptions { get; } =
    [
        new SelectOption { Header = "普通", Content = "Normal" },
        new SelectOption { Header = "应用程序", Content = "Application" },
        new SelectOption { Header = "强制普通", Content = "ForceNormal" }
    ];

    public ObservableCollection<ISelectOption> AppearanceColorSchemeOptions { get; } =
    [
        new SelectOption { Header = "Afterglow", Content = "Afterglow" },
        new SelectOption { Header = "ANSI Colors on Black", Content = "AnsiBlack" },
        new SelectOption { Header = "ANSI Colors on White", Content = "AnsiWhite" },
        new SelectOption { Header = "Arthur", Content = "Arthur" },
        new SelectOption { Header = "Belafonte Day", Content = "BelafonteDay" },
        new SelectOption { Header = "Black on White", Content = "BlackOnWhite" },
        new SelectOption { Header = "Chalk", Content = "Chalk" },
        new SelectOption { Header = "Chalkboard", Content = "Chalkboard" },
        new SelectOption { Header = "Codeschool", Content = "Codeschool" },
        new SelectOption { Header = "Earthsong", Content = "Earthsong" },
        new SelectOption { Header = "Espresso", Content = "Espresso" },
        new SelectOption { Header = "IR Black", Content = "IrBlack" },
        new SelectOption { Header = "New Black", Content = "NewBlack" },
        new SelectOption { Header = "New White", Content = "NewWhite" },
        new SelectOption { Header = "Obsidian", Content = "Obsidian" },
        new SelectOption { Header = "Pastel on Black", Content = "PastelBlack" },
        new SelectOption { Header = "Pastel on White", Content = "PastelWhite" },
        new SelectOption { Header = "White on Black", Content = "WhiteOnBlack" },
        new SelectOption { Header = "XTerm", Content = "XTerm" }
    ];

    public ObservableCollection<ISelectOption> AppearanceFontOptions { get; } = CreateFontOptions();
    public ObservableCollection<ISelectOption> AppearanceFontStyleOptions { get; } =
    [
        new SelectOption { Header = "Normal", Content = "Normal" },
        new SelectOption { Header = "Bold", Content = "Bold" },
        new SelectOption { Header = "Italic", Content = "Italic" },
        new SelectOption { Header = "Bold Italic", Content = "BoldItalic" }
    ];
    public ObservableCollection<ISelectOption> AppearanceFontSizeOptions { get; } =
    [
        new SelectOption { Header = "8", Content = "8" },
        new SelectOption { Header = "9", Content = "9" },
        new SelectOption { Header = "10", Content = "10" },
        new SelectOption { Header = "11", Content = "11" },
        new SelectOption { Header = "12", Content = "12" },
        new SelectOption { Header = "14", Content = "14" },
        new SelectOption { Header = "16", Content = "16" },
        new SelectOption { Header = "18", Content = "18" },
        new SelectOption { Header = "20", Content = "20" },
        new SelectOption { Header = "22", Content = "22" },
        new SelectOption { Header = "24", Content = "24" },
        new SelectOption { Header = "26", Content = "26" },
        new SelectOption { Header = "28", Content = "28" },
        new SelectOption { Header = "36", Content = "36" },
        new SelectOption { Header = "48", Content = "48" },
        new SelectOption { Header = "72", Content = "72" }
    ];
    public ObservableCollection<ISelectOption> AppearanceFontQualityOptions { get; } =
    [
        new SelectOption { Header = "默认", Content = "Default" },
        new SelectOption { Header = "草稿", Content = "Draft" },
        new SelectOption { Header = "预览", Content = "Proof" },
        new SelectOption { Header = "无抗锯齿", Content = "NonAntiAliased" },
        new SelectOption { Header = "抗锯齿", Content = "AntiAliased" },
        new SelectOption { Header = "ClearType", Content = "ClearType" },
        new SelectOption { Header = "自然 ClearType", Content = "NaturalClearType" }
    ];
    public ObservableCollection<ISelectOption> AppearanceBoldTextModeOptions { get; } =
    [
        new SelectOption { Header = "使用大胆的色彩", Content = "Color" },
        new SelectOption { Header = "使用粗体", Content = "Font" },
        new SelectOption { Header = "使用大胆的颜色和字体", Content = "ColorAndFont" }
    ];
    public ObservableCollection<ISelectOption> AppearanceCursorShapeOptions { get; } =
    [
        new SelectOption { Header = "块", Content = "Block" },
        new SelectOption { Header = "竖线", Content = "Vertical" },
        new SelectOption { Header = "下划线", Content = "Underline" }
    ];
    public ObservableCollection<ISelectOption> AppearanceBackgroundImagePositionOptions { get; } =
    [
        new SelectOption { Header = "中央", Content = "Center" },
        new SelectOption { Header = "瓦", Content = "Tile" },
        new SelectOption { Header = "伸展", Content = "Stretch" },
        new SelectOption { Header = "左上方", Content = "TopLeft" },
        new SelectOption { Header = "右上", Content = "TopRight" },
        new SelectOption { Header = "左下方", Content = "BottomLeft" },
        new SelectOption { Header = "右下", Content = "BottomRight" }
    ];

    public ObservableCollection<ISelectOption> AppearanceHighlightSetOptions { get; } = new();
    public ObservableCollection<ISelectOption> AdvancedLogEncodingOptions { get; } =
    [
        new SelectOption { Header = "ANSI", Content = "Ansi" },
        new SelectOption { Header = "Unicode (UTF-8)", Content = "Utf8" },
        new SelectOption { Header = "Unicode (UTF-16 LE)", Content = "Utf16Le" }
    ];
    public ObservableCollection<HighlightSet> AppearanceHighlightSets { get; } = new();
    public ObservableCollection<ISelectOption> SerialPortOptions { get; } = new();
    public ObservableCollection<ISelectOption> SshCipherOptions { get; } = CreateAlgorithmOptions("<Cipher List>", SshAlgorithmPreferenceService.DefaultCipherAlgorithms);
    public ObservableCollection<ISelectOption> SshMacOptions { get; } = CreateAlgorithmOptions("<MAC List>", SshAlgorithmPreferenceService.DefaultMacAlgorithms);
    public ObservableCollection<ISelectOption> SshKeyExchangeOptions { get; } = CreateAlgorithmOptions("<Key Exchange List>", SshAlgorithmPreferenceService.DefaultKeyExchangeAlgorithms);
    public ObservableCollection<SshTunnelRule> SshTunnelRules { get; } = new();
    public ObservableCollection<LoginScriptRule> LoginScriptRules { get; } = new();
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
        new SelectOption { Header = "无", Content = "None" },
        new SelectOption { Header = "奇", Content = "Odd" },
        new SelectOption { Header = "偶", Content = "Even" },
        new SelectOption { Header = "标记", Content = "Mark" },
        new SelectOption { Header = "空格", Content = "Space" }
    ];
    public ObservableCollection<ISelectOption> SerialFlowControlOptions { get; } =
    [
        new SelectOption { Header = "无", Content = "None" },
        new SelectOption { Header = "RTS/CTS", Content = "RTS/CTS" },
        new SelectOption { Header = "XON/XOFF", Content = "XON/XOFF" }
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
        new SelectOption { Header = "不使用", Content = "NotUsed" },
        new SelectOption { Header = "使用智能调整大小", Content = "SmartSizing" },
        new SelectOption { Header = "使用智能重新连接", Content = "SmartReconnect" },
        new SelectOption { Header = "使用旧版重新连接", Content = "LegacyReconnect" }
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

    public ObservableCollection<ISelectOption> AdvancedIpVersionOptions { get; } =
    [
        new SelectOption { Header = "自动", Content = "Auto" },
        new SelectOption { Header = "IPv4", Content = "IPv4" },
        new SelectOption { Header = "IPv6", Content = "IPv6" }
    ];

    public ObservableCollection<ISelectOption> EncodingOptions => TerminalEncodingOptions;
    public ObservableCollection<ISelectOption> SendLineEndingOptions => TerminalLineEndingOptions;
    public ObservableCollection<ISelectOption> ReceiveLineEndingOptions => TerminalReceiveLineEndingOptions;
    public ObservableCollection<ISelectOption> FunctionKeyOptions => TerminalKeyboardFunctionKeyOptions;
    public ObservableCollection<ISelectOption> LogEncodingOptions => AdvancedLogEncodingOptions;

    public void RefreshLocalization()
    {
        RefreshLocalizedOptionHeaders();
        DialogTitle = _editingSession == null
            ? L.Text("SessionEdit.TitleNew")
            : L.Text("SessionEdit.TitleProperties");

        OnPropertyChanged(nameof(NavTitleText));
        OnPropertyChanged(nameof(NavSubtitleText));
        OnPropertyChanged(nameof(ConnectionText));
        OnPropertyChanged(nameof(LoginPromptText));
        OnPropertyChanged(nameof(LoginScriptText));
        OnPropertyChanged(nameof(ProxyText));
        OnPropertyChanged(nameof(KeepAliveText));
        OnPropertyChanged(nameof(SerialText));
        OnPropertyChanged(nameof(SecurityText));
        OnPropertyChanged(nameof(TunnelText));
        OnPropertyChanged(nameof(TerminalText));
        OnPropertyChanged(nameof(KeyboardText));
        OnPropertyChanged(nameof(VtModeText));
        OnPropertyChanged(nameof(AdvancedText));
        OnPropertyChanged(nameof(AppearanceText));
        OnPropertyChanged(nameof(WindowText));
        OnPropertyChanged(nameof(HighlightText));
        OnPropertyChanged(nameof(TransferText));
        OnPropertyChanged(nameof(XymodemText));
        OnPropertyChanged(nameof(ZmodemText));
        OnPropertyChanged(nameof(LoggingText));
        OnPropertyChanged(nameof(BellText));
        OnPropertyChanged(nameof(TracingText));
        OnPropertyChanged(nameof(GeneralText));
        OnPropertyChanged(nameof(NameText));
        OnPropertyChanged(nameof(NamePlaceholderText));
        OnPropertyChanged(nameof(ProtocolText));
        OnPropertyChanged(nameof(ProtocolPlaceholderText));
        OnPropertyChanged(nameof(HostText));
        OnPropertyChanged(nameof(HostPlaceholderText));
        OnPropertyChanged(nameof(PortText));
        OnPropertyChanged(nameof(UserAuthText));
        OnPropertyChanged(nameof(UsernameText));
        OnPropertyChanged(nameof(AuthMethodText));
        OnPropertyChanged(nameof(PasswordText));
        OnPropertyChanged(nameof(PrivateKeyText));
        OnPropertyChanged(nameof(PromptPasswordText));
        OnPropertyChanged(nameof(PasswordNotSavedText));
        OnPropertyChanged(nameof(PasswordSavedEncryptedText));
        OnPropertyChanged(nameof(PrivateKeyPathText));
        OnPropertyChanged(nameof(SshTunnelTitleText));
        OnPropertyChanged(nameof(VncSshTunnelTitleText));
        OnPropertyChanged(nameof(UseVncSshTunnelText));
        OnPropertyChanged(nameof(VncSshTunnelDescriptionText));
        OnPropertyChanged(nameof(UseRdpSshTunnelText));
        OnPropertyChanged(nameof(RdpSshTunnelDescriptionText));
        OnPropertyChanged(nameof(SshHostText));
        OnPropertyChanged(nameof(SshPortText));
        OnPropertyChanged(nameof(SshUsernameText));
        OnPropertyChanged(nameof(SshPasswordText));
        OnPropertyChanged(nameof(SshPrivateKeyText));
        OnPropertyChanged(nameof(BrowseText));
        OnPropertyChanged(nameof(ReconnectText));
        OnPropertyChanged(nameof(AutoReconnectText));
        OnPropertyChanged(nameof(IntervalText));
        OnPropertyChanged(nameof(LimitText));
        OnPropertyChanged(nameof(SecondsText));
        OnPropertyChanged(nameof(MinutesText));
        OnPropertyChanged(nameof(SaveText));
        OnPropertyChanged(nameof(ConnectText));
        OnPropertyChanged(nameof(CancelText));
        OnPropertyChanged(nameof(PlaceholderText));
    }

    private void RefreshLocalizedOptionHeaders()
    {
        SetOptionHeader(ProxyOptions, "None", "Option.None");
        SetOptionHeader(TerminalEncodingOptions, "default", "Option.DefaultLanguage");
        SetOptionHeader(TerminalKeyboardFunctionKeyOptions, "Default", "Option.Default");
        SetOptionHeader(TerminalKeyboardFunctionKeyOptions, "UserCustom", "Option.UserCustom");
        SetOptionHeader(TerminalVtCursorKeyModeOptions, "Normal", "Option.Normal");
        SetOptionHeader(TerminalVtCursorKeyModeOptions, "Application", "Option.Application");
        SetOptionHeader(TerminalVtNumericKeypadModeOptions, "Normal", "Option.Normal");
        SetOptionHeader(TerminalVtNumericKeypadModeOptions, "Application", "Option.Application");
        SetOptionHeader(TerminalVtNumericKeypadModeOptions, "ForceNormal", "Option.ForceNormal");
        SetOptionHeader(AppearanceFontQualityOptions, "Default", "Option.Default");
        SetOptionHeader(AppearanceFontQualityOptions, "Draft", "Option.Draft");
        SetOptionHeader(AppearanceFontQualityOptions, "Proof", "Option.Proof");
        SetOptionHeader(AppearanceFontQualityOptions, "NonAntiAliased", "Option.NonAntiAliased");
        SetOptionHeader(AppearanceFontQualityOptions, "AntiAliased", "Option.AntiAliased");
        SetOptionHeader(AppearanceFontQualityOptions, "NaturalClearType", "Option.NaturalClearType");
        SetOptionHeader(AppearanceBoldTextModeOptions, "Color", "Option.BoldColor");
        SetOptionHeader(AppearanceBoldTextModeOptions, "Font", "Option.BoldFont");
        SetOptionHeader(AppearanceBoldTextModeOptions, "ColorAndFont", "Option.BoldColorAndFont");
        SetOptionHeader(AppearanceCursorShapeOptions, "Block", "Option.Block");
        SetOptionHeader(AppearanceCursorShapeOptions, "Vertical", "Option.Vertical");
        SetOptionHeader(AppearanceCursorShapeOptions, "Underline", "Option.Underline");
        SetOptionHeader(AppearanceBackgroundImagePositionOptions, "Center", "Option.Center");
        SetOptionHeader(AppearanceBackgroundImagePositionOptions, "Tile", "Option.Tile");
        SetOptionHeader(AppearanceBackgroundImagePositionOptions, "Stretch", "Option.Stretch");
        SetOptionHeader(AppearanceBackgroundImagePositionOptions, "TopLeft", "Option.TopLeft");
        SetOptionHeader(AppearanceBackgroundImagePositionOptions, "TopRight", "Option.TopRight");
        SetOptionHeader(AppearanceBackgroundImagePositionOptions, "BottomLeft", "Option.BottomLeft");
        SetOptionHeader(AppearanceBackgroundImagePositionOptions, "BottomRight", "Option.BottomRight");
        SetOptionHeader(SshVersionOptions, "Ssh2ThenSsh1", "Option.Ssh2ThenSsh1");
        SetOptionHeader(SshVersionOptions, "Ssh1ThenSsh2", "Option.Ssh1ThenSsh2");
        SetOptionHeader(SshVersionOptions, "Ssh2Only", "Option.Ssh2Only");
        SetOptionHeader(SshVersionOptions, "Ssh1Only", "Option.Ssh1Only");
        SetOptionHeader(SerialParityOptions, "None", "Option.None");
        SetOptionHeader(SerialParityOptions, "Odd", "Option.Odd");
        SetOptionHeader(SerialParityOptions, "Even", "Option.Even");
        SetOptionHeader(SerialParityOptions, "Mark", "Option.Mark");
        SetOptionHeader(SerialParityOptions, "Space", "Option.Space");
        SetOptionHeader(SerialFlowControlOptions, "None", "Option.None");
        SetOptionHeader(RdpResizeModeOptions, "NotUsed", "Option.NotUsed");
        SetOptionHeader(RdpResizeModeOptions, "SmartSizing", "Option.SmartSizing");
        SetOptionHeader(RdpResizeModeOptions, "SmartReconnect", "Option.SmartReconnect");
        SetOptionHeader(RdpResizeModeOptions, "LegacyReconnect", "Option.LegacyReconnect");
        SetOptionHeader(AdvancedIpVersionOptions, "Auto", "Option.Auto");

        OnPropertyChanged(nameof(ProxyOptions));
        OnPropertyChanged(nameof(TerminalEncodingOptions));
        OnPropertyChanged(nameof(TerminalKeyboardFunctionKeyOptions));
        OnPropertyChanged(nameof(TerminalVtCursorKeyModeOptions));
        OnPropertyChanged(nameof(TerminalVtNumericKeypadModeOptions));
        OnPropertyChanged(nameof(AppearanceFontQualityOptions));
        OnPropertyChanged(nameof(AppearanceBoldTextModeOptions));
        OnPropertyChanged(nameof(AppearanceCursorShapeOptions));
        OnPropertyChanged(nameof(AppearanceBackgroundImagePositionOptions));
        OnPropertyChanged(nameof(SshVersionOptions));
        OnPropertyChanged(nameof(SerialParityOptions));
        OnPropertyChanged(nameof(SerialFlowControlOptions));
        OnPropertyChanged(nameof(RdpResizeModeOptions));
        OnPropertyChanged(nameof(AdvancedIpVersionOptions));
    }

    private void SetOptionHeader(ObservableCollection<ISelectOption> options, string content, string key)
    {
        for (var i = 0; i < options.Count; i++)
        {
            if (!string.Equals(options[i].Content?.ToString(), content, StringComparison.OrdinalIgnoreCase))
                continue;

            options[i] = new SelectOption
            {
                Header = L.Text(key),
                Content = content
            };
            return;
        }
    }

    public SessionInfo? SavedSession { get; private set; }

    private readonly SessionInfo? _editingSession;

    public SessionEditViewModel()
    {
        DialogTitle = L.Text("SessionEdit.TitleNew");
        LocalizationService.Shared.LanguageChanged += (_, _) => RefreshLocalization();
        RefreshLocalizedOptionHeaders();
        LoadHighlightSets(null, "None");
        RefreshSerialPortOptions();
    }

    public SessionEditViewModel(SessionInfo session)
    {
        _editingSession = session;
        DialogTitle = L.Text("SessionEdit.TitleProperties");
        LocalizationService.Shared.LanguageChanged += (_, _) => RefreshLocalization();
        RefreshLocalizedOptionHeaders();
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
        Password = PasswordEncryptionService.Decrypt(session.Password);
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
        TerminalType = string.IsNullOrWhiteSpace(session.TerminalType) ? "xterm" : session.TerminalType;
        TerminalColumns = Math.Clamp(session.TerminalColumns, 20, 500);
        TerminalRows = Math.Clamp(session.TerminalRows, 5, 200);
        TerminalFixedSize = session.TerminalFixedSize;
        TerminalResetSizeOnConnect = session.TerminalResetSizeOnConnect;
        TerminalScrollbackSize = Math.Clamp(session.TerminalScrollbackSize, 0, 200000);
        TerminalPushClearedScreenToScrollback = session.TerminalPushClearedScreenToScrollback;
        TerminalEncoding = string.IsNullOrWhiteSpace(session.TerminalEncoding) ? "utf-8" : session.TerminalEncoding;
        TerminalTreatAmbiguousAsWide = session.TerminalTreatAmbiguousAsWide;
        TerminalSendLineEnding = string.IsNullOrWhiteSpace(session.TerminalSendLineEnding) ? "CR" : session.TerminalSendLineEnding;
        TerminalReceiveLineEnding = string.IsNullOrWhiteSpace(session.TerminalReceiveLineEnding) ? "AUTO" : session.TerminalReceiveLineEnding;
        TerminalKeyboardFunctionKeyMode = string.IsNullOrWhiteSpace(session.TerminalKeyboardFunctionKeyMode) ? "Default" : session.TerminalKeyboardFunctionKeyMode;
        TerminalKeyboardMappingFile = session.TerminalKeyboardMappingFile ?? string.Empty;
        TerminalDeleteKeySequence = string.IsNullOrWhiteSpace(session.TerminalDeleteKeySequence) ? "VT220" : session.TerminalDeleteKeySequence;
        TerminalBackspaceKeySequence = string.IsNullOrWhiteSpace(session.TerminalBackspaceKeySequence) ? "Backspace" : session.TerminalBackspaceKeySequence;
        TerminalLeftAltAsMeta = session.TerminalLeftAltAsMeta;
        TerminalRightAltAsMeta = session.TerminalRightAltAsMeta;
        TerminalCtrlAltAsAltGr = session.TerminalCtrlAltAsAltGr;
        TerminalVtAutoWrapMode = session.TerminalVtAutoWrapMode;
        TerminalVtOriginMode = session.TerminalVtOriginMode;
        TerminalVtReverseVideoMode = session.TerminalVtReverseVideoMode;
        TerminalVtNewLineMode = session.TerminalVtNewLineMode;
        TerminalVtInsertMode = session.TerminalVtInsertMode;
        TerminalVtEchoMode = session.TerminalVtEchoMode;
        TerminalVtCursorKeyMode = string.IsNullOrWhiteSpace(session.TerminalVtCursorKeyMode) ? "Normal" : session.TerminalVtCursorKeyMode;
        TerminalVtNumericKeypadMode = string.IsNullOrWhiteSpace(session.TerminalVtNumericKeypadMode) ? "Normal" : session.TerminalVtNumericKeypadMode;
        TerminalAdvancedUseApplicationCursorMode = session.TerminalAdvancedUseApplicationCursorMode;
        TerminalAdvancedShiftLimitsApplicationCursorMode = session.TerminalAdvancedShiftLimitsApplicationCursorMode;
        TerminalAdvancedClearScreenBackground = session.TerminalAdvancedClearScreenBackground;
        TerminalAdvancedScrollToBottomOnInputOutput = session.TerminalAdvancedScrollToBottomOnInputOutput;
        TerminalAdvancedSuspendScrollToBottomOnScrollLock = session.TerminalAdvancedSuspendScrollToBottomOnScrollLock;
        TerminalAdvancedScrollToBottomByKey = session.TerminalAdvancedScrollToBottomByKey;
        TerminalAdvancedDestructiveBackspace = session.TerminalAdvancedDestructiveBackspace;
        TerminalAdvancedDuplicateSessionCd = session.TerminalAdvancedDuplicateSessionCd;
        TerminalAdvancedPreinputString = session.TerminalAdvancedPreinputString ?? string.Empty;
        TerminalAdvancedUseRxvtHomeEnd = session.TerminalAdvancedUseRxvtHomeEnd;
        TerminalAdvancedDisableBlinkingText = session.TerminalAdvancedDisableBlinkingText;
        TerminalAdvancedDisableTitleChange = session.TerminalAdvancedDisableTitleChange;
        TerminalAdvancedDisableTerminalPrint = session.TerminalAdvancedDisableTerminalPrint;
        TerminalAdvancedDisableAlternateScreen = session.TerminalAdvancedDisableAlternateScreen;
        TerminalAdvancedIgnoreResizeRequest = session.TerminalAdvancedIgnoreResizeRequest;
        TerminalAdvancedAnswerback = string.IsNullOrEmpty(session.TerminalAdvancedAnswerback) ? "CxShell" : session.TerminalAdvancedAnswerback;
        TerminalAdvancedUseBuiltinLineDrawing = session.TerminalAdvancedUseBuiltinLineDrawing;
        TerminalAdvancedUseBuiltinPowerline = session.TerminalAdvancedUseBuiltinPowerline;
        AppearanceColorScheme = string.IsNullOrWhiteSpace(session.AppearanceColorScheme) ? "XTerm" : session.AppearanceColorScheme;
        AppearanceForegroundColor = ParseColorOrDefault(session.AppearanceForegroundColor, "#CCCCCC");
        AppearanceBoldForegroundColor = ParseColorOrDefault(session.AppearanceBoldForegroundColor, "#33FF33");
        AppearanceBackgroundColor = ParseColorOrDefault(session.AppearanceBackgroundColor, "#000000");
        AppearanceAnsiColors = string.IsNullOrWhiteSpace(session.AppearanceAnsiColors) ? new SessionInfo().AppearanceAnsiColors : session.AppearanceAnsiColors;
        AppearanceFontFamily = string.IsNullOrWhiteSpace(session.AppearanceFontFamily) ? "DejaVu Sans Mono" : session.AppearanceFontFamily;
        AppearanceFontStyle = string.IsNullOrWhiteSpace(session.AppearanceFontStyle) ? "Normal" : session.AppearanceFontStyle;
        AppearanceFontSize = Math.Clamp(session.AppearanceFontSize, 6, 96);
        AppearanceCjkFontFamily = string.IsNullOrWhiteSpace(session.AppearanceCjkFontFamily) ? AppearanceFontFamily : session.AppearanceCjkFontFamily;
        AppearanceCjkFontStyle = string.IsNullOrWhiteSpace(session.AppearanceCjkFontStyle) ? "Normal" : session.AppearanceCjkFontStyle;
        AppearanceCjkFontSize = Math.Clamp(session.AppearanceCjkFontSize, 6, 96);
        AppearanceUseVariablePitchFont = session.AppearanceUseVariablePitchFont;
        AppearanceFontQuality = string.IsNullOrWhiteSpace(session.AppearanceFontQuality) ? "Default" : session.AppearanceFontQuality;
        AppearanceBoldTextMode = string.IsNullOrWhiteSpace(session.AppearanceBoldTextMode) ? "ColorAndFont" : session.AppearanceBoldTextMode;
        AppearanceCursorColor = ParseColorOrDefault(session.AppearanceCursorColor, "#00FF00");
        AppearanceCursorTextColor = ParseColorOrDefault(session.AppearanceCursorTextColor, "#000000");
        AppearanceUseBlinkingCursor = session.AppearanceUseBlinkingCursor;
        AppearanceCursorBlinkSpeedMilliseconds = Math.Clamp(session.AppearanceCursorBlinkSpeedMilliseconds, 1, 5000);
        AppearanceCursorShape = string.IsNullOrWhiteSpace(session.AppearanceCursorShape) ? "Block" : session.AppearanceCursorShape;
        AppearanceWindowPaddingTop = Math.Clamp(session.AppearanceWindowPaddingTop, 0, 200);
        AppearanceWindowPaddingBottom = Math.Clamp(session.AppearanceWindowPaddingBottom, 0, 200);
        AppearanceWindowPaddingLeft = Math.Clamp(session.AppearanceWindowPaddingLeft, 0, 200);
        AppearanceWindowPaddingRight = Math.Clamp(session.AppearanceWindowPaddingRight, 0, 200);
        AppearanceLineSpacing = Math.Clamp(session.AppearanceLineSpacing, -5, 32);
        AppearanceCharacterSpacing = Math.Clamp(session.AppearanceCharacterSpacing, -5, 32);
        AppearanceTabColorMode = string.IsNullOrWhiteSpace(session.AppearanceTabColorMode) ? "Default" : session.AppearanceTabColorMode;
        AppearanceTabCustomColor = ParseColorOrDefault(session.AppearanceTabCustomColor, "#000000");
        AppearanceBackgroundImagePath = session.AppearanceBackgroundImagePath ?? string.Empty;
        AppearanceBackgroundImagePosition = string.IsNullOrWhiteSpace(session.AppearanceBackgroundImagePosition)
            ? "Center"
            : session.AppearanceBackgroundImagePosition;
        LoadHighlightSets(session.AppearanceHighlightSets, session.AppearanceHighlightSetId);
        AdvancedQuickCommandSet = string.IsNullOrWhiteSpace(session.AdvancedQuickCommandSet) ? "<<所有命令>>" : session.AdvancedQuickCommandSet;
        AdvancedDisableQuickCommandShortcuts = session.AdvancedDisableQuickCommandShortcuts;
        AdvancedFtpPort = Math.Clamp(session.AdvancedFtpPort <= 0 ? 21 : session.AdvancedFtpPort, 1, 65535);
        AdvancedCharacterDelayMilliseconds = Math.Clamp(session.AdvancedCharacterDelayMilliseconds, 0, 60000);
        AdvancedUseLineDelay = session.AdvancedUseLineDelay;
        AdvancedLineDelayMilliseconds = Math.Clamp(session.AdvancedLineDelayMilliseconds, 0, 60000);
        AdvancedUsePromptDelay = session.AdvancedUsePromptDelay;
        AdvancedPromptText = session.AdvancedPromptText ?? string.Empty;
        AdvancedPromptMaxWaitMilliseconds = Math.Clamp(session.AdvancedPromptMaxWaitMilliseconds, 0, 600000);
        AdvancedUseNagle = session.AdvancedUseNagle;
        AdvancedIpVersion = string.IsNullOrWhiteSpace(session.AdvancedIpVersion) ? "Auto" : session.AdvancedIpVersion;
        AdvancedTraceSshProtocol = session.AdvancedTraceSshProtocol;
        AdvancedTraceSshTunneling = session.AdvancedTraceSshTunneling;
        AdvancedTraceSshPackets = session.AdvancedTraceSshPackets;
        AdvancedTraceTelnetOptions = session.AdvancedTraceTelnetOptions;
        AdvancedBellMode = string.IsNullOrWhiteSpace(session.AdvancedBellMode) ? "Default" : session.AdvancedBellMode;
        AdvancedBellSoundPath = session.AdvancedBellSoundPath ?? string.Empty;
        AdvancedBellFlashInactiveWindow = session.AdvancedBellFlashInactiveWindow;
        AdvancedBellIgnoreRepeatedSeconds = Math.Clamp(session.AdvancedBellIgnoreRepeatedSeconds <= 0 ? 3 : session.AdvancedBellIgnoreRepeatedSeconds, 1, 3600);
        AdvancedBellReactivateAfterSeconds = Math.Clamp(session.AdvancedBellReactivateAfterSeconds <= 0 ? 3 : session.AdvancedBellReactivateAfterSeconds, 1, 3600);
        AdvancedLogFilePath = string.IsNullOrWhiteSpace(session.AdvancedLogFilePath) ? "%n_%Y-%m-%d_%t.log" : session.AdvancedLogFilePath;
        AdvancedLogOverwriteExisting = session.AdvancedLogOverwriteExisting;
        AdvancedLogStartOnConnect = session.AdvancedLogStartOnConnect;
        AdvancedLogPromptFileOnStart = session.AdvancedLogPromptFileOnStart;
        AdvancedLogUseRtf = session.AdvancedLogUseRtf;
        AdvancedLogIncludeTerminalCodes = session.AdvancedLogIncludeTerminalCodes;
        AdvancedLogEncoding = string.IsNullOrWhiteSpace(session.AdvancedLogEncoding) ? "Utf16Le" : session.AdvancedLogEncoding;
        AdvancedLogWriteTimestamp = session.AdvancedLogWriteTimestamp;
        AdvancedLogTimestampFormat = string.IsNullOrWhiteSpace(session.AdvancedLogTimestampFormat) ? "[%a]" : session.AdvancedLogTimestampFormat;
        RefreshAdvancedLogPreviews();
        EnableLoginScriptRules = session.EnableLoginScriptRules;
        foreach (var rule in session.LoginScriptRules.OrderBy(rule => rule.SortOrder))
            LoginScriptRules.Add(CloneLoginScriptRule(rule));
        RunLoginScriptFile = session.RunLoginScriptFile;
        LoginScriptFilePath = session.LoginScriptFilePath ?? string.Empty;
        LoginScriptParameters = session.LoginScriptParameters ?? string.Empty;
        SshRemoteCommand = session.SshRemoteCommand ?? string.Empty;
        SshVersionPolicy = string.IsNullOrWhiteSpace(session.SshVersionPolicy) ? "Ssh2Only" : session.SshVersionPolicy;
        SshUseXagent = session.SshUseXagent;
        SshForwardAgent = session.SshForwardAgent;
        SshUseCompression = session.SshUseCompression;
        SshNoTerminal = session.SshNoTerminal;
        SshAcceptAndSaveHostKey = session.SshAcceptAndSaveHostKey;
        SshAutoOpenSftpPanel = session.SshAutoOpenSftpPanel;
        SshAutoOpenMonitorPanel = session.SshAutoOpenMonitorPanel;
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
        SftpFollowTerminalDirectory = session.SftpFollowTerminalDirectory;
        SftpUseCustomServer = session.SftpUseCustomServer;
        SftpCustomServerCommand = session.SftpCustomServerCommand ?? string.Empty;
        FileTransferAlwaysAskDownloadFolder = session.FileTransferAlwaysAskDownloadFolder;
        FileTransferDownloadDirectory = session.FileTransferDownloadDirectory ?? string.Empty;
        FileTransferUploadDirectory = session.FileTransferUploadDirectory ?? string.Empty;
        FileTransferDuplicateAction = string.IsNullOrWhiteSpace(session.FileTransferDuplicateAction) ? "AutoRename" : session.FileTransferDuplicateAction;
        FileTransferUploadProtocol = string.IsNullOrWhiteSpace(session.FileTransferUploadProtocol) ? "Zmodem" : session.FileTransferUploadProtocol;
        FileTransferXymodemBlockSize = session.FileTransferXymodemBlockSize == 1024 ? 1024 : 128;
        FileTransferXmodemUploadCommand = string.IsNullOrWhiteSpace(session.FileTransferXmodemUploadCommand) ? "rx" : session.FileTransferXmodemUploadCommand;
        FileTransferYmodemUploadCommand = string.IsNullOrWhiteSpace(session.FileTransferYmodemUploadCommand) ? "rb -E" : session.FileTransferYmodemUploadCommand;
        FileTransferZmodemAutoActivate = session.FileTransferZmodemAutoActivate;
        FileTransferZmodemUploadCommand = string.IsNullOrWhiteSpace(session.FileTransferZmodemUploadCommand) ? "rz -E" : session.FileTransferZmodemUploadCommand;
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
        RdpUseSshTunnel = session.RdpUseSshTunnel;
        RdpSshHost = string.IsNullOrWhiteSpace(session.RdpSshHost) ? session.Host : session.RdpSshHost;
        RdpSshPort = Math.Clamp(session.RdpSshPort <= 0 ? 22 : session.RdpSshPort, 1, 65535).ToString();
        RdpSshUsername = session.RdpSshUsername ?? string.Empty;
        RdpSshPassword = PasswordEncryptionService.Decrypt(session.RdpSshPassword);
        RdpSshUsePrivateKey = session.RdpSshUsePrivateKey;
        RdpSshPrivateKeyPath = session.RdpSshPrivateKeyPath ?? string.Empty;
        VncUseSshTunnel = session.VncUseSshTunnel;
        VncSshHost = string.IsNullOrWhiteSpace(session.VncSshHost) ? session.Host : session.VncSshHost;
        VncSshPort = Math.Clamp(session.VncSshPort <= 0 ? 22 : session.VncSshPort, 1, 65535).ToString();
        VncSshUsername = session.VncSshUsername ?? string.Empty;
        VncSshPassword = PasswordEncryptionService.Decrypt(session.VncSshPassword);
        VncSshUsePrivateKey = session.VncSshUsePrivateKey;
        VncSshPrivateKeyPath = session.VncSshPrivateKeyPath ?? string.Empty;
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
    private async Task BrowseVncSshKey()
    {
        var lifetime = Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime;
        var topLevel = TopLevel.GetTopLevel(lifetime?.MainWindow);
        if (topLevel == null) return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select SSH private key file",
            AllowMultiple = false
        });

        if (files.Count > 0)
            VncSshPrivateKeyPath = files[0].Path.LocalPath;
    }

    [RelayCommand]
    private async Task BrowseRdpSshKey()
    {
        var lifetime = Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime;
        var topLevel = TopLevel.GetTopLevel(lifetime?.MainWindow);
        if (topLevel == null) return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select SSH private key file",
            AllowMultiple = false
        });

        if (files.Count > 0)
            RdpSshPrivateKeyPath = files[0].Path.LocalPath;
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
        session.Password = IsPasswordAuth ? PasswordEncryptionService.Encrypt(Password) : string.Empty;
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
        session.AdvancedTraceSshProtocol = AdvancedTraceSshProtocol;
        session.AdvancedTraceSshTunneling = AdvancedTraceSshTunneling;
        session.AdvancedTraceSshPackets = AdvancedTraceSshPackets;
        session.AdvancedTraceTelnetOptions = AdvancedTraceTelnetOptions;
        session.EnableLoginScriptRules = EnableLoginScriptRules;
        session.LoginScriptRules = LoginScriptRules
            .Select((rule, index) =>
            {
                var clone = CloneLoginScriptRule(rule);
                clone.SortOrder = index;
                return clone;
            })
            .ToList();
        session.RunLoginScriptFile = RunLoginScriptFile;
        session.LoginScriptFilePath = LoginScriptFilePath.Trim();
        session.LoginScriptParameters = LoginScriptParameters.Trim();
        session.SshRemoteCommand = SshRemoteCommand.Trim();
        session.SshVersionPolicy = string.IsNullOrWhiteSpace(SshVersionPolicy) ? "Ssh2Only" : SshVersionPolicy;
        session.SshUseXagent = SshUseXagent;
        session.SshForwardAgent = SshForwardAgent;
        session.SshUseCompression = SshUseCompression;
        session.SshNoTerminal = SshNoTerminal;
        session.SshAcceptAndSaveHostKey = SshAcceptAndSaveHostKey;
        session.SshAutoOpenSftpPanel = SshAutoOpenSftpPanel;
        session.SshAutoOpenMonitorPanel = SshAutoOpenMonitorPanel;
        session.SshDoNotStartFileManager = !SshAutoOpenSftpPanel;
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
        session.SftpFollowTerminalDirectory = SftpFollowTerminalDirectory;
        session.SftpUseCustomServer = SftpUseCustomServer;
        session.SftpCustomServerCommand = SftpCustomServerCommand.Trim();
        session.SerialPortName = SerialPortName.Trim();
        session.SerialBaudRate = int.TryParse(SerialBaudRate, out var serialBaudRate) ? serialBaudRate : 115200;
        session.SerialDataBits = int.TryParse(SerialDataBits, out var serialDataBits) ? serialDataBits : 8;
        session.SerialStopBits = SerialStopBits;
        session.SerialParity = SerialParity;
        session.SerialFlowControl = SerialFlowControl;
        session.TerminalType = string.IsNullOrWhiteSpace(TerminalType) ? "xterm" : TerminalType;
        session.TerminalColumns = Math.Clamp((int)TerminalColumns, 20, 500);
        session.TerminalRows = Math.Clamp((int)TerminalRows, 5, 200);
        session.TerminalFixedSize = TerminalFixedSize;
        session.TerminalResetSizeOnConnect = TerminalResetSizeOnConnect;
        session.TerminalScrollbackSize = Math.Clamp((int)TerminalScrollbackSize, 0, 200000);
        session.TerminalPushClearedScreenToScrollback = TerminalPushClearedScreenToScrollback;
        session.TerminalEncoding = string.IsNullOrWhiteSpace(TerminalEncoding) ? "utf-8" : TerminalEncoding;
        session.TerminalTreatAmbiguousAsWide = TerminalTreatAmbiguousAsWide;
        session.TerminalSendLineEnding = string.IsNullOrWhiteSpace(TerminalSendLineEnding) ? "CR" : TerminalSendLineEnding;
        session.TerminalReceiveLineEnding = string.IsNullOrWhiteSpace(TerminalReceiveLineEnding) ? "AUTO" : TerminalReceiveLineEnding;
        session.TerminalKeyboardFunctionKeyMode = string.IsNullOrWhiteSpace(TerminalKeyboardFunctionKeyMode) ? "Default" : TerminalKeyboardFunctionKeyMode;
        session.TerminalKeyboardMappingFile = TerminalKeyboardMappingFile.Trim();
        session.TerminalDeleteKeySequence = string.IsNullOrWhiteSpace(TerminalDeleteKeySequence) ? "VT220" : TerminalDeleteKeySequence;
        session.TerminalBackspaceKeySequence = string.IsNullOrWhiteSpace(TerminalBackspaceKeySequence) ? "Backspace" : TerminalBackspaceKeySequence;
        session.TerminalLeftAltAsMeta = TerminalLeftAltAsMeta;
        session.TerminalRightAltAsMeta = TerminalRightAltAsMeta;
        session.TerminalCtrlAltAsAltGr = TerminalCtrlAltAsAltGr;
        session.TerminalVtAutoWrapMode = TerminalVtAutoWrapMode;
        session.TerminalVtOriginMode = TerminalVtOriginMode;
        session.TerminalVtReverseVideoMode = TerminalVtReverseVideoMode;
        session.TerminalVtNewLineMode = TerminalVtNewLineMode;
        session.TerminalVtInsertMode = TerminalVtInsertMode;
        session.TerminalVtEchoMode = TerminalVtEchoMode;
        session.TerminalVtCursorKeyMode = TerminalVtCursorKeyMode;
        session.TerminalVtNumericKeypadMode = TerminalVtNumericKeypadMode;
        session.TerminalAdvancedUseApplicationCursorMode = TerminalAdvancedUseApplicationCursorMode;
        session.TerminalAdvancedShiftLimitsApplicationCursorMode = TerminalAdvancedShiftLimitsApplicationCursorMode;
        session.TerminalAdvancedClearScreenBackground = TerminalAdvancedClearScreenBackground;
        session.TerminalAdvancedScrollToBottomOnInputOutput = TerminalAdvancedScrollToBottomOnInputOutput;
        session.TerminalAdvancedSuspendScrollToBottomOnScrollLock = TerminalAdvancedSuspendScrollToBottomOnScrollLock;
        session.TerminalAdvancedScrollToBottomByKey = TerminalAdvancedScrollToBottomByKey;
        session.TerminalAdvancedDestructiveBackspace = TerminalAdvancedDestructiveBackspace;
        session.TerminalAdvancedDuplicateSessionCd = TerminalAdvancedDuplicateSessionCd;
        session.TerminalAdvancedPreinputString = TerminalAdvancedPreinputString.Trim();
        session.TerminalAdvancedUseRxvtHomeEnd = TerminalAdvancedUseRxvtHomeEnd;
        session.TerminalAdvancedDisableBlinkingText = TerminalAdvancedDisableBlinkingText;
        session.TerminalAdvancedDisableTitleChange = TerminalAdvancedDisableTitleChange;
        session.TerminalAdvancedDisableTerminalPrint = TerminalAdvancedDisableTerminalPrint;
        session.TerminalAdvancedDisableAlternateScreen = TerminalAdvancedDisableAlternateScreen;
        session.TerminalAdvancedIgnoreResizeRequest = TerminalAdvancedIgnoreResizeRequest;
        session.TerminalAdvancedAnswerback = string.IsNullOrWhiteSpace(TerminalAdvancedAnswerback) ? "CxShell" : TerminalAdvancedAnswerback.Trim();
        session.TerminalAdvancedUseBuiltinLineDrawing = TerminalAdvancedUseBuiltinLineDrawing;
        session.TerminalAdvancedUseBuiltinPowerline = TerminalAdvancedUseBuiltinPowerline;
        session.AppearanceColorScheme = string.IsNullOrWhiteSpace(AppearanceColorScheme) ? "XTerm" : AppearanceColorScheme;
        session.AppearanceForegroundColor = ToHex(AppearanceForegroundColor);
        session.AppearanceBoldForegroundColor = ToHex(AppearanceBoldForegroundColor);
        session.AppearanceBackgroundColor = ToHex(AppearanceBackgroundColor);
        session.AppearanceAnsiColors = string.IsNullOrWhiteSpace(AppearanceAnsiColors)
            ? new SessionInfo().AppearanceAnsiColors
            : AppearanceAnsiColors;
        session.AppearanceFontFamily = string.IsNullOrWhiteSpace(AppearanceFontFamily) ? "DejaVu Sans Mono" : AppearanceFontFamily;
        session.AppearanceFontStyle = string.IsNullOrWhiteSpace(AppearanceFontStyle) ? "Normal" : AppearanceFontStyle;
        session.AppearanceFontSize = Math.Clamp((int)AppearanceFontSize, 6, 96);
        session.AppearanceCjkFontFamily = string.IsNullOrWhiteSpace(AppearanceCjkFontFamily)
            ? session.AppearanceFontFamily
            : AppearanceCjkFontFamily;
        session.AppearanceCjkFontStyle = string.IsNullOrWhiteSpace(AppearanceCjkFontStyle) ? "Normal" : AppearanceCjkFontStyle;
        session.AppearanceCjkFontSize = Math.Clamp((int)AppearanceCjkFontSize, 6, 96);
        session.AppearanceUseVariablePitchFont = AppearanceUseVariablePitchFont;
        session.AppearanceFontQuality = string.IsNullOrWhiteSpace(AppearanceFontQuality) ? "Default" : AppearanceFontQuality;
        session.AppearanceBoldTextMode = string.IsNullOrWhiteSpace(AppearanceBoldTextMode) ? "ColorAndFont" : AppearanceBoldTextMode;
        session.AppearanceCursorColor = ToHex(AppearanceCursorColor);
        session.AppearanceCursorTextColor = ToHex(AppearanceCursorTextColor);
        session.AppearanceUseBlinkingCursor = AppearanceUseBlinkingCursor;
        session.AppearanceCursorBlinkSpeedMilliseconds = Math.Clamp((int)AppearanceCursorBlinkSpeedMilliseconds, 1, 5000);
        session.AppearanceCursorShape = string.IsNullOrWhiteSpace(AppearanceCursorShape) ? "Block" : AppearanceCursorShape;
        session.AppearanceWindowPaddingTop = Math.Clamp((int)AppearanceWindowPaddingTop, 0, 200);
        session.AppearanceWindowPaddingBottom = Math.Clamp((int)AppearanceWindowPaddingBottom, 0, 200);
        session.AppearanceWindowPaddingLeft = Math.Clamp((int)AppearanceWindowPaddingLeft, 0, 200);
        session.AppearanceWindowPaddingRight = Math.Clamp((int)AppearanceWindowPaddingRight, 0, 200);
        session.AppearanceLineSpacing = Math.Clamp((int)AppearanceLineSpacing, -5, 32);
        session.AppearanceCharacterSpacing = Math.Clamp((int)AppearanceCharacterSpacing, -5, 32);
        session.AppearanceTabColorMode = string.IsNullOrWhiteSpace(AppearanceTabColorMode) ? "Default" : AppearanceTabColorMode;
        session.AppearanceTabCustomColor = ToHex(AppearanceTabCustomColor);
        session.AppearanceBackgroundImagePath = AppearanceBackgroundImagePath.Trim();
        session.AppearanceBackgroundImagePosition = string.IsNullOrWhiteSpace(AppearanceBackgroundImagePosition)
            ? "Center"
            : AppearanceBackgroundImagePosition;
        session.AppearanceHighlightSetId = string.IsNullOrWhiteSpace(AppearanceHighlightSetId)
            ? "None"
            : AppearanceHighlightSetId;
        session.AppearanceHighlightSets = new ObservableCollection<HighlightSet>(
            AppearanceHighlightSets.Select(CloneHighlightSet));
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
        session.RdpUseSshTunnel = RdpUseSshTunnel;
        session.RdpSshHost = string.IsNullOrWhiteSpace(RdpSshHost) ? session.Host : RdpSshHost.Trim();
        session.RdpSshPort = int.TryParse(RdpSshPort, out var rdpSshPort) ? Math.Clamp(rdpSshPort, 1, 65535) : 22;
        session.RdpSshUsername = RdpSshUsername.Trim();
        session.RdpSshPassword = RdpSshUsePrivateKey ? string.Empty : PasswordEncryptionService.Encrypt(RdpSshPassword);
        session.RdpSshUsePrivateKey = RdpSshUsePrivateKey;
        session.RdpSshPrivateKeyPath = RdpSshUsePrivateKey ? RdpSshPrivateKeyPath.Trim() : string.Empty;
        session.VncUseSshTunnel = VncUseSshTunnel;
        session.VncSshHost = string.IsNullOrWhiteSpace(VncSshHost) ? session.Host : VncSshHost.Trim();
        session.VncSshPort = int.TryParse(VncSshPort, out var vncSshPort) ? Math.Clamp(vncSshPort, 1, 65535) : 22;
        session.VncSshUsername = VncSshUsername.Trim();
        session.VncSshPassword = VncSshUsePrivateKey ? string.Empty : PasswordEncryptionService.Encrypt(VncSshPassword);
        session.VncSshUsePrivateKey = VncSshUsePrivateKey;
        session.VncSshPrivateKeyPath = VncSshUsePrivateKey ? VncSshPrivateKeyPath.Trim() : string.Empty;
        session.VncSshRemoteHost = session.Host;
        session.VncSshRemotePort = session.Port;
        session.FileTransferAlwaysAskDownloadFolder = FileTransferAlwaysAskDownloadFolder;
        session.FileTransferDownloadDirectory = FileTransferDownloadDirectory.Trim();
        session.FileTransferUploadDirectory = FileTransferUploadDirectory.Trim();
        session.FileTransferDuplicateAction = string.IsNullOrWhiteSpace(FileTransferDuplicateAction)
            ? "AutoRename"
            : FileTransferDuplicateAction;
        session.FileTransferUploadProtocol = string.IsNullOrWhiteSpace(FileTransferUploadProtocol)
            ? "Zmodem"
            : FileTransferUploadProtocol;
        session.FileTransferXymodemBlockSize = FileTransferXymodemBlockSize == 1024 ? 1024 : 128;
        session.FileTransferXmodemUploadCommand = string.IsNullOrWhiteSpace(FileTransferXmodemUploadCommand) ? "rx" : FileTransferXmodemUploadCommand.Trim();
        session.FileTransferYmodemUploadCommand = string.IsNullOrWhiteSpace(FileTransferYmodemUploadCommand) ? "rb -E" : FileTransferYmodemUploadCommand.Trim();
        session.FileTransferZmodemAutoActivate = FileTransferZmodemAutoActivate;
        session.FileTransferZmodemUploadCommand = string.IsNullOrWhiteSpace(FileTransferZmodemUploadCommand) ? "rz -E" : FileTransferZmodemUploadCommand.Trim();
        session.AdvancedQuickCommandSet = string.IsNullOrWhiteSpace(AdvancedQuickCommandSet) ? "<<所有命令>>" : AdvancedQuickCommandSet.Trim();
        session.AdvancedDisableQuickCommandShortcuts = AdvancedDisableQuickCommandShortcuts;
        session.AdvancedFtpPort = Math.Clamp((int)AdvancedFtpPort, 1, 65535);
        session.AdvancedCharacterDelayMilliseconds = Math.Clamp((int)AdvancedCharacterDelayMilliseconds, 0, 60000);
        session.AdvancedUseLineDelay = AdvancedUseLineDelay;
        session.AdvancedLineDelayMilliseconds = Math.Clamp((int)AdvancedLineDelayMilliseconds, 0, 60000);
        session.AdvancedUsePromptDelay = AdvancedUsePromptDelay;
        session.AdvancedPromptText = AdvancedPromptText.Trim();
        session.AdvancedPromptMaxWaitMilliseconds = Math.Clamp((int)AdvancedPromptMaxWaitMilliseconds, 0, 600000);
        session.AdvancedUseNagle = AdvancedUseNagle;
        session.AdvancedIpVersion = string.IsNullOrWhiteSpace(AdvancedIpVersion) ? "Auto" : AdvancedIpVersion;
        session.AdvancedBellMode = string.IsNullOrWhiteSpace(AdvancedBellMode) ? "Default" : AdvancedBellMode;
        session.AdvancedBellSoundPath = AdvancedBellSoundPath.Trim();
        session.AdvancedBellFlashInactiveWindow = AdvancedBellFlashInactiveWindow;
        session.AdvancedBellIgnoreRepeatedSeconds = Math.Clamp((int)AdvancedBellIgnoreRepeatedSeconds, 1, 3600);
        session.AdvancedBellReactivateAfterSeconds = Math.Clamp((int)AdvancedBellReactivateAfterSeconds, 1, 3600);
        session.AdvancedLogFilePath = string.IsNullOrWhiteSpace(AdvancedLogFilePath) ? "%n_%Y-%m-%d_%t.log" : AdvancedLogFilePath.Trim();
        session.AdvancedLogOverwriteExisting = AdvancedLogOverwriteExisting;
        session.AdvancedLogStartOnConnect = AdvancedLogStartOnConnect;
        session.AdvancedLogPromptFileOnStart = AdvancedLogPromptFileOnStart;
        session.AdvancedLogUseRtf = AdvancedLogUseRtf;
        session.AdvancedLogIncludeTerminalCodes = AdvancedLogIncludeTerminalCodes;
        session.AdvancedLogEncoding = string.IsNullOrWhiteSpace(AdvancedLogEncoding) ? "Utf16Le" : AdvancedLogEncoding;
        session.AdvancedLogWriteTimestamp = AdvancedLogWriteTimestamp;
        session.AdvancedLogTimestampFormat = string.IsNullOrWhiteSpace(AdvancedLogTimestampFormat) ? "[%a]" : AdvancedLogTimestampFormat.Trim();

        SavedSession = session;
    }

    partial void OnSessionNameChanged(string value)
    {
        if (!string.IsNullOrWhiteSpace(value) && IsNameInvalid)
            NameStatus = InputControlStatus.Default;
        RefreshAdvancedLogPreviews();
        ClearValidationMessageIfResolved();
    }

    partial void OnHostChanged(string value)
    {
        if (!string.IsNullOrWhiteSpace(value) && IsHostInvalid)
            HostStatus = InputControlStatus.Default;
        RefreshAdvancedLogPreviews();
        ClearValidationMessageIfResolved();
    }

    partial void OnPortChanged(string value)
    {
        if (IsValidPortText(value) && IsPortInvalid)
            PortStatus = InputControlStatus.Default;
        ClearValidationMessageIfResolved();
    }

    partial void OnProtocolChanged(string value)
    {
        OnPropertyChanged(nameof(IsVncProtocol));
        OnPropertyChanged(nameof(IsRdpProtocol));
    }

    partial void OnVncSshUsePrivateKeyChanged(bool value)
    {
        OnPropertyChanged(nameof(IsVncSshPasswordAuth));
        OnPropertyChanged(nameof(IsVncSshPrivateKeyAuth));
    }

    partial void OnRdpSshUsePrivateKeyChanged(bool value)
    {
        OnPropertyChanged(nameof(IsRdpSshPasswordAuth));
        OnPropertyChanged(nameof(IsRdpSshPrivateKeyAuth));
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

    partial void OnSelectedLoginScriptRuleChanged(LoginScriptRule? value)
    {
        OnPropertyChanged(nameof(HasSelectedLoginScriptRule));
    }

    partial void OnSelectedHighlightSetChanged(HighlightSet? value)
    {
        OnPropertyChanged(nameof(HasSelectedHighlightSet));
        if (value != null)
            AppearanceHighlightSetId = value.Id.ToString();
        SelectedHighlightRule = null;
    }

    partial void OnSelectedHighlightRuleChanged(HighlightRule? value)
    {
        OnPropertyChanged(nameof(HasSelectedHighlightRule));
    }

    partial void OnSelectedPageChanged(string value)
    {
        OnPropertyChanged(nameof(IsTerminalPage));
        OnPropertyChanged(nameof(IsAppearancePage));
        OnPropertyChanged(nameof(IsAppearanceWindowPage));
        OnPropertyChanged(nameof(IsAppearanceHighlightPage));
        OnPropertyChanged(nameof(IsKeyboardPage));
        OnPropertyChanged(nameof(IsVtModePage));
        OnPropertyChanged(nameof(IsTerminalAdvancedPage));
        OnPropertyChanged(nameof(IsTransferPage));
        OnPropertyChanged(nameof(IsFileTransferXymodemPage));
        OnPropertyChanged(nameof(IsFileTransferZmodemPage));
        OnPropertyChanged(nameof(IsLoggingPage));
        OnPropertyChanged(nameof(IsBellPage));
        OnPropertyChanged(nameof(IsAdvancedPage));
        OnPropertyChanged(nameof(IsTracingPage));
    }

    partial void OnAppearanceHighlightSetIdChanged(string value)
    {
        SelectedHighlightSet = AppearanceHighlightSets.FirstOrDefault(set =>
            string.Equals(set.Id.ToString(), value, StringComparison.OrdinalIgnoreCase));
    }

    partial void OnAdvancedUseLineDelayChanged(bool value)
    {
        if (value)
            AdvancedUsePromptDelay = false;
        OnPropertyChanged(nameof(IsAdvancedLineDelay));
        OnPropertyChanged(nameof(IsAdvancedPromptDelay));
    }

    partial void OnAdvancedUsePromptDelayChanged(bool value)
    {
        if (value)
            AdvancedUseLineDelay = false;
        OnPropertyChanged(nameof(IsAdvancedLineDelay));
        OnPropertyChanged(nameof(IsAdvancedPromptDelay));
    }

    partial void OnAdvancedIpVersionChanged(string value)
    {
        OnPropertyChanged(nameof(IsAdvancedIpVersionAuto));
        OnPropertyChanged(nameof(IsAdvancedIpVersion4));
        OnPropertyChanged(nameof(IsAdvancedIpVersion6));
    }

    partial void OnAdvancedBellModeChanged(string value)
    {
        OnPropertyChanged(nameof(IsAdvancedBellModeNone));
        OnPropertyChanged(nameof(IsAdvancedBellModeDefault));
        OnPropertyChanged(nameof(IsAdvancedBellModeBuiltin));
        OnPropertyChanged(nameof(IsAdvancedBellModeSound));
        OnPropertyChanged(nameof(IsAdvancedBellSoundPathEnabled));
        OnPropertyChanged(nameof(IsAdvancedBellFlashInactiveWindowEnabled));
    }

    partial void OnAdvancedLogStartOnConnectChanged(bool value)
    {
        OnPropertyChanged(nameof(IsAdvancedLogPromptFileOnStartEnabled));
    }

    partial void OnAdvancedLogWriteTimestampChanged(bool value)
    {
        OnPropertyChanged(nameof(IsAdvancedLogTimestampSettingsEnabled));
    }

    partial void OnAdvancedLogTimestampFormatChanged(string value)
    {
        RefreshAdvancedLogPreviews();
    }

    partial void OnAdvancedLogFilePathChanged(string value)
    {
        RefreshAdvancedLogPreviews();
    }

    partial void OnUsernameChanged(string value)
    {
        RefreshAdvancedLogPreviews();
    }

    private void RefreshAdvancedLogPreviews()
    {
        var previewSession = new SessionInfo
        {
            Name = string.IsNullOrWhiteSpace(SessionName) ? "新建会话" : SessionName,
            Username = string.IsNullOrWhiteSpace(Username) ? "user" : Username,
            Host = string.IsNullOrWhiteSpace(Host) ? "host.example.com" : Host
        };
        var now = DateTime.Now;
        AdvancedLogFilePathPreview = SessionLogWriter.ExpandTemplate(
            string.IsNullOrWhiteSpace(AdvancedLogFilePath) ? "%n_%Y-%m-%d_%t.log" : AdvancedLogFilePath,
            previewSession,
            now,
            1);
        AdvancedLogTimestampPreview = SessionLogWriter.ExpandTemplate(
            string.IsNullOrWhiteSpace(AdvancedLogTimestampFormat) ? "[%a]" : AdvancedLogTimestampFormat,
            previewSession,
            now,
            1);
    }

    partial void OnSftpUseCustomServerChanged(bool value)
    {
        OnPropertyChanged(nameof(IsSftpCustomServerCommandEnabled));
    }

    partial void OnFileTransferAlwaysAskDownloadFolderChanged(bool value)
    {
        OnPropertyChanged(nameof(IsFileTransferPathSettingsEnabled));
        OnPropertyChanged(nameof(IsFileTransferUseConfiguredFolders));
    }

    partial void OnFileTransferDuplicateActionChanged(string value)
    {
        OnPropertyChanged(nameof(IsFileTransferDuplicateAutoRename));
        OnPropertyChanged(nameof(IsFileTransferDuplicateOverwrite));
    }

    partial void OnFileTransferUploadProtocolChanged(string value)
    {
        OnPropertyChanged(nameof(IsFileTransferUploadProtocolXmodem));
        OnPropertyChanged(nameof(IsFileTransferUploadProtocolYmodem));
        OnPropertyChanged(nameof(IsFileTransferUploadProtocolZmodem));
        OnPropertyChanged(nameof(IsFileTransferUploadProtocolFtp));
    }

    partial void OnFileTransferXymodemBlockSizeChanged(int value)
    {
        OnPropertyChanged(nameof(IsFileTransferXymodemBlockSize128));
        OnPropertyChanged(nameof(IsFileTransferXymodemBlockSize1024));
    }

    partial void OnSendSessionKeepAliveChanged(bool value)
    {
        OnPropertyChanged(nameof(IsSessionKeepAliveIntervalEnabled));
    }

    partial void OnSendIdleStringChanged(bool value)
    {
        OnPropertyChanged(nameof(IsIdleStringSettingsEnabled));
    }

    partial void OnRunLoginScriptFileChanged(bool value)
    {
        OnPropertyChanged(nameof(IsLoginScriptFileEnabled));
    }

    partial void OnTerminalKeyboardFunctionKeyModeChanged(string value)
    {
        OnPropertyChanged(nameof(IsKeyboardMappingFileEnabled));
    }

    partial void OnTerminalLeftAltAsMetaChanged(bool value)
    {
        OnPropertyChanged(nameof(IsCtrlAltAsAltGrEnabled));
    }

    partial void OnTerminalRightAltAsMetaChanged(bool value)
    {
    }

    partial void OnTerminalDeleteKeySequenceChanged(string value)
    {
        OnPropertyChanged(nameof(IsDeleteKeyVt220));
        OnPropertyChanged(nameof(IsDeleteKeyAscii127));
        OnPropertyChanged(nameof(IsDeleteKeyBackspace));
    }

    partial void OnTerminalBackspaceKeySequenceChanged(string value)
    {
        OnPropertyChanged(nameof(IsBackspaceKeyVt220));
        OnPropertyChanged(nameof(IsBackspaceKeyAscii127));
        OnPropertyChanged(nameof(IsBackspaceKeyBackspace));
    }

    partial void OnTerminalVtCursorKeyModeChanged(string value)
    {
        OnPropertyChanged(nameof(IsCursorKeyModeNormal));
        OnPropertyChanged(nameof(IsCursorKeyModeApplication));
    }

    partial void OnTerminalVtNumericKeypadModeChanged(string value)
    {
        OnPropertyChanged(nameof(IsNumericKeypadModeNormal));
        OnPropertyChanged(nameof(IsNumericKeypadModeApplication));
        OnPropertyChanged(nameof(IsNumericKeypadModeForceNormal));
    }

    partial void OnAppearanceColorSchemeChanged(string value)
    {
        ApplyAppearanceColorScheme(value);
    }

    partial void OnAppearanceForegroundColorChanged(Color value)
    {
        OnPropertyChanged(nameof(AppearancePreviewBoldColor));
        OnPropertyChanged(nameof(AppearanceForegroundBrush));
        OnPropertyChanged(nameof(AppearancePreviewBoldBrush));
    }

    partial void OnAppearanceBoldForegroundColorChanged(Color value)
    {
        OnPropertyChanged(nameof(AppearancePreviewBoldColor));
        OnPropertyChanged(nameof(AppearanceBoldForegroundBrush));
        OnPropertyChanged(nameof(AppearancePreviewBoldBrush));
    }

    partial void OnAppearanceBackgroundColorChanged(Color value)
    {
        OnPropertyChanged(nameof(AppearanceBackgroundBrush));
    }

    partial void OnAppearanceCursorColorChanged(Color value)
    {
        OnPropertyChanged(nameof(AppearanceCursorBrush));
    }

    partial void OnAppearanceCursorTextColorChanged(Color value)
    {
        OnPropertyChanged(nameof(AppearanceCursorTextBrush));
    }

    partial void OnAppearanceFontFamilyChanged(string value)
    {
        OnPropertyChanged(nameof(AppearancePreviewFontFamily));
    }

    partial void OnAppearanceCjkFontFamilyChanged(string value)
    {
        OnPropertyChanged(nameof(AppearancePreviewCjkFontFamily));
        OnPropertyChanged(nameof(IsAppearanceCjkHorizontalPreviewVisible));
        OnPropertyChanged(nameof(IsAppearanceCjkVerticalPreviewVisible));
    }

    partial void OnAppearanceFontStyleChanged(string value)
    {
        OnPropertyChanged(nameof(AppearancePreviewFontStyle));
        OnPropertyChanged(nameof(AppearancePreviewFontWeight));
    }

    partial void OnAppearanceFontQualityChanged(string value)
    {
        OnPropertyChanged(nameof(AppearancePreviewTextRenderingMode));
        OnPropertyChanged(nameof(AppearancePreviewTextHintingMode));
        OnPropertyChanged(nameof(AppearancePreviewBaselinePixelAlignment));
    }

    partial void OnAppearanceBoldTextModeChanged(string value)
    {
        OnPropertyChanged(nameof(AppearancePreviewBoldWeight));
        OnPropertyChanged(nameof(AppearancePreviewBoldColor));
        OnPropertyChanged(nameof(AppearancePreviewBoldBrush));
    }

    partial void OnAppearanceAnsiColorsChanged(string value)
    {
        NotifyAnsiPreviewColorsChanged();
    }

    partial void OnAppearanceCursorShapeChanged(string value)
    {
        OnPropertyChanged(nameof(IsAppearanceCursorShapeBlock));
        OnPropertyChanged(nameof(IsAppearanceCursorShapeVertical));
        OnPropertyChanged(nameof(IsAppearanceCursorShapeUnderline));
        NotifyAppearancePreviewCursorVisibilityChanged();
    }

    partial void OnAppearancePreviewCursorVisibleChanged(bool value)
    {
        NotifyAppearancePreviewCursorVisibilityChanged();
    }

    partial void OnAppearanceTabColorModeChanged(string value)
    {
        OnPropertyChanged(nameof(IsAppearanceTabColorDefault));
        OnPropertyChanged(nameof(IsAppearanceTabColorRed));
        OnPropertyChanged(nameof(IsAppearanceTabColorPurple));
        OnPropertyChanged(nameof(IsAppearanceTabColorYellow));
        OnPropertyChanged(nameof(IsAppearanceTabColorCustom));
    }

    private void NotifyAppearancePreviewCursorVisibilityChanged()
    {
        OnPropertyChanged(nameof(IsAppearanceCursorBlockVisible));
        OnPropertyChanged(nameof(IsAppearanceCursorVerticalVisible));
        OnPropertyChanged(nameof(IsAppearanceCursorUnderlineVisible));
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
        return protocol is not SessionProtocol.SERIAL;
    }

    private static bool RequiresPort(SessionProtocol protocol)
    {
        return protocol is not SessionProtocol.SERIAL;
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
            SessionProtocol.VNC => 5900,
            SessionProtocol.SERIAL => 0,
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
            ValidationMessage = L.Text("Validation.SessionNameRequired");
            return false;
        }

        if (RequiresHost(protocol) && string.IsNullOrWhiteSpace(Host))
        {
            HostStatus = InputControlStatus.Error;
            ValidationMessage = L.Text("Validation.HostRequired");
            return false;
        }

        if (RequiresPort(protocol))
        {
            if (string.IsNullOrWhiteSpace(Port))
            {
                PortStatus = InputControlStatus.Error;
                ValidationMessage = L.Text("Validation.PortRequired");
                return false;
            }

            if (!int.TryParse(Port.Trim(), out port) || port < 1 || port > 65535)
            {
                PortStatus = InputControlStatus.Error;
                ValidationMessage = L.Text("Validation.PortRange");
                return false;
            }
        }

        if (protocol == SessionProtocol.SERIAL && string.IsNullOrWhiteSpace(SerialPortName))
        {
            SerialPortStatus = InputControlStatus.Error;
            ValidationMessage = L.Text("Validation.SerialPortRequired");
            return false;
        }

        if (protocol == SessionProtocol.RDP && RdpUseSshTunnel)
        {
            if (string.IsNullOrWhiteSpace(RdpSshHost))
            {
                ValidationMessage = "SSH host is required for RDP tunnel.";
                return false;
            }

            if (!IsValidPortText(RdpSshPort))
            {
                ValidationMessage = "SSH port must be an integer between 1 and 65535.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(RdpSshUsername))
            {
                ValidationMessage = "SSH username is required for RDP tunnel.";
                return false;
            }
        }

        if (protocol == SessionProtocol.VNC && VncUseSshTunnel)
        {
            if (string.IsNullOrWhiteSpace(VncSshHost))
            {
                ValidationMessage = "SSH host is required for VNC tunnel.";
                return false;
            }

            if (!IsValidPortText(VncSshPort))
            {
                ValidationMessage = "SSH port must be an integer between 1 and 65535.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(VncSshUsername))
            {
                ValidationMessage = "SSH username is required for VNC tunnel.";
                return false;
            }

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
        var portNames = PlatformServices.GetSerialPortNames();
        if (portNames.Length == 0)
            portNames = PlatformServices.GetDefaultSerialPortNames();

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

    private static ObservableCollection<ISelectOption> CreateFontOptions()
    {
        var fontNames = new[]
            {
                "DejaVu Sans Mono",
                "Cascadia Mono",
                "Cascadia Code",
                "Consolas",
                "Courier New",
                "Fixedsys",
                "Lucida Console",
                "Lucida Sans Typewriter",
                "MS Gothic",
                "Microsoft YaHei",
                "Noto Sans CJK SC",
                "PingFang SC",
                "SimHei",
                "SimSun",
                "SimSun-ExtB",
                "SimSun-ExtG",
                "FangSong",
                "KaiTi",
                "LiSu",
                "YouYuan",
                "@仿宋",
                "@黑体",
                "@楷体",
                "@隶书",
                "@新宋体",
                "@幼圆",
                "Terminal"
            }
            .Concat(GetInstalledFontNames())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase)
            .Select(name => (ISelectOption)new SelectOption { Header = name, Content = name });

        return new ObservableCollection<ISelectOption>(fontNames);
    }

    private static bool IsVerticalFontName(string? fontFamily)
    {
        return !string.IsNullOrWhiteSpace(fontFamily) && fontFamily.TrimStart().StartsWith('@');
    }

    private static string NormalizeFontFamilyName(string? fontFamily)
    {
        if (string.IsNullOrWhiteSpace(fontFamily))
            return "DejaVu Sans Mono";

        var trimmed = fontFamily.Trim();
        return trimmed.StartsWith('@') && trimmed.Length > 1
            ? trimmed[1..]
            : trimmed;
    }

    private static TextRenderingMode GetTextRenderingMode(string? value)
        => value switch
        {
            "NonAntiAliased" => TextRenderingMode.Alias,
            "AntiAliased" => TextRenderingMode.Antialias,
            "ClearType" => TextRenderingMode.SubpixelAntialias,
            "NaturalClearType" => TextRenderingMode.SubpixelAntialias,
            _ => TextRenderingMode.Unspecified
        };

    private static TextHintingMode GetTextHintingMode(string? value)
        => value switch
        {
            "Draft" => TextHintingMode.None,
            "Proof" => TextHintingMode.Strong,
            "NonAntiAliased" => TextHintingMode.None,
            "AntiAliased" => TextHintingMode.Light,
            "ClearType" => TextHintingMode.Strong,
            "NaturalClearType" => TextHintingMode.Strong,
            _ => TextHintingMode.Unspecified
        };

    private static BaselinePixelAlignment GetBaselinePixelAlignment(string? value)
        => value switch
        {
            "NonAntiAliased" => BaselinePixelAlignment.Aligned,
            "AntiAliased" => BaselinePixelAlignment.Aligned,
            "ClearType" => BaselinePixelAlignment.Aligned,
            "NaturalClearType" => BaselinePixelAlignment.Aligned,
            _ => BaselinePixelAlignment.Unspecified
        };

    private Color GetAnsiPreviewColor(int index, string fallback)
    {
        var colors = AppearanceAnsiColors
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return index >= 0 && index < colors.Length
            ? ParseColorOrDefault(colors[index], fallback)
            : Color.Parse(fallback);
    }

    private void NotifyAnsiPreviewColorsChanged()
    {
        OnPropertyChanged(nameof(AppearanceAnsiBlack));
        OnPropertyChanged(nameof(AppearanceAnsiRed));
        OnPropertyChanged(nameof(AppearanceAnsiGreen));
        OnPropertyChanged(nameof(AppearanceAnsiYellow));
        OnPropertyChanged(nameof(AppearanceAnsiBlue));
        OnPropertyChanged(nameof(AppearanceAnsiMagenta));
        OnPropertyChanged(nameof(AppearanceAnsiCyan));
        OnPropertyChanged(nameof(AppearanceAnsiWhite));
        OnPropertyChanged(nameof(AppearanceAnsiBlackBrush));
        OnPropertyChanged(nameof(AppearanceAnsiRedBrush));
        OnPropertyChanged(nameof(AppearanceAnsiGreenBrush));
        OnPropertyChanged(nameof(AppearanceAnsiYellowBrush));
        OnPropertyChanged(nameof(AppearanceAnsiBlueBrush));
        OnPropertyChanged(nameof(AppearanceAnsiMagentaBrush));
        OnPropertyChanged(nameof(AppearanceAnsiCyanBrush));
        OnPropertyChanged(nameof(AppearanceAnsiWhiteBrush));
    }

    public static AppearanceColorPalette GetAppearanceColorSchemePalette(string value)
    {
        return value switch
        {
            "AnsiWhite" or "BlackOnWhite" or "NewWhite" or "PastelWhite" => new AppearanceColorPalette("#000000", "#000000", "#FFFFFF", "#000000", "#FFFFFF",
                "#000000;#CD0000;#00CD00;#CDCD00;#0000EE;#CD00CD;#00CDCD;#E5E5E5;#7F7F7F;#FF0000;#00FF00;#FFFF00;#5C5CFF;#FF00FF;#00FFFF;#FFFFFF"),
            "WhiteOnBlack" or "AnsiBlack" => new AppearanceColorPalette("#FFFFFF", "#FFFFFF", "#000000", "#FFFFFF", "#000000",
                "#000000;#CD0000;#00CD00;#CDCD00;#0000EE;#CD00CD;#00CDCD;#E5E5E5;#7F7F7F;#FF0000;#00FF00;#FFFF00;#5C5CFF;#FF00FF;#00FFFF;#FFFFFF"),
            "Afterglow" => new AppearanceColorPalette("#D0D0D0", "#FFD580", "#212121", "#D0D0D0", "#212121",
                "#151515;#AC4142;#7E8E50;#E5B567;#6C99BB;#9F4E85;#7DD6CF;#D0D0D0;#505050;#AC4142;#7E8E50;#E5B567;#6C99BB;#9F4E85;#7DD6CF;#F5F5F5"),
            "Arthur" => new AppearanceColorPalette("#DDEEDD", "#A4FFA4", "#1C1C1C", "#DDEEDD", "#1C1C1C",
                "#3D352A;#CD5C5C;#86AF80;#E8AE5B;#6495ED;#DEB887;#B0C4DE;#BBAA99;#554444;#CC5533;#88AA22;#FFA75D;#87CEEB;#996600;#B0C4DE;#DDCCBB"),
            "BelafonteDay" => new AppearanceColorPalette("#45373C", "#45373C", "#D5CCBA", "#45373C", "#D5CCBA",
                "#20111B;#BE100E;#858162;#EAA549;#426A79;#97522C;#989A9C;#968C83;#5E5252;#BE100E;#858162;#EAA549;#426A79;#97522C;#989A9C;#D5CCBA"),
            "Chalk" or "Chalkboard" => new AppearanceColorPalette("#D9E6F2", "#96E072", "#2B2D2E", "#D9E6F2", "#2B2D2E",
                "#000000;#C37372;#72C373;#C2C372;#7372C3;#C372C2;#72C2C3;#D9D9D9;#323232;#DCA3A3;#A3DCA3;#DCDCA3;#A3A3DC;#DCA3DC;#A3DCDC;#FFFFFF"),
            "Codeschool" => new AppearanceColorPalette("#9EA7A6", "#B5D8F6", "#232C31", "#9EA7A6", "#232C31",
                "#2A343A;#2A5491;#237986;#A03B1E;#484D79;#C59820;#B02F30;#9EA7A6;#3F4944;#2A5491;#237986;#A03B1E;#484D79;#C59820;#B02F30;#B5D8F6"),
            "Earthsong" => new AppearanceColorPalette("#E5C7A9", "#F6F7EC", "#292520", "#E5C7A9", "#292520",
                "#121418;#C94234;#85C54C;#F5AE2E;#1398B9;#D0633D;#509552;#E5C6AA;#675F54;#FF645A;#98E036;#E0D561;#5FDAFF;#FF9269;#84F088;#F6F7EC"),
            "Espresso" => new AppearanceColorPalette("#FFFFFF", "#FFFFFF", "#323232", "#FFFFFF", "#323232",
                "#353535;#D25252;#A5C261;#FFC66D;#6C99BB;#D197D9;#BED6FF;#EEEEEC;#535353;#F00C0C;#C2E075;#E1E48B;#8AB7D9;#EFB5F7;#DCF4FF;#FFFFFF"),
            "IrBlack" or "NewBlack" => new AppearanceColorPalette("#F1F1F1", "#A8FF60", "#000000", "#F1F1F1", "#000000",
                "#4E4E4E;#FF6C60;#A8FF60;#FFFFB6;#96CBFE;#FF73FD;#C6C5FE;#EEEEEE;#7C7C7C;#FFB6B0;#CEFFAB;#FFFFCB;#B5DCFF;#FF9CFE;#DFDFFE;#FFFFFF"),
            "Obsidian" => new AppearanceColorPalette("#E0E0E0", "#93C863", "#283033", "#E0E0E0", "#283033",
                "#000000;#A60001;#00BB00;#FECD22;#3A9BDB;#BB00BB;#00BBBB;#BBBBBB;#555555;#FF0003;#93C863;#FEF874;#A1D7FF;#FF55FF;#55FFFF;#FFFFFF"),
            _ => new AppearanceColorPalette("#CCCCCC", "#33FF33", "#000000", "#00FF00", "#000000",
                "#000000;#CC0000;#4E9A06;#C4A000;#3465A4;#75507B;#06989A;#D3D7CF;#555753;#EF2929;#8AE234;#FCE94F;#729FCF;#AD7FA8;#34E2E2;#EEEEEC")
        };
    }

    private void ApplyAppearanceColorScheme(string value)
    {
        var scheme = GetAppearanceColorSchemePalette(value);

        AppearanceForegroundColor = Color.Parse(scheme.Foreground);
        AppearanceBoldForegroundColor = Color.Parse(scheme.BoldForeground);
        AppearanceBackgroundColor = Color.Parse(scheme.Background);
        AppearanceCursorColor = Color.Parse(scheme.Cursor);
        AppearanceCursorTextColor = Color.Parse(scheme.CursorText);
        AppearanceAnsiColors = scheme.AnsiColors;
    }

    private static IEnumerable<string> GetInstalledFontNames()
    {
        try
        {
            return FontManager.Current.SystemFonts
                .Select(font => font.Name)
                .Where(name => !string.IsNullOrWhiteSpace(name));
        }
        catch
        {
            return [];
        }
    }

    private static Color ParseColorOrDefault(string? value, string fallback)
    {
        return Color.TryParse(value, out var color) ? color : Color.Parse(fallback);
    }

    private static string ToHex(Color color)
    {
        return $"#{color.R:X2}{color.G:X2}{color.B:X2}";
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

    public static LoginScriptRule CloneLoginScriptRule(LoginScriptRule source)
    {
        return new LoginScriptRule
        {
            Id = source.Id,
            Expect = source.Expect,
            Send = source.Send,
            HideText = source.HideText,
            SortOrder = source.SortOrder
        };
    }

    public void RefreshHighlightSetOptions()
    {
        AppearanceHighlightSetOptions.Clear();
        AppearanceHighlightSetOptions.Add(new SelectOption { Header = "None", Content = "None" });

        foreach (var set in AppearanceHighlightSets)
            AppearanceHighlightSetOptions.Add(new SelectOption { Header = set.Name, Content = set.Id.ToString() });

        if (!string.Equals(AppearanceHighlightSetId, "None", StringComparison.OrdinalIgnoreCase) &&
            AppearanceHighlightSets.All(set => !string.Equals(set.Id.ToString(), AppearanceHighlightSetId, StringComparison.OrdinalIgnoreCase)))
        {
            AppearanceHighlightSetId = "None";
        }
    }

    private void LoadHighlightSets(IEnumerable<HighlightSet>? sets, string? selectedSetId)
    {
        AppearanceHighlightSets.Clear();
        var sourceSets = sets?.Select(CloneHighlightSet).ToList() ?? [];
        if (sourceSets.Count == 0)
            sourceSets.Add(CreateSampleHighlightSet());
        EnsureSampleHighlightSet(sourceSets);

        foreach (var set in sourceSets)
            AppearanceHighlightSets.Add(set);

        AppearanceHighlightSetId = string.IsNullOrWhiteSpace(selectedSetId) ? "None" : selectedSetId;
        SelectedHighlightSet = AppearanceHighlightSets.FirstOrDefault(set =>
            string.Equals(set.Id.ToString(), AppearanceHighlightSetId, StringComparison.OrdinalIgnoreCase));
        RefreshHighlightSetOptions();
    }

    private static void EnsureSampleHighlightSet(List<HighlightSet> sets)
    {
        var sample = sets.FirstOrDefault(set =>
            string.Equals(set.Name, "New Highlight Set (Sample)", StringComparison.OrdinalIgnoreCase));

        if (sample == null)
        {
            sets.Insert(0, CreateSampleHighlightSet());
            return;
        }

        if (sample.Rules.Count > 0)
            return;

        var sampleRules = CreateSampleHighlightSet().Rules;
        foreach (var rule in sampleRules)
            sample.Rules.Add(CloneHighlightRule(rule));
        if (string.IsNullOrWhiteSpace(sample.Name))
            sample.Name = "New Highlight Set (Sample)";
    }

    public static HighlightSet CreateSampleHighlightSet()
    {
        var set = new HighlightSet { Name = "New Highlight Set (Sample)" };
        set.Rules.Add(new HighlightRule
        {
            Keyword = @"\d{3}-\d{3,4}-\d{4}",
            IsRegex = true,
            Description = "Phone number",
            ForegroundColor = "#000000",
            BackgroundColor = "#FFFF00",
            Bold = true,
            SortOrder = 0
        });
        set.Rules.Add(new HighlightRule
        {
            Keyword = @"([0-9]{1,3})\.([0-9]{1,3})\.([0-9]{1,3})\.([0-9]{1,3})",
            IsRegex = true,
            Description = "IP address",
            ForegroundColor = "#000000",
            BackgroundColor = "#FFA940",
            Italic = true,
            SortOrder = 1
        });
        set.Rules.Add(new HighlightRule
        {
            Keyword = @"[_a-z0-9-]+([._a-z0-9-]+)*@[a-z0-9-]+([.a-z0-9-]+)*",
            IsRegex = true,
            Description = "Email address",
            ForegroundColor = "#111111",
            BackgroundColor = "#9DBBFF",
            Underline = true,
            SortOrder = 2
        });
        set.Rules.Add(new HighlightRule
        {
            Keyword = @"\d",
            IsRegex = true,
            Description = "Only number",
            ForegroundColor = "#FF4D4F",
            BackgroundColor = "#000000",
            Strikethrough = true,
            SortOrder = 3
        });
        set.Rules.Add(new HighlightRule
        {
            Keyword = @"\s",
            IsRegex = true,
            Description = "Space",
            ForegroundColor = "#000000",
            BackgroundColor = "#C8C8C8",
            SortOrder = 4
        });
        return set;
    }

    public static HighlightSet CloneHighlightSet(HighlightSet source)
    {
        var clone = new HighlightSet
        {
            Id = source.Id,
            Name = string.IsNullOrWhiteSpace(source.Name) ? "Highlight Set" : source.Name
        };
        foreach (var rule in source.Rules.OrderBy(rule => rule.SortOrder))
            clone.Rules.Add(CloneHighlightRule(rule));
        return clone;
    }

    public static HighlightRule CloneHighlightRule(HighlightRule source)
    {
        return new HighlightRule
        {
            Id = source.Id,
            IsEnabled = source.IsEnabled,
            Keyword = source.Keyword,
            IsCaseSensitive = source.IsCaseSensitive,
            IsRegex = source.IsRegex,
            Description = source.Description,
            ForegroundColor = source.ForegroundColor,
            BackgroundColor = source.BackgroundColor,
            UseTerminalColor = source.UseTerminalColor,
            Bold = source.Bold,
            Italic = source.Italic,
            Underline = source.Underline,
            Strikethrough = source.Strikethrough,
            SortOrder = source.SortOrder
        };
    }
}
