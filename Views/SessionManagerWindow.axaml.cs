using System;
using AtomUI.Desktop.Controls;
using ChiXueSsh.ViewModels;

namespace ChiXueSsh.Views;

public partial class SessionManagerWindow : Window
{
    protected override Type StyleKeyOverride { get; } = typeof(Window);

    public SessionManagerWindow()
    {
        InitializeComponent();
    }

    public SessionManagerWindow(SessionTreeViewModel vm)
        : this()
    {
        vm.SelectedNode = null;
        Opened += (_, _) => DataContext = vm;
    }
}
