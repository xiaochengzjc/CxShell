using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using CxShell.Models;
using CxShell.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CxShell.ViewModels;

public enum SessionLookupStatus
{
    Found,
    NotFound,
    Ambiguous
}

public sealed record SessionLookupCandidate(SessionInfo Session, string Path);

public sealed record SessionLookupResult(
    SessionLookupStatus Status,
    SessionInfo? Session,
    IReadOnlyList<SessionLookupCandidate> Candidates)
{
    public static SessionLookupResult Found(SessionInfo session, string path)
        => new(SessionLookupStatus.Found, session, [new SessionLookupCandidate(session, path)]);

    public static SessionLookupResult NotFound()
        => new(SessionLookupStatus.NotFound, null, []);

    public static SessionLookupResult Ambiguous(IReadOnlyList<SessionLookupCandidate> candidates)
        => new(SessionLookupStatus.Ambiguous, null, candidates);
}

public sealed record SessionImportResult(int Count, bool IsOpenSshConfig);

public partial class SessionNodeViewModel : ObservableObject
{
    [ObservableProperty] private string _name;
    [ObservableProperty] private string _icon;
    [ObservableProperty] private bool _isGroup;
    public SessionInfo? Session { get; }
    public ObservableCollection<SessionNodeViewModel> Children { get; } = new();
    public string Host => Session?.Host ?? string.Empty;
    public string Username => Session?.Username ?? string.Empty;
    public string Protocol => Session?.Protocol.ToString() ?? string.Empty;
    public int? Port => Session?.Port;

    public SessionNodeViewModel(SessionInfo session)
    {
        _name = session.Name;
        _icon = "🖥";
        _isGroup = false;
        Session = session;
    }

    public SessionNodeViewModel(SessionGroup group)
    {
        _name = group.Name;
        _icon = "📁";
        _isGroup = true;
    }
}

public partial class SessionTreeViewModel : ObservableObject
{
    [ObservableProperty] private ObservableCollection<SessionNodeViewModel> _sessionNodes = new();
    [ObservableProperty] private ObservableCollection<SessionNodeViewModel> _sessionRows = new();
    [ObservableProperty] private ObservableCollection<SessionInfo> _quickSessions = new();
    [ObservableProperty] private ObservableCollection<SessionNodeViewModel> _selectedNodes = new();
    [ObservableProperty] private SessionNodeViewModel? _selectedNode;
    [ObservableProperty] private string _sessionSearchText = string.Empty;

    private readonly SessionStorageService _storage;
    private readonly ApplicationSettingsStore _settingsStore;
    private readonly MainWindowViewModel _mainWindow;
    private SessionData _data;
    private SessionInfo? _copiedSession;

    public SessionInfo? SelectedSession => SelectedNode?.Session;
    public bool CanUseSelectedSession => SelectedSession != null && SelectedNodes.Count <= 1;
    public bool HasSelectedSessions => SelectedNodes.Any(node => node.Session != null);
    public int SelectedSessionCount => SelectedNodes.Count(node => node.Session != null);
    public bool CanExportSelectedSessions => HasSelectedSessions;
    public bool HasOpenSelectedSession => GetSelectedSessions().Any(session =>
        _mainWindow.Tabs.Any(tab => tab.Session.Id == session.Id));
    public bool CanDeleteSelectedSessions => HasSelectedSessions && !HasOpenSelectedSession;
    public bool CanPaste => _copiedSession != null;
    public bool CanMoveSelectedSessionUp => GetSelectedVisibleSessionIndex() > 0;
    public bool CanMoveSelectedSessionDown
    {
        get
        {
            var index = GetSelectedVisibleSessionIndex();
            return index >= 0 && index < SessionRows.Count - 1;
        }
    }

    public MainWindowViewModel MainWindow => _mainWindow;
    public ApplicationSettings Settings => _data.Settings ??= new ApplicationSettings();
    private LocalizationService L => LocalizationService.Shared;
    public string SessionManagerTitle => L.Text("SessionManager.Title");
    public string NewText => L.Text("SessionManager.New");
    public string CopyText => L.Text("SessionManager.Copy");
    public string PasteText => L.Text("SessionManager.Paste");
    public string PropertiesText => L.Text("SessionManager.Properties");
    public string DeleteText => L.Text("SessionManager.Delete");
    public string ImportText => L.Text("SessionManager.Import");
    public string ExportText => L.Text("SessionManager.Export");
    public string MoveUpText => L.Text("SessionManager.MoveUp");
    public string MoveDownText => L.Text("SessionManager.MoveDown");
    public string SearchPlaceholderText => L.Text("SessionManager.SearchPlaceholder");
    public string ConnectText => L.Text("Toolbar.Connect");
    public string DiagnosticsText => L.Text("Diagnostics.Title");
    public string CloseText => L.Text("SessionManager.Close");
    public string ColumnNameText => L.Text("SessionManager.ColumnName");
    public string ColumnHostText => L.Text("SessionManager.ColumnHost");
    public string ColumnUsernameText => L.Text("SessionManager.ColumnUsername");
    public string ColumnProtocolText => L.Text("SessionManager.ColumnProtocol");
    public string ColumnPortText => L.Text("SessionManager.ColumnPort");
    public SessionTreeViewModel(MainWindowViewModel mainWindow)
    {
        _mainWindow = mainWindow;
        _storage = new SessionStorageService();
        _data = _storage.Load();
        _data.Settings ??= new ApplicationSettings();
        _settingsStore = new ApplicationSettingsStore();
        _data.Settings = _settingsStore.Load(_data.Settings);
        LocalizationService.Shared.SetLanguage(_data.Settings.UiLanguage);
        LocalizationService.Shared.LanguageChanged += (_, _) => NotifyLocalizationChanged();
        LoadSessions();
    }

    private void NotifyLocalizationChanged()
    {
        OnPropertyChanged(nameof(SessionManagerTitle));
        OnPropertyChanged(nameof(NewText));
        OnPropertyChanged(nameof(CopyText));
        OnPropertyChanged(nameof(PasteText));
        OnPropertyChanged(nameof(PropertiesText));
        OnPropertyChanged(nameof(DeleteText));
        OnPropertyChanged(nameof(ImportText));
        OnPropertyChanged(nameof(ExportText));
        OnPropertyChanged(nameof(MoveUpText));
        OnPropertyChanged(nameof(MoveDownText));
        OnPropertyChanged(nameof(SearchPlaceholderText));
        OnPropertyChanged(nameof(ConnectText));
        OnPropertyChanged(nameof(DiagnosticsText));
        OnPropertyChanged(nameof(CloseText));
        OnPropertyChanged(nameof(ColumnNameText));
        OnPropertyChanged(nameof(ColumnHostText));
        OnPropertyChanged(nameof(ColumnUsernameText));
        OnPropertyChanged(nameof(ColumnProtocolText));
        OnPropertyChanged(nameof(ColumnPortText));
    }

    public SessionInfo CreateSession()
    {
        return new SessionInfo
        {
            Id = Guid.NewGuid(),
            Name = string.Empty,
            Host = string.Empty,
            Username = string.Empty,
            SshAutoOpenSftpPanel = false,
            SshAutoOpenMonitorPanel = false,
            SshDoNotStartFileManager = true,
            VncScalePercent = SessionInfo.DefaultVncScalePercent,
            VncFramebufferUpdateDelayMilliseconds = SessionInfo.DefaultVncFramebufferUpdateDelayMilliseconds,
            VncCompressionLevel = SessionInfo.DefaultVncCompressionLevel,
            VncJpegQualityLevel = SessionInfo.DefaultVncJpegQualityLevel
        };
    }

    public void SaveSettings(ApplicationSettings settings)
    {
        _data.Settings = settings;
        _settingsStore.Save(settings);
    }

