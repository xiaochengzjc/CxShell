using System.Diagnostics;
using AtomUI.Desktop.Controls;
using AtomUI.Theme.Resources;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using CxShell.ViewModels;

namespace CxShell.Views;

internal static class AtomUiDialogService
{
    public static async Task ShowMessageAsync(
        TopLevel owner,
        string title,
        string message,
        MessageBoxStyle style = MessageBoxStyle.Information)
    {
        await MessageBox.ShowMessageBoxModalAsync(
            new Avalonia.Controls.TextBlock
            {
                Text = message,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center
            },
            options: new MessageBoxOptions
            {
                Title = title,
                Style = style,
                Width = 420,
                MinHeight = 150,
                PlacementTarget = owner as Control
            },
            topLevel: owner);
    }

    public static async Task<bool> ShowConfirmAsync(
        TopLevel owner,
        string title,
        string message,
        string? okText = null,
        string? cancelText = null)
    {
        var result = await MessageBox.ShowMessageBoxModalAsync(
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

    public static async Task ShowAboutAsync(
        TopLevel owner,
        string title,
        string appName,
        string versionText,
        string description,
        string builtWith,
        string githubLabel,
        string githubUrl)
    {
        var link = new Avalonia.Controls.TextBlock
        {
            Text = githubUrl,
            TextWrapping = TextWrapping.Wrap,
            Cursor = new Cursor(StandardCursorType.Hand),
            Foreground = new SolidColorBrush(ThemeTokenColorHelper.GetColor(SharedTokenKind.ColorPrimary, Color.Parse("#1677FF")))
        };
        link.PointerPressed += (_, _) => OpenUrl(githubUrl);

        var content = new StackPanel
        {
            Spacing = 14,
            Width = 430,
            Children =
            {
                new StackPanel
                {
                    Spacing = 2,
                    Children =
                    {
                        new Avalonia.Controls.TextBlock
                        {
                            Text = appName,
                            FontWeight = FontWeight.SemiBold,
                            Foreground = new SolidColorBrush(ThemeTokenColorHelper.GetColor(SharedTokenKind.ColorText, Color.Parse("#262626")))
                        },
                        new Avalonia.Controls.TextBlock
                        {
                            Text = versionText,
                            Foreground = new SolidColorBrush(ThemeTokenColorHelper.GetColor(SharedTokenKind.ColorText, Color.Parse("#262626")))
                        }
                    }
                },
                new Avalonia.Controls.TextBlock
                {
                    Text = description,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = new SolidColorBrush(ThemeTokenColorHelper.GetColor(SharedTokenKind.ColorText, Color.Parse("#262626")))
                },
                new Avalonia.Controls.TextBlock
                {
                    Text = builtWith,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = new SolidColorBrush(ThemeTokenColorHelper.GetColor(SharedTokenKind.ColorText, Color.Parse("#262626")))
                },
                new StackPanel
                {
                    Spacing = 4,
                    Children =
                    {
                        new Avalonia.Controls.TextBlock
                        {
                            Text = githubLabel,
                            Foreground = new SolidColorBrush(ThemeTokenColorHelper.GetColor(SharedTokenKind.ColorText, Color.Parse("#262626")))
                        },
                        link
                    }
                }
            }
        };

        await MessageBox.ShowMessageBoxModalAsync(
            content,
            options: new MessageBoxOptions
            {
                Title = title,
                Style = MessageBoxStyle.Information,
                Width = 560,
                MinHeight = 260,
                PlacementTarget = owner as Control
            },
            topLevel: owner);
    }

    private static void OpenUrl(string url)
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
            // Ignore shell integration failures; the visible URL can still be copied.
        }
    }
}
