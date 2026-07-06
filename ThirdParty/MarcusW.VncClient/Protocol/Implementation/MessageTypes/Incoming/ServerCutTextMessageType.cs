using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading;
using MarcusW.VncClient.Output;
using MarcusW.VncClient.Protocol.Implementation.MessageTypes.Outgoing;
using MarcusW.VncClient.Protocol.MessageTypes;
using Microsoft.Extensions.Logging;

namespace MarcusW.VncClient.Protocol.Implementation.MessageTypes.Incoming
{
    /// <summary>
    /// A message type for receiving updates about the cut buffer (clipboard) of the server.
    /// </summary>
    public class ServerCutTextMessageType : IIncomingMessageType
    {
        private const int MaxLegacyClipboardBytes = 256 * 1024;
        private const int MaxExtendedClipboardBytes = 20 * 1024 * 1024;
        private const uint FormatMask = 0x0000ffff;
        private const uint ActionMask = 0xff000000;

        private readonly RfbConnectionContext _context;
        private readonly ILogger<ServerCutTextMessageType> _logger;
        private readonly ProtocolState _state;

        /// <inheritdoc />
        public byte Id => (byte)WellKnownIncomingMessageType.ServerCutText;

        /// <inheritdoc />
        public string Name => "ServerCutText";

        /// <inheritdoc />
        public bool IsStandardMessageType => true;

        /// <summary>
        /// Initializes a new instance of the <see cref="ServerCutTextMessageType"/>.
        /// </summary>
        /// <param name="context">The connection context.</param>
        public ServerCutTextMessageType(RfbConnectionContext context)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
            _logger = context.Connection.LoggerFactory.CreateLogger<ServerCutTextMessageType>();
            _state = context.GetState<ProtocolState>();
        }

        /// <inheritdoc />
        public void ReadMessage(ITransport transport, CancellationToken cancellationToken = default)
        {
            if (transport == null)
                throw new ArgumentNullException(nameof(transport));

            cancellationToken.ThrowIfCancellationRequested();

            var transportStream = transport.Stream;

            // Read 7 header bytes (first 3 bytes are padding).
            Span<byte> header = stackalloc byte[7];
            transportStream.ReadAll(header, cancellationToken);
            var length = BinaryPrimitives.ReadInt32BigEndian(header[3..]);

            if (length >= 0)
            {
                ReadLegacyClipboardText(transportStream, length, cancellationToken);
                return;
            }

            if (length == int.MinValue)
                throw new InvalidDataException("Invalid extended clipboard length.");

            ReadExtendedClipboardMessage(transportStream, -length, cancellationToken);
        }

        private void ReadLegacyClipboardText(Stream transportStream, int textLength, CancellationToken cancellationToken)
        {
            if (textLength > MaxLegacyClipboardBytes)
            {
                _logger.LogWarning("Received cut text is too long ({textLength}). Ignoring...", textLength);
                transportStream.SkipAll(textLength, cancellationToken);
                return;
            }

            var outputHandler = _context.Connection.OutputHandler;
            if (outputHandler == null && !_logger.IsEnabled(LogLevel.Debug))
            {
                transportStream.SkipAll(textLength, cancellationToken);
                return;
            }

            var stringBuilder = new StringBuilder(textLength);
            var latin1Encoding = Encoding.GetEncoding("ISO-8859-1");

            if (textLength > 0)
            {
                var buffer = ArrayPool<byte>.Shared.Rent(Math.Min(1024, textLength));
                var bufferSpan = buffer.AsSpan();
                try
                {
                    var bytesToRead = textLength;
                    do
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        var read = transportStream.Read(bytesToRead < bufferSpan.Length ? bufferSpan[..bytesToRead] : bufferSpan);
                        if (read == 0)
                            throw new UnexpectedEndOfStreamException("Stream reached its end while trying to read the server cut text.");

                        stringBuilder.Append(latin1Encoding.GetString(bufferSpan[..read]));
                        bytesToRead -= read;
                    }
                    while (bytesToRead > 0);
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }
            }

