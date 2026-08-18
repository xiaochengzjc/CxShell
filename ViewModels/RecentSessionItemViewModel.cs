using AtomUI.Icons.AntDesign;
using Avalonia.Controls;
using CxShell.Models;
using CxShell.Services;

namespace CxShell.ViewModels;

public sealed class RecentSessionItemViewModel
{
    public RecentSessionItemViewModel(SessionInfo session, DateTimeOffset connectedAt)
        : this(session, FormatConnectedTime(connectedAt))
    {
    }

    public RecentSessionItemViewModel(SessionInfo session, string connectedText)
    {
        Session = session;
        Name = string.IsNullOrWhiteSpace(session.Name)
            ? string.IsNullOrWhiteSpace(session.Host) ? session.Protocol.ToString() : session.Host
            : session.Name;
        Endpoint = BuildEndpoint(session);
        ProtocolText = session.Protocol.ToString();
        ConnectedText = connectedText;
        Icon = SessionTabIconCatalog.CreateIcon(session.AppearanceTabIcon) ?? CreateProtocolIcon(session.Protocol);
    }

    public SessionInfo Session { get; }
    public string Name { get; }
    public string Endpoint { get; }
    public string ProtocolText { get; }
    public string ConnectedText { get; }
    public PathIcon Icon { get; }

    public static string FormatConnectedTime(DateTimeOffset timestamp)
    {
        var localTimestamp = timestamp.ToLocalTime();
        var date = localTimestamp.Date;
        if (date == DateTime.Today)
        {
            return string.Format(
                LocalizationService.Shared.Text("Welcome.RecentToday"),
                localTimestamp.ToString("HH:mm"));
        }

        if (date == DateTime.Today.AddDays(-1))
        {
            return string.Format(
                LocalizationService.Shared.Text("Welcome.RecentYesterday"),
                localTimestamp.ToString("HH:mm"));
        }

        return localTimestamp.ToString(
            LocalizationService.Shared.IsEnglish ? "MMM d, yyyy HH:mm" : "yyyy-MM-dd HH:mm");
    }

    private static string BuildEndpoint(SessionInfo session)
    {
        if (session.Protocol == SessionProtocol.SERIAL)
            return string.IsNullOrWhiteSpace(session.SerialPortName) ? "SERIAL" : session.SerialPortName;

        var host = string.IsNullOrWhiteSpace(session.Host) ? session.Protocol.ToString() : session.Host.Trim();
        var endpoint = session.Port > 0 ? $"{host}:{session.Port}" : host;
        return string.IsNullOrWhiteSpace(session.Username)
            ? endpoint
            : $"{session.Username.Trim()}@{endpoint}";
    }

    private static PathIcon CreateProtocolIcon(SessionProtocol protocol)
    {
        var kind = protocol switch
        {
            SessionProtocol.SFTP or SessionProtocol.FTP => AntDesignIconKind.FolderOpenOutlined,
            SessionProtocol.RDP or SessionProtocol.VNC => AntDesignIconKind.DesktopOutlined,
            SessionProtocol.SERIAL => AntDesignIconKind.UsbOutlined,
            SessionProtocol.TELNET or SessionProtocol.RLOGIN => AntDesignIconKind.CodeOutlined,
            SessionProtocol.SSH => AntDesignIconKind.CloudServerOutlined,
            _ => AntDesignIconKind.LinkOutlined
        };

        return (PathIcon)new AntDesignIconProvider(kind).ProvideValue(null!);
    }
}
