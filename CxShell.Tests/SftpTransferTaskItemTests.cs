using CxShell.ViewModels;

namespace CxShell.Tests;

public sealed class SftpTransferTaskItemTests
{
    [Fact]
    public void PrepareForStart_ResetsDisplayProgressForANewExecution()
    {
        var task = new SftpTransferTaskItem
        {
            TotalBytes = 100
        };

        task.PrepareForStart();
        task.MarkRunning();
        task.UpdateProgress(40, 100);
        task.MarkFailed("connection lost");

        Assert.True(task.CanRetry);
        Assert.Equal(40, task.TransferredBytes);

        task.PrepareForStart();

        Assert.Equal(SftpTransferStatus.Pending, task.Status);
        Assert.Equal(0, task.TransferredBytes);
        Assert.False(task.CanRetry);
        Assert.Null(task.ErrorMessage);
    }

    [Fact]
    public void PrepareForStart_ClearsPreviousCompletionState()
    {
        var task = new SftpTransferTaskItem
        {
            TotalBytes = 100
        };

        task.PrepareForStart();
        task.MarkRunning();
        task.UpdateProgress(100, 100);
        task.MarkCompleted();

        task.PrepareForStart();

        Assert.Equal(SftpTransferStatus.Pending, task.Status);
        Assert.Null(task.CompletedAt);
        Assert.Equal(0, task.TransferredBytes);
    }

    [Fact]
    public void CancellingStateCannotBeOverwrittenByLateTransferCallbacks()
    {
        var task = new SftpTransferTaskItem
        {
            TotalBytes = 100
        };

        task.PrepareForStart();
        task.IsExecutionActive = true;
        task.MarkRunning();
        task.MarkCancelling();

        task.MarkRunning();
        task.MarkCompleted();
        task.MarkFailed("late failure");

        Assert.Equal(SftpTransferStatus.Cancelling, task.Status);
    }

    [Fact]
    public void PrepareForResumeKeepsTheKnownPartialProgress()
    {
        var task = new SftpTransferTaskItem
        {
            TotalBytes = 100
        };

        task.PrepareForStart();
        task.MarkRunning();
        task.UpdateProgress(42, 100);
        task.MarkFailed("connection lost");

        task.PrepareForResume();

        Assert.Equal(SftpTransferStatus.Pending, task.Status);
        Assert.Equal(42, task.TransferredBytes);
        Assert.Null(task.ErrorMessage);
        Assert.False(task.IsExecutionActive);
    }

    [Fact]
    public void RestoreInterruptedStatePreservesExplicitCancellation()
    {
        var task = new SftpTransferTaskItem
        {
            TotalBytes = 100
        };

        task.RestoreInterruptedState("cancelled by user", 25, wasCancelled: true);

        Assert.Equal(SftpTransferStatus.Cancelled, task.Status);
        Assert.True(task.CanRetry);
        Assert.Equal(25, task.TransferredBytes);
    }
}
