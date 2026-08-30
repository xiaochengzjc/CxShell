using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using Avalonia.Media.Imaging;
using CxShell.Services.Agent;

namespace CxShell.ViewModels;

/// <summary>
/// A bounded local attachment prepared for one Agent message. The model sees
/// the extracted content; it never receives the user's local path.
/// </summary>
public sealed class AgentAttachmentViewModel
{
    public const int MaximumBytes = 4 * 1024 * 1024;
    public const int MaximumTextCharacters = 120 * 1024;

    private static readonly IReadOnlyDictionary<string, string> ImageMediaTypes =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [".png"] = "image/png",
            [".jpg"] = "image/jpeg",
            [".jpeg"] = "image/jpeg",
            [".gif"] = "image/gif",
            [".webp"] = "image/webp",
            [".bmp"] = "image/bmp"
        };

    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".markdown", ".json", ".xml", ".yaml", ".yml", ".csv",
        ".log", ".conf", ".config", ".ini", ".properties", ".env", ".sh", ".bash",
        ".ps1", ".bat", ".cmd", ".py", ".js", ".ts", ".java", ".cs", ".cpp", ".h",
        ".sql", ".html", ".css", ".toml"
    };

    private AgentAttachmentViewModel(
        string fileName,
        string sizeText,
        bool isImage,
        Bitmap? preview,
        AgentContentPart contentPart)
    {
        FileName = fileName;
        SizeText = sizeText;
        IsImage = isImage;
        Preview = preview;
        ContentPart = contentPart;
    }

    public string FileName { get; }
    public string SizeText { get; }
    public bool IsImage { get; }
    public Bitmap? Preview { get; }
    internal AgentContentPart ContentPart { get; }
    public string DisplayText => $"{FileName}  ({SizeText})";

    public static async Task<AgentAttachmentViewModel> FromFileAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Attachment path is required.", nameof(path));
        if (!File.Exists(path))
            throw new FileNotFoundException("Attachment file was not found.", path);

        var fileName = Path.GetFileName(path);
        var extension = Path.GetExtension(path);
        if (ImageMediaTypes.TryGetValue(extension, out var mediaType))
        {
            var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            return FromImageBytes(fileName, mediaType, bytes);
        }

        if (string.Equals(extension, ".docx", StringComparison.OrdinalIgnoreCase))
        {
            var text = await ExtractDocxTextAsync(path, cancellationToken).ConfigureAwait(false);
            return FromDocument(fileName, text);
        }

        if (TextExtensions.Contains(extension) || string.IsNullOrEmpty(extension))
        {
            var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            if (bytes.Length > MaximumBytes)
                throw new InvalidDataException("The document is too large.");

            return FromDocument(fileName, DecodeText(bytes));
        }

        throw new NotSupportedException(
            "Only images, text documents, source files, and .docx files are supported.");
    }

    public static AgentAttachmentViewModel FromImageBytes(
        string fileName,
        string mediaType,
        byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (bytes.Length == 0)
            throw new InvalidDataException("The image is empty.");
        if (bytes.Length > MaximumBytes)
            throw new InvalidDataException("The image is too large.");

        using var stream = new MemoryStream(bytes, writable: false);
        var preview = new Bitmap(stream);
        return new AgentAttachmentViewModel(
            string.IsNullOrWhiteSpace(fileName) ? "clipboard.png" : fileName,
            FormatSize(bytes.Length),
            true,
            preview,
            AgentContentPart.ImagePart(
                string.IsNullOrWhiteSpace(mediaType) ? "image/png" : mediaType,
                Convert.ToBase64String(bytes),
                fileName));
    }

    private static AgentAttachmentViewModel FromDocument(string fileName, string text)
    {
        var normalized = text.Replace("\0", string.Empty).Trim();
        if (normalized.Length == 0)
            throw new InvalidDataException("The document does not contain readable text.");
        if (normalized.Length > MaximumTextCharacters)
            normalized = normalized[..MaximumTextCharacters] + "\n[document truncated]";

        var bytes = Encoding.UTF8.GetByteCount(normalized);
        return new AgentAttachmentViewModel(
            fileName,
            FormatSize(bytes),
            false,
            null,
            AgentContentPart.TextPart(normalized, fileName));
    }

    private static async Task<string> ExtractDocxTextAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 16 * 1024,
            useAsync: true);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        var entry = archive.GetEntry("word/document.xml")
            ?? throw new InvalidDataException("The DOCX document body was not found.");
        await using var entryStream = entry.Open();
        using var reader = new StreamReader(entryStream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var xml = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        var document = XDocument.Parse(xml, LoadOptions.PreserveWhitespace);
        XNamespace word = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        var paragraphs = document
            .Descendants(word + "p")
            .Select(paragraph => string.Concat(paragraph.Descendants(word + "t").Select(text => text.Value)))
            .Where(text => !string.IsNullOrWhiteSpace(text));
        return string.Join(Environment.NewLine, paragraphs);
    }

    private static string DecodeText(byte[] bytes)
    {
        try
        {
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                .GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(936).GetString(bytes);
        }
    }

    private static string FormatSize(int bytes)
        => bytes >= 1024 * 1024
            ? $"{bytes / (1024d * 1024d):0.0} MB"
            : bytes >= 1024
                ? $"{bytes / 1024d:0.0} KB"
                : $"{bytes} B";
}
