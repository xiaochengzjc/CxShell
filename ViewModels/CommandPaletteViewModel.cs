using System.Collections.ObjectModel;
using System.ComponentModel;
using CxShell.Services;

namespace CxShell.ViewModels;

public sealed class CommandPaletteViewModel : INotifyPropertyChanged
{
    private readonly LocalizationService _localization = LocalizationService.Shared;
    private readonly Func<IReadOnlyList<CommandPaletteItem>> _itemsProvider;
    private readonly List<CommandPaletteItem> _flatItems = [];
    private IReadOnlyList<CommandPaletteItem> _allItems = [];
    private string _query = string.Empty;
    private CommandPaletteItem? _selectedItem;
    private bool _isOpen;

    public CommandPaletteViewModel(Func<IReadOnlyList<CommandPaletteItem>>? itemsProvider = null)
    {
        _itemsProvider = itemsProvider ?? (() => []);
        _localization.LanguageChanged += OnLanguageChanged;
    }

    public ObservableCollection<CommandPaletteGroup> Groups { get; } = [];

    public string Query
    {
        get => _query;
        set
        {
            value ??= string.Empty;
            if (string.Equals(_query, value, StringComparison.Ordinal))
                return;

            _query = value;
            RebuildResults();
            OnPropertyChanged(nameof(Query));
        }
    }

    public bool IsOpen
    {
        get => _isOpen;
        private set
        {
            if (_isOpen == value)
                return;

            _isOpen = value;
            OnPropertyChanged(nameof(IsOpen));
        }
    }

    public CommandPaletteItem? SelectedItem
    {
        get => _selectedItem;
        private set
        {
            if (ReferenceEquals(_selectedItem, value))
                return;

            if (_selectedItem != null)
                _selectedItem.IsSelected = false;

            _selectedItem = value;
            if (_selectedItem != null)
                _selectedItem.IsSelected = true;

            OnPropertyChanged(nameof(SelectedItem));
            OnPropertyChanged(nameof(HasResults));
        }
    }

    public int ResultCount => _flatItems.Count;
    public bool HasResults => _flatItems.Count > 0;
    public string SearchPlaceholderText => _localization.Text("Palette.SearchPlaceholder");
    public string NavigateText => _localization.Text("Palette.Navigate");
    public string ConfirmText => _localization.Text("Palette.Confirm");
    public string CloseText => _localization.Text("Palette.Close");
    public string ResultsText => _localization.Text("Palette.Results");

    public void Open()
    {
        _allItems = _itemsProvider() ?? [];
        _query = string.Empty;
        OnPropertyChanged(nameof(Query));
        RebuildResults();
        IsOpen = true;
    }

    public void Close()
    {
        IsOpen = false;
    }

    public void MoveDown() => MoveSelection(1);

    public void MoveUp() => MoveSelection(-1);

    public void ExecuteSelected()
    {
        var item = SelectedItem;
        if (item == null)
            return;

        Close();
        item.Invoke();
    }

    public void Activate(CommandPaletteItem item)
    {
        SelectedItem = item;
        ExecuteSelected();
    }

    private void MoveSelection(int delta)
    {
        if (_flatItems.Count == 0)
            return;

        var index = SelectedItem == null ? -1 : _flatItems.IndexOf(SelectedItem);
        index = (index + delta + _flatItems.Count) % _flatItems.Count;
        SelectedItem = _flatItems[index];
    }

    private void RebuildResults()
    {
        _flatItems.Clear();
        var query = _query.Trim();
        var groups = new Dictionary<string, CommandPaletteGroup>(StringComparer.Ordinal);
        var orderedGroups = new List<CommandPaletteGroup>();

        foreach (var item in _allItems)
        {
            if (!Matches(item, query))
                continue;

            if (!groups.TryGetValue(item.Category, out var group))
            {
                group = new CommandPaletteGroup(item.Category);
                groups.Add(item.Category, group);
                orderedGroups.Add(group);
            }

            group.Items.Add(item);
            _flatItems.Add(item);
        }

        Groups.Clear();
        foreach (var group in orderedGroups)
            Groups.Add(group);

        SelectedItem = _flatItems.Count > 0 ? _flatItems[0] : null;
        OnPropertyChanged(nameof(ResultCount));
        OnPropertyChanged(nameof(HasResults));
    }

    private static bool Matches(CommandPaletteItem item, string query)
    {
        if (query.Length == 0)
            return true;

        return item.Title.Contains(query, StringComparison.OrdinalIgnoreCase) ||
               (item.Hint?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false) ||
               (item.Tag?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false) ||
               IsSubsequence(item.Title, query);
    }

    private static bool IsSubsequence(string text, string query)
    {
        var queryIndex = 0;
        foreach (var character in text)
        {
            if (queryIndex < query.Length &&
                char.ToUpperInvariant(character) == char.ToUpperInvariant(query[queryIndex]))
            {
                queryIndex++;
            }
        }

        return queryIndex == query.Length;
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(SearchPlaceholderText));
        OnPropertyChanged(nameof(NavigateText));
        OnPropertyChanged(nameof(ConfirmText));
        OnPropertyChanged(nameof(CloseText));
        OnPropertyChanged(nameof(ResultsText));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged(string propertyName)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
