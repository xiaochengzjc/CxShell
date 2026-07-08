using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
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
        private static readonly UTF8Encoding StrictUtf8Encoding = new(false, true);
        private static readonly Encoding Latin1Encoding = Encoding.GetEncoding("ISO-8859-1");

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

            var text = string.Empty;
            if (textLength > 0)
            {
                var buffer = ArrayPool<byte>.Shared.Rent(textLength);
                try
                {
                    transportStream.ReadAll(buffer.AsSpan(0, textLength), cancellationToken);
                    text = DecodeLegacyClipboardText(buffer.AsSpan(0, textLength));
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
                }
            }

            outputHandler?.HandleServerClipboardUpdate(text);
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

        private static string DecodeLegacyClipboardText(ReadOnlySpan<byte> textBytes)
        {
            while (textBytes.Length > 0 && textBytes[^1] == 0)
                textBytes = textBytes[..^1];

            if (textBytes.IsEmpty)
                return string.Empty;

            EnsureCodePagesProvider();

            var candidates = new List<DecodedTextCandidate>();
            AddStrictUtf8Candidate(candidates, textBytes);
            AddEncodingCandidate(candidates, textBytes, GetCurrentAnsiEncoding());
            AddEncodingCandidate(candidates, textBytes, GetEncodingOrNull("GB18030"));
            AddEncodingCandidate(candidates, textBytes, GetEncodingOrNull(936));
            AddEncodingCandidate(candidates, textBytes, Latin1Encoding);

            var best = candidates
                .OrderBy(static candidate => candidate.Score)
                .FirstOrDefault();

            return NormalizeLegacyClipboardText(best.Text ?? Latin1Encoding.GetString(textBytes));
        }

        private static void AddStrictUtf8Candidate(List<DecodedTextCandidate> candidates, ReadOnlySpan<byte> textBytes)
        {
            try
            {
                candidates.Add(new DecodedTextCandidate(StrictUtf8Encoding.GetString(textBytes), "UTF-8"));
            }
            catch (DecoderFallbackException)
            {
            }
        }

        private static void AddEncodingCandidate(
            List<DecodedTextCandidate> candidates,
            ReadOnlySpan<byte> textBytes,
            Encoding? encoding)
        {
            if (encoding == null || candidates.Any(candidate => candidate.CodePage == encoding.CodePage))
                return;

            candidates.Add(new DecodedTextCandidate(encoding.GetString(textBytes), encoding.WebName));
        }

        private static Encoding? GetCurrentAnsiEncoding()
        {
            try
            {
                var codePage = CultureInfo.CurrentCulture.TextInfo.ANSICodePage;
                return codePage > 0 ? GetEncodingOrNull(codePage) : null;
            }
            catch
            {
                return null;
            }
        }

        private static Encoding? GetEncodingOrNull(int codePage)
        {
            try
            {
                return Encoding.GetEncoding(
                    codePage,
                    EncoderFallback.ReplacementFallback,
                    DecoderFallback.ReplacementFallback);
            }
            catch
            {
                return null;
            }
        }

        private static Encoding? GetEncodingOrNull(string name)
        {
            try
            {
                return Encoding.GetEncoding(
                    name,
                    EncoderFallback.ReplacementFallback,
                    DecoderFallback.ReplacementFallback);
            }
            catch
            {
                return null;
            }
        }

        private static void EnsureCodePagesProvider()
        {
            try
            {
                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            }
            catch
            {
            }
        }

        private static string NormalizeLegacyClipboardText(string text)
        {
            return text
                .Replace("\r\n", "\n", StringComparison.Ordinal)
                .Replace('\r', '\n');
        }

        private readonly record struct DecodedTextCandidate(string Text, string EncodingName)
        {
            public int CodePage { get; } = ResolveCodePage(EncodingName);
            public int Score { get; } = ScoreText(Text);

            private static int ResolveCodePage(string encodingName)
            {
                try
                {
                    return Encoding.GetEncoding(encodingName).CodePage;
                }
                catch
                {
                    return encodingName.GetHashCode(StringComparison.OrdinalIgnoreCase);
                }
            }
        }

        private static int ScoreText(string text)
        {
            var score = 0;
            foreach (var rune in text.EnumerateRunes())
            {
                var value = rune.Value;
                if (value == 0xfffd)
                    score += 100;
                else if (IsUnexpectedControl(value))
                    score += 40;
                else if (IsCjk(value))
                    score -= 12;
                else if (IsSuspiciousMojibake(value))
                    score += 4;
            }

            return score;
        }

        private static bool IsUnexpectedControl(int value)
        {
            return value < 0x20 && value is not '\t' and not '\n' and not '\r';
        }

        private static bool IsCjk(int value)
        {
            return value is >= 0x3400 and <= 0x4dbf or
                   >= 0x4e00 and <= 0x9fff or
                   >= 0xf900 and <= 0xfaff or
                   >= 0x20000 and <= 0x2a6df or
                   >= 0x2a700 and <= 0x2b73f or
                   >= 0x2b740 and <= 0x2b81f or
                   >= 0x2b820 and <= 0x2ceaf;
        }

        private static bool IsSuspiciousMojibake(int value)
        {
            return value is 'Ã' or 'Â' or 'Ä' or 'Å' or 'Æ' or 'Ç' or 'È' or 'É' or 'Ê' or 'Ë' or
                   'Ì' or 'Í' or 'Î' or 'Ï' or 'Ð' or 'Ñ' or 'Ò' or 'Ó' or 'Ô' or 'Õ' or 'Ö' or
                   'Ø' or 'Ù' or 'Ú' or 'Û' or 'Ü' or 'Ý' or 'Þ' or 'ß' or 'à' or 'á' or 'â' or
                   'ã' or 'ä' or 'å' or 'æ' or 'ç' or 'è' or 'é' or 'ê' or 'ë' or 'ì' or 'í' or
                   'î' or 'ï' or 'ð' or 'ñ' or 'ò' or 'ó' or 'ô' or 'õ' or 'ö' or 'ø' or 'ù' or
                   'ú' or 'û' or 'ü' or 'ý' or 'þ' or 'ÿ' or '€' or 'œ' or 'ž' or 'Ÿ' or '¡' or
                   '¢' or '£' or '¤' or '¥' or '¦' or '§' or '¨' or '©' or 'ª' or '«' or '¬' or
                   '\u00ad' or '®' or '¯' or '°' or '±' or '²' or '³' or '´' or 'µ' or '¶' or '·' or
                   '¸' or '¹' or 'º' or '»' or '¼' or '½' or '¾' or '¿' or '–' or '—' or '‘' or
                   '’' or '“' or '”' or '…';
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
