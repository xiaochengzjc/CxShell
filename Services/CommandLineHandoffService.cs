using System;
using System.IO;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CxShell.Services;

public static class CommandLineHandoffService
{
    private const int ConnectTimeoutMilliseconds = 350;
    private static readonly string UserScope = BuildUserScope();
    private static readonly string PipeName = $"CxShell.CommandLineLaunch.v1.{UserScope}";
    private static readonly string MutexName = $"CxShell.CommandLineLaunch.Mutex.v1.{UserScope}";

    public static bool TrySendToExistingInstance(string[] args)
    {
        if (!ShouldForwardToExistingInstance(args))
            return false;

        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(ConnectTimeoutMilliseconds);

            using var writer = new StreamWriter(client, new UTF8Encoding(false), leaveOpen: true)
            {
                AutoFlush = true
            };
            writer.WriteLine(EncodePayload(args));
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static IDisposable StartServer(Func<string[], Task> handler)
    {
        var mutex = TryAcquireServerMutex();
        if (mutex == null)
            return NoopDisposable.Instance;

        var cts = new CancellationTokenSource();
        var task = Task.Run(() => RunServerAsync(handler, cts.Token));
        return new ServerLifetime(cts, mutex, task);
    }

    private static bool ShouldForwardToExistingInstance(string[] args)
    {
        if (args.Length == 0 || ContainsArgument(args, "--rdp-smoke"))
            return false;

        var options = CommandLineLaunchOptions.Parse(args);
        return options.HasCommand && !options.NewWindow;
    }

    private static async Task RunServerAsync(Func<string[], Task> handler, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using var server = CreateServerStream();
                await server.WaitForConnectionAsync(cancellationToken);

                using var reader = new StreamReader(server, Encoding.UTF8, leaveOpen: true);
                var line = await reader.ReadLineAsync(cancellationToken);
                if (TryDecodePayload(line, out var args))
                    await handler(args);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                await Task.Delay(200, cancellationToken).ContinueWith(_ => { }, TaskScheduler.Default);
            }
        }
    }

    private static NamedPipeServerStream CreateServerStream()
    {
        return new NamedPipeServerStream(
            PipeName,
            PipeDirection.In,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);
    }

    private static Mutex? TryAcquireServerMutex()
    {
        try
        {
            var mutex = new Mutex(true, MutexName, out var createdNew);
            if (createdNew)
                return mutex;

            mutex.Dispose();
        }
        catch
        {
            // If mutex creation fails, avoid starting a second command receiver.
        }

        return null;
    }

    private static bool ContainsArgument(string[] args, string expected)
    {
        foreach (var arg in args)
        {
            if (string.Equals(arg, expected, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static string EncodePayload(string[] args)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(new HandoffPayload(args));
        return Convert.ToBase64String(json);
    }

    private static bool TryDecodePayload(string? payload, out string[] args)
    {
        args = [];
        if (string.IsNullOrWhiteSpace(payload))
            return false;

        try
        {
            var json = Convert.FromBase64String(payload.Trim());
            var decoded = JsonSerializer.Deserialize<HandoffPayload>(json);
            if (decoded?.Args is not { Length: > 0 } decodedArgs)
                return false;

            args = decodedArgs;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string BuildUserScope()
    {
        var identity = $"{Environment.UserDomainName}\\{Environment.UserName}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(identity));
        return Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }

    private sealed record HandoffPayload(string[] Args);

    private sealed class ServerLifetime(
        CancellationTokenSource cancellationTokenSource,
        Mutex mutex,
        Task serverTask) : IDisposable
    {
        public void Dispose()
        {
            cancellationTokenSource.Cancel();
            cancellationTokenSource.Dispose();

            try
            {
                mutex.ReleaseMutex();
            }
            catch
            {
                // Ignore shutdown races.
            }

            mutex.Dispose();
            _ = serverTask;
        }
    }

    private sealed class NoopDisposable : IDisposable
    {
        public static readonly NoopDisposable Instance = new();
        public void Dispose()
        {
        }
    }
}
