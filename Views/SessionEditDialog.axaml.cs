using System;
using Avalonia.Interactivity;
using ChiXueSsh.ViewModels;

namespace ChiXueSsh.Views;

public partial class SessionEditDialog : AtomUI.Desktop.Controls.Window
{
    protected override Type StyleKeyOverride { get; } = typeof(AtomUI.Desktop.Controls.Window);

    public SessionEditDialog()
    {
        InitializeComponent();
    }

    private void OnWindowLoaded(object? sender, RoutedEventArgs e)
    {
        SaveBtn.Click += OnSaveClick;
        CancelBtn.Click += OnCancelClick;
    }

    private void OnSaveClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is SessionEditViewModel vm)
        {
            vm.SessionName = NameBox?.Text ?? string.Empty;
            vm.Host = HostBox?.Text ?? string.Empty;
            vm.Port = PortBox?.Text ?? string.Empty;
            vm.Username = UsernameBox?.Text ?? string.Empty;

            vm.SaveCommand.Execute(null);
            if (vm.SavedSession != null)
                Close();
        }
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        Close();
    }
}
