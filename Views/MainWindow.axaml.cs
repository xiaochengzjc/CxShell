using System;
using AtomUI.Desktop.Controls;
using ChiXueSsh.ViewModels;

namespace ChiXueSsh.Views;

public partial class MainWindow : Window
{
    protected override Type StyleKeyOverride { get; } = typeof(Window);

    public MainWindow()
    {
        InitializeComponent();
        DataContext = new MainWindowViewModel();
    }
}
