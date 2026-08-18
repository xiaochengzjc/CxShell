using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using CxShell.Models;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace CxShell.Services;

public interface ISshHostKeyPrompt
{
    Task<SshHostKeyDecision> DecideAsync(SshHostKeyPromptRequest request);
}

public sealed class SshHostKeyTrustService
{
    private sealed class KnownHostsFile
    {
        public int Version { get; set; } = 1;
        public List<KnownSshHostKey> Hosts { get; set; } = [];
    }

    private static readonly TimeSpan LastSeenWriteInterval = TimeSpan.FromHours(1);
    private readonly object _storageLock = new();
    private readonly string _storagePath;
    private readonly ISshHostKeyPrompt _prompt;
    private readonly ConcurrentDictionary<string, byte> _temporaryTrust = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _endpointLocks = new(StringComparer.OrdinalIgnoreCase);
    private ApplicationSettings _settings = new();

    public static SshHostKeyTrustService Shared { get; } = new(
        Path.Combine(SessionStorageService.GetStorageDirectory(), "known_hosts.json"),
        new SshHostKeyPromptService());

    public SshHostKeyTrustService(string storagePath, ISshHostKeyPrompt prompt)
    {
        _storagePath = storagePath ?? throw new ArgumentNullException(nameof(storagePath));
        _prompt = prompt ?? throw new ArgumentNullException(nameof(prompt));
    }

    public void Configure(ApplicationSettings settings)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    public void Attach(BaseClient client, string host, int port, bool automaticallyTrustUnknown = false)
    {
        ArgumentNullException.ThrowIfNull(client);
        client.HostKeyReceived += (_, args) =>
        {
            var observation = new SshHostKeyObservation(
                NormalizeHost(host),
                NormalizePort(port),
                args.HostKeyName ?? "unknown",
                NormalizeFingerprint(args.FingerPrintSHA256),
                args.KeyLength);
            args.CanTrust = IsTrusted(observation, automaticallyTrustUnknown);
        };
    }

    public IReadOnlyList<KnownSshHostKey> GetKnownHosts()
    {
        lock (_storageLock)
        {
            return LoadFile().Hosts
                .OrderBy(item => item.Host, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.Port)
                .ThenBy(item => item.KeyType, StringComparer.OrdinalIgnoreCase)
                .Select(Clone)
                .ToArray();
        }
    }

