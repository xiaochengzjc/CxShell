using CxShell.Models;
using CxShell.Services.Agent;

namespace CxShell.Tests;

public sealed class AgentPermissionPolicyTests
{
    [Fact]
    public void CommandExecutionCanBeDisabledByPolicy()
    {
        var policy = new AgentPermissionPolicy { AllowCommandExecution = false };
        var session = CreateSession();

        var result = policy.Evaluate(session, "uname -a");

        Assert.Equal(AgentPermissionDecision.ExecutionDisabled, result.Decision);
        Assert.False(result.IsAllowed);
    }

    [Fact]
    public void DangerousCommandRequiresApprovalWhenConfigured()
    {
        var policy = new AgentPermissionPolicy { RequireApprovalForDangerousCommands = true };
        var session = CreateSession();

        var result = policy.Evaluate(session, "sudo reboot");

        Assert.Equal(AgentPermissionDecision.DangerousCommand, result.Decision);
        Assert.False(result.IsAllowed);
    }

    [Fact]
    public void DangerousCommandCanBeAllowedWhenApprovalRequirementIsDisabled()
    {
        var policy = new AgentPermissionPolicy { RequireApprovalForDangerousCommands = false };
        var session = CreateSession();

        var result = policy.Evaluate(session, "sudo reboot");

        Assert.Equal(AgentPermissionDecision.Allowed, result.Decision);
        Assert.Equal(AgentCommandRisk.Dangerous, result.Risk);
    }

    [Fact]
    public void ModifyingCommandRequiresApprovalWhenConfigured()
    {
        var policy = new AgentPermissionPolicy { RequireApprovalForChangeCommands = true };
        var session = CreateSession();

        var result = policy.Evaluate(session, "apt-get install openjdk-21-jre");

        Assert.Equal(AgentPermissionDecision.ChangeCommandApprovalRequired, result.Decision);
        Assert.Equal(AgentCommandRisk.Change, result.Risk);
        Assert.True(result.ApprovalRequired);
    }

    [Fact]
    public void AskModeRequiresApprovalForReadOnlyCommand()
    {
        var policy = new AgentPermissionPolicy
        {
            PermissionMode = AgentPermissionPolicy.AskBeforeEachCommandMode
        };
        var result = policy.Evaluate(CreateSession(), "pwd");

        Assert.Equal(AgentPermissionDecision.CommandApprovalRequired, result.Decision);
        Assert.True(result.ApprovalRequired);
        Assert.Equal(AgentCommandRisk.ReadOnly, result.Risk);
    }

    [Fact]
    public void RiskModeRequiresApprovalForModifyingCommand()
    {
        var policy = new AgentPermissionPolicy
        {
            PermissionMode = AgentPermissionPolicy.RiskBasedApprovalMode,
            RequireApprovalForChangeCommands = false,
            RequireApprovalForDangerousCommands = false
        };
        var result = policy.Evaluate(CreateSession(), "apt-get install openjdk-21-jre");

        Assert.Equal(AgentPermissionDecision.ChangeCommandApprovalRequired, result.Decision);
        Assert.True(result.ApprovalRequired);
    }

    [Fact]
    public void RiskModeRecognizesSudoOptionsBeforeModifyingCommand()
    {
        var policy = new AgentPermissionPolicy
        {
            PermissionMode = AgentPermissionPolicy.RiskBasedApprovalMode
        };
        var result = policy.Evaluate(CreateSession(), "sudo -n apt-get install nginx");

        Assert.Equal(AgentPermissionDecision.ChangeCommandApprovalRequired, result.Decision);
        Assert.Equal(AgentCommandRisk.Change, result.Risk);
        Assert.True(result.ApprovalRequired);
    }

    [Fact]
    public void FullAccessModeAllowsDangerousCommand()
    {
        var policy = new AgentPermissionPolicy
        {
            PermissionMode = AgentPermissionPolicy.FullAccessMode
        };
        var result = policy.Evaluate(CreateSession(), "sudo reboot");

        Assert.Equal(AgentPermissionDecision.Allowed, result.Decision);
        Assert.False(result.ApprovalRequired);
    }

    [Fact]
    public void ReadOnlyModeBlocksChangesButAllowsInspection()
    {
        var policy = new AgentPermissionPolicy { ReadOnlyMode = true };
        var session = CreateSession();

        var read = policy.Evaluate(session, "df -h");
        var change = policy.Evaluate(session, "systemctl restart sshd");

        Assert.Equal(AgentCommandRisk.ReadOnly, read.Risk);
        Assert.Equal(AgentPermissionDecision.Allowed, read.Decision);
        Assert.Equal(AgentCommandRisk.Change, change.Risk);
        Assert.Equal(AgentPermissionDecision.ReadOnlyMode, change.Decision);
    }

    [Fact]
    public void BlockListWinsOverAllowList()
    {
        var policy = new AgentPermissionPolicy
        {
            AllowedCommandPrefixes = "systemctl, journalctl",
            BlockedCommandPrefixes = "systemctl restart"
        };
        var session = CreateSession();

        Assert.Equal(AgentPermissionDecision.Allowed, policy.Evaluate(session, "systemctl status sshd").Decision);
        Assert.Equal(AgentPermissionDecision.CommandBlocked, policy.Evaluate(session, "systemctl restart sshd").Decision);
        Assert.Equal(AgentPermissionDecision.NotInAllowList, policy.Evaluate(session, "df -h").Decision);
    }

    [Fact]
    public void ApplicationSettingsPersistAgentExecutionPolicy()
    {
        using var directory = new TemporaryDirectory();
        var settings = new ApplicationSettings
        {
            AgentAllowCommandExecution = false,
            AgentRequireApprovalForDangerousCommands = false,
            AgentRequireApprovalForChangeCommands = true,
            AgentReadOnlyMode = true,
            AgentAllowedCommandPrefixes = "pwd, df",
            AgentBlockedCommandPrefixes = "rm",
            AgentPermissionMode = AgentPermissionPolicy.AskBeforeEachCommandMode
        };
        var store = new Services.ApplicationSettingsStore(directory.Path);

        store.Save(settings);
        var loaded = store.Load();

        Assert.False(loaded.AgentAllowCommandExecution);
        Assert.False(loaded.AgentRequireApprovalForDangerousCommands);
        Assert.True(loaded.AgentRequireApprovalForChangeCommands);
        Assert.True(loaded.AgentReadOnlyMode);
        Assert.Equal("pwd, df", loaded.AgentAllowedCommandPrefixes);
        Assert.Equal("rm", loaded.AgentBlockedCommandPrefixes);
        Assert.Equal(AgentPermissionPolicy.AskBeforeEachCommandMode, loaded.AgentPermissionMode);
    }

    private static AgentSessionSnapshot CreateSession()
        => AgentSessionSnapshot.FromSession(
            new SessionInfo
            {
                Name = "Policy test",
                Host = "policy.example",
                Protocol = SessionProtocol.SSH
            },
            isConnected: true);

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"cxshell-agent-policy-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch
            {
            }
        }
    }
}
