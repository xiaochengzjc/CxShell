using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ChiXueSsh.Services;

public enum XymodemProtocol
{
    Xmodem,
    Ymodem
}

public enum XymodemTransferDirection
{
    Upload,
    Download
}

public sealed class XymodemTransfer : IDisposable
{
    private const byte Soh = 0x01;
    private const byte Stx = 0x02;
    private const byte Eot = 0x04;
    private const byte Ack = 0x06;
    private const byte Nak = 0x15;
    private const byte Can = 0x18;
    private const byte CrcRequest = (byte)'C';
    private const byte CpmEof = 0x1A;

    private readonly XymodemProtocol _protocol;
    private readonly XymodemTransferDirection _direction;
    private readonly Action<byte[]> _sendToRemote;
    private readonly Action<byte[]> _toTerminal;
    private readonly Action<string, string> _status;
    private readonly Action _completed;
    private readonly string? _downloadDirectory;
    private readonly string _duplicateAction;
    private readonly IReadOnlyList<string> _uploadFiles;
    private readonly string? _suggestedDownloadFileName;
    private readonly List<byte> _input = new();
    private readonly object _gate = new();
    private CancellationTokenSource _receiverPromptCts = new();
    private CancellationTokenSource? _uploadWatchdogCts;

    private UploadState _uploadState = UploadState.WaitReceiverRequest;
    private DateTimeOffset _uploadWaitingSince = DateTimeOffset.UtcNow;
    private string _uploadWaitingFor = "receiver request";
    private bool _useCrc = true;
    private bool _isCompleted;
    private int _uploadIndex;
    private FileInfo? _uploadFile;
    private FileStream? _downloadStream;
    private string? _downloadFileName;
    private long _downloadExpectedSize = -1;
    private long _downloadWritten;
    private int _sendBlockIndex = 1;
    private int _expectedReceiveBlockNumber = 1;
    private byte[]? _lastSentPacket;
    private bool _sentFinalYmodemHeader;
    private bool _waitingForNextYmodemHeader;
    private long _lastUploadProgressBytes;

    public XymodemTransfer(
        XymodemProtocol protocol,
        XymodemTransferDirection direction,
        Action<byte[]> sendToRemote,
        Action<byte[]> toTerminal,
        Action<string, string> status,
        Action completed,
        string? downloadDirectory = null,
        string? duplicateAction = null,
        IReadOnlyList<string>? uploadFiles = null,
        string? suggestedDownloadFileName = null)
    {
        _protocol = protocol;
        _direction = direction;
        _sendToRemote = sendToRemote;
        _toTerminal = toTerminal;
        _status = status;
        _completed = completed;
        _downloadDirectory = downloadDirectory;
        _duplicateAction = string.IsNullOrWhiteSpace(duplicateAction) ? "AutoRename" : duplicateAction;
        _uploadFiles = uploadFiles ?? Array.Empty<string>();
        _suggestedDownloadFileName = suggestedDownloadFileName;
    }

    public static bool TryFindReceiverRequest(byte[] bytes, out int index)
    {
        for (var i = 0; i < bytes.Length; i++)
        {
            if (bytes[i] is CrcRequest or Nak)
            {
                index = i;
                return true;
            }
        }

        index = -1;
        return false;
    }

    public void Start()
    {
        if (_direction == XymodemTransferDirection.Upload)
        {
            _status($"[{ProtocolName} upload started]", "36");
            StartUploadWatchdog();
            return;
        }

        _status($"[{ProtocolName} download started]", "36");
        StartReceiverPromptLoop();
    }

    public void Feed(byte[] bytes)
    {
        lock (_gate)
        {
            if (_isCompleted)
                return;

            _input.AddRange(bytes);
            try
            {
                if (_direction == XymodemTransferDirection.Upload)
                    ProcessUpload();
                else
                    ProcessDownload();
            }
            catch (Exception ex)
            {
                _status($"[{ProtocolName} error: {ex.Message}]", "31");
                Abort();
            }
        }
    }

    public void Abort()
    {
        lock (_gate)
        {
            if (_isCompleted)
                return;

            _sendToRemote(new[] { Can, Can, Can, Can, Can });
            Complete();
        }
    }

    private string ProtocolName => _protocol == XymodemProtocol.Ymodem ? "YMODEM" : "XMODEM";

