using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CxShell.Models;
using CxShell.Services;
using CxShell.Terminal;

namespace CxShell.ViewModels;

public sealed class SessionRecordingItemViewModel
{
    public SessionRecordingItemViewModel(SessionRecording recording)
    {
        Recording = recording;
    }

    public SessionRecording Recording { get; }
    public string Label => string.IsNullOrWhiteSpace(Recording.SessionLabel)
        ? LocalizationService.Shared.Text("Recording.Unnamed")
        : Recording.SessionLabel;
    public string EndpointText => Recording.Endpoint;
    public string StartedText => Recording.StartedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
    public string DurationText => SessionRecordingViewModel.FormatDuration(Recording.DurationMilliseconds);
    public string SizeText => SessionRecordingViewModel.FormatSize(Recording.ByteSize);
}

public sealed partial class SessionRecordingViewModel : ObservableObject, IDisposable
{
    private const long IdleGapCapMilliseconds = 1000;
    private static readonly int[] PlaybackSpeeds = [1, 2, 4, 8, 16];
    private readonly SessionRecordingStore _store;
    private readonly LocalizationService _localization = LocalizationService.Shared;
    private readonly DispatcherTimer _timer;
    private IReadOnlyList<SessionRecordingChunk> _chunks = [];
    private AnsiParser _parser;
    private int _nextChunkIndex;
    private int _loadVersion;
    private bool _suppressSeek;

    [ObservableProperty] private SessionRecordingItemViewModel? _selectedRecording;
    [ObservableProperty] private TerminalBuffer _playbackBuffer;
    [ObservableProperty] private bool _isPlaying;
    [ObservableProperty] private bool _skipIdle = true;
    [ObservableProperty] private double _positionMilliseconds;
    [ObservableProperty] private double _durationMilliseconds;
    [ObservableProperty] private int _speedIndex;
    [ObservableProperty] private string _statusText = string.Empty;

