using System;
using Avalonia.Media;
using CxShell.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CxShell.ViewModels;

public partial class TerminalTabViewModel : ObservableObject
{
    [ObservableProperty] private string _title;
    [ObservableProperty] private bool _isConnected;
    [ObservableProperty] private bool _isSelected;

    public SessionInfo Session { get; }
    public TerminalViewModel Terminal { get; }
    public VncViewModel? Vnc { get; }
    public RdpViewModel? Rdp { get; }
    public SftpViewModel? FileTransfer { get; }
    public SftpViewModel CompanionSftp { get; }
    public bool IsVncSession => Vnc != null;
    public bool IsRdpSession => Rdp != null;
    public bool IsFileTransferSession => FileTransfer != null;
    public bool IsTerminalSession => Vnc == null && Rdp == null && FileTransfer == null;
    public IBrush ConnectionIndicatorBrush => new SolidColorBrush(IsConnected
        ? Color.Parse("#18C914")
        : Color.Parse("#F5222D"));
    public string ConnectionIndicatorText => IsConnected ? "已连接" : "已断开";
    public bool HasTabColor => !string.Equals(Session.AppearanceTabColorMode, "Default", StringComparison.OrdinalIgnoreCase);
    public IBrush TabColorBrush => new SolidColorBrush(ResolveTabColor());
    public IBrush TabBackgroundBrush => HasTabColor
        ? new SolidColorBrush(IsSelected ? ResolveTabColor() : ResolveMutedTabColor())
        : new SolidColorBrush(ResolveDefaultTabBackground());

    /// <summary>仅内存保存，不持久化，用于监控独立 SSH 连接</summary>
    public string? ConnectedPassword { get; set; }

    public event Action<TerminalTabViewModel>? CloseRequested;

    public TerminalTabViewModel(SessionInfo session)
        : this(session, null, null, null)
    {
    }

    public TerminalTabViewModel(SessionInfo session, VncViewModel? vnc)
        : this(session, vnc, null, null)
    {
    }

    public TerminalTabViewModel(SessionInfo session, RdpViewModel rdp)
        : this(session, null, rdp, null)
    {
    }

    public TerminalTabViewModel(SessionInfo session, SftpViewModel fileTransfer)
        : this(session, null, null, fileTransfer)
    {
    }

    private TerminalTabViewModel(SessionInfo session, VncViewModel? vnc, RdpViewModel? rdp, SftpViewModel? fileTransfer)
    {
        Session = session;
        Vnc = vnc;
        Rdp = rdp;
        FileTransfer = fileTransfer;
        CompanionSftp = new SftpViewModel();
        _title = session.Name;
        Terminal = new TerminalViewModel();

        Terminal.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(TerminalViewModel.IsConnected))
            {
                IsConnected = Terminal.IsConnected;
                NotifyConnectionIndicatorChanged();
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
                {
                    IsConnected = Vnc.IsConnected;
                    NotifyConnectionIndicatorChanged();
                }
            };
            IsConnected = Vnc.IsConnected;
            NotifyConnectionIndicatorChanged();
        }

        if (Rdp != null)
        {
            Rdp.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(RdpViewModel.IsConnected))
                {
                    IsConnected = Rdp.IsConnected;
                    NotifyConnectionIndicatorChanged();
                }
            };
            IsConnected = Rdp.IsConnected;
            NotifyConnectionIndicatorChanged();
        }

        if (FileTransfer != null)
        {
            FileTransfer.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(SftpViewModel.IsConnected))
                {
                    IsConnected = FileTransfer.IsConnected;
                    NotifyConnectionIndicatorChanged();
                }
            };
            IsConnected = FileTransfer.IsConnected;
            NotifyConnectionIndicatorChanged();
        }
    }

    private void NotifyConnectionIndicatorChanged()
    {
        OnPropertyChanged(nameof(ConnectionIndicatorBrush));
        OnPropertyChanged(nameof(ConnectionIndicatorText));
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

    partial void OnIsConnectedChanged(bool value)
    {
        NotifyConnectionIndicatorChanged();
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
        var target = ResolveDefaultTabBackground();
        return Color.FromArgb(
            color.A,
            Blend(color.R, target.R, 0.82),
            Blend(color.G, target.G, 0.82),
            Blend(color.B, target.B, 0.82));
    }

    private static byte Blend(byte source, byte target, double amount)
        => (byte)Math.Clamp(Math.Round(source + (target - source) * amount), 0, 255);

    private Color ResolveDefaultTabBackground()
    {
        return ThemeTokenColorHelper.GetColor(
            IsSelected
                ? AtomUI.Theme.Styling.SharedTokenKind.ColorBgContainer
                : AtomUI.Theme.Styling.SharedTokenKind.ColorBgLayout,
            IsSelected ? Color.Parse("#FFFFFF") : Color.Parse("#F5F5F5"));
    }

    [RelayCommand]
    private void CloseTab()
    {
        CloseRequested?.Invoke(this);
    }
}
