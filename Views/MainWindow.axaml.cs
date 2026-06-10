using System;
using AtomUI.Desktop.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using ChiXueSsh.ViewModels;

namespace ChiXueSsh.Views;

public partial class MainWindow : Window
{
    protected override Type StyleKeyOverride { get; } = typeof(Window);
    private readonly DispatcherTimer _fullScreenHintTimer;
    private bool _isPointerOverFullScreenHintArea;

    public MainWindow()
    {
        InitializeComponent();
        _fullScreenHintTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(3)
        };
        _fullScreenHintTimer.Tick += (_, _) => HideFullScreenHintIfNeeded();

        var vm = new MainWindowViewModel();
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainWindowViewModel.IsTerminalFullScreen))
            {
                WindowState = vm.IsTerminalFullScreen
                    ? Avalonia.Controls.WindowState.FullScreen
                    : Avalonia.Controls.WindowState.Normal;
                _isPointerOverFullScreenHintArea = false;
                if (vm.IsTerminalFullScreen)
                    RestartFullScreenHintTimer();
                else
                    _fullScreenHintTimer.Stop();
            }
        };
        DataContext = vm;
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
    }

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
            return;

        if (DataContext is MainWindowViewModel { IsTerminalFullScreen: true } vm)
        {
            vm.ExitTerminalFullScreen();
            e.Handled = true;
        }
    }

    private void FullScreenHintArea_OnPointerEntered(object? sender, PointerEventArgs e)
    {
        _isPointerOverFullScreenHintArea = true;
        _fullScreenHintTimer.Stop();

        if (DataContext is MainWindowViewModel { IsTerminalFullScreen: true } vm)
            vm.IsFullScreenHintVisible = true;
    }

    private void FullScreenHintArea_OnPointerExited(object? sender, PointerEventArgs e)
    {
        _isPointerOverFullScreenHintArea = false;
        RestartFullScreenHintTimer();
    }

    private void RestartFullScreenHintTimer()
    {
        _fullScreenHintTimer.Stop();

        if (DataContext is not MainWindowViewModel { IsTerminalFullScreen: true })
            return;

        _fullScreenHintTimer.Start();
    }

    private void HideFullScreenHintIfNeeded()
    {
        _fullScreenHintTimer.Stop();

        if (_isPointerOverFullScreenHintArea)
            return;

        if (DataContext is MainWindowViewModel { IsTerminalFullScreen: true } vm)
            vm.IsFullScreenHintVisible = false;
    }
}
