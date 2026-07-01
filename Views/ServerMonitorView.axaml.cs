using Avalonia.Controls;
using Avalonia;
using System.Windows.Input;

namespace CxShell.Views;

public partial class ServerMonitorView : UserControl
{
    public static readonly StyledProperty<bool> IsPanelCloseButtonVisibleProperty =
        AvaloniaProperty.Register<ServerMonitorView, bool>(nameof(IsPanelCloseButtonVisible));

    public static readonly StyledProperty<ICommand?> PanelCloseCommandProperty =
        AvaloniaProperty.Register<ServerMonitorView, ICommand?>(nameof(PanelCloseCommand));

    public bool IsPanelCloseButtonVisible
    {
        get => GetValue(IsPanelCloseButtonVisibleProperty);
        set => SetValue(IsPanelCloseButtonVisibleProperty, value);
    }

    public ICommand? PanelCloseCommand
    {
        get => GetValue(PanelCloseCommandProperty);
        set => SetValue(PanelCloseCommandProperty, value);
    }

    public ServerMonitorView()
    {
        InitializeComponent();
    }
}
