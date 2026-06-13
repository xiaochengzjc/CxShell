using System;
using System.Collections.Generic;
using System.Linq;
using ChiXueSsh.Models;
using Renci.SshNet;

namespace ChiXueSsh.Services;

public static class SshAlgorithmPreferenceService
{
    public static IReadOnlyList<string> DefaultCipherAlgorithms { get; } = LoadDefaults(info => info.Encryptions.Keys);

    public static IReadOnlyList<string> DefaultMacAlgorithms { get; } = LoadDefaults(info => info.HmacAlgorithms.Keys);

    public static IReadOnlyList<string> DefaultKeyExchangeAlgorithms { get; } = LoadDefaults(info => info.KeyExchangeAlgorithms.Keys);

    public static void Apply(ConnectionInfo connectionInfo, SessionInfo session)
    {
        ApplyAlgorithmList(connectionInfo.Encryptions, session.SshCipherAlgorithms, "cipher");
        ApplyAlgorithmList(connectionInfo.HmacAlgorithms, session.SshMacAlgorithms, "MAC");
        ApplyAlgorithmList(connectionInfo.KeyExchangeAlgorithms, session.SshKeyExchangeAlgorithms, "key exchange");
    }

    private static IReadOnlyList<string> LoadDefaults(Func<ConnectionInfo, IEnumerable<string>> selector)
    {
        var connectionInfo = new ConnectionInfo(
            "localhost",
            22,
            "user",
            new PasswordAuthenticationMethod("user", "password"));

        return selector(connectionInfo).ToArray();
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
