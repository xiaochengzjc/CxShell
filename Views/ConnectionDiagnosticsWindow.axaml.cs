using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using CxShell.ViewModels;

namespace CxShell.Views;

public partial class ConnectionDiagnosticsWindow : Window
{
    public ConnectionDiagnosticsWindow()
    {
        InitializeComponent();
        Opened += OnOpened;
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        if (DataContext is ConnectionDiagnosticsViewModel vm)
            vm.RunCommand.Execute(null);
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e)
    {
        Close();
    }

    private async void OnExportClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ConnectionDiagnosticsViewModel vm)
            return;

        try
        {
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                SuggestedFileName = $"CxShell-Diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.txt",
                DefaultExtension = "txt",
                FileTypeChoices =
                [
                    new FilePickerFileType("Text file") { Patterns = ["*.txt"] }
                ]
            });
            if (file == null)
                return;

            await File.WriteAllTextAsync(file.Path.LocalPath, vm.BuildReportText());
        }
        catch
        {
            // Export is best-effort; the diagnostic result remains available in the window.
        }
    }
}