    public static void CopySessionValues(SessionInfo target, SessionInfo source)
    {
        target.Proxy = CloneProxy(source.Proxy);
        target.ProxyServers = source.ProxyServers.Select(CloneProxy).ToList();
        target.SelectedProxyId = source.SelectedProxyId;
        target.Host = source.Host;
        target.Port = source.Port;
        target.Username = source.Username;
        target.Protocol = source.Protocol;
        target.AuthMethod = source.AuthMethod;
        target.Password = source.Password;
        target.PrivateKeyPath = source.PrivateKeyPath;
        target.PrivateKeyPassphrase = source.PrivateKeyPassphrase;
        target.RuntimePrivateKeyPassphrase = source.RuntimePrivateKeyPassphrase;
        target.AutoReconnect = source.AutoReconnect;
        target.ReconnectIntervalSeconds = source.ReconnectIntervalSeconds;
        target.ReconnectLimitMinutes = source.ReconnectLimitMinutes;
        target.SendSessionKeepAlive = source.SendSessionKeepAlive;
        target.SessionKeepAliveIntervalSeconds = source.SessionKeepAliveIntervalSeconds;
        target.SendIdleString = source.SendIdleString;
        target.IdleStringIntervalSeconds = source.IdleStringIntervalSeconds;
        target.IdleString = source.IdleString;
        target.TcpKeepAlive = source.TcpKeepAlive;
        target.EnableLoginScriptRules = source.EnableLoginScriptRules;
        target.LoginScriptRules = source.LoginScriptRules.Select(SessionEditViewModel.CloneLoginScriptRule).ToList();
        target.RunLoginScriptFile = source.RunLoginScriptFile;
        target.LoginScriptFilePath = source.LoginScriptFilePath;
        target.LoginScriptParameters = source.LoginScriptParameters;
        target.SshRemoteCommand = source.SshRemoteCommand;
        target.SshVersionPolicy = source.SshVersionPolicy;
        target.SshUseXagent = source.SshUseXagent;
        target.SshForwardAgent = source.SshForwardAgent;
        target.SshUseCompression = source.SshUseCompression;
        target.SshNoTerminal = source.SshNoTerminal;
        target.SshAcceptAndSaveHostKey = source.SshAcceptAndSaveHostKey;
        target.SshAutoOpenSftpPanel = source.SshAutoOpenSftpPanel;
        target.SshAutoOpenMonitorPanel = source.SshAutoOpenMonitorPanel;
        target.SshEnableServerMonitoring = source.SshEnableServerMonitoring;
        target.SshEnableMonitorNetworkLatency = source.SshEnableMonitorNetworkLatency;
        target.SshMonitorRefreshIntervalSeconds = source.SshMonitorRefreshIntervalSeconds;
        target.SshDoNotStartFileManager = !source.SshAutoOpenSftpPanel;
        target.SshCipherAlgorithms = source.SshCipherAlgorithms;
        target.SshMacAlgorithms = source.SshMacAlgorithms;
        target.SshKeyExchangeAlgorithms = source.SshKeyExchangeAlgorithms;
        target.SshTunnelRules = source.SshTunnelRules.Select(SessionEditViewModel.CloneTunnelRule).ToList();
        target.SshAutoRestoreTunnels = source.SshAutoRestoreTunnels;
        target.SshForwardX11 = source.SshForwardX11;
        target.SshX11UseXmanager = source.SshX11UseXmanager;
        target.SshX11Display = source.SshX11Display;
        target.TelnetUseXDisplayLocation = source.TelnetUseXDisplayLocation;
        target.TelnetXDisplayLocation = source.TelnetXDisplayLocation;
        target.TelnetOptionMode = source.TelnetOptionMode;
        target.TelnetForceCharacterAtATime = source.TelnetForceCharacterAtATime;
        target.TelnetUsernamePrompt = source.TelnetUsernamePrompt;
        target.TelnetPasswordPrompt = source.TelnetPasswordPrompt;
        target.RloginPasswordPrompt = source.RloginPasswordPrompt;
        target.RloginTerminalSpeed = source.RloginTerminalSpeed;
        target.SftpLocalStartDirectory = source.SftpLocalStartDirectory;
        target.SftpRemoteStartDirectory = source.SftpRemoteStartDirectory;
        target.SftpFollowTerminalDirectory = source.SftpFollowTerminalDirectory;
        target.SftpUseCustomServer = source.SftpUseCustomServer;
        target.SftpCustomServerCommand = source.SftpCustomServerCommand;
        target.SerialPortName = source.SerialPortName;
        target.SerialBaudRate = source.SerialBaudRate;
        target.SerialDataBits = source.SerialDataBits;
        target.SerialParity = source.SerialParity;
        target.SerialStopBits = source.SerialStopBits;
        target.SerialFlowControl = source.SerialFlowControl;
        target.RdpWindowSize = source.RdpWindowSize;
        target.RdpDesktopWidth = source.RdpDesktopWidth;
        target.RdpDesktopHeight = source.RdpDesktopHeight;
        target.RdpResizeMode = source.RdpResizeMode;
        target.RdpScreenScale = source.RdpScreenScale;
        target.RdpColorQuality = source.RdpColorQuality;
        target.RdpApplyKeyCombinations = source.RdpApplyKeyCombinations;
        target.RdpRedirectDrives = source.RdpRedirectDrives;
        target.RdpDriveName = source.RdpDriveName;
        target.RdpDrivePath = source.RdpDrivePath;
        target.RdpAudioMode = source.RdpAudioMode;
        target.RdpAudioCapture = source.RdpAudioCapture;
        target.RdpMicrophoneEnabled = source.RdpMicrophoneEnabled;
        target.RdpUseSshTunnel = source.RdpUseSshTunnel;
        target.RdpSshHost = source.RdpSshHost;
        target.RdpSshPort = source.RdpSshPort;
        target.RdpSshUsername = source.RdpSshUsername;
        target.RdpSshPassword = source.RdpSshPassword;
        target.RdpSshUsePrivateKey = source.RdpSshUsePrivateKey;
        target.RdpSshPrivateKeyPath = source.RdpSshPrivateKeyPath;
        target.VncDisplayMode = source.VncDisplayMode;
        target.VncScalePercent = source.VncScalePercent;
        target.VncResizeMode = source.VncResizeMode;
        target.VncReadOnlyMode = source.VncReadOnlyMode;
        target.VncEnableKeyboardInput = source.VncEnableKeyboardInput;
        target.VncEnableMouseInput = source.VncEnableMouseInput;
        target.VncCaptureShortcuts = source.VncCaptureShortcuts;
        target.VncCursorMode = source.VncCursorMode;
        target.VncShowToolbarButtons = source.VncShowToolbarButtons;
        target.VncClipboardMode = source.VncClipboardMode;
        target.VncEncodingProfile = source.VncEncodingProfile;
        target.VncFramebufferUpdateDelayMilliseconds = source.VncFramebufferUpdateDelayMilliseconds;
        target.VncCompressionLevel = source.VncCompressionLevel;
        target.VncJpegQualityLevel = source.VncJpegQualityLevel;
        target.VncJpegSubsampling = source.VncJpegSubsampling;
        target.VncUseSshTunnel = source.VncUseSshTunnel;
        target.VncSshHost = source.VncSshHost;
        target.VncSshPort = source.VncSshPort;
        target.VncSshUsername = source.VncSshUsername;
        target.VncSshPassword = source.VncSshPassword;
        target.VncSshUsePrivateKey = source.VncSshUsePrivateKey;
        target.VncSshPrivateKeyPath = source.VncSshPrivateKeyPath;
        target.VncSshRemoteHost = source.VncSshRemoteHost;
        target.VncSshRemotePort = source.VncSshRemotePort;
        target.TerminalFixedSize = source.TerminalFixedSize;
        target.TerminalResetSizeOnConnect = source.TerminalResetSizeOnConnect;
        target.TerminalColumns = source.TerminalColumns;
        target.TerminalRows = source.TerminalRows;
        target.TerminalType = source.TerminalType;
        target.TerminalScrollbackSize = source.TerminalScrollbackSize;
        target.TerminalPushClearedScreenToScrollback = source.TerminalPushClearedScreenToScrollback;
        target.TerminalEncoding = source.TerminalEncoding;
        target.TerminalTreatAmbiguousAsWide = source.TerminalTreatAmbiguousAsWide;
        target.TerminalSendLineEnding = source.TerminalSendLineEnding;
        target.TerminalReceiveLineEnding = source.TerminalReceiveLineEnding;
        target.TerminalKeyboardFunctionKeyMode = source.TerminalKeyboardFunctionKeyMode;
        target.TerminalKeyboardMappingFile = source.TerminalKeyboardMappingFile;
        target.TerminalDeleteKeySequence = source.TerminalDeleteKeySequence;
        target.TerminalBackspaceKeySequence = source.TerminalBackspaceKeySequence;
        target.TerminalLeftAltAsMeta = source.TerminalLeftAltAsMeta;
        target.TerminalRightAltAsMeta = source.TerminalRightAltAsMeta;
        target.TerminalCtrlAltAsAltGr = source.TerminalCtrlAltAsAltGr;
        target.TerminalVtAutoWrapMode = source.TerminalVtAutoWrapMode;
        target.TerminalVtOriginMode = source.TerminalVtOriginMode;
        target.TerminalVtReverseVideoMode = source.TerminalVtReverseVideoMode;
        target.TerminalVtNewLineMode = source.TerminalVtNewLineMode;
        target.TerminalVtInsertMode = source.TerminalVtInsertMode;
        target.TerminalVtEchoMode = source.TerminalVtEchoMode;
        target.TerminalVtCursorKeyMode = source.TerminalVtCursorKeyMode;
        target.TerminalVtNumericKeypadMode = source.TerminalVtNumericKeypadMode;
        target.TerminalAdvancedUseApplicationCursorMode = source.TerminalAdvancedUseApplicationCursorMode;
        target.TerminalAdvancedShiftLimitsApplicationCursorMode = source.TerminalAdvancedShiftLimitsApplicationCursorMode;
        target.TerminalAdvancedClearScreenBackground = source.TerminalAdvancedClearScreenBackground;
        target.TerminalAdvancedScrollToBottomOnInputOutput = source.TerminalAdvancedScrollToBottomOnInputOutput;
        target.TerminalAdvancedSuspendScrollToBottomOnScrollLock = source.TerminalAdvancedSuspendScrollToBottomOnScrollLock;
        target.TerminalAdvancedScrollToBottomByKey = source.TerminalAdvancedScrollToBottomByKey;
        target.TerminalAdvancedDestructiveBackspace = source.TerminalAdvancedDestructiveBackspace;
        target.TerminalAdvancedDuplicateSessionCd = source.TerminalAdvancedDuplicateSessionCd;
        target.TerminalAdvancedPreinputString = source.TerminalAdvancedPreinputString;
        target.TerminalAdvancedUseRxvtHomeEnd = source.TerminalAdvancedUseRxvtHomeEnd;
        target.TerminalAdvancedDisableBlinkingText = source.TerminalAdvancedDisableBlinkingText;
        target.TerminalAdvancedDisableTitleChange = source.TerminalAdvancedDisableTitleChange;
        target.TerminalAdvancedAllowTitleChange = source.TerminalAdvancedAllowTitleChange;
        target.TerminalAdvancedAllowOsc52Clipboard = source.TerminalAdvancedAllowOsc52Clipboard;
        target.TerminalAdvancedDisableTerminalPrint = source.TerminalAdvancedDisableTerminalPrint;
        target.TerminalAdvancedDisableAlternateScreen = source.TerminalAdvancedDisableAlternateScreen;
        target.TerminalAdvancedIgnoreResizeRequest = source.TerminalAdvancedIgnoreResizeRequest;
        target.TerminalAdvancedAnswerback = source.TerminalAdvancedAnswerback;
        target.TerminalAdvancedUseBuiltinLineDrawing = source.TerminalAdvancedUseBuiltinLineDrawing;
        target.TerminalAdvancedUseBuiltinPowerline = source.TerminalAdvancedUseBuiltinPowerline;
        target.AppearanceColorScheme = source.AppearanceColorScheme;
        target.AppearanceForegroundColor = source.AppearanceForegroundColor;
        target.AppearanceBoldForegroundColor = source.AppearanceBoldForegroundColor;
        target.AppearanceBackgroundColor = source.AppearanceBackgroundColor;
        target.AppearanceAnsiColors = source.AppearanceAnsiColors;
        target.AppearanceFontFamily = source.AppearanceFontFamily;
        target.AppearanceFontStyle = source.AppearanceFontStyle;
        target.AppearanceFontSize = source.AppearanceFontSize;
        target.AppearanceCjkFontFamily = source.AppearanceCjkFontFamily;
        target.AppearanceCjkFontStyle = source.AppearanceCjkFontStyle;
        target.AppearanceCjkFontSize = source.AppearanceCjkFontSize;
        target.AppearanceUseVariablePitchFont = source.AppearanceUseVariablePitchFont;
        target.AppearanceFontQuality = source.AppearanceFontQuality;
        target.AppearanceBoldTextMode = source.AppearanceBoldTextMode;
        target.AppearanceCursorColor = source.AppearanceCursorColor;
        target.AppearanceCursorTextColor = source.AppearanceCursorTextColor;
        target.AppearanceUseBlinkingCursor = source.AppearanceUseBlinkingCursor;
        target.AppearanceCursorBlinkSpeedMilliseconds = source.AppearanceCursorBlinkSpeedMilliseconds;
        target.AppearanceCursorShape = source.AppearanceCursorShape;
        target.AppearanceWindowPaddingTop = source.AppearanceWindowPaddingTop;
        target.AppearanceWindowPaddingBottom = source.AppearanceWindowPaddingBottom;
        target.AppearanceWindowPaddingLeft = source.AppearanceWindowPaddingLeft;
        target.AppearanceWindowPaddingRight = source.AppearanceWindowPaddingRight;
        target.AppearanceLineSpacing = source.AppearanceLineSpacing;
        target.AppearanceCharacterSpacing = source.AppearanceCharacterSpacing;
        target.AppearanceTabColorMode = source.AppearanceTabColorMode;
        target.AppearanceTabCustomColor = source.AppearanceTabCustomColor;
        target.AppearanceTabIcon = SessionTabIconCatalog.Normalize(source.AppearanceTabIcon);
        target.AppearanceBackgroundImagePath = source.AppearanceBackgroundImagePath;
        target.AppearanceBackgroundImagePosition = source.AppearanceBackgroundImagePosition;
        target.AppearanceHighlightSetId = source.AppearanceHighlightSetId;
        target.AppearanceHighlightSets = new ObservableCollection<HighlightSet>(
            source.AppearanceHighlightSets.Select(SessionEditViewModel.CloneHighlightSet));
        target.FileTransferAlwaysAskDownloadFolder = source.FileTransferAlwaysAskDownloadFolder;
        target.FileTransferDownloadDirectory = source.FileTransferDownloadDirectory;
        target.FileTransferUploadDirectory = source.FileTransferUploadDirectory;
        target.FileTransferDuplicateAction = source.FileTransferDuplicateAction;
        target.FileTransferUploadProtocol = source.FileTransferUploadProtocol;
        target.FileTransferXymodemBlockSize = source.FileTransferXymodemBlockSize;
        target.FileTransferXmodemUploadCommand = source.FileTransferXmodemUploadCommand;
        target.FileTransferYmodemUploadCommand = source.FileTransferYmodemUploadCommand;
        target.FileTransferZmodemAutoActivate = source.FileTransferZmodemAutoActivate;
        target.FileTransferZmodemUploadCommand = source.FileTransferZmodemUploadCommand;
        target.AdvancedQuickCommandSet = source.AdvancedQuickCommandSet;
        target.AdvancedDisableQuickCommandShortcuts = source.AdvancedDisableQuickCommandShortcuts;
        target.AdvancedFtpPort = source.AdvancedFtpPort;
        target.AdvancedCharacterDelayMilliseconds = source.AdvancedCharacterDelayMilliseconds;
        target.AdvancedUseLineDelay = source.AdvancedUseLineDelay;
        target.AdvancedLineDelayMilliseconds = source.AdvancedLineDelayMilliseconds;
        target.AdvancedUsePromptDelay = source.AdvancedUsePromptDelay;
        target.AdvancedPromptText = source.AdvancedPromptText;
        target.AdvancedPromptMaxWaitMilliseconds = source.AdvancedPromptMaxWaitMilliseconds;
        target.AdvancedUseNagle = source.AdvancedUseNagle;
        target.AdvancedIpVersion = source.AdvancedIpVersion;
        target.AdvancedTraceSshProtocol = source.AdvancedTraceSshProtocol;
        target.AdvancedTraceSshTunneling = source.AdvancedTraceSshTunneling;
        target.AdvancedTraceSshPackets = source.AdvancedTraceSshPackets;
        target.AdvancedTraceTelnetOptions = source.AdvancedTraceTelnetOptions;
        target.AdvancedBellMode = source.AdvancedBellMode;
        target.AdvancedBellSoundPath = source.AdvancedBellSoundPath;
        target.AdvancedBellFlashInactiveWindow = source.AdvancedBellFlashInactiveWindow;
        target.AdvancedBellIgnoreRepeatedSeconds = source.AdvancedBellIgnoreRepeatedSeconds;
        target.AdvancedBellReactivateAfterSeconds = source.AdvancedBellReactivateAfterSeconds;
        target.AdvancedLogFilePath = source.AdvancedLogFilePath;
        target.AdvancedLogOverwriteExisting = source.AdvancedLogOverwriteExisting;
        target.AdvancedLogStartOnConnect = source.AdvancedLogStartOnConnect;
        target.AdvancedLogPromptFileOnStart = source.AdvancedLogPromptFileOnStart;
        target.AdvancedLogUseRtf = source.AdvancedLogUseRtf;
        target.AdvancedLogIncludeTerminalCodes = source.AdvancedLogIncludeTerminalCodes;
        target.AdvancedLogEncoding = source.AdvancedLogEncoding;
        target.AdvancedLogWriteTimestamp = source.AdvancedLogWriteTimestamp;
        target.AdvancedLogTimestampFormat = source.AdvancedLogTimestampFormat;
    }

