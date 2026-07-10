using System;
using Renci.SshNet;

namespace CxShell.Services;

public sealed class SshConnectionContext(ConnectionInfo connectionInfo, IDisposable? proxyLifetime = null) : IDisposable
{
    public ConnectionInfo ConnectionInfo { get; } = connectionInfo;

    public void Dispose()
    {
        proxyLifetime?.Dispose();
    }
}
