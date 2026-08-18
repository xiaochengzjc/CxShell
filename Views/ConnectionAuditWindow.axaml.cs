using System;
using System.IO;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using CxShell.ViewModels;

namespace CxShell.Views;

public partial class ConnectionAuditWindow : Window
{
    public ConnectionAuditWindow()
    {
        InitializeComponent();
    }

    private async void OnClearClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ConnectionAuditViewModel vm)
            return;

        var confirmed = await AtomUiDialogService.ShowConfirmAsync(
            this,
            vm.TitleText,
            vm.ClearConfirmText);
        if (confirmed)
            vm.ClearCommand.Execute(null);
    }

    private async void OnExportClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ConnectionAuditViewModel vm)
            return;

        try
        {
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                SuggestedFileName = $"CxShell-ConnectionAudit-{DateTime.Now:yyyyMMdd-HHmmss}.txt",
                DefaultExtension = "txt",
                FileTypeChoices =
                [
                    new FilePickerFileType("Text file") { Patterns = ["*.txt"] }
                ]
            });
            if (file == null)
                return;

            await File.WriteAllTextAsync(file.Path.LocalPath, vm.BuildExportText());
        }
        catch
        {
            // Export is best-effort and must not close the audit window.
        }
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e)
    {
        Close();
    }
}
