using System;
using AtomUI.Desktop.Controls;
using Avalonia.Interactivity;
using CxShell.ViewModels;

namespace CxShell.Views;

public partial class SessionManagerWindow : Window
{
    protected override Type StyleKeyOverride { get; } = typeof(Window);

    public SessionManagerWindow()
    {
        InitializeComponent();
    }

    public SessionManagerWindow(SessionTreeViewModel vm)
        : this()
    {
        vm.SelectedNode = null;
        Opened += (_, _) => DataContext = vm;
    }

    private async void OnConnectClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SessionTreeViewModel vm || vm.SelectedSession == null)
            return;

        await vm.MainWindow.ConnectSession(vm.SelectedSession);
        Close();
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e)
    {
        Close();
    }
}
