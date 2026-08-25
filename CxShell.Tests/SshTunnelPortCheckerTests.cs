using System.Net;
using System.Net.Sockets;
using CxShell.Models;
using CxShell.Services;

namespace CxShell.Tests;

public sealed class SshTunnelPortCheckerTests
{
    [Fact]
    public void Check_DetectsDuplicateLocalRule()
    {
        var existing = CreateRule(SshTunnelRuleType.Local, 15432);
        var candidate = CreateRule(SshTunnelRuleType.Dynamic, 15432);

        var error = SshTunnelPortChecker.Check(candidate, [existing]);

        Assert.Contains("already used", error);
    }

    [Fact]
    public void Check_IgnoresRuleBeingEdited()
    {
        var candidate = CreateRule(SshTunnelRuleType.Local, GetAvailablePort());
        var existing = new SshTunnelRule
        {
            Id = candidate.Id,
            Type = candidate.Type,
            ListenPort = candidate.ListenPort,
            DestinationPort = candidate.DestinationPort
        };

        Assert.Null(SshTunnelPortChecker.Check(candidate, [existing]));
    }

    [Fact]
    public void Check_DetectsOccupiedLocalPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var error = SshTunnelPortChecker.Check(CreateRule(SshTunnelRuleType.Local, port), []);

        Assert.Contains("unavailable", error);
    }

    [Fact]
    public void Check_DoesNotProbeRemoteForwardPort()
    {
        Assert.Null(SshTunnelPortChecker.Check(CreateRule(SshTunnelRuleType.Remote, 22), []));
    }

    private static SshTunnelRule CreateRule(SshTunnelRuleType type, int port)
    {
        return new SshTunnelRule
        {
            Type = type,
            SourceHost = "127.0.0.1",
            ListenPort = port,
            AcceptLocalConnectionsOnly = true,
            DestinationHost = "localhost",
            DestinationPort = 22
        };
    }

    private static int GetAvailablePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
