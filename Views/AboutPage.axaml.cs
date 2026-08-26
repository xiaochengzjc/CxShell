using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Input;

namespace CxShell.Views;

public partial class AboutPage : UserControl
{
    public AboutPage()
    {
        InitializeComponent();
    }

    private void OnGitHubPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "https://github.com/xiaochengzjc/CxShell",
                UseShellExecute = true
            });
        }
        catch
        {
            // Ignore shell integration failures; the visible URL remains available.
        }

        e.Handled = true;
    }
}
