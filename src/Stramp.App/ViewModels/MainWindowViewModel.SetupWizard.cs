using CommunityToolkit.Mvvm.ComponentModel;
using Stramp.Core.Settings;
using Stramp.App.Services;

namespace Stramp.App.ViewModels;
public partial class MainWindowViewModel
{
    public SetupWizardViewModel SetupWizard { get; } = new()
    {
        InstallDirectory = PlatformServices.InstallDirectory,
    };

    [ObservableProperty]
    public partial bool IsSetupWizardOpen { get; set; }

    private void InitializeSetupWizard()
    {
        IsSetupWizardOpen = !_settings.SetupCompleted;
        SetupWizard.Finished += OnSetupWizardFinished;
    }

    private void OnSetupWizardFinished()
    {
        _settings.SetupCompleted = true;
        SettingsService.Save(_settings);
        IsSetupWizardOpen = false;
    }
}
