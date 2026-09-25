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

    public SessionRecordingPage()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        RecordingGrid.SelectionChanged += OnGridSelectionChanged;
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
        if (_viewModel != null && _gridSource != null)
            _viewModel.SelectedRecording = _gridSource.GetCurrentItem(RecordingGrid);
    }

    private void OnPlaybackUpdated()
    {
        Dispatcher.UIThread.Post(() => PlaybackTerminal.InvalidateVisual());
    }

    private async void OnDeleteClick(object? sender, RoutedEventArgs e)
    {
        if (_viewModel?.SelectedRecording == null ||
            TopLevel.GetTopLevel(this) is not TopLevel owner)
            return;

        var confirmed = await AtomUiDialogService.ShowConfirmAsync(
            owner,
            _viewModel.TitleText,
            string.Format(
                LocalizationService.Shared.Text("Recording.DeleteConfirm"),
                _viewModel.SelectedRecording.Label));
        if (confirmed)
            _viewModel.DeleteSelectedCommand.Execute(null);
    }

    private async void OnExportClick(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is not { HasSelection: true } viewModel ||
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
