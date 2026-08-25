using System.Net;
using System.Net.Sockets;
using CxShell.Models;

namespace CxShell.Services;

public static class SshTunnelPortChecker
{
    public static string? Check(SshTunnelRule candidate, IEnumerable<SshTunnelRule> existingRules)
    {
        if (candidate.Type == SshTunnelRuleType.Remote)
            return null;

        if (existingRules.Any(rule =>
                rule.Id != candidate.Id &&
                rule.Type != SshTunnelRuleType.Remote &&
                rule.ListenPort == candidate.ListenPort))
        {
            return $"Local port {candidate.ListenPort} is already used by another tunnel rule.";
        }

        try
        {
            using var listener = new TcpListener(GetBindAddress(candidate), candidate.ListenPort);
            listener.Start();
            listener.Stop();
            return null;
        }
        catch (SocketException ex)
        {
            return $"Local port {candidate.ListenPort} is unavailable: {ex.Message}";
        }
        catch (Exception ex)
        {
            return $"Local port {candidate.ListenPort} could not be checked: {ex.Message}";
        }
    }

    private static IPAddress GetBindAddress(SshTunnelRule rule)
    {
        if (rule.AcceptLocalConnectionsOnly)
            return IPAddress.Loopback;

        if (IPAddress.TryParse(rule.SourceHost, out var address))
            return address;

        return IPAddress.Any;
    }
}
