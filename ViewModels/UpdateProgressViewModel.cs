using CommunityToolkit.Mvvm.ComponentModel;

namespace CxShell.ViewModels;

public partial class UpdateProgressViewModel : ObservableObject
{
    [ObservableProperty] private string _title = string.Empty;
    [ObservableProperty] private string _statusText = string.Empty;
    [ObservableProperty] private string _versionText = string.Empty;
    [ObservableProperty] private int _progress;
    [ObservableProperty] private bool _isIndeterminate = true;
    [ObservableProperty] private bool _canCancel = true;
    [ObservableProperty] private string _backgroundText = string.Empty;
    [ObservableProperty] private string _cancelText = string.Empty;

    public string ProgressText => IsIndeterminate ? string.Empty : $"{Progress}%";

    partial void OnProgressChanged(int value)
    {
        OnPropertyChanged(nameof(ProgressText));
    }

    partial void OnIsIndeterminateChanged(bool value)
    {
        OnPropertyChanged(nameof(ProgressText));
    }
}
