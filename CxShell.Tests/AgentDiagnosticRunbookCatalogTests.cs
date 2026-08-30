using System.Text;
using CxShell.Models;
using CxShell.Services.Agent;

namespace CxShell.Tests;

public sealed class AgentDiagnosticRunbookCatalogTests
{
    [Fact]
    public void LinuxSshRunbookContainsOnlyReadOnlyChecks()
    {
        var session = CreateSession("Linux/Unix");

        var created = AgentDiagnosticRunbookCatalog.TryCreatePlan(
            session,
            AgentDiagnosticRunbookCatalog.SshScope,
            out var plan,
            out var error);

        Assert.True(created, error);
        Assert.Equal("runbook ssh", plan.DisplayCommand);
        Assert.Contains("ss -lnt", plan.Command, StringComparison.Ordinal);
        Assert.Contains("systemctl is-active", plan.Command, StringComparison.Ordinal);
        Assert.DoesNotContain("rm ", plan.Command, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sudo", plan.Command, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WindowsRdpRunbookChecksServicePolicyPortAndFirewall()
    {
        var session = CreateSession("Windows");

        Assert.True(AgentDiagnosticRunbookCatalog.TryCreatePlan(
            session,
            AgentDiagnosticRunbookCatalog.RdpScope,
            out var plan,
            out var error), error);

        var encoded = plan.Command[(plan.Command.LastIndexOf(' ') + 1)..];
        var script = Encoding.Unicode.GetString(Convert.FromBase64String(encoded));
        Assert.Contains("Get-Service -Name TermService", script, StringComparison.Ordinal);
        Assert.Contains("fDenyTSConnections", script, StringComparison.Ordinal);
        Assert.Contains("LocalPort 3389", script, StringComparison.Ordinal);
        Assert.Contains("Remote Desktop", script, StringComparison.Ordinal);
    }

    [Fact]
    public void RdpRunbookIsRejectedForLinux()
    {
        var session = CreateSession("Linux");

        Assert.False(AgentDiagnosticRunbookCatalog.TryCreatePlan(
            session,
            AgentDiagnosticRunbookCatalog.RdpScope,
            out _,
            out var error));
        Assert.Contains("Windows", error, StringComparison.OrdinalIgnoreCase);
    }

    private static AgentSessionSnapshot CreateSession(string platform)
        => new()
        {
            SessionId = Guid.NewGuid(),
            Protocol = SessionProtocol.SSH,
            Host = "example.test",
            Platform = platform,
            IsConnected = true
        };
}