    public void RemoveKnownHost(string host, int port, string keyType)
    {
        var normalizedHost = NormalizeHost(host);
        var normalizedPort = NormalizePort(port);
        lock (_storageLock)
        {
            var file = LoadFile();
            var removed = file.Hosts.RemoveAll(item =>
                EndpointMatches(item, normalizedHost, normalizedPort) &&
                string.Equals(item.KeyType, keyType, StringComparison.OrdinalIgnoreCase));
            if (removed > 0)
                SaveFile(file);
        }

        var prefix = BuildEndpointKey(normalizedHost, normalizedPort, keyType) + "|";
        foreach (var key in _temporaryTrust.Keys.Where(key => key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            _temporaryTrust.TryRemove(key, out _);
    }

    public bool IsTrusted(SshHostKeyObservation observation, bool automaticallyTrustUnknown = false)
    {
        var normalized = observation with
        {
            Host = NormalizeHost(observation.Host),
            Port = NormalizePort(observation.Port),
            KeyType = string.IsNullOrWhiteSpace(observation.KeyType) ? "unknown" : observation.KeyType.Trim(),
            Fingerprint = NormalizeFingerprint(observation.Fingerprint)
        };
        if (string.IsNullOrWhiteSpace(normalized.Host) || string.IsNullOrWhiteSpace(normalized.Fingerprint))
            return false;

        var endpointKey = BuildEndpointKey(normalized.Host, normalized.Port, normalized.KeyType);
        var semaphore = _endpointLocks.GetOrAdd(endpointKey, static _ => new SemaphoreSlim(1, 1));
        semaphore.Wait();
        try
        {
            if (_temporaryTrust.ContainsKey(BuildTrustKey(normalized)))
                return true;

            KnownSshHostKey? existing;
            lock (_storageLock)
            {
                var file = LoadFile();
                existing = file.Hosts.FirstOrDefault(item =>
                    EndpointMatches(item, normalized.Host, normalized.Port) &&
                    string.Equals(item.KeyType, normalized.KeyType, StringComparison.OrdinalIgnoreCase));
                if (existing != null && string.Equals(existing.Fingerprint, normalized.Fingerprint, StringComparison.Ordinal))
                {
                    TouchLastSeen(file, existing);
                    return true;
                }
            }

            var verification = existing == null
                ? SshHostKeyVerification.Unknown
                : SshHostKeyVerification.Changed;
            if (verification == SshHostKeyVerification.Changed && _settings.BlockChangedSshHostKeys)
                return false;

            if (verification == SshHostKeyVerification.Unknown &&
                (automaticallyTrustUnknown || !_settings.ConfirmSshHostKeyOnFirstConnection))
            {
                TrustPermanently(normalized);
                return true;
            }

            var decision = _prompt.DecideAsync(new SshHostKeyPromptRequest(
                    normalized,
                    verification,
                    existing?.Fingerprint))
                .ConfigureAwait(false)
                .GetAwaiter()
                .GetResult();
            switch (decision)
            {
                case SshHostKeyDecision.TrustOnce:
                    _temporaryTrust[BuildTrustKey(normalized)] = 0;
                    return true;
                case SshHostKeyDecision.TrustPermanently:
                    TrustPermanently(normalized);
                    return true;
                default:
                    return false;
            }
        }
        catch
        {
            return false;
        }
        finally
        {
            semaphore.Release();
        }
    }

    private void TrustPermanently(SshHostKeyObservation observation)
    {
        lock (_storageLock)
        {
            var now = DateTimeOffset.UtcNow;
            var file = LoadFile();
            var existing = file.Hosts.FirstOrDefault(item =>
                EndpointMatches(item, observation.Host, observation.Port) &&
                string.Equals(item.KeyType, observation.KeyType, StringComparison.OrdinalIgnoreCase));
            if (existing == null)
            {
                file.Hosts.Add(new KnownSshHostKey
                {
                    Host = observation.Host,
                    Port = observation.Port,
                    KeyType = observation.KeyType,
                    Fingerprint = observation.Fingerprint,
                    FirstSeenUtc = now,
                    LastSeenUtc = now
                });
            }
            else
            {
                existing.Fingerprint = observation.Fingerprint;
                existing.FirstSeenUtc = now;
                existing.LastSeenUtc = now;
            }

            SaveFile(file);
        }
    }

    private void TouchLastSeen(KnownHostsFile file, KnownSshHostKey existing)
    {
        var now = DateTimeOffset.UtcNow;
        if (now - existing.LastSeenUtc < LastSeenWriteInterval)
            return;

        existing.LastSeenUtc = now;
        SaveFile(file);
    }

    private KnownHostsFile LoadFile()
    {
        try
        {
            if (!File.Exists(_storagePath))
                return new KnownHostsFile();

            var json = File.ReadAllText(_storagePath, Encoding.UTF8);
            var file = JsonSerializer.Deserialize<KnownHostsFile>(json) ?? new KnownHostsFile();
            file.Hosts ??= [];
            return file;
        }
        catch
        {
            return new KnownHostsFile();
        }
    }

    private void SaveFile(KnownHostsFile file)
    {
        var directory = Path.GetDirectoryName(_storagePath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        var json = JsonSerializer.Serialize(file, new JsonSerializerOptions { WriteIndented = true });
        var temporaryPath = _storagePath + ".tmp";
        File.WriteAllText(temporaryPath, json, new UTF8Encoding(false));
        File.Move(temporaryPath, _storagePath, true);
    }

    private static bool EndpointMatches(KnownSshHostKey item, string host, int port)
    {
        return string.Equals(NormalizeHost(item.Host), host, StringComparison.OrdinalIgnoreCase) &&
               NormalizePort(item.Port) == port;
    }

    private static string BuildEndpointKey(string host, int port, string keyType)
        => $"{NormalizeHost(host)}|{NormalizePort(port)}|{keyType.Trim()}";

    private static string BuildTrustKey(SshHostKeyObservation observation)
        => $"{BuildEndpointKey(observation.Host, observation.Port, observation.KeyType)}|{observation.Fingerprint}";

    private static string NormalizeHost(string? host)
    {
        var value = host?.Trim() ?? string.Empty;
        if (value.Length > 1 && value[0] == '[' && value[^1] == ']')
            value = value[1..^1];
        return value.ToLowerInvariant();
    }

    private static int NormalizePort(int port) => port is >= 1 and <= 65535 ? port : 22;

    private static string NormalizeFingerprint(string? fingerprint)
    {
        var value = fingerprint?.Trim() ?? string.Empty;
        return value.StartsWith("SHA256:", StringComparison.OrdinalIgnoreCase)
            ? "SHA256:" + value[7..]
            : string.IsNullOrWhiteSpace(value) ? string.Empty : $"SHA256:{value}";
    }

    private static KnownSshHostKey Clone(KnownSshHostKey source)
    {
        return new KnownSshHostKey
        {
            Host = source.Host,
            Port = source.Port,
            KeyType = source.KeyType,
            Fingerprint = source.Fingerprint,
            FirstSeenUtc = source.FirstSeenUtc,
            LastSeenUtc = source.LastSeenUtc
        };
    }
}
