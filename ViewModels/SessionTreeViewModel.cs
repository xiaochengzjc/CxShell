using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using ChiXueSsh.Models;
using ChiXueSsh.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ChiXueSsh.ViewModels;

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
    [ObservableProperty] private SessionNodeViewModel? _selectedNode;
    [ObservableProperty] private string _sessionSearchText = string.Empty;

    private readonly SessionStorageService _storage;
    private readonly MainWindowViewModel _mainWindow;
    private SessionData _data;
    private SessionInfo? _copiedSession;

    public SessionInfo? SelectedSession => SelectedNode?.Session;
    public bool CanUseSelectedSession => SelectedSession != null;
    public bool CanPaste => _copiedSession != null;

    public MainWindowViewModel MainWindow => _mainWindow;
    public ApplicationSettings Settings => _data.Settings;

    public SessionTreeViewModel(MainWindowViewModel mainWindow)
    {
        _mainWindow = mainWindow;
        _storage = new SessionStorageService();
        _data = _storage.Load();
        _data.Settings ??= new ApplicationSettings();
        _data.Settings.GlobalDefaults ??= ApplicationSettings.CreateDefaultSession();
        LoadSessions();
    }

    public SessionInfo CreateSessionFromGlobalDefaults()
    {
        return new SessionInfo
        {
            Id = Guid.NewGuid(),
            Name = string.Empty,
            Host = string.Empty,
            Username = string.Empty
        };
    }

    public void SaveSettings(ApplicationSettings settings)
    {
        _data.Settings = settings;
        _data.Settings.GlobalDefaults ??= ApplicationSettings.CreateDefaultSession();
        _storage.Save(_data);
    }

    public SessionInfo GetEffectiveSession(SessionInfo session)
    {
        var defaults = _data.Settings.GlobalDefaults ?? ApplicationSettings.CreateDefaultSession();
        return MergeSession(defaults, session);
    }

    private static SessionInfo MergeSession(SessionInfo defaults, SessionInfo session)
    {
        var effective = CloneSession(defaults, session.Name);
        effective.Id = session.Id;
        effective.GroupId = session.GroupId;
        ApplySessionConnectionOverrides(effective, session);
        return effective;
    }

    private static void ApplySessionConnectionOverrides(SessionInfo target, SessionInfo source)
    {
        target.Proxy = CloneProxy(source.Proxy);
        target.ProxyServers = source.ProxyServers.Select(CloneProxy).ToList();
        target.SelectedProxyId = source.SelectedProxyId;
        target.Host = source.Host;
        target.Port = source.Port;
        target.Username = source.Username;
        target.Protocol = source.Protocol;
        target.AuthMethod = source.AuthMethod;
        target.PrivateKeyPath = source.PrivateKeyPath;
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
        target.SshDoNotStartFileManager = source.SshDoNotStartFileManager;
        target.SshCipherAlgorithms = source.SshCipherAlgorithms;
        target.SshMacAlgorithms = source.SshMacAlgorithms;
        target.SshKeyExchangeAlgorithms = source.SshKeyExchangeAlgorithms;
        target.SshTunnelRules = source.SshTunnelRules.Select(SessionEditViewModel.CloneTunnelRule).ToList();
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
        target.RdpAudioMode = source.RdpAudioMode;
        target.RdpAudioCapture = source.RdpAudioCapture;
    }

    partial void OnSelectedNodeChanged(SessionNodeViewModel? value)
    {
        OnPropertyChanged(nameof(SelectedSession));
        OnPropertyChanged(nameof(CanUseSelectedSession));
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
            ApplySessionConnectionOverrides(existing, session);
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
        var baseName = $"{sourceName} - 副本";
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
            PrivateKeyPath = source.PrivateKeyPath,
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
            SshDoNotStartFileManager = source.SshDoNotStartFileManager,
            SshCipherAlgorithms = source.SshCipherAlgorithms,
            SshMacAlgorithms = source.SshMacAlgorithms,
            SshKeyExchangeAlgorithms = source.SshKeyExchangeAlgorithms,
            SshTunnelRules = source.SshTunnelRules.Select(SessionEditViewModel.CloneTunnelRule).ToList(),
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
            RdpAudioMode = source.RdpAudioMode,
            RdpAudioCapture = source.RdpAudioCapture,
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
            UseSessionFile = source.UseSessionFile,
            SessionFilePath = source.SessionFilePath,
            NextProxyId = source.NextProxyId
        };
    }
}
