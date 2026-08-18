using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using AtomUI;
using AtomUI.Controls;
using AtomUI.Desktop.Controls;
using AtomUI.Theme;
using AtomUI.Theme.Algorithms;
using AtomUI.Theme.Configuration;
using CxShell.ViewModels;
using CxShell.Views;

namespace CxShell;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        InstallMacOsApplicationMenu();

        this.UseAtomUI(builder =>
        {
            var initialTheme = new ThemeConfigBuilder()
                              .WithAlgorithms(ThemeAlgorithm.Default, ThemeAlgorithm.Dark)
                              .Build();
            builder.WithInitialTheme(IThemeManager.DEFAULT_THEME_ID, initialTheme);
            builder.UseAlibabaSansFont();
            builder.UseDesktopControls();
            builder.UseDesktopColorPicker();
            builder.UseDesktopDataGrid();
        });
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow(desktop.Args ?? Array.Empty<string>());
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void InstallMacOsApplicationMenu()
    {
        if (!OperatingSystem.IsMacOS())
            return;

        var aboutItem = new NativeMenuItem
        {
            Header = "About CxShell"
        };
        aboutItem.Click += (_, _) => ShowAboutFromApplicationMenu();

        var appMenu = new NativeMenu();
        appMenu.Items.Add(aboutItem);
        NativeMenu.SetMenu(this, appMenu);
    }

    private void ShowAboutFromApplicationMenu()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime { MainWindow.DataContext: MainWindowViewModel vm } &&
            vm.ShowAboutCommand.CanExecute(null))
        {
            vm.ShowAboutCommand.Execute(null);
        }
    }
}
