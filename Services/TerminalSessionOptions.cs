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

        if (string.Equals(session?.TerminalReceiveLineEnding?.Trim(), "AUTO", StringComparison.OrdinalIgnoreCase))
            return data;

        var ending = ResolveLineEnding(session?.TerminalReceiveLineEnding, defaultValue: "CRLF");
        return NormalizeLineEndings(data, ending);
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
