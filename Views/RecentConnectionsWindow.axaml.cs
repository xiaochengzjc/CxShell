using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using CxShell.Controls;
using CxShell.ViewModels;
using AtomDataGridSelectionChangedEventArgs = AtomUI.Desktop.Controls.DataGridSelectionChangedEventArgs;

namespace CxShell.Views;

public partial class RecentConnectionsWindow : Window
{
    private DataGridSourceAdapter<RecentSessionItemViewModel>? _gridSource;

    public RecentConnectionsWindow()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        RecentConnectionsGrid.SelectionChanged += OnGridSelectionChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        DetachGridSource();
        if (DataContext is not RecentConnectionsViewModel vm)
            return;

        _gridSource = new DataGridSourceAdapter<RecentSessionItemViewModel>(vm.Entries);
        RecentConnectionsGrid.ItemsSource = _gridSource.Source;
        _gridSource.ConfigureColumns(RecentConnectionsGrid);
    }

    private void OnGridSelectionChanged(object? sender, AtomDataGridSelectionChangedEventArgs e)
    {
        if (DataContext is RecentConnectionsViewModel vm && _gridSource != null)
            vm.SelectedEntry = _gridSource.GetCurrentItem(RecentConnectionsGrid);
    }

    protected override void OnClosed(EventArgs e)
    {
        DetachGridSource();
        if (DataContext is IDisposable disposable)
            disposable.Dispose();
        base.OnClosed(e);
    }

    private void DetachGridSource()
    {
        RecentConnectionsGrid.ItemsSource = null;
        RecentConnectionsGrid.Selection = AtomUI.Desktop.Controls.DataGridSelectionState.Empty;
        RecentConnectionsGrid.CurrentRowKey = null;
        _gridSource?.Dispose();
        _gridSource = null;
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
