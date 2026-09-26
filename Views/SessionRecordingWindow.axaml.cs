using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using System.Collections.Specialized;
using CxShell.Controls;
using CxShell.ViewModels;
using AtomDataGridSelectionChangedEventArgs = AtomUI.Desktop.Controls.DataGridSelectionChangedEventArgs;

namespace CxShell.Views;

public partial class SessionRecordingWindow : Window
{
    private SessionRecordingViewModel? _viewModel;
    private DataGridSourceAdapter<SessionRecordingItemViewModel>? _gridSource;
    private bool _initialized;
    private bool _gridRefreshQueued;
    private bool _selectionSyncQueued;

    public SessionRecordingWindow()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        RecordingGrid.SelectionChanged += OnGridSelectionChanged;
        RecordingGrid.PropertyChanged += OnGridPropertyChanged;
    }

    protected override async void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        if (_initialized || DataContext is not SessionRecordingViewModel viewModel)
            return;
        _initialized = true;
        await viewModel.InitializeAsync();
    }

    protected override void OnClosed(EventArgs e)
    {
        var viewModel = _viewModel;
        BindViewModel(null);
        viewModel?.Dispose();
        DataContextChanged -= OnDataContextChanged;
        base.OnClosed(e);
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        BindViewModel(DataContext as SessionRecordingViewModel);
    }

    private void BindViewModel(SessionRecordingViewModel? viewModel)
    {
        if (_viewModel != null)
        {
            _viewModel.PlaybackUpdated -= OnPlaybackUpdated;
            _viewModel.Recordings.CollectionChanged -= OnRecordingsChanged;
        }
        DetachGridSource();
        _viewModel = viewModel;
        if (_viewModel != null)
        {
            _viewModel.PlaybackUpdated += OnPlaybackUpdated;
            _viewModel.Recordings.CollectionChanged += OnRecordingsChanged;
            AttachGridSource();
        }
    }

    private void OnRecordingsChanged(object? sender, NotifyCollectionChangedEventArgs e)
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
        DetachGridSource();
        if (_viewModel == null)
            return;

        _gridSource = new DataGridSourceAdapter<SessionRecordingItemViewModel>(
            _viewModel.Recordings,
            snapshot: true);
        RecordingGrid.ItemsSource = _gridSource.Source;
        _gridSource.ConfigureColumns(RecordingGrid);
        var selected = _viewModel.SelectedRecordings
            .Where(_viewModel.Recordings.Contains)
            .ToArray();
        _gridSource.SetSelectedItems(RecordingGrid, selected);
        if (_viewModel.SelectedRecording is { } current && selected.Contains(current))
            RecordingGrid.CurrentRowKey = _gridSource.GetKey(current);
    }

    private void DetachGridSource()
    {
        RecordingGrid.ItemsSource = null;
        RecordingGrid.Selection = AtomUI.Desktop.Controls.DataGridSelectionState.Empty;
        RecordingGrid.CurrentRowKey = null;
        _gridSource?.Dispose();
        _gridSource = null;
    }

    private void OnGridSelectionChanged(object? sender, AtomDataGridSelectionChangedEventArgs e)
    {
        QueueSelectionSync();
    }

    private void OnGridPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == AtomUI.Desktop.Controls.DataGrid.CurrentRowKeyProperty)
            QueueSelectionSync();
    }

    private void QueueSelectionSync()
    {
        if (_selectionSyncQueued)
            return;

        _selectionSyncQueued = true;
        Dispatcher.UIThread.Post(SynchronizeGridSelection, DispatcherPriority.Render);
    }

    private void SynchronizeGridSelection()
    {
        _selectionSyncQueued = false;
        if (_viewModel == null || _gridSource == null)
            return;

        _viewModel.SetGridSelection(
            _gridSource.GetSelectedItems(RecordingGrid),
            _gridSource.GetCurrentItem(RecordingGrid));
    }

    private void OnPlaybackUpdated()
    {
        PlaybackTerminal.InvalidateVisual();
    }

    private async void OnDeleteClick(object? sender, RoutedEventArgs e)
    {
        SynchronizeGridSelection();
        if (_viewModel is not { CanDeleteSelection: true } viewModel)
            return;
        var confirmed = await AtomUiDialogService.ShowConfirmAsync(
            this,
            viewModel.TitleText,
            viewModel.BuildDeleteConfirmationText());
        if (confirmed)
            viewModel.DeleteSelectedCommand.Execute(null);
    }

    private async void OnExportClick(object? sender, RoutedEventArgs e)
    {
        SynchronizeGridSelection();
        if (_viewModel is not { CanExportSelection: true } viewModel)
            return;

        try
        {
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                SuggestedFileName = $"CxShell-Recording-{DateTime.Now:yyyyMMdd-HHmmss}.cast",
                DefaultExtension = "cast",
                FileTypeChoices =
                [
                    new FilePickerFileType("asciicast v2") { Patterns = ["*.cast"] }
                ]
            });
            if (file != null)
                await File.WriteAllTextAsync(file.Path.LocalPath, viewModel.BuildAsciicast());
        }
        catch
        {
            // Export failures do not close the playback center.
        }
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e)
    {
        Close();
    }
}
