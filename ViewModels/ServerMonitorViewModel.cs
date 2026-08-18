using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;
using Avalonia.Threading;
using CxShell.Models;
using CxShell.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CxShell.ViewModels;

public partial class ServerMonitorViewModel : ObservableObject, IDisposable
{
    private readonly ServerMonitorService _service = new();
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private LocalizationService L => LocalizationService.Shared;
    private string? _currentTargetKey;
    private long _monitorGeneration;
    private readonly LatestRequestVersion _switchRequests = new();
    private bool _isDisposed;

    [ObservableProperty] private bool _isMonitoring;
    [ObservableProperty] private string? _errorMessage;
    [ObservableProperty] private double _cpuTotalUsage;
    [ObservableProperty] private MemoryInfo? _memory;
    [ObservableProperty] private NetworkSpeed? _currentNetworkSpeed;
    [ObservableProperty] private double? _networkLatencyMilliseconds;
    [ObservableProperty] private string _hostLabel = LocalizationService.Shared.Text("Monitor.NotConnected");

    public string MonitorTitleText => L.Text("UiText.001");
    public string MonitorStatusText => IsMonitoring
        ? L.Text("Monitor.StatusRunning")
        : L.Text("Monitor.StatusStopped");
    public string MemoryTitleText => L.Text("UiText.002");
    public string NetworkTitleText => L.Text("UiText.003");
    public string DownloadText => L.Text("UiText.004");
    public string UploadText => L.Text("UiText.005");
    public string LatencyText => L.Text("Monitor.Latency");
    public string LatencyDisplay => NetworkLatencyMilliseconds.HasValue
        ? $"{NetworkLatencyMilliseconds.Value:F0} ms"
        : "--";
    public string DiskTitleText => L.Text("UiText.006");
    public string DiskIoTitleText => L.Text("UiText.007");
    public string ReadText => L.Text("UiText.008");
    public string WriteText => L.Text("UiText.009");
    public string MemoryUsedDisplay => Memory == null
        ? string.Empty
        : string.Format(L.Text("Monitor.MemoryUsedFormat"), Memory.UsedFormatted);
    public string MemoryCachedDisplay => Memory == null
        ? string.Empty
        : string.Format(L.Text("Monitor.MemoryCachedFormat"), Memory.CachedFormatted);
    public string MemoryFreeDisplay => Memory == null
        ? string.Empty
        : string.Format(L.Text("Monitor.MemoryFreeFormat"), Memory.FreeFormatted);
    public string MemoryTotalDisplay => Memory == null
        ? string.Empty
        : string.Format(L.Text("Monitor.MemoryTotalFormat"), Memory.TotalFormatted);

    public ObservableCollection<CpuCoreInfo> CpuCores { get; } = new();
    public ObservableCollection<NetworkSpeed> NetworkHistory { get; } = new();
    public ObservableCollection<DiskPartitionInfo> DiskPartitions { get; } = new();
    public ObservableCollection<DiskIoInfo> DiskIo { get; } = new();

    // 折线图用的简化数值序列（最近 60 个点）
    public ObservableCollection<double> RxHistory { get; } = new();
    public ObservableCollection<double> TxHistory { get; } = new();

    public ServerMonitorViewModel()
    {
        _service.DataUpdated += OnDataUpdated;
        _service.ErrorOccurred += OnError;
        LocalizationService.Shared.LanguageChanged += OnLanguageChanged;
    }

    public void SwitchConnection(
        SessionInfo session,
        string? password,
        Func<string, TimeSpan, CancellationToken, Task<string>>? commandRunner = null,
        bool isWindowsOpenSsh = false,
        int refreshIntervalSeconds = SessionInfo.DefaultSshMonitorRefreshIntervalSeconds,
        bool enableNetworkLatencyProbe = false)
    {
        _ = SwitchConnectionAsync(
            session,
            password,
            commandRunner,
            isWindowsOpenSsh,
            refreshIntervalSeconds,
            enableNetworkLatencyProbe);
    }