    partial void OnSelectedNodeChanged(SessionNodeViewModel? value)
    {
        // DataGrid raises SelectedItem before its extended selection has settled.
        // Do not write the previous SelectedNodes collection back into the grid
        // from this callback; SelectionChanged is the source of truth for rows.
        if (value == null)
        {
            SelectedNodes.Clear();
        }

        OnPropertyChanged(nameof(SelectedSession));
        NotifySelectionStateChanged();
        OnPropertyChanged(nameof(CanMoveSelectedSessionUp));
        OnPropertyChanged(nameof(CanMoveSelectedSessionDown));
    }

    partial void OnSelectedNodesChanged(ObservableCollection<SessionNodeViewModel> value)
    {
        NotifySelectionStateChanged();
        OnPropertyChanged(nameof(CanMoveSelectedSessionUp));
        OnPropertyChanged(nameof(CanMoveSelectedSessionDown));
    }

    private void NotifySelectionStateChanged()
    {
        OnPropertyChanged(nameof(CanUseSelectedSession));
        OnPropertyChanged(nameof(HasSelectedSessions));
        OnPropertyChanged(nameof(SelectedSessionCount));
        OnPropertyChanged(nameof(CanExportSelectedSessions));
        OnPropertyChanged(nameof(HasOpenSelectedSession));
        OnPropertyChanged(nameof(CanDeleteSelectedSessions));
    }

