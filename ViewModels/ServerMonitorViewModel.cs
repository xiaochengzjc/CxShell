using System;
using System.Collections.ObjectModel;
using Avalonia.Threading;
using ChiXueSsh.Models;
using ChiXueSsh.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ChiXueSsh.ViewModels;

public partial class ServerMonitorViewModel : ObservableObject, IDisposable
{
    private readonly ServerMonitorService _service = new();

    [ObservableProperty] private bool _isMonitoring;
    [ObservableProperty] private string? _errorMessage;
    [ObservableProperty] private double _cpuTotalUsage;
    [ObservableProperty] private MemoryInfo? _memory;
    [ObservableProperty] private NetworkSpeed? _currentNetworkSpeed;
    [ObservableProperty] private string _hostLabel = "未连接";

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
    }

    public void SwitchConnection(SessionInfo session, string? password)
    {
        _service.Stop();
        IsMonitoring = false;
        ErrorMessage = null;
        HostLabel = $"{session.Username}@{session.Host}";
        ClearData();

        _ = _service.StartAsync(session, password);

        IsMonitoring = true;
    }

    public void StopMonitoring()
    {
        _service.Stop();
        IsMonitoring = false;
        HostLabel = "未连接";
        ClearData();
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
        _service.Dispose();
    }
}
