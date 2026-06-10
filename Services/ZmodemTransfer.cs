using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace ChiXueSsh.Services;

public enum ZmodemTransferDirection
{
    Download,
    Upload
}

public sealed class ZmodemTransfer : IDisposable
{
    private const byte Zpad = 0x2a;
    private const byte Zdle = 0x18;
    private const byte Xon = 0x11;
    private const byte Zbin = 0x41;
    private const byte Zhex = 0x42;
    private const byte Zbin32 = 0x43;
    private const byte Zcrce = 0x68;
    private const byte Zcrcg = 0x69;
    private const byte Zcrcq = 0x6a;
    private const byte Zcrcw = 0x6b;

    private const int Zrqinit = 0;
    private const int Zrinit = 1;
    private const int Zsinit = 2;
    private const int Zack = 3;
    private const int Zfile = 4;
    private const int Zskip = 5;
    private const int Zabort = 7;
    private const int Zfin = 8;
    private const int Zrpos = 9;
    private const int Zdata = 10;
    private const int Zeof = 11;

    private const int CanFdx = 0x01;
    private const int CanOvIo = 0x02;
    private const int EscCtl = 0x40;

    private readonly ZmodemTransferDirection _direction;
    private readonly Action<byte[]> _sendToRemote;
    private readonly Action<byte[]> _toTerminal;
    private readonly Action<string, string> _status;
    private readonly Action _completed;
    private readonly List<byte> _input = new();
    private readonly object _gate = new();
    private readonly string? _downloadDirectory;
    private readonly IReadOnlyList<string> _uploadFiles;

    private ReceiveState _receiveState = ReceiveState.WaitFileHeader;
    private SendState _sendState = SendState.WaitZrinit;
    private FileStream? _receiveStream;
    private string? _receiveFileName;
    private long _receiveOffset;
    private bool _receiveInData;
    private int? _pendingReceiveHeaderType;
    private int _uploadIndex;
    private FileInfo? _uploadFile;
    private long _uploadOffset;
    private bool _receiverEscapesControlCharacters;
    private bool _zsinitAcknowledged;
    private bool _isCompleted;

    public ZmodemTransfer(
        ZmodemTransferDirection direction,
        Action<byte[]> sendToRemote,
        Action<byte[]> toTerminal,
        Action<string, string> status,
        Action completed,
        string? downloadDirectory = null,
        IReadOnlyList<string>? uploadFiles = null)
    {
        _direction = direction;
        _sendToRemote = sendToRemote;
        _toTerminal = toTerminal;
        _status = status;
        _completed = completed;
        _downloadDirectory = downloadDirectory;
        _uploadFiles = uploadFiles ?? Array.Empty<string>();
    }

    public static bool TryFindStartupHeader(byte[] bytes, out int index, out ZmodemTransferDirection direction)
    {
        index = -1;
        direction = ZmodemTransferDirection.Download;

        for (var i = 0; i <= bytes.Length - 6; i++)
        {
            if (bytes[i] != Zpad || bytes[i + 1] != Zpad || bytes[i + 2] != Zdle || bytes[i + 3] != Zhex)
                continue;

            if (bytes[i + 4] == (byte)'0' && bytes[i + 5] == (byte)'0')
            {
                index = i;
                direction = ZmodemTransferDirection.Download;
                return true;
            }

            if (bytes[i + 4] == (byte)'0' && bytes[i + 5] == (byte)'1')
            {
                index = i;
                direction = ZmodemTransferDirection.Upload;
                return true;
            }
        }

        return false;
    }

