using Avalonia;
using System;
using Stramp.Integrations.Windows;

namespace Stramp.App;

sealed class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
#if !DEBUG
        // Running from wherever the release zip was extracted (Downloads, Desktop, ...) rather
        // than the stable per-user install folder: copy the build there and hand off to it. Gated
        // to Release builds only, so an ordinary debug run never relocates itself out of bin/.
        if (OperatingSystem.IsWindows() && WindowsAppInstaller.TryRelocateToInstallDirectory(args))
            return;
#endif
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();
}