    public async Task SwitchConnectionAsync(
        SessionInfo session,
        string? password,
        Func<string, TimeSpan, CancellationToken, Task<string>>? commandRunner = null,
        bool isWindowsOpenSsh = false,
        int refreshIntervalSeconds = SessionInfo.DefaultSshMonitorRefreshIntervalSeconds,
        bool enableNetworkLatencyProbe = false)
    {
        var requestVersion = _switchRequests.Begin();
        var effectiveRefreshInterval = Math.Clamp(
            refreshIntervalSeconds,
            SessionInfo.MinSshMonitorRefreshIntervalSeconds,
            SessionInfo.MaxSshMonitorRefreshIntervalSeconds);
        var targetKey = BuildTargetKey(
            session,
            commandRunner,
            isWindowsOpenSsh,
            effectiveRefreshInterval,
            enableNetworkLatencyProbe);
        await _lifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_isDisposed ||
                !_switchRequests.IsCurrent(requestVersion) ||
                (IsMonitoring && string.Equals(_currentTargetKey, targetKey, StringComparison.Ordinal)))
            {
                return;
            }

            // ServerMonitorService.Stop waits for an in-flight remote command. Keep
            // that wait off the UI thread before the next target replaces the service.
            await Task.Run(_service.Stop).ConfigureAwait(false);

            // A newer tab selection may have arrived while the old monitor command
            // was stopping. Skip the intermediate target instead of reconnecting it.
            if (!_switchRequests.IsCurrent(requestVersion))
                return;

