using System;
using Avalonia.Media;
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
    public bool HasTabColor => !string.Equals(Session.AppearanceTabColorMode, "Default", StringComparison.OrdinalIgnoreCase);
    public IBrush TabColorBrush => new SolidColorBrush(ResolveTabColor());

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
        OnPropertyChanged(nameof(HasTabColor));
        OnPropertyChanged(nameof(TabColorBrush));
    }

    private Color ResolveTabColor()
    {
        return Session.AppearanceTabColorMode switch
        {
            "Red" => Color.Parse("#F5222D"),
            "Purple" => Color.Parse("#722ED1"),
            "Yellow" => Color.Parse("#FAAD14"),
            "Custom" when Color.TryParse(Session.AppearanceTabCustomColor, out var color) => color,
            _ => Colors.Transparent
        };
    }

    [RelayCommand]
    private void CloseTab()
    {
        Terminal.Disconnect();
        CloseRequested?.Invoke(this);
    }
}
