using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using CxShell.Models;
using Renci.SshNet;

namespace CxShell.Services;

public sealed class ConnectionDiagnosticsService
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan BannerTimeout = TimeSpan.FromSeconds(5);

    public async Task<ConnectionDiagnosticReport> DiagnoseAsync(
        SessionInfo session,
        string? password,
        IProgress<ConnectionDiagnosticStepUpdate>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);

        var results = new List<ConnectionDiagnosticStepUpdate>
        {
            new(0, Text("Diagnostics.StepDns"), ConnectionDiagnosticStepStatus.Pending),
            new(1, Text("Diagnostics.StepTcp"), ConnectionDiagnosticStepStatus.Pending),
            new(2, Text("Diagnostics.StepBanner"), ConnectionDiagnosticStepStatus.Pending),
            new(3, Text("Diagnostics.StepAuthentication"), ConnectionDiagnosticStepStatus.Pending)
        };
        var endpoint = ResolveEntryEndpoint(session);

        if (session.Protocol != SessionProtocol.SSH)
        {
            var detail = Text("Diagnostics.SshOnly");
            for (var index = 0; index < results.Count; index++)
                Publish(results, progress, index, ConnectionDiagnosticStepStatus.Skipped, detail: detail);

            return new ConnectionDiagnosticReport
            {
                Steps = results,
                IssueTitle = Text("Diagnostics.UnsupportedTitle"),
                IssueDescription = detail,
                Suggestions = [Text("Diagnostics.SshOnlySuggestion")]
            };
        }

        IPAddress? address = null;
        string? issueTitle = null;
        string? issueDescription = null;
        var suggestions = new List<string>();

        Publish(results, progress, 0, ConnectionDiagnosticStepStatus.Running);
        var stopwatch = Stopwatch.StartNew();
        try
        {
            address = await ResolveAddressAsync(endpoint.Host, session.AdvancedIpVersion, cancellationToken)
                .ConfigureAwait(false);
            Publish(
                results,
                progress,
                0,
                ConnectionDiagnosticStepStatus.Success,
                stopwatch.ElapsedMilliseconds,
                $"{endpoint.Host} -> {address}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var detail = Trim(ex.Message);
            Publish(results, progress, 0, ConnectionDiagnosticStepStatus.Failed, stopwatch.ElapsedMilliseconds, detail);
            issueTitle = Text("Diagnostics.DnsFailed");
            issueDescription = string.Format(Text("Diagnostics.DnsFailedDescription"), endpoint.Host, detail);
            suggestions.Add(Text("Diagnostics.CheckHost"));
            suggestions.Add(Text("Diagnostics.CheckDns"));
            SkipRemaining(results, progress, 1, Text("Diagnostics.PreviousStepFailed"));
        }

        if (address != null)
        {
            Publish(results, progress, 1, ConnectionDiagnosticStepStatus.Running);
            stopwatch.Restart();
            try
            {
                using var tcp = await ConnectTcpAsync(session, endpoint.Host, endpoint.Port, address, cancellationToken).ConfigureAwait(false);
                Publish(
                    results,
                    progress,
                    1,
                    ConnectionDiagnosticStepStatus.Success,
                    stopwatch.ElapsedMilliseconds,
                    $"{endpoint.Host}:{endpoint.Port}");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                var detail = Trim(ex.Message);
                Publish(results, progress, 1, ConnectionDiagnosticStepStatus.Failed, stopwatch.ElapsedMilliseconds, detail);
                issueTitle ??= Text("Diagnostics.TcpFailed");
                issueDescription ??= string.Format(Text("Diagnostics.TcpFailedDescription"), endpoint.Host, endpoint.Port, detail);
                suggestions.Add(Text("Diagnostics.CheckFirewall"));
                suggestions.Add(Text("Diagnostics.CheckServerOnline"));
                SkipRemaining(results, progress, 2, Text("Diagnostics.PreviousStepFailed"));
            }
        }

        if (address != null && results[1].Status == ConnectionDiagnosticStepStatus.Success)
        {
            Publish(results, progress, 2, ConnectionDiagnosticStepStatus.Running);
            stopwatch.Restart();
            try
            {
                using var tcp = await ConnectTcpAsync(session, endpoint.Host, endpoint.Port, address, cancellationToken).ConfigureAwait(false);
                var banner = await ReadSshBannerAsync(tcp.GetStream(), cancellationToken).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(banner) ||
                    !banner.StartsWith("SSH-", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException(string.IsNullOrWhiteSpace(banner)
                        ? Text("Diagnostics.EmptyBanner")
                        : banner);
                }

                Publish(results, progress, 2, ConnectionDiagnosticStepStatus.Success, stopwatch.ElapsedMilliseconds, banner);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                var detail = Trim(ex.Message);
                Publish(results, progress, 2, ConnectionDiagnosticStepStatus.Failed, stopwatch.ElapsedMilliseconds, detail);
                issueTitle ??= Text("Diagnostics.BannerFailed");
                issueDescription ??= string.Format(Text("Diagnostics.BannerFailedDescription"), session.Host, detail);
                suggestions.Add(Text("Diagnostics.CheckSshService"));
                SkipRemaining(results, progress, 3, Text("Diagnostics.PreviousStepFailed"));
            }
        }

        if (address != null && results[1].Status == ConnectionDiagnosticStepStatus.Success &&
            results[2].Status == ConnectionDiagnosticStepStatus.Success)
        {
            Publish(results, progress, 3, ConnectionDiagnosticStepStatus.Running);
            stopwatch.Restart();
            try
            {
                await AuthenticateAsync(session, password, cancellationToken).ConfigureAwait(false);
                Publish(results, progress, 3, ConnectionDiagnosticStepStatus.Success, stopwatch.ElapsedMilliseconds, Text("Diagnostics.AuthenticationPassed"));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                var detail = SshServerInfo.BuildConnectionErrorMessage(ex);
                Publish(results, progress, 3, ConnectionDiagnosticStepStatus.Failed, stopwatch.ElapsedMilliseconds, detail);
                issueTitle ??= Text("Diagnostics.AuthenticationFailed");
                issueDescription ??= string.Format(Text("Diagnostics.AuthenticationFailedDescription"), detail);
                suggestions.Add(Text("Diagnostics.CheckCredentials"));
                suggestions.Add(Text("Diagnostics.CheckSshAlgorithms"));
            }
        }

        var success = issueTitle == null && results.All(step => step.Status == ConnectionDiagnosticStepStatus.Success);
        return new ConnectionDiagnosticReport
        {
            Steps = results,
            Success = success,
            IssueTitle = issueTitle,
            IssueDescription = issueDescription,
            Suggestions = suggestions.Distinct(StringComparer.CurrentCulture).ToList()
        };
    }

    private static async Task<IPAddress> ResolveAddressAsync(
        string host,
        string ipVersion,
        CancellationToken cancellationToken)
    {
        if (IPAddress.TryParse(host, out var literal))
            return literal;

        var family = string.Equals(ipVersion, "IPv6", StringComparison.OrdinalIgnoreCase)
            ? AddressFamily.InterNetworkV6
            : string.Equals(ipVersion, "IPv4", StringComparison.OrdinalIgnoreCase)
                ? AddressFamily.InterNetwork
                : AddressFamily.Unspecified;
        var addresses = await Dns.GetHostAddressesAsync(host, family, cancellationToken)
            .WaitAsync(ConnectTimeout, cancellationToken)
            .ConfigureAwait(false);
        return addresses.FirstOrDefault(address => address.AddressFamily == AddressFamily.InterNetwork) ??
               addresses.FirstOrDefault() ??
               throw new SocketException((int)SocketError.HostNotFound);
    }

    private static async Task<TcpClient> ConnectTcpAsync(
        SessionInfo session,
        string host,
        int port,
        IPAddress address,
        CancellationToken cancellationToken)
    {
        if (session.Proxy?.IsEnabled == true && session.Proxy.Protocol != ProxyProtocol.JumpHost)
        {
            return await ProxyConnectionFactory.ConnectTcpAsync(
                    host,
                    port,
                    session.Proxy,
                    cancellationToken,
                    session.AdvancedIpVersion)
                .WaitAsync(ConnectTimeout, cancellationToken)
                .ConfigureAwait(false);
        }

        var client = new TcpClient(address.AddressFamily);
        try
        {
            await client.ConnectAsync(address, port, cancellationToken)
                .AsTask()
                .WaitAsync(ConnectTimeout, cancellationToken)
                .ConfigureAwait(false);
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private static (string Host, int Port) ResolveEntryEndpoint(SessionInfo session)
    {
        var proxy = session.Proxy;
        if (proxy?.IsEnabled != true || proxy.Protocol != ProxyProtocol.JumpHost)
            return (session.Host, session.Port);

        var proxiesById = session.ProxyServers
            .Where(item => item.IsEnabled)
            .GroupBy(item => item.Id)
            .ToDictionary(group => group.Key, group => group.First());
        var visited = new HashSet<Guid>();
        while (proxy.IsEnabled && proxy.Protocol == ProxyProtocol.JumpHost && visited.Add(proxy.Id) &&
               proxy.NextProxyId is { } nextId && proxiesById.TryGetValue(nextId, out var nextProxy))
        {
            proxy = nextProxy;
        }

        return (string.IsNullOrWhiteSpace(proxy.Host) ? session.Host : proxy.Host, proxy.Port > 0 ? proxy.Port : 22);
    }

    private static async Task<string?> ReadSshBannerAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(BannerTimeout);
        var buffer = new byte[256];
        var received = new List<byte>(buffer.Length);
        while (received.Count < 8192)
        {
            var count = await stream.ReadAsync(buffer.AsMemory(), timeout.Token).ConfigureAwait(false);
            if (count == 0)
                break;

            received.AddRange(buffer.AsSpan(0, count).ToArray());
            var newline = received.IndexOf((byte)'\n');
            if (newline >= 0)
                break;
        }

        var text = System.Text.Encoding.ASCII.GetString(received.ToArray());
        return text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(line => line.StartsWith("SSH-", StringComparison.OrdinalIgnoreCase));
    }

    private static async Task AuthenticateAsync(SessionInfo session, string? password, CancellationToken cancellationToken)
    {
        var authMethods = SshAgentAuthService.CreateAuthenticationMethods(session, password);
        using var context = await Task.Run(
                () => ProxyConnectionFactory.CreateSshConnectionContext(session, authMethods),
                cancellationToken)
            .WaitAsync(ConnectTimeout, cancellationToken)
            .ConfigureAwait(false);
        var connectionInfo = context.ConnectionInfo;
        SshAlgorithmPreferenceService.Apply(connectionInfo, session);
        using var client = new SshClient(connectionInfo);
        SshHostKeyTrustService.Shared.Attach(
            client,
            session.Host,
            session.Port,
            session.SshAcceptAndSaveHostKey);

        await Task.Run(client.Connect, cancellationToken)
            .WaitAsync(ConnectTimeout, cancellationToken)
            .ConfigureAwait(false);
        client.Disconnect();
    }

    private static void Publish(
        IList<ConnectionDiagnosticStepUpdate> steps,
        IProgress<ConnectionDiagnosticStepUpdate>? progress,
        int index,
        ConnectionDiagnosticStepStatus status,
        long? elapsedMilliseconds = null,
        string? detail = null)
    {
        var update = steps[index] with
        {
            Status = status,
            ElapsedMilliseconds = elapsedMilliseconds,
            Detail = detail
        };
        steps[index] = update;
        progress?.Report(update);
    }

    private static void SkipRemaining(
        IList<ConnectionDiagnosticStepUpdate> steps,
        IProgress<ConnectionDiagnosticStepUpdate>? progress,
        int startIndex,
        string detail)
    {
        for (var index = startIndex; index < steps.Count; index++)
            Publish(steps, progress, index, ConnectionDiagnosticStepStatus.Skipped, detail: detail);
    }

    private static string Text(string key) => LocalizationService.Shared.Text(key);

    private static string Trim(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return Text("Diagnostics.UnknownError");

        var normalized = value.Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal)
            .Trim();
        return normalized.Length <= 220 ? normalized : normalized[..220] + "...";
    }
}
