using CxShell.Services;

namespace CxShell.Tests;

public sealed class LatestRequestVersionTests
{
    [Fact]
    public void BeginningANewRequestInvalidatesThePreviousRequest()
    {
        var versions = new LatestRequestVersion();

        var first = versions.Begin();
        var second = versions.Begin();

        Assert.False(versions.IsCurrent(first));
        Assert.True(versions.IsCurrent(second));
    }

    [Fact]
    public void InvalidateMakesTheCurrentRequestStale()
    {
        var versions = new LatestRequestVersion();
        var request = versions.Begin();

        versions.Invalidate();

        Assert.False(versions.IsCurrent(request));
    }

    [Fact]
    public void VersionsAlwaysIncrease()
    {
        var versions = new LatestRequestVersion();

        var first = versions.Begin();
        var second = versions.Begin();

        Assert.True(second > first);
    }
}
