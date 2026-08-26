using System;
using System.Threading;
using CxShell.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CxShell.ViewModels;

public enum SftpTransferDirection
{
    Upload,
    Download
}

public enum SftpTransferStatus
{
    Pending,
    Running,
    Retrying,
    Cancelling,
    Completed,
    Failed,
    Cancelled
}

public partial class SftpTransferTaskItem : ObservableObject
{
    private LocalizationService L => LocalizationService.Shared;
    private DateTimeOffset _lastSpeedSampleAt = DateTimeOffset.Now;
    private long _lastSpeedSampleBytes;

    [ObservableProperty] private Guid _id = Guid.NewGuid();
    [ObservableProperty] private SftpTransferDirection _direction;
    [ObservableProperty] private SftpTransferStatus _status = SftpTransferStatus.Pending;
    [ObservableProperty] private string _fileName = string.Empty;
    [ObservableProperty] private string _localPath = string.Empty;
    [ObservableProperty] private string _remotePath = string.Empty;
    [ObservableProperty] private long _totalBytes;
    [ObservableProperty] private long _transferredBytes;
    [ObservableProperty] private double _speedBytesPerSecond;
    [ObservableProperty] private string? _errorMessage;
    [ObservableProperty] private DateTimeOffset? _startedAt;
    [ObservableProperty] private DateTimeOffset? _completedAt;
    [ObservableProperty] private bool _isExecutionActive;
    [ObservableProperty] private bool _supportsRetry = true;
    [ObservableProperty] private int _automaticRetryCount;
    [ObservableProperty] private int _maxAutomaticRetries = 3;

    internal Guid ExecutionToken { get; private set; } = Guid.NewGuid();
    internal CancellationTokenSource? CancellationTokenSource { get; set; }
    internal IFileTransferService? ActiveService { get; set; }
    internal DateTimeOffset LastUiProgressAt { get; set; } = DateTimeOffset.MinValue;
    internal long LastUiProgressBytes { get; set; }

    public string DirectionText => Direction == SftpTransferDirection.Upload
        ? L.IsEnglish ? "Upload" : "\u4e0a\u4f20"
        : L.IsEnglish ? "Download" : "\u4e0b\u8f7d";

    public string StatusText => Status switch
    {
        SftpTransferStatus.Pending => L.IsEnglish ? "Pending" : "\u7b49\u5f85\u4e2d",
        SftpTransferStatus.Running => L.IsEnglish
            ? Direction == SftpTransferDirection.Upload ? "Uploading" : "Downloading"
            : Direction == SftpTransferDirection.Upload ? "\u4e0a\u4f20\u4e2d" : "\u4e0b\u8f7d\u4e2d",
        SftpTransferStatus.Retrying => L.IsEnglish
            ? $"Retrying ({AutomaticRetryCount}/{MaxAutomaticRetries})"
            : $"\u91cd\u8bd5\u4e2d ({AutomaticRetryCount}/{MaxAutomaticRetries})",
        SftpTransferStatus.Cancelling => L.IsEnglish ? "Cancelling" : "\u6b63\u5728\u53d6\u6d88",
        SftpTransferStatus.Completed => L.IsEnglish ? "Completed" : "\u5df2\u5b8c\u6210",
        SftpTransferStatus.Failed => L.IsEnglish ? "Failed" : "\u5931\u8d25",
        SftpTransferStatus.Cancelled => L.IsEnglish ? "Cancelled" : "\u5df2\u53d6\u6d88",
        _ => string.Empty
    };

    public string TargetPath => Direction == SftpTransferDirection.Upload ? RemotePath : LocalPath;

    public double ProgressPercent => TotalBytes <= 0
        ? 0
        : Math.Clamp(TransferredBytes * 100.0 / TotalBytes, 0, 100);

    public string ProgressText => TotalBytes <= 0
        ? "-"
        : $"{ProgressPercent:F1}%";

    public string SizeText => TotalBytes <= 0
        ? FormatByteSize(TransferredBytes)
        : $"{FormatByteSize(TransferredBytes)} / {FormatByteSize(TotalBytes)}";

    public string SpeedText => Status == SftpTransferStatus.Running && SpeedBytesPerSecond > 1
        ? $"{FormatByteSize((long)SpeedBytesPerSecond)}/s"
        : "-";

