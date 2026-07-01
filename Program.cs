using System;
using Avalonia;
using AtomUI;
using ReactiveUI.Avalonia;
using Velopack;

namespace CxShell;

internal class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        VelopackApp.Build()
            .SetArgs(args)
            .Run();

        BuildAvaloniaApp()
            .StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
    {
        return AppBuilder.Configure<App>()
            .UseReactiveUI(builder => { })
            .UsePlatformDetect()
            .WithAtomUIDefaultOptions()
            .LogToTrace();
    }
}
