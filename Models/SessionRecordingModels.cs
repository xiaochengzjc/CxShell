namespace CxShell.Models;

public sealed class SessionRecording
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SessionId { get; set; }
    public string SessionLabel { get; set; } = string.Empty;
    public string Endpoint { get; set; } = string.Empty;
    public string Protocol { get; set; } = string.Empty;
    public DateTimeOffset StartedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? EndedAtUtc { get; set; }
    public long ByteSize { get; set; }
    public int ChunkCount { get; set; }
    public long DurationMilliseconds { get; set; }
    public int Columns { get; set; } = 80;
    public int Rows { get; set; } = 24;
}

public sealed record SessionRecordingChunk(long OffsetMilliseconds, byte[] Data);
