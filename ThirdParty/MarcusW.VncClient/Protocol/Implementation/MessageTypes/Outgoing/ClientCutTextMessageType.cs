using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Globalization;
using System.Text;
using System.Threading;
using MarcusW.VncClient.Protocol.MessageTypes;
using Microsoft.Extensions.Logging;

namespace MarcusW.VncClient.Protocol.Implementation.MessageTypes.Outgoing
{
    /// <summary>
    /// A message type for sending <see cref="ClientCutTextMessage"/>s.
    /// </summary>
    public class ClientCutTextMessageType : IOutgoingMessageType
    {
        private readonly ProtocolState? _state;
        private readonly ILogger<ClientCutTextMessageType>? _logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="ClientCutTextMessageType"/>.
        /// </summary>
        public ClientCutTextMessageType()
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="ClientCutTextMessageType"/>.
        /// </summary>
        /// <param name="context">The connection context.</param>
        public ClientCutTextMessageType(RfbConnectionContext context)
        {
            if (context == null)
                throw new ArgumentNullException(nameof(context));

            _state = context.GetState<ProtocolState>();
            _logger = context.Connection.LoggerFactory.CreateLogger<ClientCutTextMessageType>();
        }

        /// <inheritdoc />
        public byte Id => (byte)WellKnownOutgoingMessageType.ClientCutText;

        /// <inheritdoc />
        public string Name => "ClientCutText";

        /// <inheritdoc />
        public bool IsStandardMessageType => true;

        /// <inheritdoc />
        public void WriteToTransport(IOutgoingMessage<IOutgoingMessageType> message, ITransport transport, CancellationToken cancellationToken = default)
        {
            if (message == null)
                throw new ArgumentNullException(nameof(message));
            if (transport == null)
                throw new ArgumentNullException(nameof(transport));
            if (!(message is ClientCutTextMessage clientCutTextMessage))
                throw new ArgumentException($"Message is no {nameof(ClientCutTextMessage)}.", nameof(message));

            cancellationToken.ThrowIfCancellationRequested();

            var text = clientCutTextMessage.Text ?? string.Empty;
            _state?.SetLastClientClipboardText(text);

            var containsNonLatin1Text = ContainsNonLatin1Text(text);
            if (ShouldUseExtendedClipboard(containsNonLatin1Text))
                WriteExtendedClipboardText(transport, text);
            else
                WriteLegacyClipboardText(transport, text, containsNonLatin1Text);
        }

        private bool ShouldUseExtendedClipboard(bool containsNonLatin1Text)
        {
            return _state?.ServerSupportsExtendedClipboard == true && containsNonLatin1Text;
        }

        private void WriteLegacyClipboardText(ITransport transport, string text, bool containsNonLatin1Text)
        {
            var textBytes = containsNonLatin1Text
                ? EncodeLegacyClipboardFallback(text)
                : Encoding.GetEncoding("ISO-8859-1").GetBytes(text);
            var textLength = (uint)textBytes.Length;

            Span<byte> header = stackalloc byte[8];
            header[0] = Id;
            header[1] = 0;
            header[2] = 0;
            header[3] = 0;
            BinaryPrimitives.WriteUInt32BigEndian(header[4..], textLength);

            transport.Stream.Write(header);
            if (textLength > 0)
                transport.Stream.Write(textBytes);
        }

        private void WriteExtendedClipboardText(ITransport transport, string text)
        {
            var normalizedText = NormalizeExtendedClipboardText(text);
            var textBytes = Encoding.UTF8.GetBytes(normalizedText + '\0');

            using var uncompressedPayload = new MemoryStream();
            Span<byte> sizeBuffer = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(sizeBuffer, (uint)textBytes.Length);
            uncompressedPayload.Write(sizeBuffer);
            uncompressedPayload.Write(textBytes);

            using var compressedPayload = new MemoryStream();
            using (var zlibStream = new ZLibStream(compressedPayload, CompressionLevel.Fastest, leaveOpen: true))
            {
                uncompressedPayload.Position = 0;
                uncompressedPayload.CopyTo(zlibStream);
            }

            var flags = (uint)ExtendedClipboardAction.Provide | (uint)ExtendedClipboardFormat.Text;
            var compressedBytes = compressedPayload.ToArray();
            var extendedLength = checked(4 + compressedBytes.Length);

            _logger?.LogWarning(
                "VNC clipboard: sending Extended Clipboard UTF-8 text. chars={CharCount}, utf8Bytes={Utf8Bytes}, compressedBytes={CompressedBytes}.",
                text.Length,
                textBytes.Length,
                compressedBytes.Length);

            Span<byte> header = stackalloc byte[8];
            header[0] = Id;
            header[1] = 0;
            header[2] = 0;
            header[3] = 0;
            BinaryPrimitives.WriteInt32BigEndian(header[4..], -extendedLength);

            Span<byte> flagsBuffer = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(flagsBuffer, flags);

            transport.Stream.Write(header);
            transport.Stream.Write(flagsBuffer);
            if (compressedBytes.Length > 0)
                transport.Stream.Write(compressedBytes);
        }

        private byte[] EncodeLegacyClipboardFallback(string text)
        {
            var encoding = GetLegacyClipboardFallbackEncoding();
            _logger?.LogWarning(
                "VNC clipboard: server has not confirmed Extended Clipboard; sending non-Latin text through legacy ClientCutText using code page {CodePage} ({EncodingName}). chars={CharCount}.",
                encoding.CodePage,
                encoding.EncodingName,
                text.Length);

            return encoding.GetBytes(text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n'));
        }

        private static Encoding GetLegacyClipboardFallbackEncoding()
        {
            try
            {
                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                var codePage = CultureInfo.CurrentCulture.TextInfo.ANSICodePage;
                if (codePage > 0)
                    return Encoding.GetEncoding(codePage, EncoderFallback.ReplacementFallback, DecoderFallback.ReplacementFallback);
            }
            catch
            {
            }

            return Encoding.UTF8;
        }

        private static bool ContainsNonLatin1Text(string text)
        {
            foreach (var rune in text.EnumerateRunes())
            {
                if (rune.Value > 0xff)
                    return true;
            }

            return false;
        }

        private static string NormalizeExtendedClipboardText(string text)
        {
            return text
                .Replace("\r\n", "\n", StringComparison.Ordinal)
                .Replace('\r', '\n')
                .Replace("\n", "\r\n", StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// A message for sending clipboard text to the server.
    /// </summary>
    public class ClientCutTextMessage : IOutgoingMessage<ClientCutTextMessageType>
    {
        /// <summary>
        /// Gets the clipboard text to send to the server.
        /// </summary>
        public string Text { get; }

        /// <summary>
        /// Initializes a new instance of the <see cref="ClientCutTextMessage"/>.
        /// </summary>
        /// <param name="text">The clipboard text to send.</param>
        public ClientCutTextMessage(string text)
        {
            Text = text ?? throw new ArgumentNullException(nameof(text));
        }

        /// <inheritdoc />
        public string? GetParametersOverview() => $"Text length: {Text.Length} characters";
    }
}
