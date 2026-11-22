using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Stramp.App.Services;
using Stramp.Core.Settings;

namespace Stramp.App.ViewModels;
public partial class MainWindowViewModel
{
    /// <summary>True once a check has run and ffmpeg couldn't be found.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFfmpegFound))]
    public partial bool IsFfmpegMissing { get; set; }

    public bool IsFfmpegFound => _ffmpegChecked && !IsFfmpegMissing;

    /// <summary>The "ffmpeg is missing" dialog shown after startup.</summary>
    [ObservableProperty]
    public partial bool IsFfmpegWarningOpen { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanInstallFfmpegNow))]
    public partial bool IsInstallingFfmpeg { get; set; }

    [ObservableProperty]
    public partial string FfmpegStatusText { get; set; } = "";

    public bool CanInstallFfmpeg => FfmpegService.CanInstallAutomatically;
    public bool CanInstallFfmpegNow => CanInstallFfmpeg && !IsInstallingFfmpeg;
    public string FfmpegManualHint => FfmpegService.ManualInstallHint;
    public string FfmpegInstallButtonText => FfmpegService.InstallButtonText;

    private bool _ffmpegChecked;

    private async Task CheckFfmpegAsync()
    {
        var available = await FfmpegService.IsAvailableAsync();
        _ffmpegChecked = true;
        IsFfmpegMissing = !available;
        OnPropertyChanged(nameof(IsFfmpegFound));

        // The setup wizard has its own ffmpeg step, so don't stack a dialog on top of it.
        if (!available && !_settings.SuppressFfmpegWarning && !IsSetupWizardOpen)
            IsFfmpegWarningOpen = true;
    }

    [RelayCommand]
    private async Task InstallFfmpegAsync()
    {
        if (IsInstallingFfmpeg)
            return;

        IsInstallingFfmpeg = true;
        FfmpegStatusText = OperatingSystem.IsWindows()
            ? "Installing ffmpeg with winget… this can take a minute."
            : "Installing ffmpeg… approve the password prompt, then give it a minute.";
        try
        {
            var error = await FfmpegService.InstallAsync();
            if (error is null)
            {
                IsFfmpegMissing = false;
                IsFfmpegWarningOpen = false;
                FfmpegStatusText = "ffmpeg installed.";
                StatusText = "ffmpeg installed.";
            }
            else
            {
                FfmpegStatusText = error;
            }
        }
        finally
        {
            IsInstallingFfmpeg = false;
        }
    }

    [RelayCommand]
    private async Task RecheckFfmpegAsync()
    {
        FfmpegStatusText = "";
        await CheckFfmpegAsync();
        if (!IsFfmpegMissing)
        {
            IsFfmpegWarningOpen = false;
            FfmpegStatusText = "ffmpeg found.";
        }
        else
        {
            FfmpegStatusText = "Still can't find ffmpeg on your PATH.";
        }
    }

    [RelayCommand]
    private void DismissFfmpegWarning() => IsFfmpegWarningOpen = false;

    [RelayCommand]
    private void SuppressFfmpegWarning()
    {
        _settings.SuppressFfmpegWarning = true;
        SettingsService.Save(_settings);
        IsFfmpegWarningOpen = false;
    }
}
