namespace CxShell.Models;

public enum SshHostKeyVerification
{
    Unknown,
    Trusted,
    Changed
}

public enum SshHostKeyDecision
{
    Reject,
    TrustOnce,
    TrustPermanently
}

public sealed class KnownSshHostKey
{
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 22;
    public string KeyType { get; set; } = string.Empty;
    public string Fingerprint { get; set; } = string.Empty;
    public DateTimeOffset FirstSeenUtc { get; set; }
    public DateTimeOffset LastSeenUtc { get; set; }
}

public sealed record SshHostKeyObservation(
    string Host,
    int Port,
    string KeyType,
    string Fingerprint,
    int KeyLength);

public sealed record SshHostKeyPromptRequest(
    SshHostKeyObservation Observation,
    SshHostKeyVerification Verification,
    string? PreviousFingerprint);
