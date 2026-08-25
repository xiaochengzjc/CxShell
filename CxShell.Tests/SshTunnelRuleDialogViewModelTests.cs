using CxShell.Models;
using CxShell.ViewModels;

namespace CxShell.Tests;

public sealed class SshTunnelRuleDialogViewModelTests
{
    [Fact]
    public void TryBuildRule_RejectsInvalidListenPort()
    {
        var viewModel = new SshTunnelRuleDialogViewModel(null)
        {
            ListenPort = "70000",
            DestinationPort = "22"
        };

        Assert.False(viewModel.TryBuildRule(out var rule));
        Assert.Null(rule);
        Assert.True(viewModel.HasValidationMessage);
    }

    [Fact]
    public void TryBuildRule_PreservesEditedRuleId()
    {
        var source = new SshTunnelRule
        {
            Type = SshTunnelRuleType.Local,
            ListenPort = 15432,
            DestinationHost = "db.internal",
            DestinationPort = 5432
        };
        var viewModel = new SshTunnelRuleDialogViewModel(source);

        Assert.True(viewModel.TryBuildRule(out var rule));
        Assert.NotNull(rule);
        Assert.Equal(source.Id, rule.Id);
        Assert.Equal("db.internal", rule.DestinationHost);
        Assert.Equal(5432, rule.DestinationPort);
    }
}
