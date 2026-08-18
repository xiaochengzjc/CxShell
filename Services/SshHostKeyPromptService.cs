using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using CxShell.Models;
using CxShell.ViewModels;
using CxShell.Views;

namespace CxShell.Services;

public sealed class SshHostKeyPromptService : ISshHostKeyPrompt
{
    public async Task<SshHostKeyDecision> DecideAsync(SshHostKeyPromptRequest request)
    {
        if (Dispatcher.UIThread.CheckAccess())
            return SshHostKeyDecision.Reject;

        try
        {
            return await Dispatcher.UIThread
                .InvokeAsync(() => ShowDialogAsync(request))
                .ConfigureAwait(false);
        }
        catch
        {
            return SshHostKeyDecision.Reject;
        }
    }

    private static async Task<SshHostKeyDecision> ShowDialogAsync(SshHostKeyPromptRequest request)
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
            return SshHostKeyDecision.Reject;

        var owner = desktop.Windows.FirstOrDefault(window => window.IsActive) ?? desktop.MainWindow;
        if (owner == null)
            return SshHostKeyDecision.Reject;

        var viewModel = new SshHostKeyPromptViewModel(request);
        var window = new SshHostKeyPromptWindow(viewModel);
        return await window.ShowDialog<SshHostKeyDecision>(owner);
    }
}