    private void ProcessUpload()
    {
        while (!_isCompleted)
        {
            switch (_uploadState)
            {
                case UploadState.WaitReceiverRequest:
                    if (!TryReadReceiverRequest(out var request))
                        return;

                    _useCrc = request == CrcRequest;
                    TraceUpload($"received {ControlName(request)}; crc={_useCrc}");
                    if (_protocol == XymodemProtocol.Ymodem)
                        StartYmodemFileOrFinish();
                    else
                        StartXmodemFile();
                    break;

                case UploadState.WaitHeaderAck:
                    if (!TryReadAckLike(out var headerResponse))
                        return;

                    if (headerResponse == Ack)
                    {
                        TraceUpload("received ACK for header");
                        if (_sentFinalYmodemHeader)
                        {
                            _status("[YMODEM upload finished]", "32");
                            Complete();
                            return;
                        }

                        SetUploadState(UploadState.WaitHeaderCrcRequest, "receiver request after header");
                    }
                    else if (headerResponse == Nak)
                    {
                        TraceUpload("received NAK for header; resending");
                        ResendLastPacket();
                    }
                    else
                    {
                        RemoteCancelledUpload();
                        return;
                    }
                    break;

                case UploadState.WaitHeaderCrcRequest:
                    if (!TryReadReceiverRequest(out var headerRequest))
                        return;

                    TraceUpload($"received {ControlName(headerRequest)} after header");
                    if (headerRequest == Can)
                    {
                        RemoteCancelledUpload();
                        return;
                    }

                    if (_waitingForNextYmodemHeader)
                    {
                        if (headerRequest == Nak)
                        {
                            TraceUpload("received NAK for next header; resending EOT");
                            SendEot();
                            return;
                        }

                        _waitingForNextYmodemHeader = false;
                        StartYmodemFileOrFinish();
                        return;
                    }

                    if (_uploadFile == null)
                        throw new InvalidOperationException("Upload file is not open.");

                    if (headerRequest == Nak)
                    {
                        TraceUpload("received NAK after header; resending header");
                        ResendLastPacket();
                        SetUploadState(UploadState.WaitHeaderAck, "file header ACK");
                        return;
                    }

                    SendNextDataPacket();
                    break;

                case UploadState.WaitDataAck:
                    if (!TryReadAckLike(out var dataResponse))
                        return;

                    if (dataResponse == Ack)
                    {
                        TraceUpload($"received ACK for block {_sendBlockIndex - 1}");
                        SendNextDataPacket();
                    }
                    else if (dataResponse == Nak)
                    {
                        TraceUpload($"received NAK for block {_sendBlockIndex - 1}; resending");
                        ResendLastPacket();
                    }
                    else
                    {
                        RemoteCancelledUpload();
                        return;
                    }
                    break;

                case UploadState.WaitEotAck:
                    if (!TryReadAckLike(out var eotResponse))
                        return;

                    if (eotResponse == Ack)
                    {
                        TraceUpload("received ACK for EOT");
                        FinishUploadedFile();
                    }
                    else if (eotResponse == Nak)
                    {
                        TraceUpload("received NAK for EOT; resending EOT");
                        SendEot();
                    }
                    else
                    {
                        RemoteCancelledUpload();
                        return;
                    }
                    break;
            }
        }
    }

    private void StartXmodemFile()
    {
        var firstPath = _uploadFiles.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(firstPath) || !File.Exists(firstPath))
            throw new InvalidOperationException("No upload file was selected.");

