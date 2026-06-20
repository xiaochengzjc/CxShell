using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using ChiXueSsh.Models;

namespace ChiXueSsh.Services;

public sealed class VncFramebufferEventArgs : EventArgs
{
    public VncFramebufferEventArgs(WriteableBitmap bitmap, int width, int height)
    {
        Bitmap = bitmap;
        Width = width;
        Height = height;
    }

    public WriteableBitmap Bitmap { get; }
    public int Width { get; }
    public int Height { get; }
}

public sealed class VncClientService : IDisposable
{
    private const int EncodingRaw = 0;
    private const int EncodingCopyRect = 1;
    private const int EncodingTight = 7;
    private const int EncodingZrle = 16;
    private const byte PointerEventMessage = 5;
    private const byte KeyEventMessage = 4;
    private const byte FramebufferUpdateRequestMessage = 3;
    private readonly object _writeLock = new();
    private TcpClient? _tcpClient;
    private Stream? _stream;
    private CancellationTokenSource? _readCts;
    private Task? _readTask;
    private WriteableBitmap? _bitmap;
    private int[]? _pixels;

    public event EventHandler<VncFramebufferEventArgs>? FramebufferUpdated;
    public event Action<string>? StatusChanged;
    public event Action<string>? ErrorOccurred;
    public event Action<string>? ClipboardTextReceived;
    public event Action? Disconnected;

    public int Width { get; private set; }
    public int Height { get; private set; }
    public string DesktopName { get; private set; } = string.Empty;
    public bool IsConnected => _tcpClient?.Connected == true;

    public async Task ConnectAsync(SessionInfo session, string? password, CancellationToken cancellationToken = default)
    {
        Disconnect();

        if (string.IsNullOrWhiteSpace(session.Host))
            throw new InvalidOperationException("VNC host is required.");

        var port = session.Port > 0 ? session.Port : 5900;
        StatusChanged?.Invoke($"Connecting to {session.Host}:{port}...");

        _tcpClient = new TcpClient();
        await _tcpClient.ConnectAsync(session.Host, port, cancellationToken);
        _stream = _tcpClient.GetStream();

        await HandshakeAsync(password ?? string.Empty, cancellationToken);
        await SendSetEncodingsAsync(cancellationToken);
        SendFramebufferUpdateRequest(false);

        _readCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _readTask = Task.Run(() => ReadLoopAsync(_readCts.Token), _readCts.Token);
        StatusChanged?.Invoke($"Connected: {DesktopName} {Width}x{Height}");
    }

    public void SendPointer(byte buttonMask, int x, int y)
    {
        if (_stream == null)
            return;

        x = Math.Clamp(x, 0, Math.Max(0, Width - 1));
        y = Math.Clamp(y, 0, Math.Max(0, Height - 1));
        Span<byte> buffer = stackalloc byte[6];
        buffer[0] = PointerEventMessage;
        buffer[1] = buttonMask;
        BinaryPrimitives.WriteUInt16BigEndian(buffer[2..], (ushort)x);
        BinaryPrimitives.WriteUInt16BigEndian(buffer[4..], (ushort)y);
        Write(buffer);
    }

    public void SendKey(uint keySym, bool down)
    {
        if (_stream == null || keySym == 0)
            return;

        Span<byte> buffer = stackalloc byte[8];
        buffer[0] = KeyEventMessage;
        buffer[1] = down ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteUInt32BigEndian(buffer[4..], keySym);
        Write(buffer);
    }

