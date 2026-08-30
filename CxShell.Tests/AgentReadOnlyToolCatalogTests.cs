using System.Text;
using CxShell.Models;
using CxShell.Services.Agent;

namespace CxShell.Tests;

public sealed class AgentReadOnlyToolCatalogTests
{
    [Fact]
    public void LinuxLogPlanValidatesSourceAndLineLimit()
    {
        var session = CreateSession("Linux/Unix");

        Assert.True(AgentReadOnlyToolCatalog.TryCreatePlan(
            session,
            AgentReadOnlyToolCatalog.LogsToolName,
            Json(new { source = "security", lines = 25 }),
            out var plan,
            out var error), error);

        Assert.Contains("tail -n 25", plan.Command, StringComparison.Ordinal);
        Assert.DoesNotContain("sudo", plan.Command, StringComparison.OrdinalIgnoreCase);
        Assert.False(AgentReadOnlyToolCatalog.TryCreatePlan(
            session,
            AgentReadOnlyToolCatalog.LogsToolName,
            Json(new { source = "custom", lines = 25 }),
            out _,
            out error));
        Assert.Contains("source", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PortAndServicePlansDoNotAcceptUnboundedInput()
    {
        var session = CreateSession("Linux");

        Assert.True(AgentReadOnlyToolCatalog.TryCreatePlan(
            session,
            AgentReadOnlyToolCatalog.PortCheckToolName,
            Json(new { port = 2222 }),
            out var portPlan,
            out var error), error);
        Assert.Contains("2222", portPlan.Command, StringComparison.Ordinal);

        Assert.False(AgentReadOnlyToolCatalog.TryCreatePlan(
            session,
            AgentReadOnlyToolCatalog.PortCheckToolName,
            Json(new { port = 70000 }),
            out _,
            out error));
        Assert.Contains("65535", error, StringComparison.Ordinal);

        Assert.True(AgentReadOnlyToolCatalog.TryCreatePlan(
            session,
            AgentReadOnlyToolCatalog.ServiceDetailToolName,
            Json(new { service = "sshd.service" }),
            out var servicePlan,
            out error), error);
        Assert.Contains("sshd.service", servicePlan.Command, StringComparison.Ordinal);

        Assert.False(AgentReadOnlyToolCatalog.TryCreatePlan(
            session,
            AgentReadOnlyToolCatalog.ServiceDetailToolName,
            Json(new { service = "sshd; reboot" }),
            out _,
            out error));
        Assert.Contains("service", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WindowsPlansUseEncodedPowerShellAndKnownFileTargets()
    {
        var session = CreateSession("Windows");

        Assert.True(AgentReadOnlyToolCatalog.TryCreatePlan(
            session,
            AgentReadOnlyToolCatalog.PortCheckToolName,
            Json(new { port = 15900 }),
            out var portPlan,
            out var error), error);
        var script = DecodePowerShell(portPlan.Command);
        Assert.Contains("LocalPort 15900", script, StringComparison.Ordinal);
        Assert.Contains("-NoProfile", portPlan.Command, StringComparison.Ordinal);

        Assert.True(AgentReadOnlyToolCatalog.TryCreatePlan(
            session,
            AgentReadOnlyToolCatalog.FilePreviewToolName,
            Json(new { target = "hosts", lines = 20 }),
            out var filePlan,
            out error), error);
        Assert.Contains("C:\\Windows\\System32\\drivers\\etc\\hosts", DecodePowerShell(filePlan.Command), StringComparison.Ordinal);

        Assert.False(AgentReadOnlyToolCatalog.TryCreatePlan(
            session,
            AgentReadOnlyToolCatalog.FilePreviewToolName,
            Json(new { target = "os-release" }),
            out _,
            out error));
        Assert.Contains("not available", error, StringComparison.OrdinalIgnoreCase);
    }

    private static System.Text.Json.JsonElement Json(object value)
        => System.Text.Json.JsonSerializer.SerializeToElement(value);

    private static string DecodePowerShell(string command)
    {
        var encoded = command[(command.LastIndexOf(' ') + 1)..];
        return Encoding.Unicode.GetString(Convert.FromBase64String(encoded));
    }

    private static AgentSessionSnapshot CreateSession(string platform)
        => new()
        {
            SessionId = Guid.NewGuid(),
            Protocol = SessionProtocol.SSH,
            IsConnected = true,
            Platform = platform
        };
}
