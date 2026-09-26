using System.Collections.Specialized;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using CxShell.Controls;
using CxShell.ViewModels;
using AtomDataGridSelectionChangedEventArgs = AtomUI.Desktop.Controls.DataGridSelectionChangedEventArgs;

namespace CxShell.Views;

public partial class RecentConnectionsWindow : Window
{
    private DataGridSourceAdapter<RecentSessionItemViewModel>? _gridSource;
    private RecentConnectionsViewModel? _viewModel;
    private bool _gridRefreshQueued;

    public RecentConnectionsWindow()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        RecentConnectionsGrid.SelectionChanged += OnGridSelectionChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_viewModel != null)
            _viewModel.Entries.CollectionChanged -= OnEntriesChanged;

        DetachGridSource();
        _viewModel = DataContext as RecentConnectionsViewModel;
        if (_viewModel == null)
            return;

        _viewModel.Entries.CollectionChanged += OnEntriesChanged;
        AttachGridSource();
    }

    private void OnEntriesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_gridRefreshQueued)
            return;

        _gridRefreshQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _gridRefreshQueued = false;
            if (_viewModel != null)
                AttachGridSource();
        }, DispatcherPriority.Render);
    }

    private void AttachGridSource()
    {
        var selectedSessionId = _viewModel?.SelectedEntry?.Session.Id;
        DetachGridSource();
        if (_viewModel == null)
            return;

        _gridSource = new DataGridSourceAdapter<RecentSessionItemViewModel>(
            _viewModel.Entries,
            snapshot: true);
        RecentConnectionsGrid.ItemsSource = _gridSource.Source;
        _gridSource.ConfigureColumns(RecentConnectionsGrid);

        var selected = selectedSessionId is { } id
            ? _viewModel.Entries.FirstOrDefault(item => item.Session.Id == id)
            : null;
        if (selected == null)
            return;

        _gridSource.SetSelectedItems(RecentConnectionsGrid, [selected]);
        RecentConnectionsGrid.CurrentRowKey = _gridSource.GetKey(selected);
    }

    private void OnGridSelectionChanged(object? sender, AtomDataGridSelectionChangedEventArgs e)
    {
        if (DataContext is RecentConnectionsViewModel vm && _gridSource != null)
            vm.SelectedEntry = _gridSource.GetCurrentItem(RecentConnectionsGrid);
    }

    protected override void OnClosed(EventArgs e)
    {
        if (_viewModel != null)
            _viewModel.Entries.CollectionChanged -= OnEntriesChanged;
        DetachGridSource();
        _viewModel?.Dispose();
        _viewModel = null;
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
