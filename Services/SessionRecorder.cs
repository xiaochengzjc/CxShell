using System.Diagnostics;
using System.Text;
using CxShell.Models;

namespace CxShell.Services;

public sealed class SessionRecorder : IDisposable
{
    private const int FlushIntervalMilliseconds = 600;
    private const int FlushThresholdBytes = 64 * 1024;
    private readonly object _gate = new();
    private readonly object _queueGate = new();
    private readonly SessionRecordingStore _store;
    private readonly SessionRecording _recording;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Timer _flushTimer;
    private MemoryStream _buffer = new();
    private long _bufferStartOffset;
    private Task _writeTail = Task.CompletedTask;
    private bool _disposed;
    private bool _failed;

    public SessionRecorder(
        SessionRecordingStore store,
        SessionInfo session,
        int columns,
        int rows)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        ArgumentNullException.ThrowIfNull(session);
        _recording = new SessionRecording
        {
            SessionId = session.Id,
            SessionLabel = string.IsNullOrWhiteSpace(session.Name) ? session.Host : session.Name,
            Endpoint = $"{session.Host}:{session.Port}",
            Protocol = session.Protocol.ToString(),
            Columns = Math.Clamp(columns, 20, 500),
            Rows = Math.Clamp(rows, 5, 200)
        };
        Enqueue(() => _store.SaveAsync(_recording));
        _flushTimer = new Timer(_ => Flush(), null, FlushIntervalMilliseconds, FlushIntervalMilliseconds);
    }

    public Guid RecordingId => _recording.Id;
    public event Action? Stopped;

    public Task Completion
    {
        get
        {
            lock (_queueGate)
                return _writeTail;
        }
    }

    public void Write(string text)
    {
        if (string.IsNullOrEmpty(text))
            return;
        Write(Encoding.UTF8.GetBytes(text));
    }

    public void Write(ReadOnlySpan<byte> data)
    {
        if (data.Length == 0)
            return;

        var shouldFlush = false;
        lock (_gate)
        {
            if (_disposed || _failed)
                return;
            if (_buffer.Length == 0)
                _bufferStartOffset = _clock.ElapsedMilliseconds;
            _buffer.Write(data);
            shouldFlush = _buffer.Length >= FlushThresholdBytes;
        }

        if (shouldFlush)
            Flush();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
        }

        _flushTimer.Dispose();
        FlushCore();
        _recording.EndedAtUtc = DateTimeOffset.UtcNow;
        _recording.DurationMilliseconds = Math.Max(_recording.DurationMilliseconds, _clock.ElapsedMilliseconds);
        Enqueue(() => _store.SaveAsync(_recording));
        Stopped?.Invoke();
    }

    private void Flush()
    {
        lock (_gate)
        {
            if (_disposed || _failed)
                return;
        }
        FlushCore();
    }

    private void FlushCore()
    {
        byte[] payload;
        long offset;
        lock (_gate)
        {
            if (_failed || _buffer.Length == 0)
                return;
            payload = _buffer.ToArray();
            offset = _bufferStartOffset;
            _buffer.Dispose();
            _buffer = new MemoryStream();
        }

        Enqueue(async () =>
        {
            await _store.AppendChunkAsync(_recording.Id, offset, payload).ConfigureAwait(false);
            _recording.ByteSize += payload.Length;
            _recording.ChunkCount++;
            _recording.DurationMilliseconds = Math.Max(_recording.DurationMilliseconds, offset);
            await _store.SaveAsync(_recording).ConfigureAwait(false);
        });
    }

    private void Enqueue(Func<Task> operation)
    {
        lock (_queueGate)
            _writeTail = RunAfterAsync(_writeTail, operation);
    }

    private async Task RunAfterAsync(Task previous, Func<Task> operation)
    {
        try
        {
            await previous.ConfigureAwait(false);
        }
        catch
        {
            // The first failure disables later recording writes.
        }

        if (_failed)
            return;

        try
        {
            await operation().ConfigureAwait(false);
        }
        catch
        {
            _failed = true;
        }
    }
}
