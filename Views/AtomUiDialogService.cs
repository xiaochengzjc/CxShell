using AtomUI.Desktop.Controls;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace CxShell.Views;

internal static class AtomUiDialogService
{
    public static async Task<bool> ShowConfirmAsync(
        TopLevel owner,
        string title,
        string message,
        string? okText = null,
        string? cancelText = null)
    {
        var result = await MessageBox.ShowMessageModalAsync(
            new Avalonia.Controls.TextBlock
            {
                Text = message,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center
            },
            options: new MessageBoxOptions
            {
                Title = title,
                Style = MessageBoxStyle.Confirm,
                Width = 380,
                MinHeight = 150,
                PlacementTarget = owner as Control
            },
            topLevel: owner);

        return result is DialogCode.Accepted;
    }
}
