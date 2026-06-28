using System;
using System.Collections.Generic;
using System.Linq;
using CxShell.Models;
using Renci.SshNet;

namespace CxShell.Services;

public static class SshAlgorithmPreferenceService
{
    private static readonly string[] KnownCipherAlgorithms =
    [
        "3des-cbc",
        "aes128-cbc",
        "aes128-ctr",
        "aes128-gcm@openssh.com",
        "aes192-cbc",
        "aes192-ctr",
        "aes256-cbc",
        "aes256-ctr",
        "aes256-gcm@openssh.com",
        "arcfour",
        "arcfour128",
        "arcfour256",
        "blowfish-cbc",
        "cast128-cbc",
        "chacha20-poly1305@openssh.com",
        "rijndael128-cbc",
        "rijndael192-cbc",
        "rijndael256-cbc",
        "rijndael-cbc@lysator.liu.se"
    ];

    private static readonly string[] KnownMacAlgorithms =
    [
        "hmac-md5",
        "hmac-md5-96",
        "hmac-md5-96-etm@openssh.com",
        "hmac-md5-etm@openssh.com",
        "hmac-ripemd160",
        "hmac-ripemd160@openssh.com",
        "hmac-sha1",
        "hmac-sha1-96",
        "hmac-sha1-96-etm@openssh.com",
        "hmac-sha1-etm@openssh.com",
        "hmac-sha2-256",
        "hmac-sha2-256-etm@openssh.com",
        "hmac-sha2-512",
        "hmac-sha2-512-etm@openssh.com",
        "umac-128@openssh.com",
        "umac-128-etm@openssh.com",
        "umac-64@openssh.com",
        "umac-64-etm@openssh.com"
    ];

    private static readonly string[] KnownKeyExchangeAlgorithms =
    [
        "curve25519-sha256@libssh.org",
        "curve25519-sha256",
        "ecdh-sha2-nistp256",
        "ecdh-sha2-nistp384",
        "ecdh-sha2-nistp521",
        "diffie-hellman-group-exchange-sha256",
        "diffie-hellman-group-exchange-sha1",
        "diffie-hellman-group18-sha512",
        "diffie-hellman-group17-sha512",
        "diffie-hellman-group16-sha512",
        "diffie-hellman-group15-sha512",
        "diffie-hellman-group14-sha256",
        "diffie-hellman-group14-sha1",
        "diffie-hellman-group1-sha1"
    ];

    public static IReadOnlyList<string> DefaultCipherAlgorithms { get; } = BuildAlgorithmList(
        KnownCipherAlgorithms,
        info => info.Encryptions.Keys);

    public static IReadOnlySet<string> SupportedCipherAlgorithms { get; } = LoadSupported(
        info => info.Encryptions.Keys);

    public static IReadOnlyList<string> DefaultMacAlgorithms { get; } = BuildAlgorithmList(
        KnownMacAlgorithms,
        info => info.HmacAlgorithms.Keys);

    public static IReadOnlySet<string> SupportedMacAlgorithms { get; } = LoadSupported(
        info => info.HmacAlgorithms.Keys);

    public static IReadOnlyList<string> DefaultKeyExchangeAlgorithms { get; } = BuildAlgorithmList(
        KnownKeyExchangeAlgorithms,
        info => info.KeyExchangeAlgorithms.Keys);

    public static IReadOnlySet<string> SupportedKeyExchangeAlgorithms { get; } = LoadSupported(
        info => info.KeyExchangeAlgorithms.Keys);

    public static void Apply(ConnectionInfo connectionInfo, SessionInfo session)
    {
        ApplyAlgorithmList(connectionInfo.Encryptions, session.SshCipherAlgorithms, "cipher");
        ApplyAlgorithmList(connectionInfo.HmacAlgorithms, session.SshMacAlgorithms, "MAC");
        ApplyAlgorithmList(connectionInfo.KeyExchangeAlgorithms, session.SshKeyExchangeAlgorithms, "key exchange");
    }

    private static IReadOnlyList<string> BuildAlgorithmList(
        IEnumerable<string> preferredOrder,
        Func<ConnectionInfo, IEnumerable<string>> supportedSelector)
    {
        var connectionInfo = new ConnectionInfo(
            "localhost",
            22,
            "user",
            new PasswordAuthenticationMethod("user", "password"));

        return preferredOrder
            .Concat(supportedSelector(connectionInfo))
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private static IReadOnlySet<string> LoadSupported(Func<ConnectionInfo, IEnumerable<string>> selector)
    {
        var connectionInfo = new ConnectionInfo(
            "localhost",
            22,
            "user",
            new PasswordAuthenticationMethod("user", "password"));

        return selector(connectionInfo)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToHashSet(StringComparer.Ordinal);
    }

    private static void ApplyAlgorithmList<T>(IDictionary<string, T> algorithms, string selectedAlgorithms, string label)
    {
        if (string.IsNullOrWhiteSpace(selectedAlgorithms))
            return;

        var selected = selectedAlgorithms
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (selected.Length == 0)
            return;

        var filtered = new List<KeyValuePair<string, T>>();
        foreach (var name in selected)
        {
            if (algorithms.TryGetValue(name, out var value))
                filtered.Add(new KeyValuePair<string, T>(name, value));
        }

        if (filtered.Count == 0)
            throw new NotSupportedException($"None of the selected SSH {label} algorithms are supported by SSH.NET.");

        algorithms.Clear();
        foreach (var item in filtered)
            algorithms.Add(item.Key, item.Value);
    }
}
