using System;
using AtomUI.Desktop.Controls;
using Avalonia.Interactivity;
using CxShell.ViewModels;

namespace CxShell.Views;

public partial class SshTunnelCenterWindow : Window
{
    protected override Type StyleKeyOverride { get; } = typeof(Window);

    public SshTunnelCenterWindow()
    {
        InitializeComponent();
    }

    public SshTunnelCenterWindow(SshTunnelCenterViewModel viewModel)
        : this()
    {
        DataContext = viewModel;
        Closed += (_, _) => viewModel.Dispose();
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e)
    {
        Close();
    }
}