    public void Start()
    {
        if (_direction == ZmodemTransferDirection.Download)
        {
            _status("[ZMODEM download started]", "36");
            SendHeaderHex(Zrinit, 0, 0, 0, CanFdx | CanOvIo | EscCtl);
        }
        else
        {
            _status("[ZMODEM upload started]", "36");
        }
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
                if (_direction == ZmodemTransferDirection.Download)
                    ProcessDownload();
                else
                    ProcessUpload();
            }
            catch (Exception ex)
            {
                _status($"[ZMODEM error: {ex.Message}]", "31");
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

            _sendToRemote(new byte[] { 24, 24, 24, 24, 24, 8, 8, 8, 8, 8 });
            Complete();
        }
    }

    private void ProcessDownload()
    {
        while (!_isCompleted)
        {
            if (_receiveState == ReceiveState.WaitFileHeader)
            {
                if (_pendingReceiveHeaderType == null)
                {
                    if (!TryReadHeader(out var header))
                        return;
                    _pendingReceiveHeaderType = header.Type;
                }

                switch (_pendingReceiveHeaderType.Value)
                {
                    case Zsinit:
                        if (!TryReadSubpacket(out _))
                            return;
                        SendHeaderHex(Zack);
                        _pendingReceiveHeaderType = null;
                        break;
                    case Zfile:
                        if (!TryReadSubpacket(out var fileInfoPacket))
                            return;
                        StartReceivingFile(fileInfoPacket.Payload);
                        SendHeaderHex(Zrpos, 0, 0, 0, 0);
                        _pendingReceiveHeaderType = null;
                        _receiveState = ReceiveState.WaitDataHeader;
                        break;
                    case Zfin:
                        _pendingReceiveHeaderType = null;
                        SendHeaderHex(Zfin);
                        _receiveState = ReceiveState.WaitOverAndOut;
                        return;
                    case Zabort:
                        _pendingReceiveHeaderType = null;
                        _status("[ZMODEM download aborted by remote]", "31");
                        Complete();
                        return;
                    default:
                        _pendingReceiveHeaderType = null;
                        break;
                }
            }
            else if (_receiveState == ReceiveState.WaitDataHeader)
            {
                if (!TryReadHeader(out var header))
                    return;

                if (header.Type == Zdata)
                {
                    _receiveInData = true;
                    _receiveState = ReceiveState.ReadData;
                }
                else if (header.Type == Zeof)
                {
                    FinishReceivingFile();
                    SendHeaderHex(Zrinit, 0, 0, 0, CanFdx | CanOvIo | EscCtl);
                    _receiveState = ReceiveState.WaitFileHeader;
                }
                else if (header.Type == Zfin)
                {
                    SendHeaderHex(Zfin);
                    _receiveState = ReceiveState.WaitOverAndOut;
                    return;
                }
            }
            else if (_receiveState == ReceiveState.ReadData)
            {
                if (!TryReadSubpacket(out var packet))
                    return;

                _receiveStream?.Write(packet.Payload, 0, packet.Payload.Length);
                _receiveOffset += packet.Payload.Length;

                if (packet.FrameEnd == Zcrcq || packet.FrameEnd == Zcrcw)
                    SendHeaderHex(Zack, PackUInt32(_receiveOffset));

                if (packet.FrameEnd == Zcrce || packet.FrameEnd == Zcrcw)
                {
                    _receiveInData = false;
                    _receiveState = ReceiveState.WaitDataHeader;
                }
            }
            else if (_receiveState == ReceiveState.WaitOverAndOut)
            {
                if (_input.Count < 2)
                    return;

                if (_input[0] == (byte)'O' && _input[1] == (byte)'O')
                {
                    _input.RemoveRange(0, 2);
                    _status("[ZMODEM download finished]", "32");

                    if (_input.Count > 0)
                    {
                        _toTerminal(_input.ToArray());
                        _input.Clear();
                    }

                    Complete();
                    return;
                }

                FlushGarbage(1);
            }
        }
    }

    private void ProcessUpload()
    {
        while (!_isCompleted)
        {
            if (_sendState == SendState.WaitZrinit)
            {
                if (!TryReadHeader(out var header))
                    return;

                if (header.Type == Zrinit)
                {
                    CaptureReceiverCapabilities(header);
                    BeginUploadOfferSequence();
                    return;
                }
                else if (header.Type == Zfin)
                {
                    _sendToRemote(Encoding.ASCII.GetBytes("OO"));
                    _status("[ZMODEM upload finished]", "32");
                    Complete();
                    return;
                }
            }
            else if (_sendState == SendState.WaitZsinitAck)
            {
                if (!TryReadHeader(out var header))
                    return;

                if (header.Type == Zack)
                {
                    _zsinitAcknowledged = true;
                    if (!OfferNextFile())
                        return;
                    _sendState = SendState.WaitOfferResponse;
                }
                else if (header.Type == Zrinit)
                {
                    CaptureReceiverCapabilities(header);
                    BeginUploadOfferSequence();
                    return;
                }
            }
            else if (_sendState == SendState.WaitOfferResponse)
            {
                if (!TryReadHeader(out var header))
                    return;

                if (header.Type == Zrpos)
                {
                    _uploadOffset = header.Offset;
                    SendCurrentFile();
                    _sendState = SendState.WaitFileAck;
                    return;
                }

                if (header.Type == Zrinit)
                {
                    CaptureReceiverCapabilities(header);
                    BeginUploadOfferSequence();
                    return;
                }

                if (header.Type == Zskip)
                {
                    _status("[ZMODEM receiver skipped file]", "33");
                    _uploadIndex++;
                    if (!OfferNextFile())
                        return;
                }
            }
            else if (_sendState == SendState.WaitFileAck)
            {
                if (!TryReadHeader(out var header))
                    return;

                if (header.Type == Zrinit)
                {
                    CaptureReceiverCapabilities(header);
                    _uploadIndex++;
                    BeginUploadOfferSequence();
                    return;
                }
                else if (header.Type == Zskip)
                {
                    _uploadIndex++;
                    if (!OfferNextFile())
                        return;
                    _sendState = SendState.WaitOfferResponse;
                }
            }
            else if (_sendState == SendState.WaitFin)
            {
                if (!TryReadHeader(out var header))
                    return;

                if (header.Type == Zfin)
                {
                    _sendToRemote(Encoding.ASCII.GetBytes("OO"));
                    _status("[ZMODEM upload finished]", "32");
                    Complete();
                    return;
                }
            }
        }
    }

    private bool OfferNextFile()
    {
        if (_uploadIndex >= _uploadFiles.Count)
        {
            SendHeaderHex(Zfin);
            _sendState = SendState.WaitFin;
            return false;
        }

        _uploadFile = new FileInfo(_uploadFiles[_uploadIndex]);
        if (!_uploadFile.Exists)
        {
            _status($"[ZMODEM upload skipped missing file: {_uploadFile.FullName}]", "33");
            _uploadIndex++;
            return OfferNextFile();
        }

        var bytesRemaining = _uploadFiles.Skip(_uploadIndex)
            .Select(path => new FileInfo(path))
            .Where(file => file.Exists)
            .Sum(file => file.Length);
        var payload = BuildFileInfo(_uploadFile, _uploadFiles.Count - _uploadIndex, bytesRemaining);

        _status($"[ZMODEM uploading: {_uploadFile.Name}]", "36");
        SendHeaderBinary16(Zfile, 0);
        SendSubpacket(payload, Zcrcw);
        return true;
    }

    private void CaptureReceiverCapabilities(ZHeader header)
    {
        _receiverEscapesControlCharacters = (header.Data[3] & EscCtl) != 0;
    }

    private void BeginUploadOfferSequence()
    {
        if (!_receiverEscapesControlCharacters && !_zsinitAcknowledged)
        {
            SendZsinit();
            _sendState = SendState.WaitZsinitAck;
            return;
        }

        if (!OfferNextFile())
            return;

        _sendState = SendState.WaitOfferResponse;
    }

    private void SendZsinit()
    {
        SendHeaderHex(Zsinit, 0, 0, 0, EscCtl);
        SendSubpacket(new byte[] { 0 }, Zcrcw);
    }

    private void SendCurrentFile()
    {
        if (_uploadFile == null)
            return;

        using var stream = _uploadFile.OpenRead();
        if (_uploadOffset > 0)
            stream.Seek(_uploadOffset, SeekOrigin.Begin);

        SendHeaderBinary16(Zdata, _uploadOffset);
        var buffer = new byte[8192];
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            var payload = buffer.Take(read).ToArray();
            _uploadOffset += read;
            SendSubpacket(payload, Zcrcg);
        }

        SendSubpacket(Array.Empty<byte>(), Zcrce);
        SendHeaderHex(Zeof, PackUInt32(_uploadOffset));
        _status($"[ZMODEM uploaded: {_uploadFile.Name}]", "32");
    }

    private void StartReceivingFile(byte[] payload)
    {
        if (string.IsNullOrWhiteSpace(_downloadDirectory))
            throw new InvalidOperationException("Download directory is not set.");

        var separator = Array.IndexOf(payload, (byte)0);
        var nameBytes = separator >= 0 ? payload[..separator] : payload;
        var remoteName = Encoding.UTF8.GetString(nameBytes);
        var fileName = SanitizeFileName(remoteName);
        var path = Path.Combine(_downloadDirectory, fileName);

        _receiveStream?.Dispose();
        _receiveStream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        _receiveFileName = fileName;
        _receiveOffset = 0;
        _status($"[ZMODEM downloading: {fileName}]", "36");
    }

    private void FinishReceivingFile()
    {
        var name = _receiveFileName ?? "file";
        _receiveStream?.Dispose();
        _receiveStream = null;
        _receiveFileName = null;
        _status($"[ZMODEM downloaded: {name}]", "32");
    }

    private bool TryReadHeader(out ZHeader header)
    {
        header = default;

        while (true)
        {
            var start = _input.IndexOf(Zpad);
            if (start < 0)
            {
                FlushGarbage(_input.Count);
                return false;
            }

            if (start > 0)
                FlushGarbage(start);

            if (_input.Count < 3)
                return false;

            if (_input[0] == Zpad && _input[1] == Zpad)
            {
                if (_input.Count < 4)
                    return false;
                if (_input[2] == Zdle && _input[3] == Zhex)
                    return TryReadHexHeader(out header);
            }

            if (_input[0] == Zpad && _input[1] == Zdle && (_input[2] == Zbin || _input[2] == Zbin32))
                return TryReadBinaryHeader(_input[2] == Zbin32 ? 4 : 2, out header);

            FlushGarbage(1);
        }
    }

    private bool TryReadHexHeader(out ZHeader header)
    {
        header = default;
        if (_input.Count < 18)
            return false;

        var encoded = _input.Skip(4).Take(14).ToArray();
        var raw = new byte[7];
        for (var i = 0; i < raw.Length; i++)
        {
            var hi = HexValue(encoded[i * 2]);
            var lo = HexValue(encoded[i * 2 + 1]);
            if (hi < 0 || lo < 0)
            {
                FlushGarbage(1);
                return TryReadHeader(out header);
            }
            raw[i] = (byte)((hi << 4) | lo);
        }

        var consume = 18;
        while (_input.Count > consume && IsHexHeaderTerminator(_input[consume]))
            consume++;

        _input.RemoveRange(0, consume);
        header = new ZHeader(raw[0], raw[1..5]);
        return true;
    }

    private bool TryReadBinaryHeader(int crcLength, out ZHeader header)
    {
        header = default;
        if (!TryDecodeExact(3, 5 + crcLength, out var raw, out var end))
            return false;

        _input.RemoveRange(0, end);
        header = new ZHeader(raw[0], raw[1..5]);
        return true;
    }

    private bool TryReadSubpacket(out ZSubpacket packet)
    {
        packet = default;
        var decoded = new List<byte>();

        for (var i = 0; i < _input.Count;)
        {
            var current = _input[i];
            if (current != Zdle)
            {
                decoded.Add(current);
                i++;
                continue;
            }

            if (i + 1 >= _input.Count)
                return false;

            var next = _input[i + 1];
            if (next is Zcrce or Zcrcg or Zcrcq or Zcrcw)
            {
                if (!TryDecodeExact(i + 2, 2, out _, out var end))
                    return false;

                _input.RemoveRange(0, end);
                packet = new ZSubpacket(decoded.ToArray(), next);
                return true;
            }

            decoded.Add((byte)(next ^ 0x40));
            i += 2;
        }

        return false;
    }

    private static bool IsHexHeaderTerminator(byte value)
    {
        return value is 13 or 10 or 0x8a or 0x8d or Xon or 0x91;
    }

    private static bool IsProtocolPaddingByte(byte value)
    {
        return value is Xon or 0x13 or 0x91 or 0x93 or 0x8a or 0x8d;
    }

    private bool TryDecodeExact(int start, int count, out byte[] decoded, out int end)
    {
        var result = new List<byte>(count);
        end = start;
        decoded = Array.Empty<byte>();

        while (result.Count < count)
        {
            if (end >= _input.Count)
                return false;

            var current = _input[end++];
            if (current == Zdle)
            {
                if (end >= _input.Count)
                    return false;
                current = (byte)(_input[end++] ^ 0x40);
            }
            result.Add(current);
        }

        decoded = result.ToArray();
        return true;
    }

    private void FlushGarbage(int count)
    {
        if (count <= 0)
            return;

        var garbage = _input.Take(count).ToArray();
        _input.RemoveRange(0, count);
        if (garbage.Length > 0 && _direction == ZmodemTransferDirection.Download && !_receiveInData)
        {
            var terminalBytes = garbage.Where(value => !IsProtocolPaddingByte(value)).ToArray();
            if (terminalBytes.Length > 0)
                _toTerminal(terminalBytes);
        }
    }

    private void SendHeaderHex(int type)
    {
        SendHeaderHex(type, 0, 0, 0, 0);
    }

    private void SendHeaderHex(int type, byte[] data)
    {
        SendHeaderHex(type, data[0], data[1], data[2], data[3]);
    }

    private void SendHeaderHex(int type, int b0, int b1, int b2, int b3)
    {
        var frame = new[] { (byte)type, (byte)b0, (byte)b1, (byte)b2, (byte)b3 };
        var crc = Crc16(frame);
        var bytes = new List<byte> { Zpad, Zpad, Zdle, Zhex };
        foreach (var item in frame.Concat(crc))
            bytes.AddRange(ToHex(item));
        bytes.AddRange(new byte[] { 13, 10, Xon });
        _sendToRemote(bytes.ToArray());
    }

    private void SendHeaderBinary16(int type, long offset)
    {
        var frame = new List<byte> { (byte)type };
        frame.AddRange(PackUInt32(offset));
        frame.AddRange(Crc16(frame));

        var bytes = new List<byte> { Zpad, Zdle, Zbin };
        bytes.AddRange(ZdleEncode(frame));
        _sendToRemote(bytes.ToArray());
    }

    private void SendSubpacket(byte[] payload, byte frameEnd)
    {
        var bytes = new List<byte>();
        bytes.AddRange(ZdleEncode(payload));
        bytes.Add(Zdle);
        bytes.Add(frameEnd);
        bytes.AddRange(ZdleEncode(Crc16(payload.Concat(new[] { frameEnd }).ToArray())));
        _sendToRemote(bytes.ToArray());
    }

    private static byte[] BuildFileInfo(FileInfo file, int filesRemaining, long bytesRemaining)
    {
        var mtime = Convert.ToString(new DateTimeOffset(file.LastWriteTimeUtc).ToUnixTimeSeconds(), 8);
        var text = $"{file.Name}\0{file.Length} {mtime} 0 0 {filesRemaining} {bytesRemaining}";
        return Encoding.UTF8.GetBytes(text);
    }

    private static IEnumerable<byte> ZdleEncode(IEnumerable<byte> source)
    {
        foreach (var value in source)
        {
            if (ShouldEscapeZdle(value))
            {
                yield return Zdle;
                yield return (byte)(value ^ 0x40);
            }
            else
            {
                yield return value;
            }
        }
    }

    private static bool ShouldEscapeZdle(byte value)
    {
        if ((value & 0x60) != 0)
            return false;

        return true;
    }

    private static byte[] Crc16(IReadOnlyList<byte> data)
    {
        if (data.Count == 0)
            return new byte[] { 0, 0 };

        var crc = (ushort)data[0];
        for (var i = 1; i < data.Count; i++)
            crc = UpdateCrc16(data[i], crc);
        crc = UpdateCrc16(0, UpdateCrc16(0, crc));
        return new[] { (byte)(crc >> 8), (byte)(crc & 0xff) };
    }

    private static ushort UpdateCrc16(byte value, ushort crc)
    {
        return (ushort)(Crc16Table[(crc >> 8) & 0xff] ^ ((crc & 0xff) << 8) ^ value);
    }

    private static readonly ushort[] Crc16Table = BuildCrc16Table();

    private static ushort[] BuildCrc16Table()
    {
        var table = new ushort[256];
        for (var dividend = 0; dividend < table.Length; dividend++)
        {
            var current = (dividend << 8) & 0xffff;
            for (var bit = 0; bit < 8; bit++)
            {
                current = (current & 0x8000) != 0
                    ? ((current << 1) ^ 0x1021)
                    : current << 1;
            }

            table[dividend] = (ushort)(current & 0xffff);
        }

        return table;
    }

    private static byte[] PackUInt32(long value)
    {
        var number = unchecked((uint)value);
        return new[]
        {
            (byte)(number & 0xff),
            (byte)((number >> 8) & 0xff),
            (byte)((number >> 16) & 0xff),
            (byte)((number >> 24) & 0xff)
        };
    }

    private static IEnumerable<byte> ToHex(byte value)
    {
        const string hex = "0123456789abcdef";
        yield return (byte)hex[value >> 4];
        yield return (byte)hex[value & 0x0f];
    }

    private static int HexValue(byte value)
    {
        if (value >= '0' && value <= '9') return value - '0';
        if (value >= 'a' && value <= 'f') return value - 'a' + 10;
        if (value >= 'A' && value <= 'F') return value - 'A' + 10;
        return -1;
    }

    private static string SanitizeFileName(string name)
    {
        var fileName = Path.GetFileName(string.IsNullOrWhiteSpace(name) ? "zmodem-download.bin" : name);
        foreach (var invalid in Path.GetInvalidFileNameChars())
            fileName = fileName.Replace(invalid, '_');
        return string.IsNullOrWhiteSpace(fileName) ? "zmodem-download.bin" : fileName;
    }

    private void Complete()
    {
        _isCompleted = true;
        _receiveStream?.Dispose();
        _receiveStream = null;
        _completed();
    }

    public void Dispose()
    {
        _receiveStream?.Dispose();
    }

    private enum ReceiveState
    {
        WaitFileHeader,
        WaitDataHeader,
        ReadData,
        WaitOverAndOut
    }

    private enum SendState
    {
        WaitZrinit,
        WaitZsinitAck,
        WaitOfferResponse,
        WaitFileAck,
        WaitFin
    }

    private readonly record struct ZHeader(int Type, byte[] Data)
    {
        public long Offset => (uint)(Data[0] | (Data[1] << 8) | (Data[2] << 16) | (Data[3] << 24));
    }

    private readonly record struct ZSubpacket(byte[] Payload, byte FrameEnd);
}
