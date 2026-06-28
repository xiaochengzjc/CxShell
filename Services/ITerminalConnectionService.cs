using CxShell.Models;

namespace CxShell.Services;

public interface ITerminalConnectionService : IDisposable
{
    bool IsConnected { get; }

    event Action<string>? DataReceived;
    event Func<byte[], bool>? BinaryDataReceived;
    event Action<string>? ConnectionClosed;
    event Action<string>? ErrorOccurred;

    Task ConnectAsync(
        SessionInfo session,
        string? password,
        int columns = 80,
        int rows = 24,
        CancellationToken cancellationToken = default);

    void SendData(string data);

    void SendBytes(byte[] data);

    void SendKeepAlive();

    void ResizeTerminal(int columns, int rows);

    void Disconnect();
}
