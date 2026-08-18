using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CxShell.Services;
using CxShell.ViewModels;

namespace CxShell.Views;

public partial class SessionRecordingPage : UserControl
{
    private SessionRecordingViewModel? _viewModel;
    private bool _initialized;

    public SessionRecordingPage()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
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
            _viewModel.PlaybackUpdated -= OnPlaybackUpdated;
        _viewModel = viewModel;
        if (_viewModel != null)
            _viewModel.PlaybackUpdated += OnPlaybackUpdated;
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
