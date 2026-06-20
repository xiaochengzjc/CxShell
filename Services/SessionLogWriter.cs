using System;
using System.Globalization;
using System.IO;
using System.Text;
using ChiXueSsh.Models;

namespace ChiXueSsh.Services;

public sealed class SessionLogWriter : IDisposable
{
    private readonly SessionInfo _session;
    private readonly StreamWriter _writer;
    private readonly bool _useRtf;
    private readonly bool _includeTerminalCodes;
    private readonly bool _writeTimestamp;
    private readonly string _timestampFormat;
    private bool _lineStart = true;
    private int _lineNumber = 1;
    private bool _disposed;

    private SessionLogWriter(SessionInfo session, StreamWriter writer)
    {
        _session = session;
        _writer = writer;
        _useRtf = session.AdvancedLogUseRtf;
        _includeTerminalCodes = session.AdvancedLogIncludeTerminalCodes;
        _writeTimestamp = session.AdvancedLogWriteTimestamp;
        _timestampFormat = string.IsNullOrWhiteSpace(session.AdvancedLogTimestampFormat)
            ? "[%a]"
            : session.AdvancedLogTimestampFormat;

        if (_useRtf)
            _writer.Write(@"{\rtf1\ansi\deff0{\fonttbl{\f0 Consolas;}}\f0\fs20 ");
    }

    public static SessionLogWriter Start(SessionInfo session, string? chosenPath = null)
    {
        var path = ResolveLogPath(session, chosenPath);
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        var mode = session.AdvancedLogOverwriteExisting ? FileMode.Create : FileMode.Append;
        var stream = new FileStream(path, mode, FileAccess.Write, FileShare.Read);
        var writer = new StreamWriter(stream, ResolveEncoding(session.AdvancedLogEncoding))
        {
            AutoFlush = true
        };
        return new SessionLogWriter(session, writer);
    }

    public void Write(string data)
    {
        if (_disposed || string.IsNullOrEmpty(data))
            return;

        var text = _includeTerminalCodes ? data : StripTerminalCodes(data);
        if (string.IsNullOrEmpty(text))
            return;

        foreach (var ch in text)
            WriteChar(ch);
    }

    private void WriteChar(char ch)
    {
        if (_writeTimestamp && _lineStart && ch != '\r' && ch != '\n')
            WriteText(ExpandTemplate(_timestampFormat, _session, DateTime.Now, _lineNumber) + " ");

        if (ch == '\r')
            return;

        if (ch == '\n')
        {
            WriteNewLine();
            _lineStart = true;
            _lineNumber++;
            return;
        }

        WriteText(ch.ToString());
        _lineStart = false;
    }

    private void WriteText(string text)
    {
        if (!_useRtf)
        {
            _writer.Write(text);
            return;
        }

        foreach (var ch in text)
            WriteRtfChar(ch);
    }

    private void WriteNewLine()
    {
        if (_useRtf)
            _writer.Write(@"\par ");
        else
            _writer.WriteLine();
    }

    private void WriteRtfChar(char ch)
    {
        switch (ch)
        {
            case '\\':
            case '{':
            case '}':
                _writer.Write('\\');
                _writer.Write(ch);
                break;
            case '\t':
                _writer.Write(@"\tab ");
                break;
            default:
                if (ch <= 0x7f)
                {
                    _writer.Write(ch);
                }
                else
                {
                    _writer.Write(string.Format(CultureInfo.InvariantCulture, @"\u{0}?", (short)ch));
                }
                break;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        if (_useRtf)
            _writer.Write('}');
        _writer.Dispose();
    }

    public static string ResolveLogPath(SessionInfo session, string? chosenPath = null)
    {
        var template = string.IsNullOrWhiteSpace(chosenPath)
            ? session.AdvancedLogFilePath
            : chosenPath;
        if (string.IsNullOrWhiteSpace(template))
            template = "%n_%Y-%m-%d_%t.log";

        var expanded = ExpandTemplate(template, session, DateTime.Now, 1);
        expanded = SanitizePath(expanded);
        if (!Path.IsPathRooted(expanded))
        {
            var baseDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "CxShell",
                "Logs");
            expanded = Path.Combine(baseDir, expanded);
        }

        return expanded;
    }

