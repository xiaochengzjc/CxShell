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

    [Theory]
    [InlineData("Linux/Unix")]
    [InlineData("Windows")]
    public void DiagnosticPlansAreReadOnlyInRiskMode(string platform)
    {
        var session = new AgentSessionSnapshot
        {
            SessionId = Guid.NewGuid(),
            Protocol = CxShell.Models.SessionProtocol.SSH,
            IsConnected = true,
            Platform = platform
        };
        var policy = new AgentPermissionPolicy
        {
            PermissionMode = AgentPermissionPolicy.RiskBasedApprovalMode
        };

        foreach (var scope in AgentDiagnosticCatalog.Scopes)
        {
            Assert.True(
                AgentDiagnosticCatalog.TryCreatePlan(session, scope, out var plan, out var error),
                $"Could not create {platform} {scope} plan: {error}");

            var result = policy.Evaluate(session, plan.Command);

            Assert.True(
                result.Risk == AgentCommandRisk.ReadOnly,
                $"{platform} {scope} was classified as {result.Risk}: {plan.Command}");
            Assert.True(
                result.Decision == AgentPermissionDecision.Allowed,
                $"{platform} {scope} was classified as {result.Decision}: {plan.Command}");
        }
    }
}
