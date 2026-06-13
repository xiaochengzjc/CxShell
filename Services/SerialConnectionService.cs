using System;
using System.IO;
using System.IO.Ports;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ChiXueSsh.Models;

namespace ChiXueSsh.Services;

public sealed class SerialConnectionService : ITerminalConnectionService
{
    private readonly object _writeLock = new();
    private readonly Decoder _utf8Decoder = Encoding.UTF8.GetDecoder();
    private SerialPort? _serialPort;
    private CancellationTokenSource? _readCts;
    private Task? _readTask;

    public bool IsConnected => _serialPort?.IsOpen ?? false;

    public event Action<string>? DataReceived;
    public event Func<byte[], bool>? BinaryDataReceived;
    public event Action<string>? ConnectionClosed;
    public event Action<string>? ErrorOccurred;

    public Task ConnectAsync(
        SessionInfo session,
        string? password,
        int columns = 80,
        int rows = 24,
        CancellationToken cancellationToken = default)
    {
        Disconnect();

        var portName = string.IsNullOrWhiteSpace(session.SerialPortName)
            ? session.Host
            : session.SerialPortName;
        if (string.IsNullOrWhiteSpace(portName))
            throw new InvalidOperationException("Serial port name is required.");

        _serialPort = new SerialPort(
            portName.Trim(),
            Math.Max(1, session.SerialBaudRate),
            ParseParity(session.SerialParity),
            Math.Clamp(session.SerialDataBits, 5, 8),
            ParseStopBits(session.SerialStopBits))
        {
            Encoding = Encoding.UTF8,
            ReadTimeout = 500,
            WriteTimeout = 500,
            Handshake = ParseHandshake(session.SerialFlowControl),
            DtrEnable = true,
            RtsEnable = session.SerialFlowControl.Equals("RTS/CTS", StringComparison.OrdinalIgnoreCase)
        };

        try
        {
            _serialPort.Open();
            _utf8Decoder.Reset();
            _readCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _readTask = Task.Run(() => ReadLoop(_readCts.Token), _readCts.Token);
            return Task.CompletedTask;
        }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke(ex.Message);
            Disconnect();
            throw;
        }
    }

    public void SendData(string data)
    {
        SendBytes(Encoding.UTF8.GetBytes(data));
    }

    public void SendBytes(byte[] data)
    {
        try
        {
            lock (_writeLock)
            {
                if (_serialPort?.IsOpen != true)
                    return;

                _serialPort.Write(data, 0, data.Length);
            }
        }
        catch (Exception ex) when (ex is ObjectDisposedException or IOException or InvalidOperationException or TimeoutException)
        {
            ErrorOccurred?.Invoke(ex.Message);
        }
    }

    public void SendKeepAlive()
    {
        SendBytes(new byte[] { 0 });
    }

    public void ResizeTerminal(int columns, int rows)
    {
        // Serial links do not have a standard terminal resize negotiation.
    }

    public void Disconnect()
    {
        _readCts?.Cancel();

        try
        {
            _readTask?.Wait(TimeSpan.FromSeconds(2));
        }
        catch
        {
        }

        try
        {
            if (_serialPort?.IsOpen == true)
                _serialPort.Close();
        }
        catch
        {
        }

        _serialPort?.Dispose();
        _serialPort = null;

        _readCts?.Dispose();
        _readCts = null;
        _readTask = null;
    }

    private void ReadLoop(CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];

        try
        {
            while (!cancellationToken.IsCancellationRequested && _serialPort?.IsOpen == true)
            {
                int bytesRead;
                try
                {
                    bytesRead = _serialPort.Read(buffer, 0, buffer.Length);
                }
                catch (TimeoutException)
                {
                    continue;
                }

                if (bytesRead <= 0)
                    continue;

                var chunk = new byte[bytesRead];
                Buffer.BlockCopy(buffer, 0, chunk, 0, bytesRead);
                if (BinaryDataReceived?.Invoke(chunk) == true)
                    continue;

                var charCount = _utf8Decoder.GetCharCount(chunk, 0, chunk.Length);
                if (charCount == 0)
                    continue;

                var chars = new char[charCount];
                var charsRead = _utf8Decoder.GetChars(chunk, 0, chunk.Length, chars, 0);
                DataReceived?.Invoke(new string(chars, 0, charsRead));
            }
        }
        catch (Exception ex)
        {
            if (!cancellationToken.IsCancellationRequested)
                ErrorOccurred?.Invoke(ex.Message);
        }
        finally
        {
            ConnectionClosed?.Invoke("Connection closed.");
        }
    }

    private static Parity ParseParity(string value)
    {
        return value.Trim().ToUpperInvariant() switch
        {
            "ODD" => Parity.Odd,
            "EVEN" => Parity.Even,
            "MARK" => Parity.Mark,
            "SPACE" => Parity.Space,
            _ => Parity.None
        };
    }

    private static StopBits ParseStopBits(string value)
    {
        return value.Trim().ToUpperInvariant() switch
        {
            "1.5" or "ONEPOINTFIVE" => StopBits.OnePointFive,
            "2" or "TWO" => StopBits.Two,
            _ => StopBits.One
        };
    }

    private static Handshake ParseHandshake(string value)
    {
        return value.Trim().ToUpperInvariant() switch
        {
            "RTS/CTS" or "REQUESTTOSEND" => Handshake.RequestToSend,
            "XON/XOFF" or "XONXOFF" => Handshake.XOnXOff,
            "BOTH" => Handshake.RequestToSendXOnXOff,
            _ => Handshake.None
        };
    }

    public void Dispose()
    {
        Disconnect();
    }
}
