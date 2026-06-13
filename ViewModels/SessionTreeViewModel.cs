using System;
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

    private readonly SessionStorageService _storage;
    private readonly MainWindowViewModel _mainWindow;
    private SessionData _data;
    private SessionInfo? _copiedSession;

    public SessionInfo? SelectedSession => SelectedNode?.Session;
    public bool CanUseSelectedSession => SelectedSession != null;
    public bool CanPaste => _copiedSession != null;

    public MainWindowViewModel MainWindow => _mainWindow;

    public SessionTreeViewModel(MainWindowViewModel mainWindow)
    {
        _mainWindow = mainWindow;
        _storage = new SessionStorageService();
        _data = _storage.Load();
        LoadSessions();
    }

    partial void OnSelectedNodeChanged(SessionNodeViewModel? value)
    {
        OnPropertyChanged(nameof(SelectedSession));
        OnPropertyChanged(nameof(CanUseSelectedSession));
    }

    private void LoadSessions()
    {
        SessionNodes.Clear();
        SessionRows.Clear();

        foreach (var group in _data.Groups.OrderBy(g => g.SortOrder))
        {
            var groupNode = new SessionNodeViewModel(group);
            foreach (var session in _data.Sessions.Where(s => s.GroupId == group.Id).OrderBy(s => s.SortOrder))
            {
                var sessionNode = new SessionNodeViewModel(session);
                groupNode.Children.Add(sessionNode);
                SessionRows.Add(sessionNode);
            }
            SessionNodes.Add(groupNode);
        }

        foreach (var session in _data.Sessions.Where(s => s.GroupId == null).OrderBy(s => s.SortOrder))
        {
            var sessionNode = new SessionNodeViewModel(session);
            SessionNodes.Add(sessionNode);
            SessionRows.Add(sessionNode);
        }

        RefreshQuickSessions();
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
            existing.Host = session.Host;
            existing.Port = session.Port;
            existing.Username = session.Username;
            existing.Proxy = CloneProxy(session.Proxy);
            existing.ProxyServers = session.ProxyServers.Select(CloneProxy).ToList();
            existing.SelectedProxyId = session.SelectedProxyId;
            existing.Protocol = session.Protocol;
            existing.AuthMethod = session.AuthMethod;
            existing.PrivateKeyPath = session.PrivateKeyPath;
            existing.AutoReconnect = session.AutoReconnect;
            existing.ReconnectIntervalSeconds = session.ReconnectIntervalSeconds;
            existing.ReconnectLimitMinutes = session.ReconnectLimitMinutes;
            existing.SendSessionKeepAlive = session.SendSessionKeepAlive;
            existing.SessionKeepAliveIntervalSeconds = session.SessionKeepAliveIntervalSeconds;
            existing.SendIdleString = session.SendIdleString;
            existing.IdleStringIntervalSeconds = session.IdleStringIntervalSeconds;
            existing.IdleString = session.IdleString;
            existing.TcpKeepAlive = session.TcpKeepAlive;
            existing.SshRemoteCommand = session.SshRemoteCommand;
            existing.SshVersionPolicy = session.SshVersionPolicy;
            existing.SshUseXagent = session.SshUseXagent;
            existing.SshForwardAgent = session.SshForwardAgent;
            existing.SshUseCompression = session.SshUseCompression;
            existing.SshNoTerminal = session.SshNoTerminal;
            existing.SshAcceptAndSaveHostKey = session.SshAcceptAndSaveHostKey;
            existing.SshDoNotStartFileManager = session.SshDoNotStartFileManager;
            existing.SshCipherAlgorithms = session.SshCipherAlgorithms;
            existing.SshMacAlgorithms = session.SshMacAlgorithms;
            existing.SshKeyExchangeAlgorithms = session.SshKeyExchangeAlgorithms;
            existing.SshTunnelRules = session.SshTunnelRules.Select(SessionEditViewModel.CloneTunnelRule).ToList();
            existing.SshForwardX11 = session.SshForwardX11;
            existing.SshX11UseXmanager = session.SshX11UseXmanager;
            existing.SshX11Display = session.SshX11Display;
            existing.TelnetUseXDisplayLocation = session.TelnetUseXDisplayLocation;
            existing.TelnetXDisplayLocation = session.TelnetXDisplayLocation;
            existing.TelnetOptionMode = session.TelnetOptionMode;
            existing.TelnetForceCharacterAtATime = session.TelnetForceCharacterAtATime;
            existing.TelnetUsernamePrompt = session.TelnetUsernamePrompt;
            existing.TelnetPasswordPrompt = session.TelnetPasswordPrompt;
            existing.RloginPasswordPrompt = session.RloginPasswordPrompt;
            existing.RloginTerminalSpeed = session.RloginTerminalSpeed;
            existing.SftpLocalStartDirectory = session.SftpLocalStartDirectory;
            existing.SftpRemoteStartDirectory = session.SftpRemoteStartDirectory;
            existing.SftpUseCustomServer = session.SftpUseCustomServer;
            existing.SftpCustomServerCommand = session.SftpCustomServerCommand;
            existing.SerialPortName = session.SerialPortName;
            existing.SerialBaudRate = session.SerialBaudRate;
            existing.SerialDataBits = session.SerialDataBits;
            existing.SerialParity = session.SerialParity;
            existing.SerialStopBits = session.SerialStopBits;
            existing.SerialFlowControl = session.SerialFlowControl;
            existing.RdpWindowSize = session.RdpWindowSize;
            existing.RdpDesktopWidth = session.RdpDesktopWidth;
            existing.RdpDesktopHeight = session.RdpDesktopHeight;
            existing.RdpResizeMode = session.RdpResizeMode;
            existing.RdpScreenScale = session.RdpScreenScale;
            existing.RdpColorQuality = session.RdpColorQuality;
            existing.RdpApplyKeyCombinations = session.RdpApplyKeyCombinations;
            existing.RdpRedirectDrives = session.RdpRedirectDrives;
            existing.RdpAudioMode = session.RdpAudioMode;
            existing.RdpAudioCapture = session.RdpAudioCapture;
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
