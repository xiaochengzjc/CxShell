using System;
using AtomUI.Desktop.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using ChiXueSsh.Models;
using ChiXueSsh.ViewModels;

namespace ChiXueSsh.Views;

public partial class MainWindow : Window
{
    protected override Type StyleKeyOverride { get; } = typeof(Window);
    private readonly DispatcherTimer _fullScreenHintTimer;
    private bool _isPointerOverFullScreenHintArea;
    private SessionInfo? _quickSessionContext;

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

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        QuickSessionPropertiesBtn.Click += OnQuickSessionPropertiesClick;
        QuickSessionDeleteBtn.Click += OnQuickSessionDeleteClick;
    }

    protected override void OnUnloaded(RoutedEventArgs e)
    {
        QuickSessionContextMenuPopup.Close();
        QuickSessionPropertiesBtn.Click -= OnQuickSessionPropertiesClick;
        QuickSessionDeleteBtn.Click -= OnQuickSessionDeleteClick;
        base.OnUnloaded(e);
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

    private void OnQuickSessionTagPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsRightButtonPressed)
            return;

        if (sender is Avalonia.Controls.Control { DataContext: SessionInfo session })
        {
            _quickSessionContext = session;
            QuickSessionContextMenuPopup.PlacementTarget = sender as Avalonia.Controls.Control;
            QuickSessionContextMenuPopup.IsOpen = true;
            e.Handled = true;
        }
    }

    private async void OnQuickSessionPropertiesClick(object? sender, RoutedEventArgs e)
    {
        QuickSessionContextMenuPopup.Close();
        if (_quickSessionContext == null || DataContext is not MainWindowViewModel vm)
            return;

        await vm.EditQuickSessionCommand.ExecuteAsync(_quickSessionContext);
    }

    private void OnQuickSessionDeleteClick(object? sender, RoutedEventArgs e)
    {
        QuickSessionContextMenuPopup.Close();
        if (_quickSessionContext == null || DataContext is not MainWindowViewModel vm)
            return;

        vm.RemoveQuickSessionCommand.Execute(_quickSessionContext);
        _quickSessionContext = null;
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