    public void SendClipboardText(string text)
    {
        if (_stream == null || string.IsNullOrEmpty(text))
            return;

        var bytes = Encoding.UTF8.GetBytes(text);
        var header = new byte[8];
        header[0] = 6;
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4, 4), (uint)bytes.Length);
        lock (_writeLock)
        {
            _stream.Write(header);
            _stream.Write(bytes);
            _stream.Flush();
        }
    }

    public void Disconnect()
    {
        _readCts?.Cancel();
        try
        {
            _readTask?.Wait(TimeSpan.FromSeconds(1));
        }
        catch
        {
            // Ignore shutdown failures.
        }

        _readCts?.Dispose();
        _readCts = null;
        _readTask = null;
        _stream?.Dispose();
        _stream = null;
        _tcpClient?.Dispose();
        _tcpClient = null;
    }

    private async Task HandshakeAsync(string password, CancellationToken cancellationToken)
    {
        var versionBytes = await ReadExactAsync(12, cancellationToken);
        var serverVersion = Encoding.ASCII.GetString(versionBytes);
        if (!serverVersion.StartsWith("RFB ", StringComparison.Ordinal))
            throw new InvalidOperationException("The server did not return an RFB version.");

        StatusChanged?.Invoke($"RFB server version: {serverVersion.Trim()}");

        var clientVersion = serverVersion.Contains("003.003", StringComparison.Ordinal)
            ? "RFB 003.003\n"
            : "RFB 003.008\n";
        await WriteAsync(Encoding.ASCII.GetBytes(clientVersion), cancellationToken);

        if (serverVersion.Contains("003.003", StringComparison.Ordinal))
        {
            var securityType = BinaryPrimitives.ReadUInt32BigEndian(await ReadExactAsync(4, cancellationToken));
            await HandleSecurityTypeAsync((byte)securityType, password, isVersion33: true, cancellationToken);
        }
        else
        {
            var count = (await ReadExactAsync(1, cancellationToken))[0];
            if (count == 0)
            {
                var reason = await ReadReasonAsync(cancellationToken);
                throw new InvalidOperationException(reason);
            }

            var types = await ReadExactAsync(count, cancellationToken);
            StatusChanged?.Invoke($"RFB security types: {string.Join(',', types.Select(t => t.ToString()))}");
            var selected = types.Contains((byte)2)
                ? (byte)2
                : types.Contains((byte)19) && !string.IsNullOrEmpty(password)
                    ? (byte)19
                    : types.Contains((byte)1)
                        ? (byte)1
                        : types[0];
            StatusChanged?.Invoke($"RFB selected security type: {selected}");
            await WriteAsync(new[] { selected }, cancellationToken);
            await HandleSecurityTypeAsync(selected, password, isVersion33: false, cancellationToken);
        }

        await WriteAsync(new byte[] { 1 }, cancellationToken); // shared flag
        var init = await ReadExactAsync(24, cancellationToken);
        Width = BinaryPrimitives.ReadUInt16BigEndian(init.AsSpan(0, 2));
        Height = BinaryPrimitives.ReadUInt16BigEndian(init.AsSpan(2, 2));
        var nameLength = BinaryPrimitives.ReadUInt32BigEndian(init.AsSpan(20, 4));
        DesktopName = nameLength > 0
            ? Encoding.UTF8.GetString(await ReadExactAsync(checked((int)nameLength), cancellationToken))
            : "VNC";
        StatusChanged?.Invoke($"RFB framebuffer: {Width}x{Height}, desktop={DesktopName}");

        _pixels = new int[Width * Height];
        _bitmap = new WriteableBitmap(
            new PixelSize(Width, Height),
            new Vector(96, 96),
            PixelFormat.Bgra8888,
            AlphaFormat.Opaque);

        await SendSetPixelFormatAsync(cancellationToken);
    }

    private async Task HandleSecurityTypeAsync(byte securityType, string password, bool isVersion33, CancellationToken cancellationToken)
    {
        switch (securityType)
        {
            case 1:
                if (!isVersion33)
                    await ReadSecurityResultAsync(cancellationToken);
                return;
            case 2:
                var challenge = await ReadExactAsync(16, cancellationToken);
                var response = CreateVncPasswordResponse(password, challenge);
                await WriteAsync(response, cancellationToken);
                await ReadSecurityResultAsync(cancellationToken);
                return;
            case 19:
                await HandleVeNCryptAsync(password, cancellationToken);
                return;
            default:
                throw new NotSupportedException($"VNC security type {securityType} is not supported yet.");
        }
    }

    private async Task HandleVeNCryptAsync(string password, CancellationToken cancellationToken)
    {
        StatusChanged?.Invoke("Negotiating VeNCrypt...");
        var version = await ReadExactAsync(2, cancellationToken);
        await WriteAsync(version, cancellationToken);
        var versionStatus = (await ReadExactAsync(1, cancellationToken))[0];
        if (versionStatus != 0)
            throw new InvalidOperationException("VeNCrypt version negotiation failed.");

        var subtypeCount = (await ReadExactAsync(1, cancellationToken))[0];
        var subtypes = new uint[subtypeCount];
        for (var i = 0; i < subtypeCount; i++)
            subtypes[i] = BinaryPrimitives.ReadUInt32BigEndian(await ReadExactAsync(4, cancellationToken));

        var selected = SelectVeNCryptSubtype(subtypes, !string.IsNullOrEmpty(password));
        if (selected == 0)
            throw new NotSupportedException("No supported VeNCrypt subtype was offered by the server.");
        StatusChanged?.Invoke($"VeNCrypt subtype selected: {selected}");

        var selectedBytes = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(selectedBytes, selected);
        await WriteAsync(selectedBytes, cancellationToken);

        if (selected != 256)
        {
            StatusChanged?.Invoke("Starting TLS...");
            await StartTlsAsync(cancellationToken);
        }

        if (selected is 258 or 261)
        {
            var challenge = await ReadExactAsync(16, cancellationToken);
            var response = CreateVncPasswordResponse(password, challenge);
            await WriteAsync(response, cancellationToken);
            await ReadSecurityResultAsync(cancellationToken);
        }
        else if (selected is 257 or 260)
        {
            await ReadSecurityResultAsync(cancellationToken);
        }
        else
        {
            throw new NotSupportedException($"VeNCrypt subtype {selected} is not supported yet.");
        }
    }

    private static uint SelectVeNCryptSubtype(uint[] subtypes, bool hasPassword)
    {
        if (hasPassword)
        {
            if (subtypes.Contains(258u)) return 258; // TLSVnc
            if (subtypes.Contains(261u)) return 261; // X509Vnc
        }

        if (subtypes.Contains(257u)) return 257; // TLSNone
        if (subtypes.Contains(260u)) return 260; // X509None
        return 0;
    }

    private async Task StartTlsAsync(CancellationToken cancellationToken)
    {
        if (_stream == null)
            throw new IOException("VNC stream is not connected.");

        var sslStream = new SslStream(
            _stream,
            leaveInnerStreamOpen: false,
            (sender, certificate, chain, errors) => true);
        await sslStream.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        {
            TargetHost = "vnc",
            EnabledSslProtocols = System.Security.Authentication.SslProtocols.Tls12 | System.Security.Authentication.SslProtocols.Tls13,
            CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
            RemoteCertificateValidationCallback = (_, _, _, _) => true
        }, cancellationToken);
        _stream = sslStream;
    }

    private async Task ReadSecurityResultAsync(CancellationToken cancellationToken)
    {
        var status = BinaryPrimitives.ReadUInt32BigEndian(await ReadExactAsync(4, cancellationToken));
        if (status == 0)
            return;

        var reason = await ReadReasonAsync(cancellationToken);
        throw new InvalidOperationException(string.IsNullOrWhiteSpace(reason)
            ? $"VNC authentication failed: {status}"
            : reason);
    }

    private async Task<string> ReadReasonAsync(CancellationToken cancellationToken)
    {
        try
        {
            var length = BinaryPrimitives.ReadUInt32BigEndian(await ReadExactAsync(4, cancellationToken));
            if (length == 0 || length > 4096)
                return string.Empty;

            return Encoding.UTF8.GetString(await ReadExactAsync((int)length, cancellationToken));
        }
        catch
        {
            return string.Empty;
        }
    }

    private async Task SendSetEncodingsAsync(CancellationToken cancellationToken)
    {
        Span<byte> buffer = stackalloc byte[12];
        buffer[0] = 2;
        BinaryPrimitives.WriteUInt16BigEndian(buffer[2..], 2);
        BinaryPrimitives.WriteInt32BigEndian(buffer[4..], EncodingCopyRect);
        BinaryPrimitives.WriteInt32BigEndian(buffer[8..], EncodingRaw);
        await WriteAsync(buffer.ToArray(), cancellationToken);
    }

    private async Task SendSetPixelFormatAsync(CancellationToken cancellationToken)
    {
        Span<byte> buffer = stackalloc byte[20];
        buffer[0] = 0;
        buffer[4] = 32; // bits-per-pixel
        buffer[5] = 24; // depth
        buffer[6] = 0;  // little endian
        buffer[7] = 1;  // true color
        BinaryPrimitives.WriteUInt16BigEndian(buffer[8..], 255);
        BinaryPrimitives.WriteUInt16BigEndian(buffer[10..], 255);
        BinaryPrimitives.WriteUInt16BigEndian(buffer[12..], 255);
        buffer[14] = 16; // red shift
        buffer[15] = 8;  // green shift
        buffer[16] = 0;  // blue shift
        await WriteAsync(buffer.ToArray(), cancellationToken);
    }

    private async Task ReadLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var type = (await ReadExactAsync(1, cancellationToken))[0];
                switch (type)
                {
                    case 0:
                        await HandleFramebufferUpdateAsync(cancellationToken);
                        SendFramebufferUpdateRequest(true);
                        break;
                    case 2:
                        StatusChanged?.Invoke("VNC server bell");
                        break;
                    case 3:
                        await HandleServerCutTextAsync(cancellationToken);
                        break;
                    default:
                        throw new NotSupportedException($"Unsupported VNC server message type: {type}");
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke(ex.Message);
        }
        finally
        {
            Disconnected?.Invoke();
        }
    }

    private async Task HandleFramebufferUpdateAsync(CancellationToken cancellationToken)
    {
        await ReadExactAsync(1, cancellationToken);
        var countBytes = await ReadExactAsync(2, cancellationToken);
        var rectangles = BinaryPrimitives.ReadUInt16BigEndian(countBytes);
        var changed = false;

        for (var i = 0; i < rectangles; i++)
        {
            var header = await ReadExactAsync(12, cancellationToken);
            var x = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(0, 2));
            var y = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(2, 2));
            var width = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(4, 2));
            var height = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(6, 2));
            var encoding = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(8, 4));

            if (encoding == EncodingRaw)
            {
                await ReadRawRectangleAsync(x, y, width, height, cancellationToken);
                changed = true;
            }
            else if (encoding == EncodingCopyRect)
            {
                await ReadCopyRectAsync(x, y, width, height, cancellationToken);
                changed = true;
            }
            else if (encoding == EncodingZrle)
            {
                await ReadZrleRectangleAsync(x, y, width, height, cancellationToken);
                changed = true;
            }
            else if (encoding == EncodingTight)
            {
                await ReadTightRectangleAsync(x, y, width, height, cancellationToken);
                changed = true;
            }
            else
            {
                throw new NotSupportedException($"Unsupported VNC rectangle encoding: {encoding}");
            }
        }

        if (changed)
            PublishFramebuffer();
    }

    private async Task ReadRawRectangleAsync(int x, int y, int width, int height, CancellationToken cancellationToken)
    {
        if (_pixels == null)
            return;

        var rowBytes = checked(width * 4);
        var row = new byte[rowBytes];
        for (var rowIndex = 0; rowIndex < height; rowIndex++)
        {
            await ReadExactIntoAsync(row, cancellationToken);
            var target = (y + rowIndex) * Width + x;
            for (var col = 0; col < width; col++)
            {
                var source = col * 4;
                var b = row[source];
                var g = row[source + 1];
                var r = row[source + 2];
                _pixels[target + col] = unchecked((int)0xff000000) | (r << 16) | (g << 8) | b;
            }
        }
    }

    private async Task ReadCopyRectAsync(int x, int y, int width, int height, CancellationToken cancellationToken)
    {
        if (_pixels == null)
            return;

        var source = await ReadExactAsync(4, cancellationToken);
        var sourceX = BinaryPrimitives.ReadUInt16BigEndian(source.AsSpan(0, 2));
        var sourceY = BinaryPrimitives.ReadUInt16BigEndian(source.AsSpan(2, 2));
        var copy = new int[width * height];
        for (var row = 0; row < height; row++)
            Array.Copy(_pixels, (sourceY + row) * Width + sourceX, copy, row * width, width);

        for (var row = 0; row < height; row++)
            Array.Copy(copy, row * width, _pixels, (y + row) * Width + x, width);
    }

    private async Task HandleServerCutTextAsync(CancellationToken cancellationToken)
    {
        await ReadExactAsync(3, cancellationToken);
        var length = BinaryPrimitives.ReadUInt32BigEndian(await ReadExactAsync(4, cancellationToken));
        if (length > 0)
        {
            var text = Encoding.UTF8.GetString(await ReadExactAsync(checked((int)length), cancellationToken));
            ClipboardTextReceived?.Invoke(text);
        }
    }

    private async Task ReadZrleRectangleAsync(int x, int y, int width, int height, CancellationToken cancellationToken)
    {
        var length = BinaryPrimitives.ReadUInt32BigEndian(await ReadExactAsync(4, cancellationToken));
        var compressed = await ReadExactAsync(checked((int)length), cancellationToken);
        using var input = new MemoryStream(compressed);
        using var zlib = new ZLibStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        await zlib.CopyToAsync(output, cancellationToken);
        DecodeZrlePayload(output.ToArray(), x, y, width, height);
    }

    private void DecodeZrlePayload(byte[] payload, int x, int y, int width, int height)
    {
        if (_pixels == null)
            return;

        var offset = 0;
        for (var tileY = y; tileY < y + height; tileY += 64)
        {
            var tileHeight = Math.Min(64, y + height - tileY);
            for (var tileX = x; tileX < x + width; tileX += 64)
            {
                var tileWidth = Math.Min(64, x + width - tileX);
                var subencoding = payload[offset++];
                DecodeZrleTile(payload, ref offset, tileX, tileY, tileWidth, tileHeight, subencoding);
            }
        }
    }

    private void DecodeZrleTile(byte[] payload, ref int offset, int x, int y, int width, int height, int subencoding)
    {
        if (_pixels == null)
            return;

        if (subencoding == 0)
        {
            for (var row = 0; row < height; row++)
            {
                var target = (y + row) * Width + x;
                for (var col = 0; col < width; col++)
                    _pixels[target + col] = ReadCompactPixel(payload, ref offset);
            }
            return;
        }

        if (subencoding == 1)
        {
            FillRectangle(x, y, width, height, ReadCompactPixel(payload, ref offset));
            return;
        }

        if (subencoding is >= 2 and <= 16)
        {
            var palette = ReadPalette(payload, ref offset, subencoding);
            var bits = subencoding <= 2 ? 1 : subencoding <= 4 ? 2 : 4;
            DecodePackedPalette(payload, ref offset, x, y, width, height, palette, bits);
            return;
        }

        if (subencoding == 128)
        {
            DecodePlainRle(payload, ref offset, x, y, width, height);
            return;
        }

        if (subencoding is >= 130 and <= 255)
        {
            var palette = ReadPalette(payload, ref offset, subencoding - 128);
            DecodePaletteRle(payload, ref offset, x, y, width, height, palette);
            return;
        }

        throw new NotSupportedException($"Unsupported ZRLE tile subencoding: {subencoding}");
    }

    private async Task ReadTightRectangleAsync(int x, int y, int width, int height, CancellationToken cancellationToken)
    {
        if (_pixels == null)
            return;

        var control = (await ReadExactAsync(1, cancellationToken))[0];
        var subtype = control >> 4;

        if (subtype == 8)
        {
            FillRectangle(x, y, width, height, ReadCompactPixel(await ReadExactAsync(3, cancellationToken), 0));
            return;
        }

        if (subtype == 9)
        {
            var length = await ReadCompactLengthAsync(cancellationToken);
            var jpeg = await ReadExactAsync(length, cancellationToken);
            DecodeJpegRectangle(jpeg, x, y, width, height);
            return;
        }

        if (subtype > 7)
            throw new NotSupportedException($"Unsupported Tight subencoding: {subtype}");

        var streamId = (control >> 4) & 0x03;
        _ = streamId; // Persistent Tight streams are not required when the server sends complete zlib blocks.

        var filterId = 0;
        if ((control & 0x40) != 0)
            filterId = (await ReadExactAsync(1, cancellationToken))[0];

        var expected = width * height * 3;
        byte[] decoded;
        if (expected < 12)
        {
            decoded = await ReadExactAsync(expected, cancellationToken);
        }
        else
        {
            var length = await ReadCompactLengthAsync(cancellationToken);
            var compressed = await ReadExactAsync(length, cancellationToken);
            decoded = DecompressZlib(compressed);
        }

        if (filterId == 0)
        {
            DecodeTightCopy(decoded, x, y, width, height);
        }
        else if (filterId == 1)
        {
            DecodeTightPalette(decoded, x, y, width, height);
        }
        else
        {
            throw new NotSupportedException($"Unsupported Tight filter: {filterId}");
        }
    }

    private static byte[] DecompressZlib(byte[] compressed)
    {
        using var input = new MemoryStream(compressed);
        using var zlib = new ZLibStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        zlib.CopyTo(output);
        return output.ToArray();
    }

    private void DecodeTightCopy(byte[] data, int x, int y, int width, int height)
    {
        if (_pixels == null)
            return;

        var offset = 0;
        for (var row = 0; row < height; row++)
        {
            var target = (y + row) * Width + x;
            for (var col = 0; col < width; col++)
                _pixels[target + col] = ReadCompactPixel(data, ref offset);
        }
    }

    private void DecodeTightPalette(byte[] data, int x, int y, int width, int height)
    {
        var offset = 0;
        var paletteSize = data[offset++] + 1;
        var palette = ReadPalette(data, ref offset, paletteSize);
        if (paletteSize == 2)
            DecodePackedPalette(data, ref offset, x, y, width, height, palette, 1);
        else
            DecodeIndexedPalette(data, ref offset, x, y, width, height, palette);
    }

    private void DecodeJpegRectangle(byte[] jpeg, int x, int y, int width, int height)
    {
        if (_pixels == null)
            return;

        using var sourceStream = new MemoryStream(jpeg);
        using var bitmap = WriteableBitmap.DecodeToWidth(sourceStream, width);
        using var framebuffer = bitmap.Lock();
        var rowPixels = Math.Min(width, framebuffer.RowBytes / 4);
        var row = new int[rowPixels];
        for (var rowIndex = 0; rowIndex < Math.Min(height, framebuffer.Size.Height); rowIndex++)
        {
            Marshal.Copy(framebuffer.Address + rowIndex * framebuffer.RowBytes, row, 0, rowPixels);
            Array.Copy(row, 0, _pixels, (y + rowIndex) * Width + x, Math.Min(rowPixels, width));
        }
    }

    private int[] ReadPalette(byte[] payload, ref int offset, int size)
    {
        var palette = new int[size];
        for (var i = 0; i < palette.Length; i++)
            palette[i] = ReadCompactPixel(payload, ref offset);
        return palette;
    }

    private void DecodePackedPalette(byte[] payload, ref int offset, int x, int y, int width, int height, int[] palette, int bitsPerPixel)
    {
        if (_pixels == null)
            return;

        var mask = (1 << bitsPerPixel) - 1;
        var pixelsPerByte = 8 / bitsPerPixel;
        for (var row = 0; row < height; row++)
        {
            var target = (y + row) * Width + x;
            var col = 0;
            while (col < width)
            {
                var value = payload[offset++];
                for (var shift = 8 - bitsPerPixel; shift >= 0 && col < width; shift -= bitsPerPixel)
                {
                    var index = (value >> shift) & mask;
                    _pixels[target + col] = palette[Math.Min(index, palette.Length - 1)];
                    col++;
                }
            }

            var consumed = (width + pixelsPerByte - 1) / pixelsPerByte;
            var expectedOffset = offset; // rows are byte-aligned; loop already consumes that exact count.
            _ = consumed;
            _ = expectedOffset;
        }
    }

    private void DecodeIndexedPalette(byte[] payload, ref int offset, int x, int y, int width, int height, int[] palette)
    {
        if (_pixels == null)
            return;

        for (var row = 0; row < height; row++)
        {
            var target = (y + row) * Width + x;
            for (var col = 0; col < width; col++)
            {
                var index = payload[offset++];
                _pixels[target + col] = palette[Math.Min(index, palette.Length - 1)];
            }
        }
    }

    private void DecodePlainRle(byte[] payload, ref int offset, int x, int y, int width, int height)
    {
        if (_pixels == null)
            return;

        var total = width * height;
        var written = 0;
        while (written < total)
        {
            var pixel = ReadCompactPixel(payload, ref offset);
            var run = ReadRunLength(payload, ref offset);
            for (var i = 0; i < run && written < total; i++, written++)
            {
                var row = written / width;
                var col = written % width;
                _pixels[(y + row) * Width + x + col] = pixel;
            }
        }
    }

    private void DecodePaletteRle(byte[] payload, ref int offset, int x, int y, int width, int height, int[] palette)
    {
        if (_pixels == null)
            return;

        var total = width * height;
        var written = 0;
        while (written < total)
        {
            var index = payload[offset++];
            var run = (index & 0x80) != 0 ? ReadRunLength(payload, ref offset) : 1;
            var pixel = palette[Math.Min(index & 0x7f, palette.Length - 1)];
            for (var i = 0; i < run && written < total; i++, written++)
            {
                var row = written / width;
                var col = written % width;
                _pixels[(y + row) * Width + x + col] = pixel;
            }
        }
    }

    private static int ReadRunLength(byte[] payload, ref int offset)
    {
        var length = 1;
        byte value;
        do
        {
            value = payload[offset++];
            length += value;
        } while (value == 255);

        return length;
    }

    private void FillRectangle(int x, int y, int width, int height, int pixel)
    {
        if (_pixels == null)
            return;

        for (var row = 0; row < height; row++)
        {
            var target = (y + row) * Width + x;
            Array.Fill(_pixels, pixel, target, width);
        }
    }

    private static int ReadCompactPixel(byte[] data, int offset)
    {
        var mutableOffset = offset;
        return ReadCompactPixel(data, ref mutableOffset);
    }

    private static int ReadCompactPixel(byte[] data, ref int offset)
    {
        var b = data[offset++];
        var g = data[offset++];
        var r = data[offset++];
        return unchecked((int)0xff000000) | (r << 16) | (g << 8) | b;
    }

    private async Task<int> ReadCompactLengthAsync(CancellationToken cancellationToken)
    {
        var b1 = (await ReadExactAsync(1, cancellationToken))[0];
        var length = b1 & 0x7f;
        if ((b1 & 0x80) == 0)
            return length;

        var b2 = (await ReadExactAsync(1, cancellationToken))[0];
        length |= (b2 & 0x7f) << 7;
        if ((b2 & 0x80) == 0)
            return length;

        var b3 = (await ReadExactAsync(1, cancellationToken))[0];
        length |= b3 << 14;
        return length;
    }

    private void SendFramebufferUpdateRequest(bool incremental)
    {
        if (_stream == null || Width <= 0 || Height <= 0)
            return;

        Span<byte> buffer = stackalloc byte[10];
        buffer[0] = FramebufferUpdateRequestMessage;
        buffer[1] = incremental ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteUInt16BigEndian(buffer[6..], (ushort)Width);
        BinaryPrimitives.WriteUInt16BigEndian(buffer[8..], (ushort)Height);
        Write(buffer);
    }

    private void PublishFramebuffer()
    {
        if (_pixels == null)
            return;

        var bitmap = new WriteableBitmap(
            new PixelSize(Width, Height),
            new Vector(96, 96),
            PixelFormat.Bgra8888,
            AlphaFormat.Opaque);

        using (var framebuffer = bitmap.Lock())
        {
            Marshal.Copy(_pixels, 0, framebuffer.Address, Math.Min(_pixels.Length, framebuffer.RowBytes * framebuffer.Size.Height / 4));
        }

        _bitmap = bitmap;
        FramebufferUpdated?.Invoke(this, new VncFramebufferEventArgs(bitmap, Width, Height));
    }

    private async Task<byte[]> ReadExactAsync(int length, CancellationToken cancellationToken)
    {
        var buffer = new byte[length];
        await ReadExactIntoAsync(buffer, cancellationToken);
        return buffer;
    }

    private async Task ReadExactIntoAsync(byte[] buffer, CancellationToken cancellationToken)
    {
        if (_stream == null)
            throw new IOException("VNC stream is not connected.");

        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await _stream.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset), cancellationToken);
            if (read <= 0)
                throw new IOException("VNC connection closed.");

            offset += read;
        }
    }

    private Task WriteAsync(byte[] bytes, CancellationToken cancellationToken)
    {
        if (_stream == null)
            throw new IOException("VNC stream is not connected.");

        lock (_writeLock)
            return _stream.WriteAsync(bytes, cancellationToken).AsTask();
    }

    private void Write(ReadOnlySpan<byte> bytes)
    {
        if (_stream == null)
            return;

        lock (_writeLock)
            _stream.Write(bytes);
    }

    private static byte[] CreateVncPasswordResponse(string password, byte[] challenge)
    {
        var key = new byte[8];
        var passwordBytes = Encoding.ASCII.GetBytes(password);
        for (var i = 0; i < key.Length && i < passwordBytes.Length; i++)
            key[i] = ReverseBits(passwordBytes[i]);

        using var des = DES.Create();
        des.Mode = CipherMode.ECB;
        des.Padding = PaddingMode.None;
        des.Key = key;

        using var encryptor = des.CreateEncryptor();
        return encryptor.TransformFinalBlock(challenge, 0, challenge.Length);
    }

    private static byte ReverseBits(byte value)
    {
        var result = 0;
        for (var i = 0; i < 8; i++)
            result = (result << 1) | ((value >> i) & 1);

        return (byte)result;
    }

    public void Dispose()
    {
        Disconnect();
    }
}
