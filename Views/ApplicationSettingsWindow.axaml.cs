using System;
using AtomUI.Desktop.Controls;
using Avalonia.Interactivity;
using CxShell.ViewModels;

namespace CxShell.Views;

public partial class ApplicationSettingsWindow : Window
{
    protected override Type StyleKeyOverride { get; } = typeof(Window);

    public ApplicationSettingsWindow()
    {
        InitializeComponent();
    }

    public ApplicationSettingsWindow(ApplicationSettingsViewModel vm)
        : this()
    {
        DataContext = vm;
        Closed += (_, _) => vm.Dispose();
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e)
    {
        Close();
    }
}