            var generation = Interlocked.Increment(ref _monitorGeneration);
            _currentTargetKey = targetKey;
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (IsCurrentGeneration(generation))
                {
                    ClearData();
                    ErrorMessage = null;
                    HostLabel = $"{session.Username}@{session.Host}";
                    IsMonitoring = true;
                }
            });

            if (!IsCurrentGeneration(generation))
                return;

            try
            {
                await _service.StartAsync(
                        session,
                        password,
                        commandRunner,
                        isWindowsOpenSsh,
                        effectiveRefreshInterval,
                        enableNetworkLatencyProbe,
                        generation)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (!IsCurrentGeneration(generation))
                        return;

                    ErrorMessage = ex.Message;
                    IsMonitoring = false;
                    _currentTargetKey = null;
                });
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public void StopMonitoring()
    {
        _ = StopMonitoringAsync();
    }

    public async Task StopMonitoringAsync()
    {
        await StopMonitoringAsync(invalidateSwitchRequests: true).ConfigureAwait(false);
    }

    private async Task StopMonitoringAsync(bool invalidateSwitchRequests)
    {
        if (invalidateSwitchRequests)
            _switchRequests.Invalidate();

        await _lifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_isDisposed)
                return;

            await Task.Run(_service.Stop).ConfigureAwait(false);

            var generation = Interlocked.Increment(ref _monitorGeneration);
            _currentTargetKey = null;
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (!IsCurrentGeneration(generation))
                    return;

                IsMonitoring = false;
                HostLabel = LocalizationService.Shared.Text("Monitor.NotConnected");
                ClearData();
            });
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private bool IsCurrentGeneration(long generation)
    {
        return !_isDisposed && generation == Volatile.Read(ref _monitorGeneration);
    }

    private static string BuildTargetKey(
        SessionInfo session,
        Func<string, TimeSpan, CancellationToken, Task<string>>? commandRunner,
        bool isWindowsOpenSsh,
        int refreshIntervalSeconds,
        bool enableNetworkLatencyProbe)
    {
        var runnerTargetId = commandRunner?.Target == null
            ? "direct"
            : RuntimeHelpers.GetHashCode(commandRunner.Target).ToString();
        var runnerMethod = commandRunner?.Method.Name ?? "ssh";
        var targetKind = isWindowsOpenSsh ? "windows" : "linux";

        return string.Join('|',
            session.Id,
            session.Protocol,
            session.Username ?? string.Empty,
            session.Host ?? string.Empty,
            session.Port,
            targetKind,
            refreshIntervalSeconds,
            enableNetworkLatencyProbe,
            runnerMethod,
            runnerTargetId);
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        if (!IsMonitoring)
            HostLabel = LocalizationService.Shared.Text("Monitor.NotConnected");

        NotifyLocalizedPropertiesChanged();
        RefreshCpuCoreLabels();
    }

    partial void OnIsMonitoringChanged(bool value)
    {
        OnPropertyChanged(nameof(MonitorStatusText));
    }

    partial void OnMemoryChanged(MemoryInfo? value)
    {
        NotifyMemoryDisplayChanged();
    }

    partial void OnNetworkLatencyMillisecondsChanged(double? value)
    {
        OnPropertyChanged(nameof(LatencyDisplay));
    }

    private void NotifyLocalizedPropertiesChanged()
    {
        OnPropertyChanged(nameof(MonitorTitleText));
        OnPropertyChanged(nameof(MonitorStatusText));
        OnPropertyChanged(nameof(MemoryTitleText));
        OnPropertyChanged(nameof(NetworkTitleText));
        OnPropertyChanged(nameof(DownloadText));
        OnPropertyChanged(nameof(UploadText));
        OnPropertyChanged(nameof(LatencyText));
        OnPropertyChanged(nameof(LatencyDisplay));
        OnPropertyChanged(nameof(DiskTitleText));
        OnPropertyChanged(nameof(DiskIoTitleText));
        OnPropertyChanged(nameof(ReadText));
        OnPropertyChanged(nameof(WriteText));
        NotifyMemoryDisplayChanged();
    }

    private void NotifyMemoryDisplayChanged()
    {
        OnPropertyChanged(nameof(MemoryUsedDisplay));
        OnPropertyChanged(nameof(MemoryCachedDisplay));
        OnPropertyChanged(nameof(MemoryFreeDisplay));
        OnPropertyChanged(nameof(MemoryTotalDisplay));
    }

    private void RefreshCpuCoreLabels()
    {
        if (CpuCores.Count == 0)
            return;

        var cores = CpuCores.ToArray();
        CpuCores.Clear();
        foreach (var core in cores)
            CpuCores.Add(core);
    }

    private void ClearData()
    {
        CpuCores.Clear();
        NetworkHistory.Clear();
        RxHistory.Clear();
        TxHistory.Clear();
        DiskPartitions.Clear();
        DiskIo.Clear();
        Memory = null;
        CurrentNetworkSpeed = null;
        NetworkLatencyMilliseconds = null;
        CpuTotalUsage = 0;
    }

    private void OnError(string message, long generation, bool isFatal)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (!IsCurrentGeneration(generation))
                return;

            ErrorMessage = message;
            if (isFatal)
            {
                IsMonitoring = false;
                _currentTargetKey = null;
                ClearData();
            }
        });
    }

    private void OnDataUpdated(MonitorSnapshot snapshot, long generation)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (!IsCurrentGeneration(generation))
                return;

            ErrorMessage = null;
            IsMonitoring = true;

            // CPU
            CpuCores.Clear();
            foreach (var core in snapshot.CpuCores)
                CpuCores.Add(core);
            CpuTotalUsage = snapshot.CpuCores.Count > 0 ? snapshot.CpuCores[0].UsagePercent : 0;

            // Memory
            Memory = snapshot.Memory;

            // Network
            if (snapshot.NetworkSpeed != null)
            {
                CurrentNetworkSpeed = snapshot.NetworkSpeed;

                NetworkHistory.Add(snapshot.NetworkSpeed);
                if (NetworkHistory.Count > 60)
                    NetworkHistory.RemoveAt(0);

                RxHistory.Add(snapshot.NetworkSpeed.RxBytesPerSec);
                if (RxHistory.Count > 60) RxHistory.RemoveAt(0);

                TxHistory.Add(snapshot.NetworkSpeed.TxBytesPerSec);
                if (TxHistory.Count > 60) TxHistory.RemoveAt(0);
            }

            NetworkLatencyMilliseconds = snapshot.NetworkLatencyMilliseconds;

            // Disk partitions
            DiskPartitions.Clear();
            foreach (var p in snapshot.DiskPartitions)
                DiskPartitions.Add(p);

            // Disk IO
            DiskIo.Clear();
            foreach (var d in snapshot.DiskIo)
                DiskIo.Add(d);
        });
    }

    public void Dispose()
    {
        _isDisposed = true;
        Interlocked.Increment(ref _monitorGeneration);
        _service.DataUpdated -= OnDataUpdated;
        _service.ErrorOccurred -= OnError;
        LocalizationService.Shared.LanguageChanged -= OnLanguageChanged;
        _ = DisposeServiceAsync();
    }

    private async Task DisposeServiceAsync()
    {
        await _lifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await Task.Run(_service.Stop).ConfigureAwait(false);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }
}
