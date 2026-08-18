using System.ComponentModel;

namespace CxShell.ViewModels;

public sealed class CommandPaletteItem : INotifyPropertyChanged
{
    private bool _isSelected;

    public CommandPaletteItem(
        string category,
        string title,
        Action invoke,
        string? hint = null,
        string? tag = null,
        bool isSession = false)
    {
        Category = category;
        Title = title;
        Invoke = invoke;
        Hint = hint;
        Tag = tag;
        IsSession = isSession;
    }

    public string Category { get; }
    public string Title { get; }
    public string? Hint { get; }
    public string? Tag { get; }
    public bool IsSession { get; }
    public Action Invoke { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value)
                return;

            _isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed class CommandPaletteGroup(string category)
{
    public string Category { get; } = category;
    public System.Collections.ObjectModel.ObservableCollection<CommandPaletteItem> Items { get; } = [];
}
