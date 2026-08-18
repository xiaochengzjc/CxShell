using System.Threading;

namespace CxShell.Services;

/// <summary>
/// Tracks the newest lifecycle request so stale asynchronous work can exit before
/// replacing a connection selected by the user more recently.
/// </summary>
public sealed class LatestRequestVersion
{
    private long _version;

    public long Begin()
    {
        return Interlocked.Increment(ref _version);
    }

    public void Invalidate()
    {
        Interlocked.Increment(ref _version);
    }

    public bool IsCurrent(long version)
    {
        return version == Volatile.Read(ref _version);
    }
}
