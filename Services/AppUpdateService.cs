using Velopack;
using Velopack.Sources;

namespace CxShell.Services;

public enum AppUpdateCheckStatus
{
    NotInstalled,
    NoUpdate,
    UpdateAvailable,
    PendingRestart,
    Failed
}

public sealed class AppUpdateHandle
{
    private readonly UpdateInfo? _updateInfo;

    internal AppUpdateHandle(UpdateManager manager, UpdateInfo? updateInfo, VelopackAsset targetAsset)
    {
        Manager = manager;
        _updateInfo = updateInfo;
        TargetAsset = targetAsset;
    }

    internal UpdateManager Manager { get; }
    internal VelopackAsset TargetAsset { get; }

    public string CurrentVersion => Manager.CurrentVersion?.ToString() ?? "unknown";
    public string TargetVersion => TargetAsset.Version.ToString();
    public bool RequiresDownload => _updateInfo != null;
    public string? ReleaseNotes => string.IsNullOrWhiteSpace(TargetAsset.NotesMarkdown)
        ? null
        : TargetAsset.NotesMarkdown;

    internal UpdateInfo RequireUpdateInfo()
    {
        return _updateInfo ?? throw new InvalidOperationException("This update is already downloaded and pending restart.");
    }
}

public sealed record AppUpdateCheckResult(
    AppUpdateCheckStatus Status,
    AppUpdateHandle? Update,
    string? ErrorMessage = null);

public sealed class AppUpdateService
{
    private const string RepositoryUrl = "https://github.com/xiaochengzjc/CxShell";

    public async Task<AppUpdateCheckResult> CheckForUpdatesAsync(
        bool includePrerelease,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var manager = CreateManager(includePrerelease);
            if (!manager.IsInstalled)
                return new AppUpdateCheckResult(AppUpdateCheckStatus.NotInstalled, null);

            if (manager.UpdatePendingRestart is { } pending)
            {
                return new AppUpdateCheckResult(
                    AppUpdateCheckStatus.PendingRestart,
                    new AppUpdateHandle(manager, null, pending));
            }

            cancellationToken.ThrowIfCancellationRequested();
            var updateInfo = await manager.CheckForUpdatesAsync();
            if (updateInfo == null)
                return new AppUpdateCheckResult(AppUpdateCheckStatus.NoUpdate, null);

            return new AppUpdateCheckResult(
                AppUpdateCheckStatus.UpdateAvailable,
                new AppUpdateHandle(manager, updateInfo, updateInfo.TargetFullRelease));
        }
        catch (Exception ex)
        {
            return new AppUpdateCheckResult(AppUpdateCheckStatus.Failed, null, ex.Message);
        }
    }

    public async Task DownloadUpdatesAsync(
        AppUpdateHandle update,
        Action<int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        await update.Manager.DownloadUpdatesAsync(update.RequireUpdateInfo(), progress, cancellationToken);
    }

    public void ApplyUpdatesAndRestart(AppUpdateHandle update, string[]? restartArgs = null)
    {
        update.Manager.ApplyUpdatesAndRestart(update.TargetAsset, restartArgs ?? Array.Empty<string>());
    }

    private static UpdateManager CreateManager(bool includePrerelease)
    {
        var source = new GithubSource(RepositoryUrl, accessToken: null, prerelease: includePrerelease);
        var options = new UpdateOptions
        {
            MaximumDeltasBeforeFallback = 5
        };
        return new UpdateManager(source, options);
    }
}
