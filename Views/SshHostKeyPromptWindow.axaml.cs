using System;
using Avalonia.Interactivity;
using CxShell.Models;
using CxShell.ViewModels;

namespace CxShell.Views;

public partial class SshHostKeyPromptWindow : AtomUI.Desktop.Controls.Window
{
    private bool _hasResult;

    protected override Type StyleKeyOverride { get; } = typeof(AtomUI.Desktop.Controls.Window);

    public SshHostKeyPromptWindow()
    {
        InitializeComponent();
    }

    public SshHostKeyPromptWindow(SshHostKeyPromptViewModel viewModel)
        : this()
    {
        DataContext = viewModel;
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        CloseWithResult(SshHostKeyDecision.Reject);
    }

    private void OnTrustOnceClick(object? sender, RoutedEventArgs e)
    {
        CloseWithResult(SshHostKeyDecision.TrustOnce);
    }

    private void OnTrustPermanentlyClick(object? sender, RoutedEventArgs e)
    {
        CloseWithResult(SshHostKeyDecision.TrustPermanently);
    }

    private void CloseWithResult(SshHostKeyDecision decision)
    {
        if (_hasResult)
            return;

        _hasResult = true;
        Close(decision);
    }
}
