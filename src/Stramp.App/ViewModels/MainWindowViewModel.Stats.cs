using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using Stramp.Core.Models;
using Stramp.Core.Playback;
using Stramp.Core.Settings;

namespace Stramp.App.ViewModels;
public partial class MainWindowViewModel
{
    /// <summary>
    /// Share of a track's length that has to be actually listened to before it counts as a
    /// "play" toward the stats panel, so skipping past a song right after starting it doesn't
    /// inflate its play count the same as hearing it through.
    /// </summary>
    private const double PlayCountThresholdFraction = 1.0 / 3.0;

    [RelayCommand]
    private void OpenStats()
    {
        AccrueListeningTime();
        lock (_listeningStatsLock)
        {
            Stats.Refresh(_listeningHistory);
        }
        IsStatsOpen = true;
    }

    [RelayCommand]
    private void CloseStats() => IsStatsOpen = false;

    partial void OnIsPlayingChanged(bool value)
    {
        if (value)
        {
            if (!_isSeeking)
                _listeningStopwatch.Restart();
        }
        else
        {
            AccrueListeningTime();
            _listeningStopwatch.Reset();
            SettingsService.Save(_settings);
            lock (_listeningStatsLock)
            {
                _listeningHistoryStore.Save(_listeningHistory);
            }
        }
    }

    private void AccrueListeningTime()
    {
        lock (_listeningStatsLock)
        {
            if (!_listeningStopwatch.IsRunning)
                return;

            var elapsed = _listeningStopwatch.Elapsed.TotalSeconds;
            _listeningStopwatch.Restart();

            if (elapsed <= 0 || !_settings.ListeningStatsEnabled)
                return;

            var currentSong = _queue.Current;
            _settings.AddListeningTime(elapsed);
            _listeningHistory.AddListeningTime(elapsed, currentSong);
            RecordTrackPlayIfThresholdReached(currentSong, elapsed);

            if (DateTime.UtcNow - _lastListeningSaveUtc >= TimeSpan.FromSeconds(30))
            {
                _lastListeningSaveUtc = DateTime.UtcNow;
                SettingsService.Save(_settings);
                _listeningHistoryStore.Save(_listeningHistory);
            }
        }

        if (IsSettingsOpen)
        {
            if (Dispatcher.UIThread.CheckAccess())
                UpdateListeningStatsDisplay();
            else
                Dispatcher.UIThread.Post(UpdateListeningStatsDisplay);
        }

        if (IsStatsOpen)
        {
            if (Dispatcher.UIThread.CheckAccess())
                RefreshStatsDisplay();
            else
                Dispatcher.UIThread.Post(RefreshStatsDisplay);
        }
    }

    /// <summary>
    /// Counts a play once <see cref="_currentTrackListenedSeconds"/> crosses
    /// <see cref="PlayCountThresholdFraction"/> of the track's length. Must be called with
    /// <see cref="_listeningStatsLock"/> already held; <see cref="StartPlaybackNow"/> resets the
    /// counters whenever a track starts (fresh or looped) so each listen gets its own count.
    /// </summary>
    private void RecordTrackPlayIfThresholdReached(Song? song, double elapsedSeconds)
    {
        if (song is null || _currentTrackPlayRecorded)
            return;

        _currentTrackListenedSeconds += elapsedSeconds;

        var durationSeconds = song.Duration.TotalSeconds;
        var threshold = durationSeconds > 0 ? durationSeconds * PlayCountThresholdFraction : 0;
        if (_currentTrackListenedSeconds < threshold)
            return;

        _currentTrackPlayRecorded = true;
        _listeningHistory.RecordTrackPlay(song);
    }

    private void RefreshStatsDisplay()
    {
        lock (_listeningStatsLock)
        {
            Stats.Refresh(_listeningHistory);
        }
    }

    private void UpdateListeningStatsDisplay()
    {
        double totalSec;
        double monthSec;
        lock (_listeningStatsLock)
        {
            totalSec = Math.Max(_settings.TotalListenedSeconds, _listeningHistory.TotalSecondsListened);
            monthSec = Math.Max(_settings.GetCurrentMonthListenedSeconds(), _listeningHistory.GetCurrentMonthSeconds());
        }

        TotalTimeListenedText = ListeningDurationFormatter.Format(totalSec);
        TotalTimeListenedTooltip = ListeningDurationFormatter.FormatTooltip(totalSec, "Total time listened");

        MonthlyTimeListenedText = ListeningDurationFormatter.Format(monthSec);
        var currentMonthName = DateTime.Now.ToString("MMMM yyyy");
        MonthlyTimeListenedTooltip = ListeningDurationFormatter.FormatTooltip(monthSec, currentMonthName);
    }

}