    public static string ExpandTemplate(string template, SessionInfo session, DateTime timestamp, int lineNumber)
    {
        var shortHost = session.Host ?? string.Empty;
        var dotIndex = shortHost.IndexOf('.');
        if (dotIndex > 0)
            shortHost = shortHost[..dotIndex];

        return template
            .Replace("%n", session.Name ?? string.Empty, StringComparison.Ordinal)
            .Replace("%u", session.Username ?? string.Empty, StringComparison.Ordinal)
            .Replace("%(HN)", session.Host ?? string.Empty, StringComparison.Ordinal)
            .Replace("%(hn)", shortHost, StringComparison.Ordinal)
            .Replace("%Y", timestamp.ToString("yyyy", CultureInfo.CurrentCulture), StringComparison.Ordinal)
            .Replace("%m", timestamp.ToString("MM", CultureInfo.CurrentCulture), StringComparison.Ordinal)
            .Replace("%d", timestamp.ToString("dd", CultureInfo.CurrentCulture), StringComparison.Ordinal)
            .Replace("%t", timestamp.ToString("HHmmss", CultureInfo.CurrentCulture), StringComparison.Ordinal)
            .Replace("%h", timestamp.ToString("HH", CultureInfo.CurrentCulture), StringComparison.Ordinal)
            .Replace("%M", timestamp.ToString("mm", CultureInfo.CurrentCulture), StringComparison.Ordinal)
            .Replace("%s", timestamp.ToString("ss", CultureInfo.CurrentCulture), StringComparison.Ordinal)
            .Replace("%N", timestamp.ToString("fff", CultureInfo.CurrentCulture), StringComparison.Ordinal)
            .Replace("%a", timestamp.ToString("G", CultureInfo.CurrentCulture), StringComparison.Ordinal)
            .Replace("%l", lineNumber.ToString(CultureInfo.CurrentCulture), StringComparison.Ordinal);
    }

    private static Encoding ResolveEncoding(string? value)
    {
        return value switch
        {
            "Utf8" => new UTF8Encoding(encoderShouldEmitUTF8Identifier: true),
            "Ansi" => Encoding.Default,
            _ => Encoding.Unicode
        };
    }

    private static string StripTerminalCodes(string value)
    {
        var builder = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            var ch = value[i];
            if (ch == '\x1b')
            {
                i = SkipEscapeSequence(value, i);
                continue;
            }

            if (ch == '\r' || ch == '\n' || ch == '\t' || ch >= ' ')
                builder.Append(ch);
        }

        return builder.ToString();
    }

    private static int SkipEscapeSequence(string value, int index)
    {
        if (index + 1 >= value.Length)
            return index;

        var next = value[index + 1];
        if (next == '[')
        {
            for (var i = index + 2; i < value.Length; i++)
            {
                if (value[i] >= 0x40 && value[i] <= 0x7e)
                    return i;
            }
            return value.Length - 1;
        }

        if (next == ']')
        {
            for (var i = index + 2; i < value.Length; i++)
            {
                if (value[i] == '\a')
                    return i;
                if (value[i] == '\x1b' && i + 1 < value.Length && value[i + 1] == '\\')
                    return i + 1;
            }
            return value.Length - 1;
        }

        return Math.Min(index + 1, value.Length - 1);
    }

    private static string SanitizePath(string path)
    {
        var root = Path.GetPathRoot(path);
        var rest = string.IsNullOrEmpty(root) ? path : path[root.Length..];
        var separators = new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar };
        var parts = rest.Split(separators, StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < parts.Length; i++)
            parts[i] = SanitizeFileName(parts[i]);

        var sanitized = Path.Combine(parts);
        return string.IsNullOrEmpty(root) ? sanitized : Path.Combine(root, sanitized);
    }

    private static string SanitizeFileName(string value)
    {
        foreach (var ch in Path.GetInvalidFileNameChars())
            value = value.Replace(ch, '_');
        return value;
    }
}
