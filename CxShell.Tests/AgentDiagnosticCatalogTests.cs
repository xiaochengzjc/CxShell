using System.Text;
using CxShell.Services.Agent;

namespace CxShell.Tests;

public sealed class AgentDiagnosticCatalogTests
{
    [Fact]
    public void LinuxDiskPlanUsesTheFixedReadOnlyCommand()
    {
        var session = new AgentSessionSnapshot
        {
            SessionId = Guid.NewGuid(),
            Protocol = CxShell.Models.SessionProtocol.SSH,
            IsConnected = true,
            Platform = "Linux/Unix"
        };

        var created = AgentDiagnosticCatalog.TryCreatePlan(
            session,
            AgentDiagnosticCatalog.DiskScope,
            out var plan,
            out var error);

        Assert.True(created, error);
        Assert.Equal("diagnostic disk", plan.DisplayCommand);
        Assert.Contains("df -P -h", plan.Command, StringComparison.Ordinal);
        Assert.DoesNotContain("rm", plan.Command, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WindowsPlanUsesEncodedPowerShellAndSupportsOnlyKnownScopes()
    {
        var session = new AgentSessionSnapshot
        {
            SessionId = Guid.NewGuid(),
            Protocol = CxShell.Models.SessionProtocol.SSH,
            IsConnected = true,
            Platform = "Windows"
        };

        Assert.True(AgentDiagnosticCatalog.TryCreatePlan(
            session,
            AgentDiagnosticCatalog.SystemScope,
            out var plan,
            out var error), error);

        var encoded = plan.Command[(plan.Command.LastIndexOf(' ') + 1)..];
        var script = Encoding.Unicode.GetString(Convert.FromBase64String(encoded));
        Assert.StartsWith("powershell.exe -NoProfile -NonInteractive", plan.Command, StringComparison.Ordinal);
        Assert.Contains("Get-CimInstance Win32_OperatingSystem", script, StringComparison.Ordinal);

        Assert.False(AgentDiagnosticCatalog.TryCreatePlan(
            session,
            "delete",
            out _,
            out error));
        Assert.Contains("scope", error, StringComparison.OrdinalIgnoreCase);
    }
}
