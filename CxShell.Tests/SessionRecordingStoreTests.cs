using CxShell.Models;
using CxShell.Services;

namespace CxShell.Tests;

public sealed class SessionRecordingStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "CxShell.Tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Store_RoundTripsMetadataAndChunks()
    {
        var store = new SessionRecordingStore(_directory);
        var recording = CreateRecording();
        await store.SaveAsync(recording);
        await store.AppendChunkAsync(recording.Id, 25, "first"u8.ToArray());
        await store.AppendChunkAsync(recording.Id, 150, "second"u8.ToArray());

        var recordings = await store.ListAsync();
        var chunks = await store.GetChunksAsync(recording.Id);

        var saved = Assert.Single(recordings);
        Assert.Equal(recording.Id, saved.Id);
        Assert.Equal("server-a", saved.SessionLabel);
        Assert.Collection(
            chunks,
            item =>
            {
                Assert.Equal(25, item.OffsetMilliseconds);
                Assert.Equal("first"u8.ToArray(), item.Data);
            },
            item =>
            {
                Assert.Equal(150, item.OffsetMilliseconds);
                Assert.Equal("second"u8.ToArray(), item.Data);
            });
    }

    [Fact]
    public async Task GetChunks_IgnoresTruncatedTail()
    {
        var store = new SessionRecordingStore(_directory);
        var recording = CreateRecording();
        await store.SaveAsync(recording);
        await store.AppendChunkAsync(recording.Id, 10, "complete"u8.ToArray());
        var dataPath = Path.Combine(_directory, $"{recording.Id:N}.data");
        await using (var stream = new FileStream(dataPath, FileMode.Append, FileAccess.Write))
            await stream.WriteAsync(new byte[] { 1, 2, 3, 4 });

        var chunks = await store.GetChunksAsync(recording.Id);

        var chunk = Assert.Single(chunks);
        Assert.Equal("complete"u8.ToArray(), chunk.Data);
    }

    [Fact]
    public async Task Recorder_FlushesFinalChunkAndMetadata()
    {
        var store = new SessionRecordingStore(_directory);
        var session = new SessionInfo
        {
            Id = Guid.NewGuid(),
            Name = "recorded-session",
            Host = "example.test",
            Port = 2222,
            Protocol = SessionProtocol.SSH
        };
        var recorder = new SessionRecorder(store, session, 120, 36);
        recorder.Write("hello\r\n");
        recorder.Dispose();
        await recorder.Completion;

        var recording = Assert.Single(await store.ListAsync());
        var chunk = Assert.Single(await store.GetChunksAsync(recording.Id));
        Assert.Equal("recorded-session", recording.SessionLabel);
        Assert.Equal("example.test:2222", recording.Endpoint);
        Assert.Equal(120, recording.Columns);
        Assert.Equal(36, recording.Rows);
        Assert.NotNull(recording.EndedAtUtc);
        Assert.Equal("hello\r\n", System.Text.Encoding.UTF8.GetString(chunk.Data));
    }

    [Fact]
    public async Task CleanupExpired_RemovesOnlyOldRecordings()
    {
        var store = new SessionRecordingStore(_directory);
        var old = CreateRecording();
        old.StartedAtUtc = DateTimeOffset.UtcNow.AddDays(-40);
        old.EndedAtUtc = old.StartedAtUtc.AddMinutes(1);
        var current = CreateRecording();
        await store.SaveAsync(old);
        await store.SaveAsync(current);

        await store.CleanupExpiredAsync(30);

        var remaining = Assert.Single(await store.ListAsync());
        Assert.Equal(current.Id, remaining.Id);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, true);
    }

    private static SessionRecording CreateRecording()
    {
        return new SessionRecording
        {
            SessionId = Guid.NewGuid(),
            SessionLabel = "server-a",
            Endpoint = "server-a:22",
            Protocol = "SSH",
            Columns = 100,
            Rows = 30
        };
    }
}
