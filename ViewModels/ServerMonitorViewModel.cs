using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.ObjectModel;
using Avalonia.Threading;
using CxShell.Models;
using CxShell.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CxShell.ViewModels;

public partial class ServerMonitorViewModel : ObservableObject, IDisposable
{
    private readonly ServerMonitorService _service = new();
    private LocalizationService L => LocalizationService.Shared;

    [ObservableProperty] private bool _isMonitoring;
    [ObservableProperty] private string? _errorMessage;
    [ObservableProperty] private double _cpuTotalUsage;
    [ObservableProperty] private MemoryInfo? _memory;
    [ObservableProperty] private NetworkSpeed? _currentNetworkSpeed;
    [ObservableProperty] private string _hostLabel = LocalizationService.Shared.Text("Monitor.NotConnected");

    public string MonitorTitleText => L.Text("UiText.001");
    public string MonitorStatusText => IsMonitoring
        ? L.Text("Monitor.StatusRunning")
        : L.Text("Monitor.StatusStopped");
    public string MemoryTitleText => L.Text("UiText.002");
    public string NetworkTitleText => L.Text("UiText.003");
    public string DownloadText => L.Text("UiText.004");
    public string UploadText => L.Text("UiText.005");
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
        bool isWindowsOpenSsh = false)
    {
        _service.Stop();
        IsMonitoring = false;
        ErrorMessage = null;
        HostLabel = $"{session.Username}@{session.Host}";
        ClearData();

        _ = _service.StartAsync(session, password, commandRunner, isWindowsOpenSsh);

        IsMonitoring = true;
    }

    public void StopMonitoring()
    {
        _service.Stop();
        IsMonitoring = false;
        HostLabel = LocalizationService.Shared.Text("Monitor.NotConnected");
        ClearData();
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

    private void NotifyLocalizedPropertiesChanged()
    {
        OnPropertyChanged(nameof(MonitorTitleText));
        OnPropertyChanged(nameof(MonitorStatusText));
        OnPropertyChanged(nameof(MemoryTitleText));
        OnPropertyChanged(nameof(NetworkTitleText));
        OnPropertyChanged(nameof(DownloadText));
        OnPropertyChanged(nameof(UploadText));
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
        CpuTotalUsage = 0;
    }

    private void OnError(string message)
    {
        Dispatcher.UIThread.Post(() =>
        {
            ErrorMessage = message;
            IsMonitoring = false;
        });
    }

    private void OnDataUpdated(MonitorSnapshot snapshot)
    {
        Dispatcher.UIThread.Post(() =>
        {
            ErrorMessage = null;

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
        _service.DataUpdated -= OnDataUpdated;
        _service.ErrorOccurred -= OnError;
        LocalizationService.Shared.LanguageChanged -= OnLanguageChanged;
        _service.Dispose();
    }
}
