using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace CxShell.Views;

public partial class AboutPage : UserControl
{
    private const string GitHubUrl = "https://github.com/xiaochengzjc/CxShell";

    public AboutPage()
    {
        InitializeComponent();
    }

    private void OnGitHubClick(object? sender, RoutedEventArgs e)
    {
        OpenExternalLink(GitHubUrl);
    }

    private static void OpenExternalLink(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
        catch
        {
            // External link launching is best-effort on desktop platforms.
        }
    }
}
