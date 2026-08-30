using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace CxShell.Views;

public partial class ImagePreviewWindow : AtomUI.Desktop.Controls.Window
{
    public ImagePreviewWindow()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
            return;

        Close();
        e.Handled = true;
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e)
    {
        Close();
        e.Handled = true;
    }
}