    public string RemainingText
    {
        get
        {
            if (Status != SftpTransferStatus.Running || SpeedBytesPerSecond <= 1 || TotalBytes <= 0)
                return "-";

            var remainingBytes = Math.Max(0, TotalBytes - TransferredBytes);
            var remaining = TimeSpan.FromSeconds(remainingBytes / SpeedBytesPerSecond);
            if (remaining.TotalHours >= 1)
                return $"{(int)remaining.TotalHours:D2}:{remaining.Minutes:D2}:{remaining.Seconds:D2}";

            return $"{remaining.Minutes:D2}:{remaining.Seconds:D2}";
        }
    }

    public bool CanCancel => IsExecutionActive &&
                             (Status is SftpTransferStatus.Pending or
                              SftpTransferStatus.Running or
                              SftpTransferStatus.Retrying);
    public bool CanRetry => SupportsRetry && !IsExecutionActive &&
                            (Status is SftpTransferStatus.Failed or SftpTransferStatus.Cancelled);
    public bool CanRemove => !IsExecutionActive &&
                            (Status is not SftpTransferStatus.Pending and
                              not SftpTransferStatus.Running and
                              not SftpTransferStatus.Retrying and
                              not SftpTransferStatus.Cancelling);
    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);

    public void PrepareForStart()
    {
        ExecutionToken = Guid.NewGuid();
        Status = SftpTransferStatus.Pending;
        ErrorMessage = null;
        CompletedAt = null;
        TransferredBytes = 0;
        SpeedBytesPerSecond = 0;
        AutomaticRetryCount = 0;
        LastUiProgressAt = DateTimeOffset.MinValue;
        LastUiProgressBytes = 0;
        ResetCancellation();
    }

    public void PrepareForResume()
    {
        ExecutionToken = Guid.NewGuid();
        Status = SftpTransferStatus.Pending;
        ErrorMessage = null;
        CompletedAt = null;
        if (TotalBytes > 0)
            TransferredBytes = Math.Clamp(TransferredBytes, 0, TotalBytes);
        else
            TransferredBytes = Math.Max(0, TransferredBytes);
        SpeedBytesPerSecond = 0;
        AutomaticRetryCount = 0;
        LastUiProgressAt = DateTimeOffset.MinValue;
        LastUiProgressBytes = TransferredBytes;
        ResetCancellation();
        NotifyComputedProperties();
    }

    internal bool IsCurrentExecution(Guid executionToken)
        => ExecutionToken == executionToken;

    internal Guid BeginExecutionAttempt()
    {
        ExecutionToken = Guid.NewGuid();
        return ExecutionToken;
    }

    public void RefreshLocalization()
    {
        OnPropertyChanged(nameof(DirectionText));
        OnPropertyChanged(nameof(StatusText));
    }

    public void MarkRunning()
    {
        if (Status is SftpTransferStatus.Completed or
            SftpTransferStatus.Failed or
            SftpTransferStatus.Cancelled or
            SftpTransferStatus.Cancelling)
        {
            return;
        }

        StartedAt = DateTimeOffset.Now;
        ErrorMessage = null;
        Status = SftpTransferStatus.Running;
        _lastSpeedSampleAt = StartedAt.Value;
        _lastSpeedSampleBytes = TransferredBytes;
        NotifyComputedProperties();
    }

    public void UpdateProgress(long transferredBytes, long totalBytes)
    {
        var now = DateTimeOffset.Now;
        if (totalBytes >= 0)
            TotalBytes = totalBytes;

        var nextTransferred = TotalBytes > 0
            ? Math.Clamp(transferredBytes, 0, TotalBytes)
            : Math.Max(0, transferredBytes);

        var elapsed = now - _lastSpeedSampleAt;
        if (elapsed.TotalMilliseconds >= 250)
        {
            var deltaBytes = Math.Max(0, nextTransferred - _lastSpeedSampleBytes);
            SpeedBytesPerSecond = deltaBytes / Math.Max(0.001, elapsed.TotalSeconds);
            _lastSpeedSampleAt = now;
            _lastSpeedSampleBytes = nextTransferred;
        }

        TransferredBytes = nextTransferred;
        NotifyComputedProperties();
    }

    public void MarkCompleted()
    {
        if (Status is SftpTransferStatus.Completed or
            SftpTransferStatus.Failed or
            SftpTransferStatus.Cancelled or
            SftpTransferStatus.Cancelling)
        {
            return;
        }

        if (TotalBytes > 0)
            TransferredBytes = TotalBytes;
        SpeedBytesPerSecond = 0;
        ErrorMessage = null;
        CompletedAt = DateTimeOffset.Now;
        Status = SftpTransferStatus.Completed;
        NotifyComputedProperties();
    }

    public void MarkFailed(string message)
    {
        if (Status is SftpTransferStatus.Completed or
            SftpTransferStatus.Cancelled or
            SftpTransferStatus.Cancelling)
            return;

        ErrorMessage = message;
        SpeedBytesPerSecond = 0;
        CompletedAt = DateTimeOffset.Now;
        Status = SftpTransferStatus.Failed;
        NotifyComputedProperties();
    }

    public void MarkRetrying(int retryCount, int maxRetries, string message)
    {
        if (Status is SftpTransferStatus.Completed or
            SftpTransferStatus.Cancelled or
            SftpTransferStatus.Cancelling)
        {
            return;
        }

        AutomaticRetryCount = Math.Max(0, retryCount);
        MaxAutomaticRetries = Math.Max(0, maxRetries);
        ErrorMessage = message;
        SpeedBytesPerSecond = 0;
        CompletedAt = null;
        Status = SftpTransferStatus.Retrying;
        NotifyComputedProperties();
    }

    public void MarkCancelled()
    {
        if (Status is SftpTransferStatus.Completed or SftpTransferStatus.Cancelled)
            return;

        SpeedBytesPerSecond = 0;
        CompletedAt = DateTimeOffset.Now;
        Status = SftpTransferStatus.Cancelled;
        NotifyComputedProperties();
    }

    public void MarkCancelling()
    {
        SpeedBytesPerSecond = 0;
        Status = SftpTransferStatus.Cancelling;
        NotifyComputedProperties();
    }

    internal void ResetCancellation()
    {
        CancellationTokenSource?.Dispose();
        CancellationTokenSource = new CancellationTokenSource();
        ActiveService = null;
    }

    internal void ClearRuntimeHandles()
    {
        ActiveService = null;
    }

    internal void RestoreInterruptedState(
        string message,
        long transferredBytes,
        bool wasCancelled = false)
    {
        ExecutionToken = Guid.NewGuid();
        ResetCancellation();
        TransferredBytes = Math.Max(0, transferredBytes);
        ErrorMessage = message;
        CompletedAt = DateTimeOffset.Now;
        Status = wasCancelled
            ? SftpTransferStatus.Cancelled
            : SftpTransferStatus.Failed;
        IsExecutionActive = false;
        NotifyComputedProperties();
    }

    partial void OnDirectionChanged(SftpTransferDirection value)
    {
        OnPropertyChanged(nameof(DirectionText));
        OnPropertyChanged(nameof(TargetPath));
        OnPropertyChanged(nameof(StatusText));
    }

    partial void OnStatusChanged(SftpTransferStatus value)
    {
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(CanCancel));
        OnPropertyChanged(nameof(CanRetry));
        OnPropertyChanged(nameof(CanRemove));
        OnPropertyChanged(nameof(SpeedText));
        OnPropertyChanged(nameof(RemainingText));
    }

    partial void OnAutomaticRetryCountChanged(int value)
    {
        OnPropertyChanged(nameof(StatusText));
    }

    partial void OnMaxAutomaticRetriesChanged(int value)
    {
        OnPropertyChanged(nameof(StatusText));
    }

    partial void OnLocalPathChanged(string value)
    {
        OnPropertyChanged(nameof(TargetPath));
    }

    partial void OnRemotePathChanged(string value)
    {
        OnPropertyChanged(nameof(TargetPath));
    }

    partial void OnTotalBytesChanged(long value)
    {
        NotifyComputedProperties();
    }

    partial void OnTransferredBytesChanged(long value)
    {
        NotifyComputedProperties();
    }

    partial void OnSpeedBytesPerSecondChanged(double value)
    {
        OnPropertyChanged(nameof(SpeedText));
        OnPropertyChanged(nameof(RemainingText));
    }

    partial void OnErrorMessageChanged(string? value)
    {
        OnPropertyChanged(nameof(HasError));
    }

    partial void OnIsExecutionActiveChanged(bool value)
    {
        OnPropertyChanged(nameof(CanCancel));
        OnPropertyChanged(nameof(CanRetry));
        OnPropertyChanged(nameof(CanRemove));
    }

    partial void OnSupportsRetryChanged(bool value)
    {
        OnPropertyChanged(nameof(CanRetry));
    }

    private void NotifyComputedProperties()
    {
        OnPropertyChanged(nameof(ProgressPercent));
        OnPropertyChanged(nameof(ProgressText));
        OnPropertyChanged(nameof(SizeText));
        OnPropertyChanged(nameof(SpeedText));
        OnPropertyChanged(nameof(RemainingText));
    }

    private static string FormatByteSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):F1} GB";
    }
}
