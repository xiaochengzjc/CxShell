using CxShell.Services;

namespace CxShell.Tests;

public sealed class OpenSshConfigParserTests
{
    [Fact]
    public void Parse_ResolvesConcreteHostSettingsAndJumpChain()
    {
        const string config = """
            Host app-prod
                HostName 10.0.0.4
                User root
                Port 2222
                IdentityFile ~/.ssh/id_ed25519
                ProxyJump bastion@jump.example:2201,second-hop

            Host *
                User fallback
                Port 2200
            """;

        var entries = OpenSshConfigParser.Parse(config, @"C:\Users\tester\.ssh\config");

        var entry = Assert.Single(entries);
        Assert.Equal("app-prod", entry.Alias);
        Assert.Equal("10.0.0.4", entry.Host);
        Assert.Equal(2222, entry.Port);
        Assert.Equal("root", entry.Username);
        Assert.EndsWith(Path.Combine(".ssh", "id_ed25519"), entry.IdentityFile, StringComparison.OrdinalIgnoreCase);
        Assert.Collection(
            entry.JumpHosts,
            first =>
            {
                Assert.Equal("jump.example", first.Host);
                Assert.Equal(2201, first.Port);
                Assert.Equal("bastion", first.Username);
            },
            second =>
            {
                Assert.Equal("second-hop", second.Host);
                Assert.Equal(22, second.Port);
                Assert.Equal(string.Empty, second.Username);
            });
    }

    [Fact]
    public void Parse_AppliesWildcardDefaultsWithoutCreatingWildcardSessions()
    {
        const string config = """
            Host *
                Port 2200
            Host build-*
                HostName build.internal
            Host build-prod
                User deploy
            Host !build-prod build-*
                User rejected
            """;

        var entries = OpenSshConfigParser.Parse(config);

        var entry = Assert.Single(entries);
        Assert.Equal("build-prod", entry.Alias);
        Assert.Equal("build.internal", entry.Host);
        Assert.Equal(2200, entry.Port);
        Assert.Equal("deploy", entry.Username);
    }

    [Fact]
    public void LooksLikeConfig_DistinguishesJsonSessionData()
    {
        Assert.True(OpenSshConfigParser.LooksLikeConfig("Host server\n  HostName example.com"));
        Assert.False(OpenSshConfigParser.LooksLikeConfig("{\"Format\":\"CxShell.SessionExport\"}"));
    }
}
