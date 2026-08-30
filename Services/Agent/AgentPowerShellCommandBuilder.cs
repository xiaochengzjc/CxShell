using System.Text;

namespace CxShell.Services.Agent;

/// <summary>
/// Builds PowerShell commands that are safe to send through Windows OpenSSH.
/// PowerShell's EncodedCommand input is UTF-16LE, which keeps non-ASCII script
/// text intact even when the remote shell uses an OEM code page.
/// </summary>
internal static class AgentPowerShellCommandBuilder
{
    private const string PowerShellPrefix =
        "powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand ";

    public static string BuildWindowsAgentCommand(string command)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        return BuildEncodedCommand(command);
    }

    public static string BuildEncodedCommand(string script)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(script);

        var normalizedScript =
            "[Console]::OutputEncoding = [System.Text.Encoding]::UTF8\r\n" +
            "$OutputEncoding = [System.Text.Encoding]::UTF8\r\n" +
            "& {\r\n" +
            script.Trim() +
            "\r\n} 2>&1\r\n" +
            "if ($LASTEXITCODE -is [int]) { exit $LASTEXITCODE }\r\n";
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(normalizedScript));
        return PowerShellPrefix + encoded;
    }
}