    partial void OnSessionSearchTextChanged(string value)
    {
        ApplySessionFilter();
    }

    private void LoadSessions()
    {
        SessionNodes.Clear();

        foreach (var group in _data.Groups.OrderBy(g => g.SortOrder))
        {
            var groupNode = new SessionNodeViewModel(group);
            foreach (var session in _data.Sessions.Where(s => s.GroupId == group.Id).OrderBy(s => s.SortOrder))
            {
                var sessionNode = new SessionNodeViewModel(session);
                groupNode.Children.Add(sessionNode);
            }
            SessionNodes.Add(groupNode);
        }

        foreach (var session in _data.Sessions.Where(s => s.GroupId == null).OrderBy(s => s.SortOrder))
        {
            var sessionNode = new SessionNodeViewModel(session);
            SessionNodes.Add(sessionNode);
        }

        RefreshQuickSessions();
        ApplySessionFilter();
    }

    private void ApplySessionFilter()
    {
        var selectedSessionId = SelectedSession?.Id;
        var query = SessionSearchText?.Trim();
        var rows = string.IsNullOrWhiteSpace(query)
            ? GetOrderedSessions()
            : GetOrderedSessions().Where(session => MatchesSessionSearch(session, query));

        SessionRows.Clear();
        foreach (var session in rows)
            SessionRows.Add(new SessionNodeViewModel(session));

        if (selectedSessionId.HasValue)
            SelectedNode = SessionRows.FirstOrDefault(node => node.Session?.Id == selectedSessionId.Value);
        else
            SelectedNode = null;

        SelectedNodes.Clear();
        if (SelectedNode != null)
            SelectedNodes.Add(SelectedNode);

        OnPropertyChanged(nameof(CanMoveSelectedSessionUp));
        OnPropertyChanged(nameof(CanMoveSelectedSessionDown));
    }

    private int GetSelectedVisibleSessionIndex()
    {
        var selectedSessionId = SelectedSession?.Id;
        if (!selectedSessionId.HasValue)
            return -1;

        for (var i = 0; i < SessionRows.Count; i++)
        {
            if (SessionRows[i].Session?.Id == selectedSessionId.Value)
                return i;
        }

        return -1;
    }

    private IEnumerable<SessionInfo> GetOrderedSessions()
    {
        foreach (var group in _data.Groups.OrderBy(g => g.SortOrder))
        {
            foreach (var session in _data.Sessions.Where(s => s.GroupId == group.Id).OrderBy(s => s.SortOrder))
                yield return session;
        }

        foreach (var session in _data.Sessions.Where(s => s.GroupId == null).OrderBy(s => s.SortOrder))
            yield return session;
    }

    public IReadOnlyList<SessionInfo> GetAllSessions()
    {
        return GetOrderedSessions().ToArray();
    }

    public SessionInfo? FindSessionById(Guid sessionId)
    {
        return _data.Sessions.FirstOrDefault(session => session.Id == sessionId);
    }

    public SessionLookupResult FindSessionByPath(string? sessionPath)
    {
        sessionPath = NormalizeSessionPath(sessionPath);
        if (string.IsNullOrWhiteSpace(sessionPath))
            return SessionLookupResult.NotFound();

        if (Guid.TryParse(sessionPath, out var sessionId))
        {
            var session = FindSessionById(sessionId);
            return session == null
                ? SessionLookupResult.NotFound()
                : SessionLookupResult.Found(session, BuildSessionPath(session));
        }

        var containsGroupPath = sessionPath.Contains('/', StringComparison.Ordinal);
        if (!containsGroupPath)
        {
            var exactNameMatches = _data.Sessions
                .Where(session => string.Equals(NormalizeSessionPath(session.Name), sessionPath, StringComparison.OrdinalIgnoreCase))
                .Select(ToLookupCandidate)
                .ToArray();
            if (exactNameMatches.Length == 1)
                return SessionLookupResult.Found(exactNameMatches[0].Session, exactNameMatches[0].Path);
            if (exactNameMatches.Length > 1)
                return SessionLookupResult.Ambiguous(exactNameMatches);
        }

        var pathMatches = _data.Sessions
            .Where(session => string.Equals(BuildSessionPath(session), sessionPath, StringComparison.OrdinalIgnoreCase))
            .Select(ToLookupCandidate)
            .ToArray();
        if (pathMatches.Length == 1)
            return SessionLookupResult.Found(pathMatches[0].Session, pathMatches[0].Path);
        if (pathMatches.Length > 1)
            return SessionLookupResult.Ambiguous(pathMatches);

        return SessionLookupResult.NotFound();
    }

