using System;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;
using CxShell.ViewModels;

namespace CxShell.Views;

public partial class VncWindow : AtomUI.Desktop.Controls.Window
{
    public VncWindow()
    {
        InitializeComponent();
        Opened += (_, _) => RemoteView?.Focus();
        Closed += (_, _) =>
        {
            if (DataContext is IDisposable disposable)
                disposable.Dispose();
        };
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (DataContext is not VncViewModel vm)
            return;

        vm.PropertyChanged += async (_, args) =>
        {
            if (args.PropertyName != nameof(VncViewModel.RemoteClipboardText) || string.IsNullOrEmpty(vm.RemoteClipboardText))
                return;

            await Dispatcher.UIThread.InvokeAsync(async () =>
            {
                var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
                if (clipboard != null)
                    await clipboard.SetTextAsync(vm.RemoteClipboardText);
            });
        };
    }

    private async void OnSendClipboardClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not VncViewModel vm)
            return;

        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        var text = clipboard == null ? null : await clipboard.TryGetTextAsync();
        if (!string.IsNullOrEmpty(text))
            await vm.SendClipboardTextAndPasteAsync(text);
    }
}
