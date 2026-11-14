using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Stramp.App.Services;
using Stramp.App.Theming;
using Stramp.App.ViewModels;
using Stramp.App.Views;
using Stramp.Audio;
using Stramp.Core.Settings;

namespace Stramp.App;

/// <summary>The Avalonia application: loads settings and opens the main window.</summary>
/// <remarks>
/// Applies the saved theme before the window exists, so the first frame already has the user's
/// colors, then registers the window with <see cref="VisualThemeStyles"/> so the active visual theme
/// reaches it. Only the classic desktop lifetime is supported.
/// </remarks>
public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var settings = SettingsService.Load();
            ThemeService.Apply(settings);

            var window = new MainWindow
            {
                DataContext = new MainWindowViewModel(new WasapiMediaPlayer(), settings),
            };
            VisualThemeStyles.Host(window);
            desktop.MainWindow = window;
        }

        base.OnFrameworkInitializationCompleted();
    }
}
