using System;
using System.Threading.Tasks;
using AtomUI.Desktop.Controls;
using Avalonia.Interactivity;
using CxShell.Models;
using CxShell.ViewModels;

namespace CxShell.Views;

public partial class SshTunnelRuleDialogWindow : Window
{
    private SshTunnelRule? _result;

    protected override Type StyleKeyOverride { get; } = typeof(Window);

    public SshTunnelRuleDialogWindow()
    {
        InitializeComponent();
    }

    public SshTunnelRuleDialogWindow(SshTunnelRuleDialogViewModel viewModel)
        : this()
    {
        DataContext = viewModel;
    }

    public async Task<SshTunnelRule?> ShowRuleDialogAsync(Window owner)
    {
        await ShowDialog(owner);
        return _result;
    }

    private void OnSaveClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SshTunnelRuleDialogViewModel viewModel ||
            !viewModel.TryBuildRule(out var rule))
        {
            return;
        }

        _result = rule;
        Close();
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        Close();
    }
}
