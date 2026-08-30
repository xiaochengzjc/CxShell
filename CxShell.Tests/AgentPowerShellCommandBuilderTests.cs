using System.Text;
using CxShell.Services.Agent;

namespace CxShell.Tests;

public sealed class AgentPowerShellCommandBuilderTests
{
    [Fact]
    public void WindowsAgentCommandEncodesUnicodeAndUsesUtf8Output()
    {
        const string command = "Write-Output '安装 Java 运行时'; throw '下载文件不存在'";

        var encodedCommand = AgentPowerShellCommandBuilder.BuildWindowsAgentCommand(command);
        var encoded = encodedCommand[(encodedCommand.LastIndexOf(' ') + 1)..];
        var script = Encoding.Unicode.GetString(Convert.FromBase64String(encoded));

        Assert.StartsWith(
            "powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand ",
            encodedCommand,
            StringComparison.Ordinal);
        Assert.Contains("[Console]::OutputEncoding = [System.Text.Encoding]::UTF8", script, StringComparison.Ordinal);
        Assert.Contains("$OutputEncoding = [System.Text.Encoding]::UTF8", script, StringComparison.Ordinal);
        Assert.Contains("安装 Java 运行时", script, StringComparison.Ordinal);
        Assert.Contains("下载文件不存在", script, StringComparison.Ordinal);
        Assert.Contains("} 2>&1", script, StringComparison.Ordinal);
        Assert.DoesNotContain("安装 Java 运行时", encodedCommand, StringComparison.Ordinal);
    }

    [Fact]
    public void EncodedDiagnosticScriptKeepsTheSameWindowsPowerShellContract()
    {
        var encodedCommand = AgentPowerShellCommandBuilder.BuildEncodedCommand(
            "Write-Output '中文输出'\nGet-Date");
        var encoded = encodedCommand[(encodedCommand.LastIndexOf(' ') + 1)..];
        var script = Encoding.Unicode.GetString(Convert.FromBase64String(encoded));

        Assert.Contains("Write-Output '中文输出'", script, StringComparison.Ordinal);
        Assert.Contains("Get-Date", script, StringComparison.Ordinal);
        Assert.Contains("2>&1", script, StringComparison.Ordinal);
    }
}
