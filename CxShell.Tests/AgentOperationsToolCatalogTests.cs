using System.Text.Json;
using CxShell.Models;
using CxShell.Services.Agent;

namespace CxShell.Tests;

public sealed class AgentOperationsToolCatalogTests
{
    [Fact]
    public void RuntimeCheckDefaultsToAllSupportedRuntimes()
    {
        var session = CreateSession("Linux/Unix");

        var created = AgentReadOnlyToolCatalog.TryCreatePlan(
            session,
            AgentReadOnlyToolCatalog.RuntimeCheckToolName,
            JsonDocument.Parse("{}").RootElement,
            out var plan,
            out var error);

        Assert.True(created, error);
        Assert.Equal("runtime check all", plan.DisplayCommand);
        Assert.Contains("java", plan.Command, StringComparison.Ordinal);
        Assert.Contains("python3", plan.Command, StringComparison.Ordinal);
        Assert.Contains("dotnet", plan.Command, StringComparison.Ordinal);
        Assert.Contains("node", plan.Command, StringComparison.Ordinal);
    }

    [Fact]
    public void PackageQueryRejectsShellSyntax()
    {
        var session = CreateSession("Linux/Unix");
        using var arguments = JsonDocument.Parse("{\"name\":\"openssh; rm -rf /\"}");

        var created = AgentReadOnlyToolCatalog.TryCreatePlan(
            session,
            AgentReadOnlyToolCatalog.PackageQueryToolName,
            arguments.RootElement,
            out _,
            out var error);

        Assert.False(created);
        Assert.Contains("package_query name", error, StringComparison.Ordinal);
    }

    [Fact]
    public void DiskCleanupAdviceIsExplicitlyReadOnlyOnWindows()
    {
        var session = CreateSession("Windows");
        using var arguments = JsonDocument.Parse("{\"scope\":\"all\"}");

        var created = AgentReadOnlyToolCatalog.TryCreatePlan(
            session,
            AgentReadOnlyToolCatalog.DiskCleanupAdviceToolName,
            arguments.RootElement,
            out var plan,
            out var error);

        Assert.True(created, error);
        var script = DecodePowerShell(plan.Command);
        Assert.Contains("Read-only analysis", script, StringComparison.Ordinal);
        Assert.DoesNotContain("Remove-Item", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Clear-", script, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NewToolsArePublishedInTheRuntimeToolDefinitions()
    {
        var names = AgentRunCoordinator.GetToolDefinitions()
            .Select(tool => tool.Name)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Contains(AgentReadOnlyToolCatalog.PackageQueryToolName, names);
        Assert.Contains(AgentReadOnlyToolCatalog.RuntimeCheckToolName, names);
        Assert.Contains(AgentReadOnlyToolCatalog.DiskCleanupAdviceToolName, names);
    }

    private static AgentSessionSnapshot CreateSession(string platform)
        => new()
        {
            SessionId = Guid.NewGuid(),
            Name = "test",
            Protocol = SessionProtocol.SSH,
            Host = "host",
            Port = 22,
            Username = "user",
            Platform = platform,
            IsConnected = true
        };

    private static string DecodePowerShell(string command)
    {
        var encoded = command[(command.LastIndexOf(' ') + 1)..];
        return System.Text.Encoding.Unicode.GetString(Convert.FromBase64String(encoded));
    }
}
