using System;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using ChiXueSsh.ViewModels;

namespace ChiXueSsh.Views;

public partial class TerminalView : UserControl
{
    private TerminalViewModel? _boundVm;
    private Controls.TerminalControl? _terminal;

    public TerminalView()
    {
        InitializeComponent();
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        _terminal = this.FindControl<Controls.TerminalControl>("Terminal");
        TryBind();
    }

    protected override void OnUnloaded(RoutedEventArgs e)
    {
        Unbind();
        base.OnUnloaded(e);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        // Only re-bind if we're already loaded (terminal control exists)
        if (_terminal != null)
        {
            Unbind();
            TryBind();
        }
    }

    private void TryBind()
    {
        if (_terminal == null) return;
        if (DataContext is not TerminalViewModel vm) return;

        _boundVm = vm;

        _terminal.InputReceived += OnInputReceived;
        _terminal.SizeChanged2 += OnSizeChanged;
        _terminal.PointerPressed += OnPointerPressed;
        vm.BufferChanged += OnBufferChanged;
        vm.PropertyChanged += OnVmPropertyChanged;

        // 立即刷新一次
        _terminal.InvalidateVisual();
    }

    private void Unbind()
    {
        if (_terminal != null)
        {
            _terminal.InputReceived -= OnInputReceived;
            _terminal.SizeChanged2 -= OnSizeChanged;
            _terminal.PointerPressed -= OnPointerPressed;
        }

        if (_boundVm != null)
        {
            _boundVm.BufferChanged -= OnBufferChanged;
            _boundVm.PropertyChanged -= OnVmPropertyChanged;
        }

        _boundVm = null;
    }

    private void OnInputReceived(string data) => _boundVm?.SendInput(data);
    private void OnSizeChanged(int cols, int rows) => _boundVm?.Resize(cols, rows);
    private void OnPointerPressed(object? s, Avalonia.Input.PointerPressedEventArgs e) => _terminal?.Focus();
    private void OnBufferChanged() => _terminal?.InvalidateVisual();

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TerminalViewModel.Buffer))
        {
            _terminal?.InvalidateVisual();
        }
    }
}
