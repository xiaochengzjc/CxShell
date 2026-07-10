using System;

namespace CxShell.Services;

public static class FileTransferProtocolOptions
{
    public static string NormalizeUploadProtocol(string? value)
    {
        return value?.Trim().ToLowerInvariant() switch
        {
            "xmodem" => "Xmodem",
            "ymodem" => "Ymodem",
            _ => "Auto"
        };
    }
}
