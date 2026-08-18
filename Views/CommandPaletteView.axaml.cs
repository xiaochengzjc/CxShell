using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CxShell.ViewModels;

namespace CxShell.Views;

public partial class CommandPaletteView : UserControl
{
    private CommandPaletteViewModel? _viewModel;

    public CommandPaletteView()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, OnKeyDownTunnel, RoutingStrategies.Tunnel);
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_viewModel != null)
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;

        _viewModel = DataContext as CommandPaletteViewModel;
        if (_viewModel != null)
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(CommandPaletteViewModel.IsOpen) || _viewModel?.IsOpen != true)
            return;

        Dispatcher.UIThread.Post(() =>
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
        }, DispatcherPriority.Input);
    }

    private void OnKeyDownTunnel(object? sender, KeyEventArgs e)
    {
        if (_viewModel == null || !_viewModel.IsOpen)
            return;

        switch (e.Key)
        {
            case Key.Down:
                _viewModel.MoveDown();
                BringSelectedItemIntoView();
                e.Handled = true;
                break;
            case Key.Up:
                _viewModel.MoveUp();
                BringSelectedItemIntoView();
                e.Handled = true;
                break;
            case Key.Enter:
                _viewModel.ExecuteSelected();
                e.Handled = true;
                break;
            case Key.Escape:
                _viewModel.Close();
                e.Handled = true;
                break;
        }
    }

    private void OnItemPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_viewModel != null && sender is Control { DataContext: CommandPaletteItem item })
        {
            _viewModel.Activate(item);
            e.Handled = true;
        }
    }

    private void BringSelectedItemIntoView()
    {
        if (_viewModel?.SelectedItem is not { } selected)
            return;

        var item = this.GetVisualDescendants()
            .OfType<Border>()
            .FirstOrDefault(border =>
                border.Classes.Contains("palette-item") &&
                ReferenceEquals(border.DataContext, selected));
        item?.BringIntoView();
    }
}