            outputHandler?.HandleServerClipboardUpdate(stringBuilder.ToString());
        }

        private void ReadExtendedClipboardMessage(Stream transportStream, int messageLength, CancellationToken cancellationToken)
        {
            if (messageLength < 4)
            {
                transportStream.SkipAll(messageLength, cancellationToken);
                return;
            }

            if (messageLength > MaxExtendedClipboardBytes)
            {
                _logger.LogWarning("Received extended clipboard message is too long ({messageLength}). Ignoring...", messageLength);
                transportStream.SkipAll(messageLength, cancellationToken);
                return;
            }

            var payload = new byte[messageLength];
            transportStream.ReadAll(payload, cancellationToken);

            var flags = BinaryPrimitives.ReadUInt32BigEndian(payload.AsSpan(0, 4));
            var formats = (ExtendedClipboardFormat)(flags & FormatMask);
            var actions = (ExtendedClipboardAction)(flags & ActionMask);

            if ((actions & ExtendedClipboardAction.Caps) != 0)
            {
                HandleExtendedClipboardCaps(formats, actions, payload.AsSpan(4));
                return;
            }

            if ((actions & ExtendedClipboardAction.Notify) != 0)
            {
                _logger.LogWarning("VNC clipboard: server sent Extended Clipboard notify. formats={Formats}.", formats);
                _context.Connection.OutputHandler?.HandleExtendedClipboardNotify(formats);
                return;
            }

            if ((actions & ExtendedClipboardAction.Request) != 0)
            {
                _logger.LogWarning("VNC clipboard: server requested Extended Clipboard data. formats={Formats}.", formats);
                _context.Connection.OutputHandler?.HandleExtendedClipboardRequest(formats);
                RespondToExtendedClipboardRequest(formats, cancellationToken);
                return;
            }

            if ((actions & ExtendedClipboardAction.Provide) != 0)
            {
                _logger.LogWarning("VNC clipboard: server provided Extended Clipboard data. formats={Formats}, compressedBytes={CompressedBytes}.", formats, payload.Length - 4);
                HandleExtendedClipboardProvide(formats, payload.AsSpan(4));
            }
        }

        private void HandleExtendedClipboardCaps(ExtendedClipboardFormat formats, ExtendedClipboardAction actions, ReadOnlySpan<byte> sizesPayload)
        {
            var capabilities = new ExtendedClipboardCapabilities
            {
                SupportedFormats = formats,
                SupportedActions = actions
            };

            var offset = 0;
            foreach (var format in EnumerateFormats(formats))
            {
                if (offset + 4 > sizesPayload.Length)
                    break;

                var size = BinaryPrimitives.ReadUInt32BigEndian(sizesPayload.Slice(offset, 4));
                offset += 4;

                switch (format)
                {
                    case ExtendedClipboardFormat.Text:
                        capabilities.MaxTextSize = size;
                        break;
                    case ExtendedClipboardFormat.Rtf:
                        capabilities.MaxRtfSize = size;
                        break;
                    case ExtendedClipboardFormat.Html:
                        capabilities.MaxHtmlSize = size;
                        break;
                    case ExtendedClipboardFormat.Dib:
                        capabilities.MaxDibSize = size;
                        break;
                    case ExtendedClipboardFormat.Files:
                        capabilities.MaxFilesSize = size;
                        break;
                }
            }

            _state.ServerSupportsExtendedClipboard = true;
            _state.ServerClipboardCapabilities = capabilities;
            _logger.LogWarning(
                "VNC clipboard: server confirmed Extended Clipboard. formats={Formats}, actions={Actions}, maxTextSize={MaxTextSize}.",
                capabilities.SupportedFormats,
                capabilities.SupportedActions,
                capabilities.MaxTextSize);
        }

        private void HandleExtendedClipboardProvide(ExtendedClipboardFormat formats, ReadOnlySpan<byte> compressedPayload)
        {
            try
            {
                var data = DecodeExtendedClipboardData(formats, compressedPayload);
                _context.Connection.OutputHandler?.HandleExtendedClipboardData(data);

                if (!string.IsNullOrEmpty(data.Text))
                    _context.Connection.OutputHandler?.HandleServerClipboardUpdate(data.Text);
            }
            catch (InvalidDataException ex)
            {
                _logger.LogWarning(ex, "Failed to decode extended clipboard data.");
            }
        }

        private ExtendedClipboardData DecodeExtendedClipboardData(ExtendedClipboardFormat formats, ReadOnlySpan<byte> compressedPayload)
        {
            using var compressedStream = new MemoryStream(compressedPayload.ToArray(), writable: false);
            using var zlibStream = new ZLibStream(compressedStream, CompressionMode.Decompress);
            using var uncompressedStream = new MemoryStream();
            zlibStream.CopyTo(uncompressedStream);

            var uncompressed = uncompressedStream.ToArray();
            var data = new ExtendedClipboardData
            {
                AvailableFormats = formats
            };

            var offset = 0;
            foreach (var format in EnumerateFormats(formats))
            {
                if (offset + 4 > uncompressed.Length)
                    break;

                var size = checked((int)BinaryPrimitives.ReadUInt32BigEndian(uncompressed.AsSpan(offset, 4)));
                offset += 4;

                if (size < 0 || offset + size > uncompressed.Length)
                    throw new InvalidDataException("Invalid extended clipboard format payload size.");

                var formatBytes = uncompressed.AsSpan(offset, size);
                offset += size;

                switch (format)
                {
                    case ExtendedClipboardFormat.Text:
                        data.Text = DecodeExtendedClipboardText(formatBytes);
                        break;
                    case ExtendedClipboardFormat.Rtf:
                        data.Rtf = formatBytes.ToArray();
                        break;
                    case ExtendedClipboardFormat.Html:
                        data.Html = Encoding.UTF8.GetString(formatBytes);
                        break;
                    case ExtendedClipboardFormat.Dib:
                        data.Dib = formatBytes.ToArray();
                        break;
                }
            }

            return data;
        }

        private void RespondToExtendedClipboardRequest(ExtendedClipboardFormat requestedFormats, CancellationToken cancellationToken)
        {
            var lastClientClipboardText = _state.LastClientClipboardText;
            if ((requestedFormats & ExtendedClipboardFormat.Text) == 0 ||
                string.IsNullOrEmpty(lastClientClipboardText))
            {
                return;
            }

            var messageSender = _context.MessageSender;
            if (messageSender == null)
                return;

            messageSender.EnqueueMessage(new ClientCutTextMessage(lastClientClipboardText), cancellationToken);
        }

        private static string DecodeExtendedClipboardText(ReadOnlySpan<byte> textBytes)
        {
            if (textBytes.Length > 0 && textBytes[^1] == 0)
                textBytes = textBytes[..^1];

            return Encoding.UTF8.GetString(textBytes)
                .Replace("\r\n", "\n", StringComparison.Ordinal)
                .Replace('\r', '\n');
        }

        private static IEnumerable<ExtendedClipboardFormat> EnumerateFormats(ExtendedClipboardFormat formats)
        {
            if (HasFormat(formats, ExtendedClipboardFormat.Text))
                yield return ExtendedClipboardFormat.Text;
            if (HasFormat(formats, ExtendedClipboardFormat.Rtf))
                yield return ExtendedClipboardFormat.Rtf;
            if (HasFormat(formats, ExtendedClipboardFormat.Html))
                yield return ExtendedClipboardFormat.Html;
            if (HasFormat(formats, ExtendedClipboardFormat.Dib))
                yield return ExtendedClipboardFormat.Dib;
            if (HasFormat(formats, ExtendedClipboardFormat.Files))
                yield return ExtendedClipboardFormat.Files;
        }

        private static bool HasFormat(ExtendedClipboardFormat formats, ExtendedClipboardFormat format)
        {
            return (formats & format) != 0;
        }
    }
}
