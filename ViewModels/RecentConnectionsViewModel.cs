using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CxShell.Models;
using CxShell.Services;

namespace CxShell.ViewModels;

public partial class RecentConnectionsViewModel : ObservableObject, IDisposable
{
    private readonly ConnectionAuditService _auditService;
    private readonly Func<IReadOnlyList<SessionInfo>> _getSessions;
    private readonly Func<SessionInfo, Task> _connect;
    private IReadOnlyList<RecentSessionItemViewModel> _allEntries = [];

    [ObservableProperty] private string _filterText = string.Empty;
    [ObservableProperty] private RecentSessionItemViewModel? _selectedEntry;

    public ObservableCollection<RecentSessionItemViewModel> Entries { get; } = new();
    public bool HasEntries => Entries.Count > 0;
    public string TitleText => Text("RecentConnections.Title");
    public string DescriptionText => Text("RecentConnections.Description");
    public string FilterPlaceholderText => Text("RecentConnections.FilterPlaceholder");
    public string RefreshText => Text("RecentConnections.Refresh");
    public string ConnectText => Text("RecentConnections.Connect");
    public string CloseText => Text("RecentConnections.Close");
    public string EmptyText => Text("RecentConnections.Empty");
    public string ColumnSessionText => Text("RecentConnections.ColumnSession");
    public string ColumnEndpointText => Text("RecentConnections.ColumnEndpoint");
    public string ColumnProtocolText => Text("RecentConnections.ColumnProtocol");
    public string ColumnTimeText => Text("RecentConnections.ColumnTime");
    public string CountText => string.Format(Text("RecentConnections.Count"), Entries.Count);

    public RecentConnectionsViewModel(
        ConnectionAuditService auditService,
        Func<IReadOnlyList<SessionInfo>> getSessions,
        Func<SessionInfo, Task> connect)
    {
        _auditService = auditService ?? throw new ArgumentNullException(nameof(auditService));
        _getSessions = getSessions ?? throw new ArgumentNullException(nameof(getSessions));
        _connect = connect ?? throw new ArgumentNullException(nameof(connect));
        LocalizationService.Shared.LanguageChanged += OnLanguageChanged;
        Refresh();
    }

    [RelayCommand]
    private void Refresh()
    {
        var sessionsById = _getSessions()
            .GroupBy(session => session.Id)
            .ToDictionary(group => group.Key, group => group.First());
        _allEntries = _auditService
            .ReadRecentSuccessfulConnections(ConnectionAuditService.MaximumEntries)
            .Where(entry => sessionsById.ContainsKey(entry.SessionId))
            .Select(entry => new RecentSessionItemViewModel(
                sessionsById[entry.SessionId],
                entry.LocalTimestamp))
            .ToArray();
        ApplyFilter();
    }

    [RelayCommand(CanExecute = nameof(CanConnect))]
    private async Task Connect(RecentSessionItemViewModel? item)
    {
        if (item == null)
            return;

        await _connect(item.Session);
    }

    private static bool CanConnect(RecentSessionItemViewModel? item) => item != null;

    partial void OnFilterTextChanged(string value) => ApplyFilter();

    partial void OnSelectedEntryChanged(RecentSessionItemViewModel? value)
    {
        ConnectCommand.NotifyCanExecuteChanged();
    }

    private void ApplyFilter()
    {
        var query = FilterText.Trim();
        var filtered = string.IsNullOrWhiteSpace(query)
            ? _allEntries
            : _allEntries.Where(item => Matches(item, query)).ToArray();

        Entries.Clear();
        foreach (var item in filtered)
            Entries.Add(item);

        if (SelectedEntry != null && !Entries.Contains(SelectedEntry))
            SelectedEntry = null;
        OnPropertyChanged(nameof(HasEntries));
        OnPropertyChanged(nameof(CountText));
    }

    private static bool Matches(RecentSessionItemViewModel item, string query)
    {
        return item.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
               item.Endpoint.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
               item.ProtocolText.Contains(query, StringComparison.CurrentCultureIgnoreCase);
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(TitleText));
        OnPropertyChanged(nameof(DescriptionText));
        OnPropertyChanged(nameof(FilterPlaceholderText));
        OnPropertyChanged(nameof(RefreshText));
        OnPropertyChanged(nameof(ConnectText));
        OnPropertyChanged(nameof(CloseText));
        OnPropertyChanged(nameof(EmptyText));
        OnPropertyChanged(nameof(ColumnSessionText));
        OnPropertyChanged(nameof(ColumnEndpointText));
        OnPropertyChanged(nameof(ColumnProtocolText));
        OnPropertyChanged(nameof(ColumnTimeText));
        Refresh();
    }

    public void Dispose()
    {
        LocalizationService.Shared.LanguageChanged -= OnLanguageChanged;
    }

    private static string Text(string key) => LocalizationService.Shared.Text(key);
}
