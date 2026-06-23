using System;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using ChiXueSsh.Models;

namespace ChiXueSsh.Services;

public static class RdpLaunchService
{
    public static RdpLaunchResult Launch(SessionInfo session)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("RDP launch currently requires the Windows Remote Desktop client.");

        if (string.IsNullOrWhiteSpace(session.Host))
            throw new InvalidOperationException("RDP host is required.");

        var filePath = Path.Combine(
            Path.GetTempPath(),
            "ChiXueSsh",
            "Rdp",
            $"{SanitizeFileName(session.Name)}-{session.Id:N}.rdp");

        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        File.WriteAllText(filePath, BuildRdpFile(session), Encoding.Unicode);

        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "mstsc.exe",
                Arguments = Quote(filePath),
                UseShellExecute = false
            },
            EnableRaisingEvents = true
        };

        if (!process.Start())
            throw new InvalidOperationException("Failed to start mstsc.exe.");

        return new RdpLaunchResult(filePath, process);
    }

    private static string BuildRdpFile(SessionInfo session)
    {
        var (screenMode, width, height) = GetDisplaySettings(session);
        var audioMode = session.RdpAudioMode switch
        {
            "PlayLocal" => 0,
            "PlayRemote" => 2,
            _ => 1
        };

        var builder = new StringBuilder();
        builder.AppendLine($"full address:s:{BuildAddress(session)}");
        if (!string.IsNullOrWhiteSpace(session.Username))
            builder.AppendLine($"username:s:{session.Username}");

        builder.AppendLine($"screen mode id:i:{screenMode}");
        builder.AppendLine($"desktopwidth:i:{width}");
        builder.AppendLine($"desktopheight:i:{height}");
        builder.AppendLine($"session bpp:i:{GetColorDepth(session.RdpColorQuality)}");
        builder.AppendLine($"keyboardhook:i:{(session.RdpApplyKeyCombinations ? 2 : 0)}");
        builder.AppendLine($"redirectclipboard:i:1");
        builder.AppendLine($"redirectdrives:i:{(session.RdpRedirectDrives ? 1 : 0)}");
        builder.AppendLine($"audiomode:i:{audioMode}");
        builder.AppendLine($"audiocapturemode:i:{(session.RdpAudioCapture ? 1 : 0)}");
        builder.AppendLine($"autoreconnection enabled:i:{(session.AutoReconnect ? 1 : 0)}");
        builder.AppendLine($"smart sizing:i:{(session.RdpResizeMode == "SmartSizing" ? 1 : 0)}");
        builder.AppendLine($"dynamic resolution:i:{(session.RdpResizeMode == "SmartReconnect" ? 1 : 0)}");

        if (int.TryParse(session.RdpScreenScale, NumberStyles.Integer, CultureInfo.InvariantCulture, out var scale))
        {
            scale = Math.Clamp(scale, 100, 500);
            builder.AppendLine($"desktopscalefactor:i:{scale}");
        }

        return builder.ToString();
    }

    private static (int screenMode, int width, int height) GetDisplaySettings(SessionInfo session)
    {
        if (session.RdpWindowSize == "FullScreen")
            return (2, Math.Max(1, session.RdpDesktopWidth), Math.Max(1, session.RdpDesktopHeight));

        if (session.RdpWindowSize.Contains('x', StringComparison.OrdinalIgnoreCase))
        {
            var parts = session.RdpWindowSize.Split('x');
            if (parts.Length == 2 &&
                int.TryParse(parts[0], out var presetWidth) &&
                int.TryParse(parts[1], out var presetHeight))
            {
                return (1, presetWidth, presetHeight);
            }
        }

        return (1, Math.Max(1, session.RdpDesktopWidth), Math.Max(1, session.RdpDesktopHeight));
    }

    private static int GetColorDepth(string value)
    {
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var depth)
            ? Math.Clamp(depth, 15, 32)
            : 32;
    }

    private static string BuildAddress(SessionInfo session)
    {
        var host = session.Host.Trim();
        return session.Port is > 0 and not 3389
            ? $"{host}:{session.Port}"
            : host;
    }

    private static string SanitizeFileName(string value)
    {
        var name = string.IsNullOrWhiteSpace(value) ? "rdp" : value.Trim();
        foreach (var ch in Path.GetInvalidFileNameChars())
            name = name.Replace(ch, '_');

        return name;
    }

    private static string Quote(string value)
    {
        return "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
    }
}

public sealed record RdpLaunchResult(string FilePath, Process Process);
