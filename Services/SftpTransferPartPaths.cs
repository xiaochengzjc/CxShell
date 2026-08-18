using System;

namespace CxShell.Services;

public static class SftpTransferPartPaths
{
    private const string PartialFileSuffix = ".cxshell.part";

    public static string GetLocalPath(string localPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localPath);
        return localPath + PartialFileSuffix;
    }

    public static string GetRemotePath(string remotePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(remotePath);

        var separatorIndex = remotePath.LastIndexOf('/');
        if (separatorIndex < 0)
            return remotePath + PartialFileSuffix;

        return remotePath[..(separatorIndex + 1)] + "." +
               remotePath[(separatorIndex + 1)..] + PartialFileSuffix;
    }
}
