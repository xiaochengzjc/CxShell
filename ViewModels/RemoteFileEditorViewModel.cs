using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CxShell.ViewModels;

public partial class RemoteFileEditorViewModel : ObservableObject
{
    private readonly Func<string, Task> _saveAsync;
    private string _savedText;

    [ObservableProperty] private string _text;
    [ObservableProperty] private bool _isDirty;
    [ObservableProperty] private bool _isSaving;
    [ObservableProperty] private string _statusText;

    public string FileName { get; }
    public string RemotePath { get; }
    public string WindowTitle => $"{(IsDirty ? "* " : string.Empty)}{FileName} - CxShell";

    public RemoteFileEditorViewModel(
        string fileName,
        string remotePath,
        string text,
        string statusText,
        Func<string, Task> saveAsync)
    {
        FileName = fileName;
        RemotePath = remotePath;
        _text = text;
        _savedText = text;
        _statusText = statusText;
        _saveAsync = saveAsync;
    }

    partial void OnTextChanged(string value)
    {
        IsDirty = !string.Equals(value, _savedText, StringComparison.Ordinal);
    }

    partial void OnIsDirtyChanged(bool value)
    {
        OnPropertyChanged(nameof(WindowTitle));
    }

    [RelayCommand]
    private async Task Save()
    {
        if (IsSaving)
            return;

        IsSaving = true;
        StatusText = "Saving...";
        try
        {
            await _saveAsync(Text);
            _savedText = Text;
            IsDirty = false;
            StatusText = "Saved";
        }
        catch (Exception ex)
        {
            StatusText = $"Save failed: {ex.Message}";
            throw;
        }
        finally
        {
            IsSaving = false;
        }
    }
}
