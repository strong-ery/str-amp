using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Stramp.Core.Settings;
using Stramp.Core.Updates;
using Stramp.Integrations.Windows;

namespace Stramp.App.ViewModels;
public partial class MainWindowViewModel
{
    /// <summary>How long a successful check is trusted before the next launch asks GitHub again.</summary>
    private static readonly TimeSpan UpdateCheckInterval = TimeSpan.FromHours(24);

    private readonly UpdateChecker _updateChecker = new();
    private UpdateInfo? _pendingUpdate;

    [ObservableProperty]
    public partial bool IsUpdateAvailable { get; set; }

    [ObservableProperty]
    public partial string UpdateVersion { get; set; } = "";

    [ObservableProperty]
    public partial string UpdateReleaseNotes { get; set; } = "";

    [ObservableProperty]
    public partial bool IsDownloadingUpdate { get; set; }

    [ObservableProperty]
    public partial string UpdateStatusText { get; set; } = "";

    /// <summary>
    /// Flips once an update has been staged and the app is about to close so the relaunch helper
    /// can take over; the view closes the window when this changes, same as the close button.
    /// </summary>
    [ObservableProperty]
    public partial bool IsRestartingForUpdate { get; set; }

    private async Task CheckForUpdatesAsync()
    {
        if (_settings.LastUpdateCheckUtc is { } last && DateTime.UtcNow - last < UpdateCheckInterval)
            return;

        UpdateInfo? update;
        try
        {
            update = await _updateChecker.CheckAsync();
        }
        catch
        {
            // No network, or GitHub hiccuped: try again on the next launch rather than nag now.
            return;
        }

        _settings.LastUpdateCheckUtc = DateTime.UtcNow;
        SettingsService.Save(_settings);

        if (update is null ||
            string.Equals(update.Version, _settings.SkippedUpdateVersion, StringComparison.OrdinalIgnoreCase))
            return;

        _pendingUpdate = update;
        UpdateVersion = update.Version;
        UpdateReleaseNotes = update.ReleaseNotes ?? "";
        IsUpdateAvailable = true;
    }

    [RelayCommand]
    private void DismissUpdate() => IsUpdateAvailable = false;

    [RelayCommand]
    private void SkipUpdate()
    {
        if (_pendingUpdate is { } update)
        {
            _settings.SkippedUpdateVersion = update.Version;
            SettingsService.Save(_settings);
        }

        IsUpdateAvailable = false;
    }

    [RelayCommand]
    private async Task InstallUpdateAsync()
    {
        if (_pendingUpdate is not { DownloadUrl: { } downloadUrl } update)
            return;

        IsDownloadingUpdate = true;
        UpdateStatusText = "Downloading update…";
        try
        {
            var progress = new Progress<double>(fraction => UpdateStatusText = $"Downloading update… {fraction:P0}");
            var stagingDirectory = await UpdateDownloader.DownloadAndExtractAsync(downloadUrl, update.Version, progress);

            UpdateStatusText = "Installing…";
            if (!WindowsAppUpdater.TryApplyUpdate(stagingDirectory, out var error))
            {
                UpdateStatusText = $"Couldn't install the update: {error}";
                IsDownloadingUpdate = false;
                return;
            }

            // The helper is waiting for this process to exit before it can replace these files.
            AccrueListeningTime();
            SettingsService.Save(_settings);
            lock (_listeningStatsLock)
            {
                _listeningHistoryStore.Save(_listeningHistory);
            }
            IsRestartingForUpdate = true;
        }
        catch (Exception ex)
        {
            UpdateStatusText = $"Couldn't download the update: {ex.Message}";
            IsDownloadingUpdate = false;
        }
    }
}
