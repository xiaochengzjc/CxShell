using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;
using CxShell.ViewModels;

namespace CxShell.Views;

public partial class VncView : UserControl
{
    private VncViewModel? _boundVm;

    public VncView()
    {
        InitializeComponent();
        AttachedToVisualTree += (_, _) =>
        {
            RemoteView?.Focus();
        };
        DetachedFromVisualTree += (_, _) => UnbindViewModel();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        UnbindViewModel();
        if (DataContext is not VncViewModel vm)
            return;

        _boundVm = vm;
        vm.PropertyChanged += OnViewModelPropertyChanged;
    }

    private async void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (_boundVm == null ||
            args.PropertyName != nameof(VncViewModel.RemoteClipboardText) ||
            string.IsNullOrEmpty(_boundVm.RemoteClipboardText))
        {
            return;
        }

        await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard != null)
                await clipboard.SetTextAsync(_boundVm.RemoteClipboardText);
        });
    }

    private void UnbindViewModel()
    {
        if (_boundVm == null)
            return;

        _boundVm.PropertyChanged -= OnViewModelPropertyChanged;
        _boundVm = null;
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
