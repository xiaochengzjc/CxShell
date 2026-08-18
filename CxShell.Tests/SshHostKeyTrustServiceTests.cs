using CxShell.Models;
using CxShell.Services;

namespace CxShell.Tests;

public sealed class SshHostKeyTrustServiceTests
{
    [Fact]
    public void PermanentTrust_PersistsAndSkipsFuturePrompt()
    {
        var path = CreateTempPath();
        try
        {
            var firstPrompt = new StubPrompt(SshHostKeyDecision.TrustPermanently);
            var firstService = CreateService(path, firstPrompt);

            Assert.True(firstService.IsTrusted(CreateObservation("SHA256:first")));
            Assert.Equal(1, firstPrompt.CallCount);

            var secondPrompt = new StubPrompt(SshHostKeyDecision.Reject);
            var secondService = CreateService(path, secondPrompt);
            Assert.True(secondService.IsTrusted(CreateObservation("SHA256:first")));
            Assert.Equal(0, secondPrompt.CallCount);
        }
        finally
        {
            DeleteTempPath(path);
        }
    }

    [Fact]
    public void TrustOnce_OnlyLivesForCurrentServiceInstance()
    {
        var path = CreateTempPath();
        try
        {
            var prompt = new StubPrompt(SshHostKeyDecision.TrustOnce);
            var service = CreateService(path, prompt);

            Assert.True(service.IsTrusted(CreateObservation("SHA256:temporary")));
            Assert.True(service.IsTrusted(CreateObservation("SHA256:temporary")));
            Assert.Equal(1, prompt.CallCount);

            var nextPrompt = new StubPrompt(SshHostKeyDecision.Reject);
            var nextService = CreateService(path, nextPrompt);
            Assert.False(nextService.IsTrusted(CreateObservation("SHA256:temporary")));
            Assert.Equal(1, nextPrompt.CallCount);
        }
        finally
        {
            DeleteTempPath(path);
        }
    }

    [Fact]
    public void ChangedFingerprint_IsBlockedWithoutPromptByDefault()
    {
        var path = CreateTempPath();
        try
        {
            var service = CreateService(path, new StubPrompt(SshHostKeyDecision.TrustPermanently));
            Assert.True(service.IsTrusted(CreateObservation("SHA256:original")));

            var changedPrompt = new StubPrompt(SshHostKeyDecision.TrustPermanently);
            var changedService = CreateService(path, changedPrompt);
            Assert.False(changedService.IsTrusted(CreateObservation("SHA256:changed")));
            Assert.Equal(0, changedPrompt.CallCount);
            Assert.Equal("SHA256:original", Assert.Single(changedService.GetKnownHosts()).Fingerprint);
        }
        finally
        {
            DeleteTempPath(path);
        }
    }

    [Fact]
    public void ChangedFingerprint_CanBeConfirmedWhenBlockingIsDisabled()
    {
        var path = CreateTempPath();
        try
        {
            var service = CreateService(path, new StubPrompt(SshHostKeyDecision.TrustPermanently));
            Assert.True(service.IsTrusted(CreateObservation("SHA256:original")));

            var changedPrompt = new StubPrompt(SshHostKeyDecision.TrustPermanently);
            var changedService = CreateService(path, changedPrompt, blockChanged: false);
            Assert.True(changedService.IsTrusted(CreateObservation("SHA256:changed")));
            Assert.Equal(SshHostKeyVerification.Changed, changedPrompt.LastRequest?.Verification);
            Assert.Equal("SHA256:original", changedPrompt.LastRequest?.PreviousFingerprint);
            Assert.Equal("SHA256:changed", Assert.Single(changedService.GetKnownHosts()).Fingerprint);
        }
        finally
        {
            DeleteTempPath(path);
        }
    }

    [Fact]
    public void AutomaticFirstTrust_PersistsWithoutPrompt()
    {
        var path = CreateTempPath();
        try
        {
            var prompt = new StubPrompt(SshHostKeyDecision.Reject);
            var service = CreateService(path, prompt);

            Assert.True(service.IsTrusted(CreateObservation("raw-fingerprint"), automaticallyTrustUnknown: true));
            Assert.Equal(0, prompt.CallCount);
            Assert.Equal("SHA256:raw-fingerprint", Assert.Single(service.GetKnownHosts()).Fingerprint);
        }
        finally
        {
            DeleteTempPath(path);
        }
    }

    [Fact]
    public void RemoveKnownHost_RequiresConfirmationOnNextConnection()
    {
        var path = CreateTempPath();
        try
        {
            var service = CreateService(path, new StubPrompt(SshHostKeyDecision.TrustPermanently));
            Assert.True(service.IsTrusted(CreateObservation("SHA256:saved")));

            service.RemoveKnownHost("example.test", 22, "ssh-ed25519");

            Assert.Empty(service.GetKnownHosts());
            var prompt = new StubPrompt(SshHostKeyDecision.Reject);
            var nextService = CreateService(path, prompt);
            Assert.False(nextService.IsTrusted(CreateObservation("SHA256:saved")));
            Assert.Equal(1, prompt.CallCount);
        }
        finally
        {
            DeleteTempPath(path);
        }
    }

    private static SshHostKeyTrustService CreateService(
        string path,
        StubPrompt prompt,
        bool blockChanged = true)
    {
        var service = new SshHostKeyTrustService(path, prompt);
        service.Configure(new ApplicationSettings
        {
            ConfirmSshHostKeyOnFirstConnection = true,
            BlockChangedSshHostKeys = blockChanged
        });
        return service;
    }

    private static SshHostKeyObservation CreateObservation(string fingerprint)
        => new("Example.Test", 22, "ssh-ed25519", fingerprint, 256);

    private static string CreateTempPath()
    {
        return Path.Combine(
            Path.GetTempPath(),
            "CxShellTests",
            Guid.NewGuid().ToString("N"),
            "known_hosts.json");
    }

    private static void DeleteTempPath(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }

    private sealed class StubPrompt(SshHostKeyDecision decision) : ISshHostKeyPrompt
    {
        public int CallCount { get; private set; }
        public SshHostKeyPromptRequest? LastRequest { get; private set; }

        public Task<SshHostKeyDecision> DecideAsync(SshHostKeyPromptRequest request)
        {
            CallCount++;
            LastRequest = request;
            return Task.FromResult(decision);
        }
    }
}
