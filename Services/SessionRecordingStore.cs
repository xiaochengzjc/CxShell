using System.Buffers.Binary;
using System.Text.Json;
using CxShell.Models;

namespace CxShell.Services;

public sealed class SessionRecordingStore
{
    private const int ChunkHeaderSize = sizeof(long) + sizeof(int);
    private const int MaximumChunkSize = 4 * 1024 * 1024;
    private readonly string _storageDirectory;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };

    public static SessionRecordingStore Shared { get; } = new(
        Path.Combine(SessionStorageService.GetStorageDirectory(), "recordings"));

    public SessionRecordingStore(string storageDirectory)
    {
        _storageDirectory = storageDirectory ?? throw new ArgumentNullException(nameof(storageDirectory));
    }

    public async Task SaveAsync(SessionRecording recording, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(recording);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(_storageDirectory);
            var path = GetMetadataPath(recording.Id);
            var temporaryPath = path + ".tmp";
            var json = JsonSerializer.Serialize(recording, _jsonOptions);
            await File.WriteAllTextAsync(temporaryPath, json, cancellationToken).ConfigureAwait(false);
            File.Move(temporaryPath, path, true);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task AppendChunkAsync(
        Guid recordingId,
        long offsetMilliseconds,
        ReadOnlyMemory<byte> data,
        CancellationToken cancellationToken = default)
    {
        if (data.Length == 0)
            return;
        if (data.Length > MaximumChunkSize)
            throw new InvalidDataException($"Recording chunk exceeds {MaximumChunkSize} bytes.");

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(_storageDirectory);
            await using var stream = new FileStream(
                GetDataPath(recordingId),
                FileMode.Append,
                FileAccess.Write,
                FileShare.Read,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var header = new byte[ChunkHeaderSize];
            BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(0, sizeof(long)), Math.Max(0, offsetMilliseconds));
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(sizeof(long), sizeof(int)), data.Length);
            await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
            await stream.WriteAsync(data, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<SessionRecording>> ListAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!Directory.Exists(_storageDirectory))
                return [];

            var recordings = new List<SessionRecording>();
            foreach (var path in Directory.EnumerateFiles(_storageDirectory, "*.meta.json"))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
                    var recording = JsonSerializer.Deserialize<SessionRecording>(json);
                    if (recording != null)
                        recordings.Add(recording);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    // Keep one damaged entry from hiding valid recordings.
                }
            }

            return recordings
                .OrderByDescending(item => item.StartedAtUtc)
                .ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<SessionRecordingChunk>> GetChunksAsync(
        Guid recordingId,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var path = GetDataPath(recordingId);
            if (!File.Exists(path))
                return [];

            var chunks = new List<SessionRecordingChunk>();
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var header = new byte[ChunkHeaderSize];
            while (await ReadExactlyOrEndAsync(stream, header, cancellationToken).ConfigureAwait(false))
            {
                var offset = BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(0, sizeof(long)));
                var length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(sizeof(long), sizeof(int)));
                if (length is <= 0 or > MaximumChunkSize)
                    break;

                var data = new byte[length];
                if (!await ReadExactlyOrEndAsync(stream, data, cancellationToken).ConfigureAwait(false))
                    break;
                chunks.Add(new SessionRecordingChunk(Math.Max(0, offset), data));
            }

            return chunks;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DeleteAsync(Guid recordingId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            File.Delete(GetMetadataPath(recordingId));
            File.Delete(GetDataPath(recordingId));
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task CleanupExpiredAsync(int retentionDays, CancellationToken cancellationToken = default)
    {
        var cutoff = DateTimeOffset.UtcNow.AddDays(-Math.Clamp(retentionDays, 1, 3650));
        var recordings = await ListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var recording in recordings.Where(item => (item.EndedAtUtc ?? item.StartedAtUtc) < cutoff))
            await DeleteAsync(recording.Id, cancellationToken).ConfigureAwait(false);
    }

    private string GetMetadataPath(Guid id) => Path.Combine(_storageDirectory, $"{id:N}.meta.json");

    private string GetDataPath(Guid id) => Path.Combine(_storageDirectory, $"{id:N}.data");

    private static async Task<bool> ReadExactlyOrEndAsync(
        Stream stream,
        Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[total..], cancellationToken).ConfigureAwait(false);
            if (read == 0)
                return false;
            total += read;
        }

        return true;
    }
}
