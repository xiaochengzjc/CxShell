using System.ComponentModel;
using System.IO;
using AvaloniaEdit.Document;
using AvaloniaEdit.TextMate;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using CxShell.ViewModels;
using TextMateSharp.Grammars;
using AtomWindow = AtomUI.Desktop.Controls.Window;

namespace CxShell.Views;

public partial class RemoteFileEditorWindow : AtomWindow
{
    private bool _isClosingConfirmed;
    private bool _isUpdatingEditorText;
    private TextMate.Installation? _textMateInstallation;

    public RemoteFileEditorWindow()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Closing += OnClosing;
        Closed += OnClosed;
        Editor.TextChanged += OnEditorTextChanged;
        AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel);
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (DataContext is not RemoteFileEditorViewModel vm)
            return;

        _isUpdatingEditorText = true;
        Editor.Document = new TextDocument(vm.Text);
        _isUpdatingEditorText = false;

        InstallSyntaxHighlighting(vm.RemotePath);
    }

    private void InstallSyntaxHighlighting(string remotePath)
    {
        try
        {
            _textMateInstallation?.Dispose();
            var registryOptions = new RegistryOptions(ThemeName.LightPlus);
            _textMateInstallation = Editor.InstallTextMate(registryOptions);

            var extension = Path.GetExtension(remotePath);
            if (string.IsNullOrWhiteSpace(extension))
                return;

            var language = registryOptions.GetLanguageByExtension(extension);
            if (language == null)
                return;

            var scopeName = registryOptions.GetScopeByLanguageId(language.Id);
            if (!string.IsNullOrWhiteSpace(scopeName))
                _textMateInstallation.SetGrammar(scopeName);
        }
        catch
        {
            _textMateInstallation?.Dispose();
            _textMateInstallation = null;
        }
    }

    private void OnEditorTextChanged(object? sender, EventArgs e)
    {
        if (_isUpdatingEditorText || DataContext is not RemoteFileEditorViewModel vm)
            return;

        var text = Editor.Document?.Text ?? Editor.Text;
        if (!string.Equals(vm.Text, text, StringComparison.Ordinal))
            vm.Text = text;
    }

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.S && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            e.Handled = true;
            if (DataContext is RemoteFileEditorViewModel vm && vm.SaveCommand.CanExecute(null))
                _ = vm.SaveCommand.ExecuteAsync(null);
        }
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e)
    {
        Close();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _textMateInstallation?.Dispose();
        _textMateInstallation = null;
    }

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_isClosingConfirmed || DataContext is not RemoteFileEditorViewModel { IsDirty: true } vm)
            return;

        e.Cancel = true;
        var close = await AtomUiDialogService.ShowConfirmAsync(
            this,
            "关闭编辑器",
            $"文件“{vm.FileName}”有未保存的修改，确定关闭吗？",
            "关闭",
            "取消");
        if (!close)
            return;

        _isClosingConfirmed = true;
        Close();
    }
}
