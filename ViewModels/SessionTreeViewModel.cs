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
    [ObservableProperty] private SessionNodeViewModel? _selectedNode;

    private readonly SessionStorageService _storage;
    private readonly MainWindowViewModel _mainWindow;
    private SessionData _data;

    public SessionInfo? SelectedSession => SelectedNode?.Session;

    public MainWindowViewModel MainWindow => _mainWindow;

    public SessionTreeViewModel(MainWindowViewModel mainWindow)
    {
        _mainWindow = mainWindow;
        _storage = new SessionStorageService();
        _data = _storage.Load();
        LoadSessions();
    }

    private void LoadSessions()
    {
        SessionNodes.Clear();

        foreach (var group in _data.Groups.OrderBy(g => g.SortOrder))
        {
            var groupNode = new SessionNodeViewModel(group);
            foreach (var session in _data.Sessions.Where(s => s.GroupId == group.Id).OrderBy(s => s.SortOrder))
            {
                groupNode.Children.Add(new SessionNodeViewModel(session));
            }
            SessionNodes.Add(groupNode);
        }

        foreach (var session in _data.Sessions.Where(s => s.GroupId == null).OrderBy(s => s.SortOrder))
        {
            SessionNodes.Add(new SessionNodeViewModel(session));
        }
    }

    public void AddSession(SessionInfo session)
    {
        _data.Sessions.Add(session);
        _storage.Save(_data);
        SessionNodes.Add(new SessionNodeViewModel(session));
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
            existing.AuthMethod = session.AuthMethod;
            existing.PrivateKeyPath = session.PrivateKeyPath;
            _storage.Save(_data);
            LoadSessions();
        }
    }

    public void DeleteSession(SessionInfo session)
    {
        _data.Sessions.RemoveAll(s => s.Id == session.Id);
        _storage.Save(_data);
        LoadSessions();
    }
}
