using System.Collections.Generic;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ChiXueSsh.ViewModels;

public partial class TerminalTabGroupViewModel : ObservableObject
{
    public ObservableCollection<TerminalTabViewModel> Tabs { get; } = new();

    [ObservableProperty] private TerminalTabViewModel? _selectedTab;
    [ObservableProperty] private bool _isSelected;

    public bool HasTabs => Tabs.Count > 0;
    public bool IsSelectedTerminalSession => SelectedTab?.IsTerminalSession == true;
    public bool IsSelectedVncSession => SelectedTab?.IsVncSession == true;
    public bool IsSelectedFileTransferSession => SelectedTab?.IsFileTransferSession == true;

    public TerminalTabGroupViewModel()
    {
        Tabs.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasTabs));
    }

    public TerminalTabGroupViewModel(TerminalTabViewModel tab)
        : this()
    {
        AddTab(tab);
    }

    public void AddTab(TerminalTabViewModel tab)
    {
        if (!Tabs.Contains(tab))
            Tabs.Add(tab);

        SelectedTab = tab;
    }

    public void RemoveTab(TerminalTabViewModel tab)
    {
        if (!Tabs.Remove(tab))
            return;

        if (SelectedTab == tab)
            SelectedTab = Tabs.Count > 0 ? Tabs[^1] : null;
    }

    partial void OnSelectedTabChanged(TerminalTabViewModel? value)
    {
        OnPropertyChanged(nameof(IsSelectedTerminalSession));
        OnPropertyChanged(nameof(IsSelectedVncSession));
        OnPropertyChanged(nameof(IsSelectedFileTransferSession));
    }
}

public sealed class TileTabGroupRowViewModel
{
    public ObservableCollection<TerminalTabGroupViewModel> Groups { get; }

    public TileTabGroupRowViewModel(IEnumerable<TerminalTabGroupViewModel> groups)
    {
        Groups = new ObservableCollection<TerminalTabGroupViewModel>(groups);
    }
}
