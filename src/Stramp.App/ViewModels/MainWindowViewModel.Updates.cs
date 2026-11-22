using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Stramp.Core.Settings;
using Stramp.Core.Updates;
using Stramp.App.Services;

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
        UpdateReleaseNotes = ReleaseNotesText.ToPlain(update.ReleaseNotes);
        IsUpdateAvailable = true;
    }

    [ObservableProperty]
    public partial bool IsCheckingForUpdates { get; set; }

    /// <summary>The menu's "Check for Updates": asks GitHub right now, ignoring the 24-hour wait and any
    /// version that was skipped (asking by hand means the user wants to hear about it).</summary>
    [RelayCommand]
    private async Task CheckForUpdatesNowAsync()
    {
        if (IsCheckingForUpdates)
            return;

        IsCheckingForUpdates = true;
        StatusText = "Checking for updates…";
        try
        {
            var update = await _updateChecker.CheckAsync(throwOnFailure: true);
            _settings.LastUpdateCheckUtc = DateTime.UtcNow;
            SettingsService.Save(_settings);

            if (update is null)
            {
                StatusText = $"You're up to date (str-amp {AppVersion.Current}).";
                return;
            }

            _pendingUpdate = update;
            UpdateVersion = update.Version;
            UpdateReleaseNotes = ReleaseNotesText.ToPlain(update.ReleaseNotes);
            IsUpdateAvailable = true;
            StatusText = "";
        }
        catch
        {
            StatusText = "Couldn't reach GitHub. Check your connection and try again.";
        }
        finally
        {
            IsCheckingForUpdates = false;
        }
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
            if (!PlatformServices.TryApplyUpdate(stagingDirectory, out var error))
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
