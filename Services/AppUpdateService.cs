using System.Net;
using System.Net.Http;
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

public sealed record MacInstallPermissionInfo(
    bool IsMacOs,
    string? AppBundlePath,
    string RecommendedUserApplicationsPath,
    bool IsSystemApplicationsInstall,
    bool CanWriteInstallDirectory)
{
    public bool MayRequireAdminPassword =>
        IsMacOs &&
        IsSystemApplicationsInstall &&
        !CanWriteInstallDirectory &&
        !string.IsNullOrWhiteSpace(AppBundlePath);
}

public sealed class AppUpdateService
{
    private const string ReleaseDownloadBaseUrl = "https://github.com/xiaochengzjc/CxShell/releases/latest/download";
    private const double UpdateDownloadTimeoutMinutes = 15;

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

    public MacInstallPermissionInfo GetMacInstallPermissionInfo()
    {
        var userApplicationsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Applications",
            "CxShell.app");

        if (!OperatingSystem.IsMacOS())
        {
            return new MacInstallPermissionInfo(
                false,
                null,
                userApplicationsPath,
                false,
                true);
        }

        var appBundlePath = FindCurrentMacAppBundlePath();
        var installDirectory = string.IsNullOrWhiteSpace(appBundlePath)
            ? null
            : Path.GetDirectoryName(appBundlePath);
        var isSystemApplicationsInstall =
            IsUnderDirectory(appBundlePath, "/Applications") ||
            IsUnderDirectory(appBundlePath, "/System/Applications");
        var canWriteInstallDirectory = string.IsNullOrWhiteSpace(installDirectory) ||
                                       CanWriteDirectory(installDirectory);

        return new MacInstallPermissionInfo(
            true,
            appBundlePath,
            userApplicationsPath,
            isSystemApplicationsInstall,
            canWriteInstallDirectory);
    }

    private static UpdateManager CreateManager(bool includePrerelease)
    {
        _ = includePrerelease;

        var source = new SimpleWebSource(
            ReleaseDownloadBaseUrl,
            new RetryingFileDownloader(),
            timeout: UpdateDownloadTimeoutMinutes);
        var options = new UpdateOptions
        {
            MaximumDeltasBeforeFallback = 5
        };
        return new UpdateManager(source, options);
    }

    private static string? FindCurrentMacAppBundlePath()
    {
        try
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory != null)
            {
                if (directory.Name.EndsWith(".app", StringComparison.OrdinalIgnoreCase))
                    return directory.FullName;

                directory = directory.Parent;
            }
        }
        catch
        {
            return null;
        }

        return null;
    }

    private static bool IsUnderDirectory(string? path, string root)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        try
        {
            var fullPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
            var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
            return string.Equals(fullPath, fullRoot, StringComparison.Ordinal) ||
                   fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private static bool CanWriteDirectory(string directory)
    {
        try
        {
            if (!Directory.Exists(directory))
                return false;

            var testFile = Path.Combine(directory, $".cxshell-write-test-{Guid.NewGuid():N}.tmp");
            using (File.Create(testFile, 1, FileOptions.DeleteOnClose))
            {
            }

            if (File.Exists(testFile))
                File.Delete(testFile);

            return true;
        }
        catch
        {
            return false;
        }
    }

    private sealed class RetryingFileDownloader : HttpClientFileDownloader
    {
        private const int MaxAttempts = 3;

        public override async Task DownloadFile(
            string url,
            string targetFile,
            Action<int> progress,
            IDictionary<string, string>? headers,
            double timeout,
            CancellationToken cancelToken = default)
        {
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    await base.DownloadFile(url, targetFile, progress, headers, timeout, cancelToken);
                    return;
                }
                catch (Exception ex) when (ShouldRetry(ex, cancelToken, attempt))
                {
                    TryDeletePartialFile(targetFile);
                    await Task.Delay(GetRetryDelay(attempt), cancelToken);
                }
            }
        }

        public override async Task<byte[]> DownloadBytes(
            string url,
            IDictionary<string, string>? headers,
            double timeout)
        {
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    return await base.DownloadBytes(url, headers, timeout);
                }
                catch (Exception ex) when (ShouldRetry(ex, CancellationToken.None, attempt))
                {
                    await Task.Delay(GetRetryDelay(attempt));
                }
            }
        }

        public override async Task<string> DownloadString(
            string url,
            IDictionary<string, string>? headers,
            double timeout)
        {
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    return await base.DownloadString(url, headers, timeout);
                }
                catch (Exception ex) when (ShouldRetry(ex, CancellationToken.None, attempt))
                {
                    await Task.Delay(GetRetryDelay(attempt));
                }
            }
        }

        private static bool ShouldRetry(Exception ex, CancellationToken cancellationToken, int attempt)
        {
            if (attempt >= MaxAttempts || cancellationToken.IsCancellationRequested)
                return false;

            if (ex is HttpRequestException { StatusCode: HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound })
                return false;

            return true;
        }

        private static TimeSpan GetRetryDelay(int attempt)
        {
            return TimeSpan.FromSeconds(Math.Min(2 * attempt, 8));
        }

        private static void TryDeletePartialFile(string path)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch
            {
                // A stale partial update file will be overwritten on the next retry.
            }
        }
    }
}
