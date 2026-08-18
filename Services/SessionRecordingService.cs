using CxShell.Models;
using System.Collections.Concurrent;

namespace CxShell.Services;

public sealed class SessionRecordingService
{
    private ApplicationSettings _settings = new();
    private readonly ConcurrentDictionary<Guid, SessionRecorder> _activeRecorders = new();

    public static SessionRecordingService Shared { get; } = new();

    public SessionRecordingStore Store { get; } = SessionRecordingStore.Shared;

    public bool IsEnabled => _settings.RecordTerminalSessions;

    public void Configure(ApplicationSettings settings)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    public SessionRecorder? Start(SessionInfo session, int columns, int rows)
    {
        if (!IsEnabled)
            return null;

        var recorder = new SessionRecorder(Store, session, columns, rows);
        _activeRecorders[recorder.RecordingId] = recorder;
        recorder.Stopped += () =>
        {
            _ = RemoveWhenFlushedAsync(recorder);
        };
        return recorder;
    }

    public bool IsActive(Guid recordingId) => _activeRecorders.ContainsKey(recordingId);

    public Task CleanupExpiredAsync(CancellationToken cancellationToken = default)
    {
        return Store.CleanupExpiredAsync(_settings.RecordingRetentionDays, cancellationToken);
    }

    private async Task RemoveWhenFlushedAsync(SessionRecorder recorder)
    {
        try
        {
            await recorder.Completion.ConfigureAwait(false);
        }
        finally
        {
            _activeRecorders.TryRemove(recorder.RecordingId, out _);
        }
    }
}