    public string GetSessionPath(SessionInfo session) => BuildSessionPath(session);

    public string BuildSessionLaunchCommand(SessionInfo session)
    {
        var executablePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executablePath))
            executablePath = Path.Combine(AppContext.BaseDirectory, "CxShell.exe");

        return $"{QuoteCommandArgument(executablePath)} {QuoteCommandArgument(session.Id.ToString())}";
    }

    public bool UpdateSessionSecret(Guid sessionId, Action<SessionInfo> update)
    {
        var existing = _data.Sessions.FirstOrDefault(session => session.Id == sessionId);
        if (existing == null)
            return false;

        update(existing);
        _storage.Save(_data);
        LoadSessions();
        return true;
    }

    private SessionLookupCandidate ToLookupCandidate(SessionInfo session)
    {
        return new SessionLookupCandidate(session, BuildSessionPath(session));
    }

    private string BuildSessionPath(SessionInfo session)
    {
        var names = new List<string>();
        if (session.GroupId.HasValue)
            AddGroupPath(session.GroupId.Value, names, new HashSet<Guid>());

        names.Add(session.Name);
        return NormalizeSessionPath(string.Join("/", names));
    }

    private void AddGroupPath(Guid groupId, List<string> names, HashSet<Guid> visited)
    {
        if (!visited.Add(groupId))
            return;

        var group = _data.Groups.FirstOrDefault(item => item.Id == groupId);
        if (group == null)
            return;

        if (group.ParentId.HasValue)
            AddGroupPath(group.ParentId.Value, names, visited);

        names.Add(group.Name);
    }

    private static string NormalizeSessionPath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        value = value.Trim().Trim('"', '\'').Replace('\\', '/');
        while (value.Contains("//", StringComparison.Ordinal))
            value = value.Replace("//", "/", StringComparison.Ordinal);

        return value.Trim('/');
    }

    private static string QuoteCommandArgument(string value)
    {
        return "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
    }

    private static bool MatchesSessionSearch(SessionInfo session, string query)
    {
        return Contains(session.Name, query) ||
               Contains(session.Host, query) ||
               Contains(session.Username, query) ||
               Contains(session.Protocol.ToString(), query) ||
               session.Port.ToString().Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    private static bool Contains(string? value, string query)
    {
        return !string.IsNullOrWhiteSpace(value) &&
               value.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    public void AddSession(SessionInfo session)
    {
        _data.Sessions.Add(session);
        _storage.Save(_data);
        LoadSessions();
    }

    public void UpdateSession(SessionInfo session)
    {
        var existing = _data.Sessions.FirstOrDefault(s => s.Id == session.Id);
        if (existing != null)
        {
            existing.Name = session.Name;
            CopySessionValues(existing, session);
            _storage.Save(_data);
            LoadSessions();
        }
    }

    public void DeleteSession(SessionInfo session)
    {
        _data.Sessions.RemoveAll(s => s.Id == session.Id);
        _data.QuickSessionIds.RemoveAll(id => id == session.Id);
        _storage.Save(_data);
        LoadSessions();
        SelectedNode = null;
    }

    public IReadOnlyList<SessionInfo> GetSelectedSessions()
    {
        return SelectedNodes
            .Select(node => node.Session)
            .Where(session => session != null)
            .Cast<SessionInfo>()
            .DistinctBy(session => session.Id)
            .ToList();
    }

    public void SetSelectedNodes(IEnumerable<SessionNodeViewModel> nodes)
    {
        var selected = nodes
            .Where(node => node.Session != null)
            .Distinct()
            .ToList();

        SelectedNodes.Clear();
        foreach (var node in selected)
            SelectedNodes.Add(node);

        if (selected.Count == 1)
            SelectedNode = selected[0];
        else if (selected.Count == 0)
            SelectedNode = null;

        NotifySelectionStateChanged();
    }

    public void ExportSelectedSessions(string path)
    {
        var sessions = GetSelectedSessions();
        if (sessions.Count == 0)
            throw new InvalidOperationException("No sessions are selected.");

        SessionTransferService.Export(path, _data.Groups, sessions);
    }

    public int ImportSessions(string path)
    {
        var package = SessionTransferService.Load(path);
        var groupMap = new Dictionary<Guid, Guid>();
        var importedGroups = new List<SessionGroup>();

        foreach (var group in package.Groups.OrderBy(item => GetGroupDepth(item, package.Groups)))
        {
            Guid? parentId = group.ParentId.HasValue && groupMap.TryGetValue(group.ParentId.Value, out var mappedParent)
                ? mappedParent
                : null;
            var name = CreateUniqueGroupName(group.Name, parentId, importedGroups);
            var imported = SessionTransferService.CloneImportedGroup(
                group,
                Guid.NewGuid(),
                parentId,
                name,
                _data.Groups.Count + importedGroups.Count);
            imported.ParentId = parentId;
            groupMap[group.Id] = imported.Id;
            importedGroups.Add(imported);
        }

        var importedSessions = new List<SessionInfo>();
        var nextSortOrder = _data.Sessions.Count == 0 ? 0 : _data.Sessions.Max(session => session.SortOrder) + 1;
        foreach (var source in package.Sessions)
        {
            Guid? groupId = source.GroupId.HasValue && groupMap.TryGetValue(source.GroupId.Value, out var mappedGroup)
                ? mappedGroup
                : null;
            var name = CreateUniqueSessionName(source.Name, importedSessions);
            importedSessions.Add(SessionTransferService.CloneImportedSession(source, groupId, name, nextSortOrder++));
        }

        _data.Groups.AddRange(importedGroups);
        _data.Sessions.AddRange(importedSessions);
        _storage.Save(_data);
        LoadSessions();

        if (importedSessions.Count > 0)
            SelectedNode = SessionRows.FirstOrDefault(node => node.Session?.Id == importedSessions[0].Id);

        return importedSessions.Count;
    }

    public SessionImportResult ImportFile(string path)
    {
        var content = File.ReadAllText(path);
        if (OpenSshConfigParser.LooksLikeConfig(content))
            return new SessionImportResult(ImportOpenSshConfig(path, content), true);

        return new SessionImportResult(ImportSessions(path), false);
    }

    private int ImportOpenSshConfig(string path, string content)
    {
        var entries = OpenSshConfigParser.Parse(content, path);
        if (entries.Count == 0)
            throw new InvalidDataException("The OpenSSH config contains no concrete Host entries.");

        var importedSessions = new List<SessionInfo>();
        var nextSortOrder = _data.Sessions.Count == 0 ? 0 : _data.Sessions.Max(session => session.SortOrder) + 1;
        foreach (var entry in entries)
        {
            var session = new SessionInfo
            {
                Id = Guid.NewGuid(),
                Name = CreateUniqueSessionName(entry.Alias, importedSessions),
                Host = entry.Host,
                Port = entry.Port,
                Username = entry.Username,
                Protocol = SessionProtocol.SSH,
                AuthMethod = entry.IdentityFile != null ? AuthMethod.PrivateKey : AuthMethod.Password,
                PrivateKeyPath = entry.IdentityFile,
                SortOrder = nextSortOrder++
            };

            ConfigureImportedJumpHosts(session, entry);
            importedSessions.Add(session);
        }

        _data.Sessions.AddRange(importedSessions);
        _storage.Save(_data);
        LoadSessions();
        SelectedNode = SessionRows.FirstOrDefault(node => node.Session?.Id == importedSessions[0].Id);
        return importedSessions.Count;
    }

    private static void ConfigureImportedJumpHosts(SessionInfo session, OpenSshConfigEntry entry)
    {
        if (entry.JumpHosts.Count == 0)
            return;

        var proxies = entry.JumpHosts.Select(jump => new ProxySettings
        {
            Protocol = ProxyProtocol.JumpHost,
            Host = jump.Host,
            Port = jump.Port,
            Username = jump.Username,
            AuthMethod = entry.IdentityFile != null ? AuthMethod.PrivateKey : AuthMethod.Password,
            PrivateKeyPath = entry.IdentityFile ?? string.Empty,
            UseAgent = true
        }).ToList();

        for (var index = 0; index < proxies.Count - 1; index++)
            proxies[index].NextProxyId = proxies[index + 1].Id;

        session.Proxy = proxies[0];
        session.ProxyServers = proxies.Skip(1).ToList();
    }

    public void DeleteSelectedSessions()
    {
        var sessions = GetSelectedSessions();
        if (sessions.Count == 0 || sessions.Any(session => _mainWindow.Tabs.Any(tab => tab.Session.Id == session.Id)))
            return;

        var ids = sessions.Select(session => session.Id).ToHashSet();
        _data.Sessions.RemoveAll(session => ids.Contains(session.Id));
        _data.QuickSessionIds.RemoveAll(ids.Contains);
        _storage.Save(_data);
        LoadSessions();
        SelectedNode = null;
    }

    private string CreateUniqueSessionName(string sourceName, IEnumerable<SessionInfo> pending)
    {
        var baseName = string.IsNullOrWhiteSpace(sourceName) ? "Imported session" : sourceName.Trim();
        var name = baseName;
        var suffix = 2;
        while (_data.Sessions.Any(session => string.Equals(session.Name, name, StringComparison.OrdinalIgnoreCase)) ||
               pending.Any(session => string.Equals(session.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            name = $"{baseName} ({suffix++})";
        }

        return name;
    }

    private string CreateUniqueGroupName(string sourceName, Guid? parentId, IEnumerable<SessionGroup> pending)
    {
        var baseName = string.IsNullOrWhiteSpace(sourceName) ? "Imported group" : sourceName.Trim();
        var name = baseName;
        var suffix = 2;
        while (_data.Groups.Any(group => group.ParentId == parentId && string.Equals(group.Name, name, StringComparison.OrdinalIgnoreCase)) ||
               pending.Any(group => group.ParentId == parentId && string.Equals(group.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            name = $"{baseName} ({suffix++})";
        }

        return name;
    }

    private static int GetGroupDepth(SessionGroup group, IReadOnlyCollection<SessionGroup> groups)
    {
        var depth = 0;
        var parentId = group.ParentId;
        var visited = new HashSet<Guid>();
        while (parentId.HasValue && visited.Add(parentId.Value))
        {
            var parent = groups.FirstOrDefault(item => item.Id == parentId.Value);
            if (parent == null)
                break;

            depth++;
            parentId = parent.ParentId;
        }

        return depth;
    }

    public void MoveSelectedSessionUp()
    {
        MoveSelectedSession(-1);
    }

    public void MoveSelectedSessionDown()
    {
        MoveSelectedSession(1);
    }

    private void MoveSelectedSession(int direction)
    {
        var selected = SelectedSession;
        if (selected == null)
            return;

        var visibleSessions = SessionRows
            .Select(row => row.Session)
            .Where(session => session != null)
            .Cast<SessionInfo>()
            .ToList();
        var visibleIndex = visibleSessions.FindIndex(session => session.Id == selected.Id);
        var targetVisibleIndex = visibleIndex + direction;
        if (visibleIndex < 0 || targetVisibleIndex < 0 || targetVisibleIndex >= visibleSessions.Count)
            return;

        var target = visibleSessions[targetVisibleIndex];
        var selectedIndex = _data.Sessions.FindIndex(session => session.Id == selected.Id);
        var targetIndex = _data.Sessions.FindIndex(session => session.Id == target.Id);
        if (selectedIndex < 0 || targetIndex < 0)
            return;

        (_data.Sessions[selectedIndex], _data.Sessions[targetIndex]) =
            (_data.Sessions[targetIndex], _data.Sessions[selectedIndex]);
        NormalizeSessionSortOrders();
        _storage.Save(_data);
        LoadSessions();
        SelectedNode = SessionRows.FirstOrDefault(node => node.Session?.Id == selected.Id);
    }

    private void NormalizeSessionSortOrders()
    {
        for (var i = 0; i < _data.Sessions.Count; i++)
            _data.Sessions[i].SortOrder = i;
    }

    public bool IsQuickSession(SessionInfo session)
    {
        return _data.QuickSessionIds.Contains(session.Id);
    }

    public void AddQuickSession(SessionInfo session)
    {
        if (_data.Sessions.All(existing => existing.Id != session.Id))
            return;

        if (!_data.QuickSessionIds.Contains(session.Id))
        {
            _data.QuickSessionIds.Add(session.Id);
            _storage.Save(_data);
        }

        RefreshQuickSessions();
    }

    public void RemoveQuickSession(SessionInfo session)
    {
        if (_data.QuickSessionIds.RemoveAll(id => id == session.Id) > 0)
            _storage.Save(_data);

        RefreshQuickSessions();
    }

    public void MoveQuickSession(SessionInfo source, SessionInfo target, bool insertAfter)
    {
        if (source.Id == target.Id)
            return;

        var sourceIndex = _data.QuickSessionIds.IndexOf(source.Id);
        var targetIndex = _data.QuickSessionIds.IndexOf(target.Id);
        if (sourceIndex < 0 || targetIndex < 0)
            return;

        _data.QuickSessionIds.RemoveAt(sourceIndex);
        var insertIndex = _data.QuickSessionIds.IndexOf(target.Id);
        if (insertIndex < 0)
            return;

        if (insertAfter)
            insertIndex++;

        insertIndex = Math.Clamp(insertIndex, 0, _data.QuickSessionIds.Count);
        _data.QuickSessionIds.Insert(insertIndex, source.Id);
        _storage.Save(_data);
        RefreshQuickSessions();
    }

    private void RefreshQuickSessions()
    {
        QuickSessions.Clear();

        var missingIds = false;
        foreach (var id in _data.QuickSessionIds.ToList())
        {
            var session = _data.Sessions.FirstOrDefault(s => s.Id == id);
            if (session == null)
            {
                missingIds = true;
                continue;
            }

            QuickSessions.Add(session);
        }

        if (missingIds)
        {
            _data.QuickSessionIds.RemoveAll(id => _data.Sessions.All(session => session.Id != id));
            _storage.Save(_data);
        }
    }

    public void CopySelectedSession()
    {
        if (SelectedSession == null)
            return;

        _copiedSession = CloneSession(SelectedSession, SelectedSession.Name);
        OnPropertyChanged(nameof(CanPaste));
    }

    public void PasteCopiedSession()
    {
        if (_copiedSession == null)
            return;

        var name = CreateUniqueCopyName(_copiedSession.Name);
        var pastedSession = CloneSession(_copiedSession, name);
        pastedSession.SortOrder = _data.Sessions.Count == 0
            ? 0
            : _data.Sessions.Max(session => session.SortOrder) + 1;

        AddSession(pastedSession);
        SelectedNode = SessionRows.FirstOrDefault(node => node.Session?.Id == pastedSession.Id);
    }

    private string CreateUniqueCopyName(string sourceName)
    {
        var baseName = string.Format(LocalizationService.Shared.Text("Session.CopySuffix"), sourceName);
        var name = baseName;
        var suffix = 2;

        while (_data.Sessions.Any(session => string.Equals(session.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            name = $"{baseName} ({suffix++})";
        }

        return name;
    }

    private static SessionInfo CloneSession(SessionInfo source, string name)
    {
        return new SessionInfo
        {
            Id = Guid.NewGuid(),
            Name = name,
            GroupId = source.GroupId,
            Host = source.Host,
            Port = source.Port,
            Username = source.Username,
            Proxy = CloneProxy(source.Proxy),
            ProxyServers = source.ProxyServers.Select(CloneProxy).ToList(),
            SelectedProxyId = source.SelectedProxyId,
            Protocol = source.Protocol,
            AuthMethod = source.AuthMethod,
            Password = source.Password,
            PrivateKeyPath = source.PrivateKeyPath,
            PrivateKeyPassphrase = source.PrivateKeyPassphrase,
            RuntimePrivateKeyPassphrase = source.RuntimePrivateKeyPassphrase,
            AutoReconnect = source.AutoReconnect,
            ReconnectIntervalSeconds = source.ReconnectIntervalSeconds,
            ReconnectLimitMinutes = source.ReconnectLimitMinutes,
            SendSessionKeepAlive = source.SendSessionKeepAlive,
            SessionKeepAliveIntervalSeconds = source.SessionKeepAliveIntervalSeconds,
            SendIdleString = source.SendIdleString,
            IdleStringIntervalSeconds = source.IdleStringIntervalSeconds,
            IdleString = source.IdleString,
            TcpKeepAlive = source.TcpKeepAlive,
            EnableLoginScriptRules = source.EnableLoginScriptRules,
            LoginScriptRules = source.LoginScriptRules.Select(SessionEditViewModel.CloneLoginScriptRule).ToList(),
            RunLoginScriptFile = source.RunLoginScriptFile,
            LoginScriptFilePath = source.LoginScriptFilePath,
            LoginScriptParameters = source.LoginScriptParameters,
            SshRemoteCommand = source.SshRemoteCommand,
            SshVersionPolicy = source.SshVersionPolicy,
            SshUseXagent = source.SshUseXagent,
            SshForwardAgent = source.SshForwardAgent,
            SshUseCompression = source.SshUseCompression,
            SshNoTerminal = source.SshNoTerminal,
            SshAcceptAndSaveHostKey = source.SshAcceptAndSaveHostKey,
            SshAutoOpenSftpPanel = source.SshAutoOpenSftpPanel,
            SshAutoOpenMonitorPanel = source.SshAutoOpenMonitorPanel,
            SshEnableServerMonitoring = source.SshEnableServerMonitoring,
            SshEnableMonitorNetworkLatency = source.SshEnableMonitorNetworkLatency,
            SshMonitorRefreshIntervalSeconds = source.SshMonitorRefreshIntervalSeconds,
            SshDoNotStartFileManager = !source.SshAutoOpenSftpPanel,
            SshCipherAlgorithms = source.SshCipherAlgorithms,
            SshMacAlgorithms = source.SshMacAlgorithms,
            SshKeyExchangeAlgorithms = source.SshKeyExchangeAlgorithms,
            SshTunnelRules = source.SshTunnelRules.Select(SessionEditViewModel.CloneTunnelRule).ToList(),
            SshAutoRestoreTunnels = source.SshAutoRestoreTunnels,
            SshForwardX11 = source.SshForwardX11,
            SshX11UseXmanager = source.SshX11UseXmanager,
            SshX11Display = source.SshX11Display,
            TelnetUseXDisplayLocation = source.TelnetUseXDisplayLocation,
            TelnetXDisplayLocation = source.TelnetXDisplayLocation,
            TelnetOptionMode = source.TelnetOptionMode,
            TelnetForceCharacterAtATime = source.TelnetForceCharacterAtATime,
            TelnetUsernamePrompt = source.TelnetUsernamePrompt,
            TelnetPasswordPrompt = source.TelnetPasswordPrompt,
            RloginPasswordPrompt = source.RloginPasswordPrompt,
            RloginTerminalSpeed = source.RloginTerminalSpeed,
            SftpLocalStartDirectory = source.SftpLocalStartDirectory,
            SftpRemoteStartDirectory = source.SftpRemoteStartDirectory,
            SftpFollowTerminalDirectory = source.SftpFollowTerminalDirectory,
            SftpUseCustomServer = source.SftpUseCustomServer,
            SftpCustomServerCommand = source.SftpCustomServerCommand,
            SerialPortName = source.SerialPortName,
            SerialBaudRate = source.SerialBaudRate,
            SerialDataBits = source.SerialDataBits,
            SerialParity = source.SerialParity,
            SerialStopBits = source.SerialStopBits,
            SerialFlowControl = source.SerialFlowControl,
            RdpWindowSize = source.RdpWindowSize,
            RdpDesktopWidth = source.RdpDesktopWidth,
            RdpDesktopHeight = source.RdpDesktopHeight,
            RdpResizeMode = source.RdpResizeMode,
            RdpScreenScale = source.RdpScreenScale,
            RdpColorQuality = source.RdpColorQuality,
            RdpApplyKeyCombinations = source.RdpApplyKeyCombinations,
            RdpRedirectDrives = source.RdpRedirectDrives,
            RdpDriveName = source.RdpDriveName,
            RdpDrivePath = source.RdpDrivePath,
            RdpAudioMode = source.RdpAudioMode,
            RdpAudioCapture = source.RdpAudioCapture,
            RdpMicrophoneEnabled = source.RdpMicrophoneEnabled,
            RdpUseSshTunnel = source.RdpUseSshTunnel,
            RdpSshHost = source.RdpSshHost,
            RdpSshPort = source.RdpSshPort,
            RdpSshUsername = source.RdpSshUsername,
            RdpSshPassword = source.RdpSshPassword,
            RdpSshUsePrivateKey = source.RdpSshUsePrivateKey,
            RdpSshPrivateKeyPath = source.RdpSshPrivateKeyPath,
            VncDisplayMode = source.VncDisplayMode,
            VncScalePercent = source.VncScalePercent,
            VncResizeMode = source.VncResizeMode,
            VncReadOnlyMode = source.VncReadOnlyMode,
            VncEnableKeyboardInput = source.VncEnableKeyboardInput,
            VncEnableMouseInput = source.VncEnableMouseInput,
            VncCaptureShortcuts = source.VncCaptureShortcuts,
            VncCursorMode = source.VncCursorMode,
            VncShowToolbarButtons = source.VncShowToolbarButtons,
            VncClipboardMode = source.VncClipboardMode,
            VncEncodingProfile = source.VncEncodingProfile,
            VncFramebufferUpdateDelayMilliseconds = source.VncFramebufferUpdateDelayMilliseconds,
            VncCompressionLevel = source.VncCompressionLevel,
            VncJpegQualityLevel = source.VncJpegQualityLevel,
            VncJpegSubsampling = source.VncJpegSubsampling,
            VncUseSshTunnel = source.VncUseSshTunnel,
            VncSshHost = source.VncSshHost,
            VncSshPort = source.VncSshPort,
            VncSshUsername = source.VncSshUsername,
            VncSshPassword = source.VncSshPassword,
            VncSshUsePrivateKey = source.VncSshUsePrivateKey,
            VncSshPrivateKeyPath = source.VncSshPrivateKeyPath,
            VncSshRemoteHost = source.VncSshRemoteHost,
            VncSshRemotePort = source.VncSshRemotePort,
            TerminalFixedSize = source.TerminalFixedSize,
            TerminalColumns = source.TerminalColumns,
            TerminalRows = source.TerminalRows,
            TerminalType = source.TerminalType,
            TerminalEncoding = source.TerminalEncoding,
            TerminalTreatAmbiguousAsWide = source.TerminalTreatAmbiguousAsWide,
            TerminalSendLineEnding = source.TerminalSendLineEnding,
            TerminalReceiveLineEnding = source.TerminalReceiveLineEnding,
            TerminalKeyboardFunctionKeyMode = source.TerminalKeyboardFunctionKeyMode,
            TerminalKeyboardMappingFile = source.TerminalKeyboardMappingFile,
            TerminalDeleteKeySequence = source.TerminalDeleteKeySequence,
            TerminalBackspaceKeySequence = source.TerminalBackspaceKeySequence,
            TerminalLeftAltAsMeta = source.TerminalLeftAltAsMeta,
            TerminalRightAltAsMeta = source.TerminalRightAltAsMeta,
            TerminalCtrlAltAsAltGr = source.TerminalCtrlAltAsAltGr,
            TerminalVtAutoWrapMode = source.TerminalVtAutoWrapMode,
            TerminalVtOriginMode = source.TerminalVtOriginMode,
            TerminalVtReverseVideoMode = source.TerminalVtReverseVideoMode,
            TerminalVtNewLineMode = source.TerminalVtNewLineMode,
            TerminalVtInsertMode = source.TerminalVtInsertMode,
            TerminalVtEchoMode = source.TerminalVtEchoMode,
            TerminalVtCursorKeyMode = source.TerminalVtCursorKeyMode,
            TerminalVtNumericKeypadMode = source.TerminalVtNumericKeypadMode,
            TerminalAdvancedUseApplicationCursorMode = source.TerminalAdvancedUseApplicationCursorMode,
            TerminalAdvancedShiftLimitsApplicationCursorMode = source.TerminalAdvancedShiftLimitsApplicationCursorMode,
            TerminalAdvancedClearScreenBackground = source.TerminalAdvancedClearScreenBackground,
            TerminalAdvancedScrollToBottomOnInputOutput = source.TerminalAdvancedScrollToBottomOnInputOutput,
            TerminalAdvancedSuspendScrollToBottomOnScrollLock = source.TerminalAdvancedSuspendScrollToBottomOnScrollLock,
            TerminalAdvancedScrollToBottomByKey = source.TerminalAdvancedScrollToBottomByKey,
            TerminalAdvancedDuplicateSessionCd = source.TerminalAdvancedDuplicateSessionCd,
            TerminalAdvancedPreinputString = source.TerminalAdvancedPreinputString,
            TerminalAdvancedUseRxvtHomeEnd = source.TerminalAdvancedUseRxvtHomeEnd,
            TerminalAdvancedDisableBlinkingText = source.TerminalAdvancedDisableBlinkingText,
            TerminalAdvancedDisableTitleChange = source.TerminalAdvancedDisableTitleChange,
            TerminalAdvancedAllowTitleChange = source.TerminalAdvancedAllowTitleChange,
            TerminalAdvancedAllowOsc52Clipboard = source.TerminalAdvancedAllowOsc52Clipboard,
            TerminalAdvancedDisableTerminalPrint = source.TerminalAdvancedDisableTerminalPrint,
            TerminalAdvancedDisableAlternateScreen = source.TerminalAdvancedDisableAlternateScreen,
            TerminalAdvancedIgnoreResizeRequest = source.TerminalAdvancedIgnoreResizeRequest,
            TerminalAdvancedAnswerback = source.TerminalAdvancedAnswerback,
            TerminalAdvancedUseBuiltinLineDrawing = source.TerminalAdvancedUseBuiltinLineDrawing,
            TerminalAdvancedUseBuiltinPowerline = source.TerminalAdvancedUseBuiltinPowerline,
            AppearanceColorScheme = source.AppearanceColorScheme,
            AppearanceForegroundColor = source.AppearanceForegroundColor,
            AppearanceBoldForegroundColor = source.AppearanceBoldForegroundColor,
            AppearanceBackgroundColor = source.AppearanceBackgroundColor,
            AppearanceAnsiColors = source.AppearanceAnsiColors,
            AppearanceFontFamily = source.AppearanceFontFamily,
            AppearanceFontStyle = source.AppearanceFontStyle,
            AppearanceFontSize = source.AppearanceFontSize,
            AppearanceCjkFontFamily = source.AppearanceCjkFontFamily,
            AppearanceCjkFontStyle = source.AppearanceCjkFontStyle,
            AppearanceCjkFontSize = source.AppearanceCjkFontSize,
            AppearanceUseVariablePitchFont = source.AppearanceUseVariablePitchFont,
            AppearanceFontQuality = source.AppearanceFontQuality,
            AppearanceBoldTextMode = source.AppearanceBoldTextMode,
            AppearanceCursorColor = source.AppearanceCursorColor,
            AppearanceCursorTextColor = source.AppearanceCursorTextColor,
            AppearanceUseBlinkingCursor = source.AppearanceUseBlinkingCursor,
            AppearanceCursorBlinkSpeedMilliseconds = source.AppearanceCursorBlinkSpeedMilliseconds,
            AppearanceCursorShape = source.AppearanceCursorShape,
            AppearanceWindowPaddingTop = source.AppearanceWindowPaddingTop,
            AppearanceWindowPaddingBottom = source.AppearanceWindowPaddingBottom,
            AppearanceWindowPaddingLeft = source.AppearanceWindowPaddingLeft,
            AppearanceWindowPaddingRight = source.AppearanceWindowPaddingRight,
            AppearanceLineSpacing = source.AppearanceLineSpacing,
            AppearanceCharacterSpacing = source.AppearanceCharacterSpacing,
            AppearanceTabColorMode = source.AppearanceTabColorMode,
            AppearanceTabCustomColor = source.AppearanceTabCustomColor,
            AppearanceTabIcon = SessionTabIconCatalog.Normalize(source.AppearanceTabIcon),
            AppearanceBackgroundImagePath = source.AppearanceBackgroundImagePath,
            AppearanceBackgroundImagePosition = source.AppearanceBackgroundImagePosition,
            AppearanceHighlightSetId = source.AppearanceHighlightSetId,
            AppearanceHighlightSets = new ObservableCollection<HighlightSet>(
                source.AppearanceHighlightSets.Select(SessionEditViewModel.CloneHighlightSet)),
            FileTransferAlwaysAskDownloadFolder = source.FileTransferAlwaysAskDownloadFolder,
            FileTransferDownloadDirectory = source.FileTransferDownloadDirectory,
            FileTransferUploadDirectory = source.FileTransferUploadDirectory,
            FileTransferDuplicateAction = source.FileTransferDuplicateAction,
            FileTransferUploadProtocol = source.FileTransferUploadProtocol,
            FileTransferXymodemBlockSize = source.FileTransferXymodemBlockSize,
            FileTransferXmodemUploadCommand = source.FileTransferXmodemUploadCommand,
            FileTransferYmodemUploadCommand = source.FileTransferYmodemUploadCommand,
            FileTransferZmodemAutoActivate = source.FileTransferZmodemAutoActivate,
            FileTransferZmodemUploadCommand = source.FileTransferZmodemUploadCommand,
            AdvancedBellMode = source.AdvancedBellMode,
            AdvancedBellSoundPath = source.AdvancedBellSoundPath,
            AdvancedBellFlashInactiveWindow = source.AdvancedBellFlashInactiveWindow,
            AdvancedBellIgnoreRepeatedSeconds = source.AdvancedBellIgnoreRepeatedSeconds,
            AdvancedBellReactivateAfterSeconds = source.AdvancedBellReactivateAfterSeconds,
            AdvancedLogFilePath = source.AdvancedLogFilePath,
            AdvancedLogOverwriteExisting = source.AdvancedLogOverwriteExisting,
            AdvancedLogStartOnConnect = source.AdvancedLogStartOnConnect,
            AdvancedLogPromptFileOnStart = source.AdvancedLogPromptFileOnStart,
            AdvancedLogUseRtf = source.AdvancedLogUseRtf,
            AdvancedLogIncludeTerminalCodes = source.AdvancedLogIncludeTerminalCodes,
            AdvancedLogEncoding = source.AdvancedLogEncoding,
            AdvancedLogWriteTimestamp = source.AdvancedLogWriteTimestamp,
            AdvancedLogTimestampFormat = source.AdvancedLogTimestampFormat,
            SortOrder = source.SortOrder,
            CreatedAt = DateTime.Now
        };
    }

    private static ProxySettings CloneProxy(ProxySettings? source)
    {
        source ??= new ProxySettings();
        return new ProxySettings
        {
            Protocol = source.Protocol,
            Id = source.Id,
            Name = source.Name,
            Host = source.Host,
            Port = source.Port,
            Username = source.Username,
            Password = source.Password,
            AuthMethod = source.AuthMethod,
            PrivateKeyPath = source.PrivateKeyPath,
            PrivateKeyPassphrase = source.PrivateKeyPassphrase,
            RuntimePrivateKeyPassphrase = source.RuntimePrivateKeyPassphrase,
            UseAgent = source.UseAgent,
            UseSessionFile = source.UseSessionFile,
            SessionFilePath = source.SessionFilePath,
            NextProxyId = source.NextProxyId
        };
    }
}
