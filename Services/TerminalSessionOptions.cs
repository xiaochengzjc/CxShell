using System;
using System.Text;
using ChiXueSsh.Models;

namespace ChiXueSsh.Services;

public static class TerminalSessionOptions
{
    static TerminalSessionOptions()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public static string GetTerminalType(SessionInfo session)
    {
        return string.IsNullOrWhiteSpace(session.TerminalType)
            ? "xterm"
            : session.TerminalType.Trim();
    }

    public static Encoding GetEncoding(SessionInfo session)
    {
        var name = string.IsNullOrWhiteSpace(session.TerminalEncoding)
            ? "utf-8"
            : session.TerminalEncoding.Trim();

        if (string.Equals(name, "default", StringComparison.OrdinalIgnoreCase))
            return Encoding.Default;

        if (string.Equals(name, "iso-8859-8-i", StringComparison.OrdinalIgnoreCase))
            name = "iso-8859-8";

        try
        {
            return Encoding.GetEncoding(name, EncoderFallback.ReplacementFallback, DecoderFallback.ReplacementFallback);
        }
        catch
        {
            return Encoding.UTF8;
        }
    }

    public static string NormalizeSendLineEndings(string data, SessionInfo? session)
    {
        if (string.IsNullOrEmpty(data))
            return data;

        var ending = ResolveLineEnding(session?.TerminalSendLineEnding, defaultValue: "CR");
        return NormalizeLineEndings(data, ending);
    }

    public static string NormalizeReceiveLineEndings(string data, SessionInfo? session)
    {
        if (string.IsNullOrEmpty(data))
            return data;

        var mode = session?.TerminalReceiveLineEnding?.Trim();
        if (string.IsNullOrWhiteSpace(mode) ||
            string.Equals(mode, "AUTO", StringComparison.OrdinalIgnoreCase))
        {
            return data;
        }

        if (string.Equals(mode, "CRLF", StringComparison.OrdinalIgnoreCase))
            return NormalizeReceiveToCrLf(data);

        var ending = ResolveLineEnding(mode, defaultValue: "AUTO");
        if (string.Equals(ending, "AUTO", StringComparison.OrdinalIgnoreCase))
            return data;

        return NormalizeLineEndings(data, ending);
    }

    private static string NormalizeReceiveToCrLf(string data)
    {
        var builder = new StringBuilder(data.Length);
        for (var i = 0; i < data.Length; i++)
        {
            var ch = data[i];
            if (ch == '\r')
            {
                builder.Append(ch);
                if (i + 1 < data.Length && data[i + 1] == '\n')
                    builder.Append(data[++i]);
                continue;
            }

            if (ch == '\n')
            {
                builder.Append('\r');
                builder.Append('\n');
                continue;
            }

            builder.Append(ch);
        }

        return builder.ToString();
    }

    public static string ResolveLineEnding(string? value, string defaultValue)
    {
        return value?.Trim().ToUpperInvariant() switch
        {
            "LF" => "\n",
            "CRLF" => "\r\n",
            "CR" => "\r",
            _ => defaultValue.Equals("LF", StringComparison.OrdinalIgnoreCase)
                ? "\n"
                : defaultValue.Equals("CRLF", StringComparison.OrdinalIgnoreCase)
                    ? "\r\n"
                    : defaultValue.Equals("AUTO", StringComparison.OrdinalIgnoreCase)
                        ? "AUTO"
                        : "\r"
        };
    }

    private static string NormalizeLineEndings(string data, string ending)
    {
        return data
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Replace("\n", ending, StringComparison.Ordinal);
    }
}
