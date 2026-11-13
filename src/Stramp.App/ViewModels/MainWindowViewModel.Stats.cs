using Avalonia.Threading;
using Stramp.Core.Playback;
using Stramp.Core.Settings;

namespace Stramp.App.ViewModels;
public partial class MainWindowViewModel
{
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

            if (elapsed <= 0)
                return;

            var currentSong = _queue.Current;
            _settings.AddListeningTime(elapsed);
            _listeningHistory.AddListeningTime(elapsed, currentSong);

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
