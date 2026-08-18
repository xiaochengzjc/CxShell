using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using CxShell.Models;
using CxShell.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CxShell.ViewModels;

public sealed class ConnectionAuditEntryViewModel
{
    public ConnectionAuditEntry Entry { get; }
    public string TimestampText => Entry.LocalTimestamp.ToString("yyyy-MM-dd HH:mm:ss");
    public string SessionText => string.IsNullOrWhiteSpace(Entry.SessionName) ? Entry.Host : Entry.SessionName;
    public string EndpointText => string.IsNullOrWhiteSpace(Entry.Username)
        ? $"{Entry.Host}:{Entry.Port}"
        : $"{Entry.Username}@{Entry.Host}:{Entry.Port}";
    public string ProtocolText => Entry.Protocol.ToString();
    public string EventText => LocalizationService.Shared.Text($"Audit.Event.{Entry.EventType}");
    public string Detail => Entry.Detail;
    public bool IsFailure => Entry.EventType == ConnectionAuditEventType.Failed;

    public ConnectionAuditEntryViewModel(ConnectionAuditEntry entry)
    {
        Entry = entry;
    }
}

public partial class ConnectionAuditViewModel : ObservableObject
{
    private readonly ConnectionAuditService _service;
    private IReadOnlyList<ConnectionAuditEntry> _allEntries = [];

    [ObservableProperty] private string _filterText = string.Empty;
    [ObservableProperty] private bool _showFailuresOnly;

    public ObservableCollection<ConnectionAuditEntryViewModel> Entries { get; } = new();
    public string TitleText => Text("Audit.Title");
    public string DescriptionText => Text("Audit.Description");
    public string FilterPlaceholderText => Text("Audit.FilterPlaceholder");
    public string FailuresOnlyText => Text("Audit.FailuresOnly");
    public string RefreshText => Text("Audit.Refresh");
    public string ClearText => Text("Audit.Clear");
    public string ClearConfirmText => Text("Audit.ClearConfirm");
    public string ExportText => Text("Audit.Export");
    public string CloseText => Text("Audit.Close");
    public string EmptyText => Text("Audit.Empty");
    public string ColumnTimeText => Text("Audit.ColumnTime");
    public string ColumnSessionText => Text("Audit.ColumnSession");
    public string ColumnEndpointText => Text("Audit.ColumnEndpoint");
    public string ColumnProtocolText => Text("Audit.ColumnProtocol");
    public string ColumnEventText => Text("Audit.ColumnEvent");
    public string ColumnDetailText => Text("Audit.ColumnDetail");
    public string CountText => string.Format(Text("Audit.Count"), Entries.Count);
    public bool HasEntries => Entries.Count > 0;
    public bool CanExport => HasEntries;

    public ConnectionAuditViewModel(ConnectionAuditService service)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        Refresh();
    }

    [RelayCommand]
    private void Refresh()
    {
        _allEntries = _service.ReadRecent();
        ApplyFilter();
    }

    [RelayCommand]
    private void Clear()
    {
        _service.Clear();
        _allEntries = [];
        ApplyFilter();
    }

    public IReadOnlyList<ConnectionAuditEntry> GetVisibleEntries()
        => Entries.Select(item => item.Entry).ToList();

    public string BuildExportText()
    {
        var builder = new StringBuilder();
        builder.AppendLine(Text("Audit.ReportTitle"));
        builder.AppendLine(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        builder.AppendLine(new string('-', 80));
        foreach (var item in Entries)
        {
            builder.AppendLine($"{item.TimestampText} | {item.EventText} | {item.ProtocolText} | {item.EndpointText}");
            builder.AppendLine($"  {item.SessionText}{(string.IsNullOrWhiteSpace(item.Detail) ? string.Empty : $": {item.Detail}")}");
        }

        return builder.ToString();
    }

    public void NotifyLocalizationChanged()
    {
        OnPropertyChanged(nameof(TitleText));
        OnPropertyChanged(nameof(DescriptionText));
        OnPropertyChanged(nameof(FilterPlaceholderText));
        OnPropertyChanged(nameof(FailuresOnlyText));
        OnPropertyChanged(nameof(RefreshText));
        OnPropertyChanged(nameof(ClearText));
        OnPropertyChanged(nameof(ClearConfirmText));
        OnPropertyChanged(nameof(ExportText));
        OnPropertyChanged(nameof(CloseText));
        OnPropertyChanged(nameof(EmptyText));
        OnPropertyChanged(nameof(ColumnTimeText));
        OnPropertyChanged(nameof(ColumnSessionText));
        OnPropertyChanged(nameof(ColumnEndpointText));
        OnPropertyChanged(nameof(ColumnProtocolText));
        OnPropertyChanged(nameof(ColumnEventText));
        OnPropertyChanged(nameof(ColumnDetailText));
        ApplyFilter();
    }

    partial void OnFilterTextChanged(string value) => ApplyFilter();

    partial void OnShowFailuresOnlyChanged(bool value) => ApplyFilter();

    private void ApplyFilter()
    {
        var query = FilterText.Trim();
        var filtered = _allEntries.Where(entry =>
            (!ShowFailuresOnly || entry.EventType == ConnectionAuditEventType.Failed) &&
            (string.IsNullOrWhiteSpace(query) || Matches(entry, query)));

        Entries.Clear();
        foreach (var entry in filtered)
            Entries.Add(new ConnectionAuditEntryViewModel(entry));

        OnPropertyChanged(nameof(HasEntries));
        OnPropertyChanged(nameof(CanExport));
        OnPropertyChanged(nameof(CountText));
    }

    private static bool Matches(ConnectionAuditEntry entry, string query)
    {
        return entry.SessionName.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
               entry.Host.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
               entry.Username.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
               entry.Protocol.ToString().Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
               entry.EventType.ToString().Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
               entry.Detail.Contains(query, StringComparison.CurrentCultureIgnoreCase);
    }

    private static string Text(string key) => LocalizationService.Shared.Text(key);
}
