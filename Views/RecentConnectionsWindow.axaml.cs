using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using CxShell.ViewModels;

namespace CxShell.Views;

public partial class RecentConnectionsWindow : Window
{
    public RecentConnectionsWindow()
    {
        InitializeComponent();
    }

    protected override void OnClosed(EventArgs e)
    {
        if (DataContext is IDisposable disposable)
            disposable.Dispose();
        base.OnClosed(e);
    }

    private void OnRecentConnectionDoubleTapped(object? sender, TappedEventArgs e)
    {
        ConnectSelected();
        e.Handled = true;
    }

    private void OnRecentConnectionsKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;

        ConnectSelected();
        e.Handled = true;
    }

    private void ConnectSelected()
    {
        if (DataContext is not RecentConnectionsViewModel { SelectedEntry: { } item } viewModel)
            return;

        viewModel.ConnectCommand.Execute(item);
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e)
    {
        Close();
    }
}
