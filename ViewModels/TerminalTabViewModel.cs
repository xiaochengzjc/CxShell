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
    public VncViewModel? Vnc { get; }
    public bool IsVncSession => Vnc != null;
    public bool IsTerminalSession => Vnc == null;
    public bool HasTabColor => !string.Equals(Session.AppearanceTabColorMode, "Default", StringComparison.OrdinalIgnoreCase);
    public IBrush TabColorBrush => new SolidColorBrush(ResolveTabColor());
    public IBrush TabBackgroundBrush => HasTabColor
        ? new SolidColorBrush(IsSelected ? ResolveTabColor() : ResolveMutedTabColor())
        : new SolidColorBrush(ResolveDefaultTabBackground());

    /// <summary>仅内存保存，不持久化，用于监控独立 SSH 连接</summary>
    public string? ConnectedPassword { get; set; }

    public event Action<TerminalTabViewModel>? CloseRequested;

    public TerminalTabViewModel(SessionInfo session)
        : this(session, null)
    {
    }

    public TerminalTabViewModel(SessionInfo session, VncViewModel? vnc)
    {
        Session = session;
        Vnc = vnc;
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

        if (Vnc != null)
        {
            Vnc.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(VncViewModel.IsConnected))
                    IsConnected = Vnc.IsConnected;
            };
            IsConnected = Vnc.IsConnected;
        }
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
        OnPropertyChanged(nameof(TabBackgroundBrush));
    }

    partial void OnIsSelectedChanged(bool value)
    {
        OnPropertyChanged(nameof(TabBackgroundBrush));
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

    private Color ResolveMutedTabColor()
    {
        var color = ResolveTabColor();
        return Color.FromArgb(
            color.A,
            Blend(color.R, 255, 0.86),
            Blend(color.G, 255, 0.86),
            Blend(color.B, 255, 0.86));
    }

    private static byte Blend(byte source, byte target, double amount)
        => (byte)Math.Clamp(Math.Round(source + (target - source) * amount), 0, 255);

    private Color ResolveDefaultTabBackground()
    {
        return IsSelected
            ? ThemeTokenColorHelper.GetColor(AtomUI.Theme.Styling.SharedTokenKind.ColorBgContainer, Color.Parse("#1E1E1E"))
            : ThemeTokenColorHelper.GetColor(AtomUI.Theme.Styling.SharedTokenKind.ColorFillQuaternary, Color.Parse("#151515"));
    }

    [RelayCommand]
    private void CloseTab()
    {
        Terminal.Disconnect();
        Vnc?.Dispose();
        CloseRequested?.Invoke(this);
    }
}
