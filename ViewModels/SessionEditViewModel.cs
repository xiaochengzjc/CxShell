using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using ChiXueSsh.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ChiXueSsh.ViewModels;

public partial class SessionEditViewModel : ObservableObject
{
    [ObservableProperty] private string _dialogTitle = "新建会话";
    [ObservableProperty] private string _sessionName = string.Empty;
    [ObservableProperty] private string _host = string.Empty;
    [ObservableProperty] private string _port = "22";
    [ObservableProperty] private string _username = string.Empty;
    [ObservableProperty] private bool _isPasswordAuth = true;
    [ObservableProperty] private bool _isPrivateKeyAuth;
    [ObservableProperty] private string _privateKeyPath = string.Empty;

    public SessionInfo? SavedSession { get; private set; }

    private readonly SessionInfo? _editingSession;

    public SessionEditViewModel()
    {
    }

    public SessionEditViewModel(SessionInfo session)
    {
        _editingSession = session;
        DialogTitle = "编辑会话";
        SessionName = session.Name;
        Host = session.Host;
        Port = session.Port.ToString();
        Username = session.Username;
        IsPasswordAuth = session.AuthMethod == AuthMethod.Password;
        IsPrivateKeyAuth = session.AuthMethod == AuthMethod.PrivateKey;
        PrivateKeyPath = session.PrivateKeyPath ?? string.Empty;
    }

    [RelayCommand]
    private async Task BrowseKey()
    {
        var lifetime = Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime;
        var topLevel = TopLevel.GetTopLevel(lifetime?.MainWindow);
        if (topLevel == null) return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择私钥文件",
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
        if (string.IsNullOrWhiteSpace(Host))
            return;
        if (string.IsNullOrWhiteSpace(Username))
            return;

        int port = 22;
        int.TryParse(Port, out port);
        if (port <= 0 || port > 65535) port = 22;

        var session = _editingSession ?? new SessionInfo();
        session.Name = string.IsNullOrWhiteSpace(SessionName) ? Host : SessionName;
        session.Host = Host;
        session.Port = port;
        session.Username = Username;
        session.AuthMethod = IsPrivateKeyAuth ? AuthMethod.PrivateKey : AuthMethod.Password;
        session.PrivateKeyPath = IsPrivateKeyAuth ? PrivateKeyPath : null;

        SavedSession = session;
    }

    [RelayCommand]
    private void Cancel()
    {
        SavedSession = null;
    }
}
