using System;
using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CxShell.Controls;
using CxShell.Services;
using CxShell.ViewModels;
using AtomDataGridSelectionChangedEventArgs = AtomUI.Desktop.Controls.DataGridSelectionChangedEventArgs;

namespace CxShell.Views;

public partial class SessionRecordingPage : UserControl
{
    private SessionRecordingViewModel? _viewModel;
    private DataGridSourceAdapter<SessionRecordingItemViewModel>? _gridSource;
    private bool _initialized;
    private bool _gridRefreshQueued;
    private bool _selectionSyncQueued;

    public SessionRecordingPage()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        RecordingGrid.SelectionChanged += OnGridSelectionChanged;
        RecordingGrid.PropertyChanged += OnGridPropertyChanged;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (!_initialized && DataContext is SessionRecordingViewModel viewModel)
        {
            _initialized = true;
            _ = viewModel.InitializeAsync();
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        BindViewModel(null);
        base.OnDetachedFromVisualTree(e);
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        BindViewModel(DataContext as SessionRecordingViewModel);
        if (_initialized || VisualRoot == null)
            return;
        if (DataContext is not SessionRecordingViewModel viewModel)
            return;

        _initialized = true;
        _ = viewModel.InitializeAsync();
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
        Dispatcher.UIThread.Post(() => PlaybackTerminal.InvalidateVisual());
    }

    private async void OnDeleteClick(object? sender, RoutedEventArgs e)
    {
        SynchronizeGridSelection();
        if (_viewModel is not { CanDeleteSelection: true } viewModel ||
            TopLevel.GetTopLevel(this) is not TopLevel owner)
            return;

        var confirmed = await AtomUiDialogService.ShowConfirmAsync(
            owner,
            viewModel.TitleText,
            viewModel.BuildDeleteConfirmationText());
        if (confirmed)
            viewModel.DeleteSelectedCommand.Execute(null);
    }

    private async void OnExportClick(object? sender, RoutedEventArgs e)
    {
        SynchronizeGridSelection();
        if (_viewModel is not { CanExportSelection: true } viewModel ||
            TopLevel.GetTopLevel(this) is not TopLevel owner)
            return;

        try
        {
            var file = await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                SuggestedFileName = $"CxShell-Recording-{DateTime.Now:yyyyMMdd-HHmmss}.cast",
                DefaultExtension = "cast",
                FileTypeChoices =
                [
                    new FilePickerFileType("asciicast v2") { Patterns = ["*.cast"] }
                ]
            });
            if (file != null)
                await System.IO.File.WriteAllTextAsync(file.Path.LocalPath, viewModel.BuildAsciicast());
        }
        catch
        {
            // Export failures do not close the settings center.
        }
    }
}
