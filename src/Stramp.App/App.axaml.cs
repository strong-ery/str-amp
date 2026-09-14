using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Stramp.App.Services;
using Stramp.App.ViewModels;
using Stramp.App.Views;
using Stramp.Audio;
using Stramp.Core.Settings;

namespace Stramp.App;

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

            desktop.MainWindow = new MainWindow
            {
                DataContext = new MainWindowViewModel(new WasapiMediaPlayer(), settings),
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
