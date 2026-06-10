using System;
using ChiXueSsh.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ChiXueSsh.ViewModels;

public partial class TerminalTabViewModel : ObservableObject
{
    [ObservableProperty] private string _title;
    [ObservableProperty] private bool _isConnected;
    [ObservableProperty] private bool _isSelected;

    public SessionInfo Session { get; }
    public TerminalViewModel Terminal { get; }

    /// <summary>仅内存保存，不持久化，用于监控独立 SSH 连接</summary>
    public string? ConnectedPassword { get; set; }

    public event Action<TerminalTabViewModel>? CloseRequested;

    public TerminalTabViewModel(SessionInfo session)
    {
        Session = session;
        _title = session.Name;
        Terminal = new TerminalViewModel();

        Terminal.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(TerminalViewModel.IsConnected))
            {
                IsConnected = Terminal.IsConnected;
                UpdateTitle();
            }
            if (e.PropertyName == nameof(TerminalViewModel.HostInfo))
            {
                UpdateTitle();
            }
        };
    }

    private void UpdateTitle()
    {
        if (!Terminal.IsConnected && IsConnected)
        {
            // Was connected, now disconnected
            Title = $"[断开] {Session.Name}";
        }
        else
        {
            Title = Session.Name;
        }
    }

    public void NotifyThemeChanged()
    {
        OnPropertyChanged(nameof(IsSelected));
    }

    [RelayCommand]
    private void CloseTab()
    {
        Terminal.Disconnect();
        CloseRequested?.Invoke(this);
    }
}
