using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using CxShell.ViewModels;

namespace CxShell.Views;

public partial class SessionRecordingWindow : Window
{
    private SessionRecordingViewModel? _viewModel;
    private bool _initialized;

    public SessionRecordingWindow()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
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
        BindViewModel(null);
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
            _viewModel.Dispose();
        }
        _viewModel = viewModel;
        if (_viewModel != null)
            _viewModel.PlaybackUpdated += OnPlaybackUpdated;
    }

    private void OnPlaybackUpdated()
    {
        PlaybackTerminal.InvalidateVisual();
    }

    private async void OnDeleteClick(object? sender, RoutedEventArgs e)
    {
        if (_viewModel?.SelectedRecording == null)
            return;
        var confirmed = await AtomUiDialogService.ShowConfirmAsync(
            this,
            _viewModel.TitleText,
            string.Format(
                Services.LocalizationService.Shared.Text("Recording.DeleteConfirm"),
                _viewModel.SelectedRecording.Label));
        if (confirmed)
            _viewModel.DeleteSelectedCommand.Execute(null);
    }

    private async void OnExportClick(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is not { HasSelection: true } viewModel)
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
