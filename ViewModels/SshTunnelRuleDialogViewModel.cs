using System.Collections.ObjectModel;
using AtomUI.Controls;
using AtomUI.Controls.Primitives;
using AtomUI.Desktop.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CxShell.Models;
using CxShell.Services;

namespace CxShell.ViewModels;

public sealed partial class SshTunnelRuleDialogViewModel : ObservableObject
{
    private readonly Guid _ruleId;

    [ObservableProperty] private ISelectOption? _selectedTypeOption;
    [ObservableProperty] private string _sourceHost = "localhost";
    [ObservableProperty] private string _listenPort = string.Empty;
    [ObservableProperty] private bool _acceptLocalConnectionsOnly = true;
    [ObservableProperty] private string _destinationHost = "localhost";
    [ObservableProperty] private string _destinationPort = string.Empty;
    [ObservableProperty] private string _description = string.Empty;
    [ObservableProperty] private string _validationMessage = string.Empty;

    public ObservableCollection<ISelectOption> TypeOptions { get; } =
    [
        new SelectOption { Header = Text("TunnelCenter.TypeLocal"), Content = SshTunnelRuleType.Local.ToString() },
        new SelectOption { Header = Text("TunnelCenter.TypeRemote"), Content = SshTunnelRuleType.Remote.ToString() },
        new SelectOption { Header = "Dynamic (SOCKS4/5)", Content = SshTunnelRuleType.Dynamic.ToString() }
    ];

    public bool IsDynamic => SelectedType == SshTunnelRuleType.Dynamic;
    public bool IsRemote => SelectedType == SshTunnelRuleType.Remote;
    public bool ShowDestination => !IsDynamic;
    public bool IsLocalOnlyEnabled => !IsRemote;
    public bool HasValidationMessage => !string.IsNullOrWhiteSpace(ValidationMessage);
    public bool IsEditing => _ruleId != Guid.Empty;

    public string TitleText => Text(IsEditing ? "TunnelCenter.EditRule" : "TunnelCenter.AddRule");
    public string TypeText => Text("TunnelCenter.RuleType");
    public string SourceHostText => Text("TunnelCenter.SourceHost");
    public string ListenPortText => Text("TunnelCenter.ListenPort");
    public string AcceptLocalOnlyText => Text("TunnelCenter.AcceptLocalOnly");
    public string DestinationHostText => Text("TunnelCenter.DestinationHost");
    public string DestinationPortText => Text("TunnelCenter.DestinationPort");
    public string DescriptionText => Text("TunnelCenter.RuleDescription");
    public string SaveText => Text("TunnelCenter.SaveRule");
    public string CancelText => Text("TunnelCenter.Cancel");
    public string SourceHostHintText => Text("TunnelCenter.SourceHostHint");
    public string DestinationHostHintText => Text("TunnelCenter.DestinationHostHint");
    public string ListenPortHintText => Text("TunnelCenter.ListenPortHint");

    public SshTunnelRuleDialogViewModel(SshTunnelRule? source)
    {
        if (source == null)
        {
            SelectedTypeOption = TypeOptions[0];
            return;
        }

        _ruleId = source.Id;
        SelectedTypeOption = TypeOptions.FirstOrDefault(option =>
            string.Equals(option.Content?.ToString(), source.Type.ToString(), StringComparison.OrdinalIgnoreCase))
            ?? TypeOptions[0];
        SourceHost = source.SourceHost;
        ListenPort = source.ListenPort > 0 ? source.ListenPort.ToString() : string.Empty;
        AcceptLocalConnectionsOnly = source.AcceptLocalConnectionsOnly;
        DestinationHost = source.DestinationHost;
        DestinationPort = source.DestinationPort > 0 ? source.DestinationPort.ToString() : string.Empty;
        Description = source.Description;
    }

    public bool TryBuildRule(out SshTunnelRule? rule)
    {
        ValidationMessage = string.Empty;

        if (!TryReadPort(ListenPort, out var listenPort))
        {
            ValidationMessage = Text("TunnelCenter.ListenPortRequired");
            rule = null;
            return false;
        }

        var type = SelectedType;
        var destinationPort = 0;
        if (type != SshTunnelRuleType.Dynamic && !TryReadPort(DestinationPort, out destinationPort))
        {
            ValidationMessage = Text("TunnelCenter.DestinationPortRequired");
            rule = null;
            return false;
        }

        rule = new SshTunnelRule
        {
            Id = _ruleId == Guid.Empty ? Guid.NewGuid() : _ruleId,
            Type = type,
            SourceHost = string.IsNullOrWhiteSpace(SourceHost) ? "localhost" : SourceHost.Trim(),
            ListenPort = listenPort,
            AcceptLocalConnectionsOnly = AcceptLocalConnectionsOnly,
            DestinationHost = type == SshTunnelRuleType.Dynamic
                ? string.Empty
                : string.IsNullOrWhiteSpace(DestinationHost) ? "localhost" : DestinationHost.Trim(),
            DestinationPort = destinationPort,
            Description = Description.Trim()
        };
        return true;
    }

    partial void OnSelectedTypeOptionChanged(ISelectOption? value)
    {
        OnPropertyChanged(nameof(IsDynamic));
        OnPropertyChanged(nameof(IsRemote));
        OnPropertyChanged(nameof(ShowDestination));
        OnPropertyChanged(nameof(IsLocalOnlyEnabled));
    }

    partial void OnValidationMessageChanged(string value)
    {
        OnPropertyChanged(nameof(HasValidationMessage));
    }

    private SshTunnelRuleType SelectedType
        => Enum.TryParse<SshTunnelRuleType>(SelectedTypeOption?.Content?.ToString(), out var type)
            ? type
            : SshTunnelRuleType.Local;

    private static bool TryReadPort(string value, out int port)
        => int.TryParse(value.Trim(), out port) && port is >= 1 and <= 65535;

    private static string Text(string key) => LocalizationService.Shared.Text(key);
}
