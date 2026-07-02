using System.ComponentModel;
using Avalonia.Interactivity;
using CxShell.ViewModels;
using AtomWindow = AtomUI.Desktop.Controls.Window;

namespace CxShell.Views;

public partial class UpdateProgressWindow : AtomWindow
{
    private bool _allowClose;

    public event EventHandler? CancelRequested;

    public UpdateProgressWindow()
    {
        InitializeComponent();
        Closing += OnClosing;
    }

    public void CloseForCompletion()
    {
        _allowClose = true;
        Close();
    }

    private void OnBackgroundClick(object? sender, RoutedEventArgs e)
    {
        Hide();
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is UpdateProgressViewModel vm)
            vm.CanCancel = false;

        CancelRequested?.Invoke(this, EventArgs.Empty);
        CloseForCompletion();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_allowClose)
            return;

        e.Cancel = true;
        Hide();
    }
}
