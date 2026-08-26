using System;
using System.Collections.Generic;

namespace CxShell.Models;

public sealed class SftpTransferQueueRecord
{
    public Guid TaskId { get; set; }
    public Guid SessionId { get; set; }
    public string Protocol { get; set; } = nameof(SessionProtocol.SFTP);
    public string Direction { get; set; } = "Download";
    public string FileName { get; set; } = string.Empty;
    public string LocalPath { get; set; } = string.Empty;
    public string RemotePath { get; set; } = string.Empty;
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; }
    public string Username { get; set; } = string.Empty;
    public long TotalBytes { get; set; }
    public long TransferredBytes { get; set; }
    public string Status { get; set; } = "Failed";
    public string? ErrorMessage { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class SftpTransferQueueData
{
    public string Format { get; set; } = "CxShell.SftpTransferQueue";
    public string Version { get; set; } = "1.0";
    public List<SftpTransferQueueRecord> Transfers { get; set; } = new();
}
