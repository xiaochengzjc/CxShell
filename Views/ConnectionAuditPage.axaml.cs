using System;
using System.IO;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using CxShell.ViewModels;

namespace CxShell.Views;

public partial class ConnectionAuditPage : UserControl
{
    public ConnectionAuditPage()
    {
        InitializeComponent();
    }

    private async void OnClearClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ConnectionAuditViewModel vm ||
            TopLevel.GetTopLevel(this) is not TopLevel owner)
            return;

        var confirmed = await AtomUiDialogService.ShowConfirmAsync(
            owner,
            vm.TitleText,
            vm.ClearConfirmText);
        if (confirmed)
            vm.ClearCommand.Execute(null);
    }

    private async void OnExportClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ConnectionAuditViewModel vm ||
            TopLevel.GetTopLevel(this) is not TopLevel owner)
            return;

        try
        {
            var file = await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                SuggestedFileName = $"CxShell-ConnectionAudit-{DateTime.Now:yyyyMMdd-HHmmss}.txt",
                DefaultExtension = "txt",
                FileTypeChoices =
                [
                    new FilePickerFileType("Text file") { Patterns = ["*.txt"] }
                ]
            });
            if (file != null)
                await File.WriteAllTextAsync(file.Path.LocalPath, vm.BuildExportText());
        }
        catch
        {
            // Export is best-effort and must not close the settings center.
        }
    }
}
