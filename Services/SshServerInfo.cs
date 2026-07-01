using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace CxShell.Services;

public static class SshServerInfo
{
    public static bool IsWindowsOpenSshServer(string? serverVersion)
    {
        return !string.IsNullOrWhiteSpace(serverVersion) &&
               (serverVersion.Contains("OpenSSH_for_Windows", StringComparison.OrdinalIgnoreCase) ||
                serverVersion.Contains("Win32-OpenSSH", StringComparison.OrdinalIgnoreCase));
    }

    public static string BuildConnectionErrorMessage(Exception ex)
    {
        var message = ex.Message;
        if (!message.Contains("does not contain an SSH identification string", StringComparison.OrdinalIgnoreCase))
            return message;

        var response = TryExtractSshIdentificationResponse(message);
        if (string.IsNullOrWhiteSpace(response))
            return "SSH server did not return an SSH protocol banner. Check the host, port, firewall, and sshd service.";

        if (response.Contains("Not allowed at this time", StringComparison.OrdinalIgnoreCase))
        {
            return "SSH server temporarily rejected this client before the protocol handshake: Not allowed at this time. " +
                   "Wait a moment or check the server sshd rate-limit/penalty settings.";
        }

        return $"SSH server returned non-SSH text before the protocol handshake: {response}";
    }

    public static bool IsLikelyTransientOpenFailure(Exception ex)
    {
        for (var current = ex; current != null; current = current.InnerException)
        {
            var message = current.Message;
            if (message.Contains("forcibly closed", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("强迫关闭", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("Not allowed at this time", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("connection was aborted", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("connection reset", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string? TryExtractSshIdentificationResponse(string message)
    {
        var bytes = new List<byte>();
        using var reader = new StringReader(message);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            var trimmed = line.TrimStart();
            if (trimmed.Length < 8 || !IsHexOffset(trimmed[..8]))
                continue;

            var tokens = trimmed[8..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            foreach (var token in tokens)
            {
                if (token.Length != 2 ||
                    !byte.TryParse(token, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value))
                {
                    break;
                }

                bytes.Add(value);
            }
        }

        if (bytes.Count == 0)
            return null;

        var decoded = Encoding.UTF8.GetString(bytes.ToArray());
        var builder = new StringBuilder(decoded.Length);
        var previousWasSpace = false;
        foreach (var ch in decoded)
        {
            if (char.IsControl(ch))
            {
                if (!previousWasSpace)
                {
                    builder.Append(' ');
                    previousWasSpace = true;
                }

                continue;
            }

            builder.Append(ch);
            previousWasSpace = char.IsWhiteSpace(ch);
        }

        return builder.ToString().Trim();
    }

    private static bool IsHexOffset(string text)
    {
        if (text.Length != 8)
            return false;

        foreach (var ch in text)
        {
            if (!Uri.IsHexDigit(ch))
                return false;
        }

        return true;
    }
}