        _uploadFile = new FileInfo(firstPath);
        _waitingForNextYmodemHeader = false;
        _lastUploadProgressBytes = 0;
        _sendBlockIndex = 1;
        _status($"[XMODEM uploading: {_uploadFile.Name}]", "36");
        SendNextDataPacket();
    }

    private void StartYmodemFileOrFinish()
    {
        if (_uploadIndex >= _uploadFiles.Count)
        {
            _uploadFile = null;
            _waitingForNextYmodemHeader = false;
            _sentFinalYmodemHeader = true;
            _lastSentPacket = BuildPacket(Soh, 0, new byte[128]);
            _sendToRemote(_lastSentPacket);
            TraceUpload("sent final empty header");
            SetUploadState(UploadState.WaitHeaderAck, "final empty header ACK");
            return;
        }

        var path = _uploadFiles[_uploadIndex];
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            _uploadIndex++;
            StartYmodemFileOrFinish();
            return;
        }

        _uploadFile = new FileInfo(path);
        _waitingForNextYmodemHeader = false;
        _lastUploadProgressBytes = 0;
        _sentFinalYmodemHeader = false;
        _sendBlockIndex = 1;
        var remainingFiles = _uploadFiles.Skip(_uploadIndex)
            .Select(candidate => string.IsNullOrWhiteSpace(candidate) ? null : new FileInfo(candidate))
            .Where(file => file?.Exists == true)
            .ToArray();
        var bytesRemaining = remainingFiles.Sum(file => file!.Length);
        _status($"[YMODEM uploading: {_uploadFile.Name}]", "36");
        _lastSentPacket = BuildPacket(Soh, 0, BuildYmodemHeaderPayload(_uploadFile, remainingFiles.Length, bytesRemaining));
        _sendToRemote(_lastSentPacket);
        TraceUpload($"sent header for {_uploadFile.Name}, size={_uploadFile.Length}, filesRemaining={remainingFiles.Length}, bytesRemaining={bytesRemaining}");
        SetUploadState(UploadState.WaitHeaderAck, "file header ACK");
    }

    private void SendNextDataPacket()
    {
        if (_uploadFile == null)
            throw new InvalidOperationException("Upload file is not open.");

        var blockSize = _protocol == XymodemProtocol.Ymodem ? 1024 : 128;
        var offset = (long)(_sendBlockIndex - 1) * blockSize;
        if (offset >= _uploadFile.Length)
        {
            SendEot();
            return;
        }

        var payload = new byte[blockSize];
        Array.Fill(payload, CpmEof);
        using (var stream = File.OpenRead(_uploadFile.FullName))
        {
            stream.Position = offset;
            var read = stream.Read(payload, 0, payload.Length);
            if (read < payload.Length)
                Array.Fill(payload, CpmEof, read, payload.Length - read);
        }

        _lastSentPacket = BuildPacket(blockSize == 1024 ? Stx : Soh, _sendBlockIndex & 0xff, payload);
        _sendToRemote(_lastSentPacket);
        TraceUpload($"sent block {_sendBlockIndex}, size={blockSize}");
        ReportUploadProgress(Math.Min(offset + blockSize, _uploadFile.Length));
        _sendBlockIndex++;
        SetUploadState(UploadState.WaitDataAck, $"data block {_sendBlockIndex - 1} ACK");
    }

    private void ReportUploadProgress(long uploadedBytes)
    {
        if (_uploadFile == null)
            return;

        if (uploadedBytes < _uploadFile.Length && uploadedBytes - _lastUploadProgressBytes < 1024 * 1024)
            return;

        _lastUploadProgressBytes = uploadedBytes;
        var percent = _uploadFile.Length <= 0
            ? 100
            : (int)Math.Min(100, uploadedBytes * 100 / _uploadFile.Length);
        _status($"[{ProtocolName} upload progress: {_uploadFile.Name} {percent}%]", "90");
    }

    private void SendEot()
    {
        _lastSentPacket = new[] { Eot };
        _sendToRemote(_lastSentPacket);
        TraceUpload("sent EOT");
        SetUploadState(UploadState.WaitEotAck, "EOT ACK");
    }

    private void FinishUploadedFile()
    {
        if (_uploadFile != null)
            _status($"[{ProtocolName} uploaded: {_uploadFile.Name}]", "32");

        if (_protocol == XymodemProtocol.Xmodem)
        {
            _status("[XMODEM upload finished]", "32");
            Complete();
            return;
        }

        _uploadIndex++;
        _uploadFile = null;
        _waitingForNextYmodemHeader = true;
        SetUploadState(UploadState.WaitHeaderCrcRequest, "next-file receiver request");
    }

    private void SetUploadState(UploadState state, string waitingFor)
    {
        _uploadState = state;
        _uploadWaitingFor = waitingFor;
        _uploadWaitingSince = DateTimeOffset.UtcNow;
        TraceUpload($"waiting for {waitingFor}");
    }

    private void TraceUpload(string message)
    {
        _ = message;
    }

    private void StartUploadWatchdog()
    {
        _uploadWatchdogCts?.Cancel();
        _uploadWatchdogCts?.Dispose();
        _uploadWatchdogCts = new CancellationTokenSource();
        _uploadWaitingSince = DateTimeOffset.UtcNow;
        var token = _uploadWatchdogCts.Token;

        _ = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), token);
                    lock (_gate)
                    {
                        if (_isCompleted || _direction != XymodemTransferDirection.Upload)
                            return;

                        var idle = DateTimeOffset.UtcNow - _uploadWaitingSince;
                        if (idle < TimeSpan.FromSeconds(15))
                            continue;

                        _status($"[{ProtocolName} upload timed out waiting for {_uploadWaitingFor}]", "31");
                        _sendToRemote(new[] { Can, Can, Can, Can, Can });
                        Complete();
                        return;
                    }
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    _status($"[{ProtocolName} upload watchdog stopped: {ex.Message}]", "31");
                    return;
                }
            }
        }, token);
    }

    private static string ControlName(byte value)
    {
        return value switch
        {
            Ack => "ACK",
            Nak => "NAK",
            Can => "CAN",
            CrcRequest => "C",
            Eot => "EOT",
            _ => $"0x{value:X2}"
        };
    }

    private void ProcessDownload()
    {
        while (!_isCompleted)
        {
            if (!TryReadProtocolPacket(out var packet))
                return;

            _receiverPromptCts.Cancel();

            if (packet.Kind == PacketKind.Cancel)
            {
                _status($"[{ProtocolName} download cancelled by remote]", "31");
                Complete();
                return;
            }

            if (packet.Kind == PacketKind.Eot)
            {
                FinishDownloadedFile();
                _sendToRemote(new[] { Ack });
                if (_protocol == XymodemProtocol.Ymodem)
                {
                    _sendToRemote(new[] { CrcRequest });
                    StartReceiverPromptLoop();
                }
                else
                {
                    _status("[XMODEM download finished]", "32");
                    Complete();
                }

                continue;
            }

            if (packet.Kind != PacketKind.Data || packet.Payload == null)
                continue;

            if (_protocol == XymodemProtocol.Ymodem && packet.BlockNumber == 0)
            {
                if (!HandleYmodemHeaderPacket(packet.Payload))
                    return;

                continue;
            }

            var expectedBlockNumber = _expectedReceiveBlockNumber & 0xff;
            var previousBlockNumber = (_expectedReceiveBlockNumber - 1) & 0xff;
            if (packet.BlockNumber == previousBlockNumber)
            {
                _sendToRemote(new[] { Ack });
                continue;
            }

            if (packet.BlockNumber != expectedBlockNumber)
            {
                _sendToRemote(new[] { Nak });
                continue;
            }

            EnsureDownloadStream();
            WriteDownloadPayload(packet.Payload);
            _expectedReceiveBlockNumber++;
            _sendToRemote(new[] { Ack });
        }
    }

    private bool HandleYmodemHeaderPacket(byte[] payload)
    {
        var fileName = ReadNullTerminatedAscii(payload, 0);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            _sendToRemote(new[] { Ack });
            _status("[YMODEM download finished]", "32");
            Complete();
            return false;
        }

        var metadataStart = fileName.Length + 1;
        var metadata = metadataStart < payload.Length ? ReadNullTerminatedAscii(payload, metadataStart) : string.Empty;
        _downloadExpectedSize = TryParseYmodemSize(metadata);
        _downloadFileName = Path.GetFileName(fileName);
        EnsureDownloadStream();
        _expectedReceiveBlockNumber = 1;
        _downloadWritten = 0;
        _status($"[YMODEM downloading: {_downloadFileName}]", "36");
        _sendToRemote(new[] { Ack, CrcRequest });
        return true;
    }

    private void EnsureDownloadStream()
    {
        if (_downloadStream != null)
            return;

        var directory = _downloadDirectory;
        if (string.IsNullOrWhiteSpace(directory))
            throw new InvalidOperationException("Download directory is not set.");

        Directory.CreateDirectory(directory);
        var fileName = _downloadFileName;
        if (string.IsNullOrWhiteSpace(fileName))
            fileName = string.IsNullOrWhiteSpace(_suggestedDownloadFileName)
                ? $"xmodem-{DateTime.Now:yyyyMMdd-HHmmss}.bin"
                : Path.GetFileName(_suggestedDownloadFileName);

        _downloadFileName = fileName;
        var path = ResolveReceivePath(directory, fileName);
        _downloadStream = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.Read);
        _status($"[{ProtocolName} downloading: {Path.GetFileName(path)}]", "36");
    }

    private void WriteDownloadPayload(byte[] payload)
    {
        if (_downloadStream == null)
            throw new InvalidOperationException("Download file is not open.");

        var count = payload.Length;
        if (_downloadExpectedSize >= 0)
        {
            var remaining = _downloadExpectedSize - _downloadWritten;
            if (remaining <= 0)
                count = 0;
            else if (remaining < count)
                count = (int)remaining;
        }

        if (count > 0)
        {
            _downloadStream.Write(payload, 0, count);
            _downloadWritten += count;
        }
    }

    private void FinishDownloadedFile()
    {
        if (_downloadStream == null)
            return;

        var name = _downloadFileName ?? "download";
        try
        {
            if (_protocol == XymodemProtocol.Xmodem && _downloadExpectedSize < 0)
                TrimTrailingCpmEof(_downloadStream);
        }
        catch
        {
            // XMODEM padding trim is best-effort; the transfer itself has already completed.
        }
        finally
        {
            _downloadStream.Dispose();
            _downloadStream = null;
            _downloadExpectedSize = -1;
            _downloadWritten = 0;
            _expectedReceiveBlockNumber = 1;
        }

        _status($"[{ProtocolName} downloaded: {name}]", "32");
    }

    private string ResolveReceivePath(string directory, string fileName)
    {
        var safeName = Path.GetFileName(fileName);
        var path = Path.Combine(directory, safeName);
        if (string.Equals(_duplicateAction, "Overwrite", StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
            return path;

        var name = Path.GetFileNameWithoutExtension(safeName);
        var extension = Path.GetExtension(safeName);
        for (var i = 1; i < 10000; i++)
        {
            var candidate = Path.Combine(directory, $"{name} ({i}){extension}");
            if (!File.Exists(candidate))
                return candidate;
        }

        throw new IOException($"Cannot create unique file name for {safeName}.");
    }

    private bool TryReadProtocolPacket(out ProtocolPacket packet)
    {
        packet = default;

        while (_input.Count > 0)
        {
            var first = _input[0];
            if (first is Soh or Stx or Eot or Can)
                break;

            var index = _input.FindIndex(b => b is Soh or Stx or Eot or Can);
            if (index < 0)
            {
                FlushTerminalBytes(_input.ToArray());
                _input.Clear();
                return false;
            }

            FlushTerminalBytes(_input.Take(index).ToArray());
            _input.RemoveRange(0, index);
        }

        if (_input.Count == 0)
            return false;

        var lead = _input[0];
        if (lead == Eot)
        {
            _input.RemoveAt(0);
            packet = new ProtocolPacket(PacketKind.Eot, 0, null);
            return true;
        }

        if (lead == Can)
        {
            _input.RemoveAt(0);
            packet = new ProtocolPacket(PacketKind.Cancel, 0, null);
            return true;
        }

        var blockSize = lead == Stx ? 1024 : 128;
        var checksumSize = 2;
        var totalSize = 3 + blockSize + checksumSize;
        if (_input.Count < totalSize)
            return false;

        var blockNumber = _input[1];
        var inverseBlockNumber = _input[2];
        var payload = _input.Skip(3).Take(blockSize).ToArray();
        var checksumOffset = 3 + blockSize;
        var receivedCrc = (ushort)((_input[checksumOffset] << 8) | _input[checksumOffset + 1]);
        _input.RemoveRange(0, totalSize);

        if (((blockNumber + inverseBlockNumber) & 0xff) != 0xff || Crc16(payload) != receivedCrc)
        {
            _sendToRemote(new[] { Nak });
            return false;
        }

        packet = new ProtocolPacket(PacketKind.Data, blockNumber, payload);
        return true;
    }

    private bool TryReadReceiverRequest(out byte request)
    {
        request = 0;
        while (_input.Count > 0)
        {
            var value = _input[0];
            _input.RemoveAt(0);
            if (value is CrcRequest or Nak)
            {
                request = value;
                return true;
            }

            if (value == Can)
            {
                request = value;
                return true;
            }
        }

        return false;
    }

    private bool TryReadAckLike(out byte response)
    {
        response = 0;
        while (_input.Count > 0)
        {
            var value = _input[0];
            _input.RemoveAt(0);
            if (value is Ack or Nak or Can)
            {
                response = value;
                return true;
            }
        }

        return false;
    }

    private void RemoteCancelledUpload()
    {
        _input.Clear();
        _sendToRemote(new[] { Can, Can, Can });
        _status($"[{ProtocolName} upload cancelled by remote]", "31");
        Complete();
    }

    private void ResendLastPacket()
    {
        if (_lastSentPacket != null)
            _sendToRemote(_lastSentPacket);
    }

    private byte[] BuildPacket(byte marker, int blockNumber, byte[] payload)
    {
        var packet = new byte[3 + payload.Length + (_useCrc ? 2 : 1)];
        packet[0] = marker;
        packet[1] = (byte)(blockNumber & 0xff);
        packet[2] = (byte)(0xff - packet[1]);
        Buffer.BlockCopy(payload, 0, packet, 3, payload.Length);
        if (_useCrc)
        {
            var crc = Crc16(payload);
            packet[^2] = (byte)(crc >> 8);
            packet[^1] = (byte)(crc & 0xff);
        }
        else
        {
            packet[^1] = Checksum(payload);
        }

        return packet;
    }

    private static byte[] BuildYmodemHeaderPayload(FileInfo file, int filesRemaining, long bytesRemaining)
    {
        var payload = new byte[128];
        var mtime = Convert.ToString(ToUnixTime(file.LastWriteTimeUtc), 8);
        var mode = Convert.ToString(0x81A4, 8); // regular file, 0644
        var header = Encoding.ASCII.GetBytes($"{file.Name}\0{file.Length} {mtime} {mode} 0 {filesRemaining} {bytesRemaining}\0");
        Buffer.BlockCopy(header, 0, payload, 0, Math.Min(header.Length, payload.Length));
        return payload;
    }

    private static long ToUnixTime(DateTime value)
    {
        return new DateTimeOffset(value).ToUnixTimeSeconds();
    }

    private static string ReadNullTerminatedAscii(byte[] bytes, int offset)
    {
        var end = offset;
        while (end < bytes.Length && bytes[end] != 0)
            end++;

        return end <= offset ? string.Empty : Encoding.ASCII.GetString(bytes, offset, end - offset);
    }

    private static long TryParseYmodemSize(string metadata)
    {
        var first = metadata.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return long.TryParse(first, out var value) ? value : -1;
    }

    private static ushort Crc16(byte[] data)
    {
        ushort crc = 0;
        foreach (var value in data)
        {
            crc ^= (ushort)(value << 8);
            for (var i = 0; i < 8; i++)
                crc = (crc & 0x8000) != 0 ? (ushort)((crc << 1) ^ 0x1021) : (ushort)(crc << 1);
        }

        return crc;
    }

    private static byte Checksum(byte[] data)
    {
        var sum = 0;
        foreach (var value in data)
            sum = (sum + value) & 0xff;
        return (byte)sum;
    }

    private static void TrimTrailingCpmEof(FileStream stream)
    {
        if (stream.Length == 0)
            return;

        stream.Flush();
        var trimTo = stream.Length;
        while (trimTo > 0)
        {
            stream.Position = trimTo - 1;
            if (stream.ReadByte() != CpmEof)
                break;
            trimTo--;
        }

        stream.SetLength(trimTo);
    }

    private void FlushTerminalBytes(byte[] bytes)
    {
        if (bytes.Length > 0)
            _toTerminal(bytes);
    }

    private void StartReceiverPromptLoop()
    {
        if (_receiverPromptCts.IsCancellationRequested)
        {
            _receiverPromptCts.Dispose();
            _receiverPromptCts = new CancellationTokenSource();
        }

        if (_receiverPromptCts.IsCancellationRequested)
            return;

        var token = _receiverPromptCts.Token;
        _ = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                _sendToRemote(new[] { CrcRequest });
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        });
    }

    private void Complete()
    {
        if (_isCompleted)
            return;

        _isCompleted = true;
        _uploadWatchdogCts?.Cancel();
        _uploadWatchdogCts?.Dispose();
        _uploadWatchdogCts = null;
        _receiverPromptCts.Cancel();
        _downloadStream?.Dispose();
        _downloadStream = null;
        _completed();
    }

    public void Dispose()
    {
        _uploadWatchdogCts?.Cancel();
        _uploadWatchdogCts?.Dispose();
        _uploadWatchdogCts = null;
        _receiverPromptCts.Cancel();
        _receiverPromptCts.Dispose();
        _downloadStream?.Dispose();
    }

    private enum UploadState
    {
        WaitReceiverRequest,
        WaitHeaderAck,
        WaitHeaderCrcRequest,
        WaitDataAck,
        WaitEotAck
    }

    private enum PacketKind
    {
        Data,
        Eot,
        Cancel
    }

    private readonly record struct ProtocolPacket(PacketKind Kind, int BlockNumber, byte[]? Payload);
}
