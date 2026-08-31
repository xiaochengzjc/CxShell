using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace CxShell.Views;

public partial class AgentSettingsPage : UserControl
{
    private const string RoutinRegistrationUrl = "https://routin.ai/register?planInviteCode=PE32VR2X";

    public AgentSettingsPage()
    {
        InitializeComponent();
    }

    private void OnRoutinRegistrationClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = RoutinRegistrationUrl,
                UseShellExecute = true
            });
        }
        catch
        {
            // External link launching is best-effort on desktop platforms.
        }
    }
}