    public SessionRecordingViewModel(SessionRecordingStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _playbackBuffer = CreateBuffer(80, 24);
        _parser = new AnsiParser(_playbackBuffer);
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(33), DispatcherPriority.Background, OnPlaybackTick);
        _localization.LanguageChanged += OnLanguageChanged;
    }

    public ObservableCollection<SessionRecordingItemViewModel> Recordings { get; } = new();
    public bool HasRecordings => Recordings.Count > 0;
    public bool CanDeleteSelection => SelectedRecording != null &&
                                      !SessionRecordingService.Shared.IsActive(SelectedRecording.Recording.Id);
    public bool HasSelection => SelectedRecording != null && _chunks.Count > 0;
    public string TitleText => Text("Recording.Title");
    public string DescriptionText => Text("Recording.Description");
    public string ListText => Text("Recording.List");
    public string EmptyText => Text("Recording.Empty");
    public string PlayerText => SelectedRecording == null
        ? Text("Recording.Select")
        : string.Format(Text("Recording.PlayerTitle"), SelectedRecording.Label, SelectedRecording.StartedText);
    public string RefreshText => Text("Recording.Refresh");
    public string ExportText => Text("Recording.Export");
    public string DeleteText => Text("Recording.Delete");
    public string CloseText => Text("Recording.Close");
    public string PlayText => IsPlaying ? Text("Recording.Pause") : Text("Recording.Play");
    public string SkipIdleText => Text("Recording.SkipIdle");
    public string SpeedText => $"{PlaybackSpeeds[SpeedIndex]}x";
    public string PositionText => FormatDuration((long)PositionMilliseconds);
    public string TotalDurationText => FormatDuration((long)DurationMilliseconds);
    public string ColumnSessionText => Text("Recording.ColumnSession");
    public string ColumnStartedText => Text("Recording.ColumnStarted");
    public string ColumnDurationText => Text("Recording.ColumnDuration");
    public string ColumnSizeText => Text("Recording.ColumnSize");

    public event Action? PlaybackUpdated;

    public async Task InitializeAsync()
    {
        await RefreshAsync().ConfigureAwait(false);
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        var selectedId = SelectedRecording?.Recording.Id;
        try
        {
            var recordings = await _store.ListAsync();
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                Recordings.Clear();
                foreach (var recording in recordings)
                    Recordings.Add(new SessionRecordingItemViewModel(recording));
                OnPropertyChanged(nameof(HasRecordings));
                SelectedRecording = Recordings.FirstOrDefault(item => item.Recording.Id == selectedId)
                    ?? Recordings.FirstOrDefault();
                StatusText = Recordings.Count == 0 ? EmptyText : string.Empty;
            });
        }
        catch (Exception ex)
        {
            StatusText = string.Format(Text("Recording.LoadFailed"), ex.Message);
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void TogglePlayback()
    {
        if (IsPlaying)
        {
            Pause();
            return;
        }

        if (!HasSelection)
            return;
        if (PositionMilliseconds >= DurationMilliseconds && DurationMilliseconds > 0)
        {
            SetPositionInternal(0);
            Seek(0);
        }

        IsPlaying = true;
        _timer.Start();
    }

    [RelayCommand]
    private void CycleSpeed()
    {
        SpeedIndex = (SpeedIndex + 1) % PlaybackSpeeds.Length;
    }

    [RelayCommand(CanExecute = nameof(CanDeleteSelected))]
    private async Task DeleteSelectedAsync()
    {
        var selected = SelectedRecording;
        if (selected == null)
            return;

        Pause();
        try
        {
            if (SessionRecordingService.Shared.IsActive(selected.Recording.Id))
                return;
            await _store.DeleteAsync(selected.Recording.Id);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                Recordings.Remove(selected);
                SelectedRecording = Recordings.FirstOrDefault();
                OnPropertyChanged(nameof(HasRecordings));
                StatusText = Recordings.Count == 0 ? EmptyText : string.Empty;
            });
        }
        catch (Exception ex)
        {
            StatusText = string.Format(Text("Recording.DeleteFailed"), ex.Message);
        }
    }

    public string BuildAsciicast()
    {
        var recording = SelectedRecording?.Recording;
        if (recording == null)
            return string.Empty;

        var builder = new StringBuilder();
        builder.AppendLine(JsonSerializer.Serialize(new
        {
            version = 2,
            width = recording.Columns,
            height = recording.Rows,
            timestamp = recording.StartedAtUtc.ToUnixTimeSeconds(),
            title = SelectedRecording?.Label
        }));
        foreach (var chunk in _chunks)
        {
            builder.AppendLine(JsonSerializer.Serialize(new object[]
            {
                chunk.OffsetMilliseconds / 1000.0,
                "o",
                Encoding.UTF8.GetString(chunk.Data)
            }));
        }

        return builder.ToString();
    }

    public void Dispose()
    {
        Pause();
        _localization.LanguageChanged -= OnLanguageChanged;
    }

    partial void OnSelectedRecordingChanged(SessionRecordingItemViewModel? value)
    {
        OnPropertyChanged(nameof(PlayerText));
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(CanDeleteSelection));
        DeleteSelectedCommand.NotifyCanExecuteChanged();
        TogglePlaybackCommand.NotifyCanExecuteChanged();
        _ = LoadSelectedAsync(value, ++_loadVersion);
    }

    partial void OnIsPlayingChanged(bool value)
    {
        OnPropertyChanged(nameof(PlayText));
    }

    partial void OnPositionMillisecondsChanged(double value)
    {
        OnPropertyChanged(nameof(PositionText));
        if (!_suppressSeek)
            Seek((long)value);
    }

    partial void OnDurationMillisecondsChanged(double value)
    {
        OnPropertyChanged(nameof(TotalDurationText));
    }

    partial void OnSpeedIndexChanged(int value)
    {
        if (value < 0 || value >= PlaybackSpeeds.Length)
        {
            SpeedIndex = Math.Clamp(value, 0, PlaybackSpeeds.Length - 1);
            return;
        }
        OnPropertyChanged(nameof(SpeedText));
    }

    private bool CanDeleteSelected() => SelectedRecording != null;

    private async Task LoadSelectedAsync(SessionRecordingItemViewModel? item, int version)
    {
        Pause();
        _chunks = [];
        _nextChunkIndex = 0;
        SetPositionInternal(0);
        if (item == null)
        {
            DurationMilliseconds = 0;
            ResetBuffer(80, 24);
            return;
        }

        try
        {
            var chunks = await _store.GetChunksAsync(item.Recording.Id);
            if (version != _loadVersion)
                return;

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                _chunks = chunks;
                DurationMilliseconds = Math.Max(
                    item.Recording.DurationMilliseconds,
                    chunks.Count > 0 ? chunks[^1].OffsetMilliseconds : 0);
                ResetBuffer(item.Recording.Columns, item.Recording.Rows);
                StatusText = chunks.Count == 0 ? Text("Recording.NoOutput") : string.Empty;
                OnPropertyChanged(nameof(HasSelection));
                TogglePlaybackCommand.NotifyCanExecuteChanged();
            });
        }
        catch (Exception ex)
        {
            StatusText = string.Format(Text("Recording.LoadFailed"), ex.Message);
        }
    }

    private void OnPlaybackTick(object? sender, EventArgs e)
    {
        if (_chunks.Count == 0)
        {
            Pause();
            return;
        }

        var next = PositionMilliseconds + 33.0 * PlaybackSpeeds[SpeedIndex];
        if (SkipIdle && _nextChunkIndex < _chunks.Count)
        {
            var upcoming = _chunks[_nextChunkIndex].OffsetMilliseconds;
            if (upcoming - next > IdleGapCapMilliseconds)
                next = upcoming - IdleGapCapMilliseconds;
        }

        SetPositionInternal(Math.Min(next, DurationMilliseconds));
        FeedUntil((long)PositionMilliseconds);
        if (PositionMilliseconds >= DurationMilliseconds)
            Pause();
    }

    private void Pause()
    {
        _timer.Stop();
        IsPlaying = false;
    }

    private void Seek(long targetMilliseconds)
    {
        if (_chunks.Count == 0)
            return;
        var recording = SelectedRecording?.Recording;
        ResetBuffer(recording?.Columns ?? 80, recording?.Rows ?? 24);
        _nextChunkIndex = 0;
        FeedUntil(targetMilliseconds);
    }

    private void FeedUntil(long targetMilliseconds)
    {
        while (_nextChunkIndex < _chunks.Count &&
               _chunks[_nextChunkIndex].OffsetMilliseconds <= targetMilliseconds)
        {
            _parser.Process(Encoding.UTF8.GetString(_chunks[_nextChunkIndex].Data));
            _nextChunkIndex++;
        }
        PlaybackBuffer.MarkAllDirty();
        PlaybackUpdated?.Invoke();
    }

    private void ResetBuffer(int columns, int rows)
    {
        PlaybackBuffer = CreateBuffer(columns, rows);
        _parser = new AnsiParser(PlaybackBuffer);
        PlaybackUpdated?.Invoke();
    }

    private void SetPositionInternal(double value)
    {
        _suppressSeek = true;
        try
        {
            PositionMilliseconds = value;
        }
        finally
        {
            _suppressSeek = false;
        }
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(TitleText));
        OnPropertyChanged(nameof(DescriptionText));
        OnPropertyChanged(nameof(ListText));
        OnPropertyChanged(nameof(EmptyText));
        OnPropertyChanged(nameof(PlayerText));
        OnPropertyChanged(nameof(RefreshText));
        OnPropertyChanged(nameof(ExportText));
        OnPropertyChanged(nameof(DeleteText));
        OnPropertyChanged(nameof(CloseText));
        OnPropertyChanged(nameof(PlayText));
        OnPropertyChanged(nameof(SkipIdleText));
        OnPropertyChanged(nameof(ColumnSessionText));
        OnPropertyChanged(nameof(ColumnStartedText));
        OnPropertyChanged(nameof(ColumnDurationText));
        OnPropertyChanged(nameof(ColumnSizeText));
    }

    private static TerminalBuffer CreateBuffer(int columns, int rows)
    {
        return new TerminalBuffer(
            Math.Clamp(columns, 20, 500),
            Math.Clamp(rows, 5, 200),
            20000);
    }

    internal static string FormatDuration(long milliseconds)
    {
        var duration = TimeSpan.FromMilliseconds(Math.Max(0, milliseconds));
        return duration.TotalHours >= 1
            ? duration.ToString(@"h\:mm\:ss")
            : duration.ToString(@"mm\:ss");
    }

    internal static string FormatSize(long bytes)
    {
        return bytes switch
        {
            < 1024 => $"{bytes} B",
            < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
            _ => $"{bytes / 1024.0 / 1024.0:0.#} MB"
        };
    }

    private string Text(string key) => _localization.Text(key);
}
